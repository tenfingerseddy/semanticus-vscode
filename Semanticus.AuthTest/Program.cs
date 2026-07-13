using System;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using TOM = Microsoft.AnalysisServices.Tabular;
using AS = Microsoft.AnalysisServices;
using Adomd = Microsoft.AnalysisServices.AdomdClient;

// Empirically find which XMLA token-auth mechanism a live endpoint accepts.
//   usage: authtest <endpoint> [authMode=azcli] [tenantId]
if (args.Length == 0) { Console.Error.WriteLine("usage: authtest <endpoint> [authMode] [tenantId]"); return 1; }
var endpoint = args[0];
var authMode = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : "azcli";
var tenantId = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : null;

Console.WriteLine($"endpoint = {endpoint}\nauth = {authMode}  tenant = {tenantId ?? "(default)"}\n");
var tok = await EntraToken.AcquireFullAsync(authMode, null, CancellationToken.None, tenantId);
Console.WriteLine($"token acquired: {tok.Token.Length} chars, expires {tok.ExpiresOn:u}\n");

void Result(string label, Action body)
{
    try { body(); Console.WriteLine($"[OK ]  {label}"); }
    catch (Exception ex)
    {
        var msg = ex.Message.Replace("\r", " ").Replace("\n", " ");
        if (msg.Length > 160) msg = msg.Substring(0, 160);
        Console.WriteLine($"[ERR]  {label}\n         {ex.GetType().Name}: {msg}");
    }
}

// S1: AMO TOM.Server + Server.AccessToken (current open_live approach)
Result("S1 AMO Server.AccessToken + Connect(Data Source=)", () =>
{
    using var s = new TOM.Server();
    s.AccessToken = new AS.AccessToken(tok.Token, tok.ExpiresOn);
    s.Connect("Data Source=" + endpoint);
    Console.WriteLine($"         -> {s.Databases.Count} database(s): {string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Cast<TOM.Database>(s.Databases), d => d.Name))}");
    s.Disconnect();
});

// S2: AMO TOM.Server + AccessToken WITH a refresh callback registered (some AMO versions only honor the
//     token when an OnAccessTokenExpired handler is present).
Result("S2 AMO Server.AccessToken + OnAccessTokenExpired callback", () =>
{
    using var s = new TOM.Server();
    s.AccessToken = new AS.AccessToken(tok.Token, tok.ExpiresOn);
    s.OnAccessTokenExpired = old => new AS.AccessToken(tok.Token, tok.ExpiresOn);
    s.Connect("Data Source=" + endpoint);
    Console.WriteLine($"         -> {s.Databases.Count} database(s)");
    s.Disconnect();
});

// S3: ADOMD with Password=token (what LiveConnection / connect_xmla uses today)
Result("S3 ADOMD Password=token + DMV query", () =>
{
    var cs = $"Data Source={endpoint};Application Name=Semanticus;Password={tok.Token}";
    using var c = new Adomd.AdomdConnection(cs);
    c.Open();
    using var cmd = new Adomd.AdomdCommand("SELECT [CATALOG_NAME] FROM $SYSTEM.DBSCHEMA_CATALOGS", c);
    using var r = cmd.ExecuteReader();
    int n = 0; string first = null;
    while (r.Read()) { if (first == null) first = Convert.ToString(r[0]); n++; }
    Console.WriteLine($"         -> {n} catalog(s); first = {first}");
    c.Close();
});

// S4: ADOMD with AdomdConnection.AccessToken property (if present in this version)
Result("S4 ADOMD .AccessToken property + DMV query", () =>
{
    var cs = $"Data Source={endpoint};Application Name=Semanticus";
    using var c = new Adomd.AdomdConnection(cs);
    c.AccessToken = new AS.AccessToken(tok.Token, tok.ExpiresOn);
    c.Open();
    using var cmd = new Adomd.AdomdCommand("SELECT [CATALOG_NAME] FROM $SYSTEM.DBSCHEMA_CATALOGS", c);
    using var r = cmd.ExecuteReader();
    int n = 0; while (r.Read()) n++;
    Console.WriteLine($"         -> {n} catalog(s)");
    c.Close();
});

Console.WriteLine("\n==== AUTHTEST DONE ====");
return 0;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using TOM = Microsoft.AnalysisServices.Tabular;
using AS = Microsoft.AnalysisServices;

// Metadata-only live deploy. Pushes ONLY the explicitly-authored changes (from content.json) plus the
// optimised model's linguistic schema (synonyms + AI instructions) to a live XMLA model via Model.SaveChanges().
// No data/partition changes. DRY-RUN by default; pass --commit to actually write.
//   usage: deploy <optimised.bim> <content.json> <endpoint> <database> <authMode=serviceprincipal> [--commit]
if (args.Length < 4) { Console.Error.WriteLine("usage: deploy <optimised.bim> <content.json> <endpoint> <database> [authMode] [--commit]"); return 1; }
var optBim = args[0];
var contentPath = args[1];
var endpoint = args[2];
var database = args[3];
var authMode = args.Length > 4 && !args[4].StartsWith("--") ? args[4] : "serviceprincipal";
var commit = args.Contains("--commit");

Console.WriteLine($"== Semanticus metadata deploy ==  {(commit ? "COMMIT (live write)" : "DRY RUN (no write)")}");
Console.WriteLine($"endpoint={endpoint}  db={database}  auth={authMode}\n");

var content = JsonDocument.Parse(File.ReadAllText(contentPath)).RootElement;
var optDb = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(optBim), null, AS.CompatibilityMode.PowerBI);
var optModel = optDb.Model;

var tok = await EntraToken.AcquireFullAsync(authMode, null, CancellationToken.None, null);
using var server = new TOM.Server();
server.AccessToken = new AS.AccessToken(tok.Token, tok.ExpiresOn);
server.Connect("Data Source=" + endpoint);
var liveDb = server.Databases.FindByName(database) ?? throw new InvalidOperationException($"Database '{database}' not found.");
var live = liveDb.Model;
Console.WriteLine($"connected: {liveDb.Name}  ({live.Tables.Count} tables, {live.Tables.Sum(t => t.Measures.Count)} measures)\n");

TOM.NamedMetadataObject Resolve(string r)
{
    var parts = r.Split(new[] { ':' }, 2);
    var kind = parts[0]; var path = parts[1];
    if (kind == "table") return live.Tables.Find(path);
    var slash = path.IndexOf('/');
    var tbl = live.Tables.Find(path.Substring(0, slash));
    if (tbl == null) return null;
    var name = path.Substring(slash + 1);
    return kind == "column" ? (TOM.NamedMetadataObject)tbl.Columns.Find(name) : tbl.Measures.Find(name);
}

int desc = 0, hid = 0, cat = 0, ren = 0, cult = 0;
var renames = new List<(string table, string oldName, string newName)>();
var log = new List<string>();

foreach (var e in content.GetProperty("edits").EnumerateArray())
{
    var r = e.GetProperty("ref").GetString();
    var obj = Resolve(r);
    if (obj == null) { Console.Error.WriteLine($"  WARN: {r} not found on live model"); continue; }

    if (e.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String && d.GetString().Length > 0)
    {
        var text = d.GetString();
        var cur = obj is TOM.Table t ? t.Description : obj is TOM.Column c ? c.Description : ((TOM.Measure)obj).Description;
        if (cur != text) { if (obj is TOM.Table t2) t2.Description = text; else if (obj is TOM.Column c2) c2.Description = text; else ((TOM.Measure)obj).Description = text; desc++; }
    }
    if (e.TryGetProperty("hide", out var h) && (h.ValueKind == JsonValueKind.True || h.ValueKind == JsonValueKind.False) && obj is TOM.Column hc)
    { var hv = h.GetBoolean(); if (hc.IsHidden != hv) { hc.IsHidden = hv; hid++; log.Add($"{(hv ? "hide" : "show")}  {r}"); } }
    if (e.TryGetProperty("dataCategory", out var dcv) && dcv.ValueKind == JsonValueKind.String && obj is TOM.Column cc)
    { var v = dcv.GetString(); if (cc.DataCategory != v) { cc.DataCategory = v; cat++; log.Add($"dataCategory {v}  {r}"); } }
    if (e.TryGetProperty("rename", out var rn) && rn.ValueKind == JsonValueKind.String && rn.GetString().Length > 0)
    { var p = r.Split(new[] { ':' }, 2)[1]; var sl = p.IndexOf('/'); renames.Add((p.Substring(0, sl), p.Substring(sl + 1), rn.GetString())); }
}

// Renames last (a rename invalidates the old name for any earlier lookup).
foreach (var (table, oldName, newName) in renames)
{
    var col = live.Tables.Find(table)?.Columns.Find(oldName);
    if (col == null) { Console.Error.WriteLine($"  WARN: rename target {table}[{oldName}] not found"); continue; }
    if (col.Name != newName) { col.Name = newName; ren++; log.Add($"rename {table}[{oldName}] -> {newName}"); }
}

// Linguistic schema (synonyms + AI instructions) — copy the authored LSDL (Culture.LinguisticMetadata) from the optimised model.
foreach (var oc in optModel.Cultures)
{
    var olm = oc.LinguisticMetadata;
    if (olm == null || string.IsNullOrEmpty(olm.Content)) continue;
    var lc = live.Cultures.Find(oc.Name);
    if (lc == null) { lc = new TOM.Culture { Name = oc.Name }; live.Cultures.Add(lc); }
    if (lc.LinguisticMetadata?.Content != olm.Content)
    {
        var lm = new TOM.LinguisticMetadata();
        lm.ContentType = olm.ContentType;   // set ContentType (Json) BEFORE Content — the Content setter validates against it
        lm.Content = olm.Content;
        lc.LinguisticMetadata = lm;
        cult++; log.Add($"culture {oc.Name}: linguistic schema (synonyms + AI instructions)");
    }
}

Console.WriteLine($"CHANGE SET: {desc} descriptions, {hid} visibility, {cat} data-categories, {ren} renames, {cult} linguistic-schema");
foreach (var l in log.Take(40)) Console.WriteLine("   " + l);

if (!commit) { Console.WriteLine("\nDRY RUN — nothing written. Re-run with --commit to deploy."); return 0; }

Console.WriteLine("\ncommitting via Model.SaveChanges() ...");
live.SaveChanges();
Console.WriteLine("==== DEPLOY COMMITTED (metadata pushed to live) ====");
return 0;

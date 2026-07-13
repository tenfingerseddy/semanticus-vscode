using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Analysis;

// Live verification harness for the open_live door + the live query tools.
//   usage: liveprobe <endpoint> [database] [authMode=azcli|interactive|devicecode|token] [tenantId] [rawToken]
// We acquire the Entra token ONCE here and reuse it for both open_live and connect_xmla, so any
// interactive mode (devicecode/interactive) prompts at most once. Read-only: it loads + scans the
// model and runs a couple of probe queries. It never saves or deploys anything.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: liveprobe <endpoint> [database] [authMode=azcli|interactive|devicecode|token] [tenantId] [rawToken]");
    return 1;
}
var endpoint = args[0];
var database = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : null;
var authMode = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : "azcli";
var tenantId = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3]) ? args[3] : null;
var rawToken = args.Length > 4 && !string.IsNullOrWhiteSpace(args[4]) ? args[4] : null;

Console.WriteLine($"== Semanticus LiveProbe ==");
Console.WriteLine($"endpoint = {endpoint}");
Console.WriteLine($"database = {database ?? "(first/only dataset)"}");
Console.WriteLine($"auth     = {authMode}   tenant = {tenantId ?? "(default)"}");

// 1) Acquire the token once (devicecode prints a URL+code to stderr; complete it in a browser).
string token;
try
{
    Console.WriteLine("\n[1/4] Acquiring Entra token...");
    token = await EntraToken.AcquireAsync(authMode, rawToken, CancellationToken.None, tenantId);
    Console.WriteLine("      token acquired (" + token.Length + " chars).");
}
catch (Exception ex)
{
    Console.Error.WriteLine("AUTH FAILED: " + ex.GetType().Name + ": " + ex.Message);
    if (ex.InnerException != null) Console.Error.WriteLine("  inner: " + ex.InnerException.Message);
    return 2;
}

var sessions = new SessionManager();
var engine = new LocalEngine(sessions);

// 2) open_live — load the FULL editable model via TOM. This is the Phase-2 unlock.
OpenResult open;
try
{
    Console.WriteLine("\n[2/4] open_live — loading the editable model via TOM...");
    open = await engine.OpenLiveAsync(endpoint, database, "token", token, null);
}
catch (Exception ex)
{
    Console.Error.WriteLine("OPEN_LIVE FAILED:");
    for (var e = ex; e != null; e = e.InnerException)
        Console.Error.WriteLine("  " + e.GetType().FullName + ": " + e.Message);
    Console.Error.WriteLine("  -- stack --\n" + ex.StackTrace);
    return 3;
}
Console.WriteLine($"      LOADED '{open.ModelName}'  source={open.Source}");
Console.WriteLine($"      tables={open.Tables}  measures={open.Measures}");

// Live-source binding: open_live should bind the session to its source (endpoint + the resolved dataset) so
// deploy_live can push back without re-supplying the connection. Holds no token/secret — only the coordinates.
var si = await engine.SessionInfoAsync();
var boundOk = si.LiveBound && si.LiveEndpoint == endpoint && !string.IsNullOrEmpty(si.LiveDatabase);
Console.WriteLine($"      [{(boundOk ? "PASS" : "FAIL")}] session live-bound: LiveBound={si.LiveBound}  endpoint={si.LiveEndpoint}  db={si.LiveDatabase}");

// 3) AI-readiness scan/grade on the live-loaded model.
Console.WriteLine("\n[3/4] AI-readiness scan...");
var card = await engine.AiReadinessScanAsync();
Console.WriteLine($"      GRADE {card.Grade}   overall {card.Overall}   raw {card.RawOverall}   findings {card.Findings.Count()}");
if (card.GatedBy != null && card.GatedBy.Length > 0) Console.WriteLine("      GATED: " + string.Join(" | ", card.GatedBy));
Console.WriteLine("      -- categories --");
foreach (var c in card.Categories.OrderByDescending(c => c.Weight))
    Console.WriteLine($"        {c.Category,-16} score {c.Score,6:F1}  appl {c.Applicable,5}  viol {c.Violations,5}");
Console.WriteLine("      -- findings by rule --");
foreach (var g in card.Findings.GroupBy(f => f.RuleId).OrderByDescending(g => g.Count()))
    Console.WriteLine($"        {g.Key,-26} x{g.Count()}");

// 3b) deploy_live round-trip test (DRY RUN — no write). Makes one trivial in-session edit; the dry-run change
//     set should be exactly that edit (proves session->live LineageTag matching + a clean serialize round-trip).
if (args.Contains("--deploytest"))
{
    Console.WriteLine("\n[deploy-test] deploy_live DRY RUN tests (read-only, no write)...");
    var clean = await engine.DeployLiveAsync(endpoint, open.ModelName, "token", token, null, false);
    Console.WriteLine($"      baseline (no edits): total={clean.TotalChanges}  conflicts={clean.Conflicts.Length}  unmatched={clean.Unmatched.Length}  liveOnly={clean.LiveOnly.Length}  (expect total 0 = clean read-only round-trip)");

    // 1 description edit + 1 rename (the rename triggers FormulaFixup on dependents, so the deploy should carry the
    // rename AND the fixed-up dependent DAX together — a consistency check).
    await engine.SetDescriptionAsync("measure:Key Measures/Sales Amount", "Net revenue [deploy-test marker]", "agent");
    await engine.RenameObjectAsync("measure:Key Measures/Total Quantity", "Total Quantity (units)", "agent");
    var rep = await engine.DeployLiveAsync(endpoint, open.ModelName, "token", token, null, false);
    Console.WriteLine($"      after desc+rename: total={rep.TotalChanges}  desc={rep.Descriptions} ren={rep.Renames} expr={rep.Expressions} cult={rep.Cultures}");
    Console.WriteLine($"      committed={rep.Committed}  conflicts={rep.Conflicts.Length}  unmatched={rep.Unmatched.Length}  liveOnly={rep.LiveOnly.Length}  error={rep.Error ?? "none"}");
    foreach (var c in rep.Changes.Take(10)) Console.WriteLine("        change: " + c);

    // Deploy-to-source: deploy_live with NO endpoint must produce the SAME dry-run change set as the explicit
    // call (endpoint + database resolve from the session's bound origin). rawToken is still supplied since the
    // probe authenticates with authMode "token". Both are dry-run (commit=false) — nothing is written.
    var bySource = await engine.DeployLiveAsync(null, null, "token", token, null, false);
    var sameSet = bySource.TotalChanges == rep.TotalChanges && bySource.Endpoint == rep.Endpoint
        && bySource.Database == rep.Database   // prove it resolved to the SAME model, not just the same change count
        && bySource.Conflicts.Length == rep.Conflicts.Length && bySource.Unmatched.Length == rep.Unmatched.Length
        && bySource.LiveOnly.Length == rep.LiveOnly.Length && bySource.Error == null;
    Console.WriteLine($"      [{(sameSet ? "PASS" : "FAIL")}] deploy-to-source (no endpoint) == explicit dry-run: total {bySource.TotalChanges} vs {rep.TotalChanges}; endpoint '{bySource.Endpoint}'; db bound='{bySource.Database}' explicit='{rep.Database}'");
}

// 4) Live query tools (reuse the same token for the ADOMD connection). Best-effort; a failure here
//    does not invalidate the open_live result above.
Console.WriteLine("\n[4/4] Live query tools (connect_xmla + sample queries)...");
try
{
    var conn = await engine.ConnectXmlaAsync(endpoint, open.ModelName, "token", token);
    Console.WriteLine($"      connected: {conn.Connected}  kind={conn.Kind}  src={conn.DataSource}");

    var ping = await engine.RunDaxAsync("EVALUATE ROW(\"ping\", 1 + 1)", 10);
    Console.WriteLine($"      run_dax ping: " + (ping.Error ?? $"{ping.RowCount} row(s), {ping.ElapsedMs}ms"));

    var dmv = await engine.RunDmvAsync("SELECT [TABLE_NAME], [ROWS_COUNT] FROM $SYSTEM.DISCOVER_STORAGE_TABLES ORDER BY [ROWS_COUNT] DESC", 10);
    Console.WriteLine($"      run_dmv top tables: " + (dmv.Error ?? $"{dmv.RowCount} row(s)"));
    if (dmv.Error == null) foreach (var r in dmv.Rows.Take(5)) Console.WriteLine($"          {r[0]}  rows={r[1]}");

    var vpaq = await engine.VertiPaqScanAsync(10);
    Console.WriteLine($"      vpaq_scan: " + (vpaq.Error ?? $"model {vpaq.ModelSize / 1024.0 / 1024.0:F1} MB, {vpaq.Tables?.Length ?? 0} tables; top cols:"));
    if (vpaq.Error == null && vpaq.TopColumns != null)
        foreach (var col in vpaq.TopColumns.Take(5)) Console.WriteLine($"          {col.Table}[{col.Column}]  {col.TotalSize / 1024.0 / 1024.0:F2} MB");

    // Wave-2 dmv readiness rules: the live cardinality-aware scan (COLUMNSTATISTICS -> Q&A-index rules).
    var liveCard = await engine.AiReadinessScanLiveAsync();
    var cl = liveCard.Categories.FirstOrDefault(c => c.Category == "CopilotLimits");
    var offlineCl = card.Categories.FirstOrDefault(c => c.Category == "CopilotLimits");
    var scaleF = liveCard.Findings.Where(f => f.RuleId.StartsWith("SCALE-")).ToList();
    Console.WriteLine($"      ai_readiness_scan_live: grade {liveCard.Grade} ({liveCard.Overall}); CopilotLimits score {cl?.Score:F1} appl {cl?.Applicable} viol {cl?.Violations}");
    // Assert the live path actually evaluated the dmv rules (CopilotLimits applicable grows beyond the offline scan).
    var dmvRan = cl != null && offlineCl != null && cl.Applicable > offlineCl.Applicable;
    Console.WriteLine($"      [{(dmvRan ? "PASS" : "FAIL")}] dmv rules ran live: CopilotLimits applicable {offlineCl?.Applicable} -> {cl?.Applicable}; SCALE-* findings {scaleF.Count}");
    foreach (var f in scaleF.Take(5)) Console.WriteLine($"          {f.RuleId}: {f.ObjectName}");
}
catch (Exception ex)
{
    Console.Error.WriteLine("      live-query battery error (non-fatal): " + ex.Message);
}

Console.WriteLine("\n==== LIVEPROBE DONE ====");
return 0;

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Analysis;

if (args.Length == 0) { Console.Error.WriteLine("usage: probe <model path>"); return 1; }
var path = args[0];
var sessions = new SessionManager();
var engine = new LocalEngine(sessions);
Console.WriteLine("Opening: " + path);
await engine.OpenAsync(path);
var card = await engine.AiReadinessScanAsync();

Console.WriteLine($"\nGRADE {card.Grade}   overall {card.Overall}   raw {card.RawOverall}");
if (card.GatedBy != null && card.GatedBy.Length > 0) Console.WriteLine("GATED: " + string.Join(" | ", card.GatedBy));

// Prep-for-AI surface diagnostic (raw counts the rules score on).
var prep = await sessions.Current.ReadAsync(PrepForAiReader.Read);
Console.WriteLine($"\n== Prep-for-AI == lsdl {prep.HasLinguisticSchema}  qna {(prep.QnaEnabled?.ToString() ?? "?")}  verifiedAnswers {prep.VerifiedAnswerCount}  aiInstrLen {prep.AiInstructionsLength}  schemaExcluded {prep.AiSchemaExcludedFields}");

Console.WriteLine("\n== Categories ==");
foreach (var c in card.Categories.OrderByDescending(c => c.Weight))
    Console.WriteLine($"  {c.Category,-16} score {c.Score,6:F1}  appl {c.Applicable,5}  viol {c.Violations,5}  hasRules {c.HasRules}");

Console.WriteLine("\n== Findings by rule (count + sample objects) ==");
foreach (var g in card.Findings.GroupBy(f => f.RuleId).OrderBy(g => g.Key))
{
    var names = string.Join(", ", g.Take(10).Select(f => f.ObjectName));
    Console.WriteLine($"  {g.Key,-22} x{g.Count(),-4} {names}");
}

// Focus: the newest rules — show every offending object so false positives are visible.
var NEW = new[] { "SUMMARIZE-DIMENSION", "NAME-TECH-PREFIX", "DESC-LONG-OBJECT", "NAME-INVALID-CHARS", "MEAS-DUP-EXPR" };
Console.WriteLine("\n== New-rule offenders (full) ==");
foreach (var id in NEW)
{
    var fs = card.Findings.Where(f => f.RuleId == id).ToList();
    Console.WriteLine($"  {id} ({fs.Count}):");
    foreach (var f in fs) Console.WriteLine($"      - {f.ObjectName}");
}

// BPA (Best Practice Analyzer) — now defaulting to the bundled Power BI standard ruleset. Surfaces the rule
// count, violations by rule, and any rule-errors (rules whose Dynamic-LINQ expression our engine can't evaluate).
var bpa = await engine.BpaScanAsync();
Console.WriteLine($"\n== BPA == rules {bpa.RuleCount}  violations {bpa.ViolationCount}  autoFixable {bpa.AutoFixable}  ruleErrors {bpa.RuleErrors.Length}");
foreach (var g in bpa.Violations.GroupBy(v => v.RuleId).OrderByDescending(g => g.Count()))
    Console.WriteLine($"  {g.Key,-36} x{g.Count(),-4} (sev {g.First().Severity})");
if (bpa.RuleErrors.Length > 0)
{
    Console.WriteLine("  -- rule errors (skipped gracefully) --");
    foreach (var e in bpa.RuleErrors) Console.WriteLine($"      ! {e}");
}

// RLS roles — list existing, then probe create/filter/delete (net-zero) to confirm role editing works on this
// model's compatibility/governance mode (esp. Power BI mode).
var existingRoles = await engine.ListRolesAsync();
Console.WriteLine($"\n== RLS == {existingRoles.Length} existing role(s)");
foreach (var r in existingRoles)
    Console.WriteLine($"  {r.Name} [{r.ModelPermission}]  filters {r.TableFilters.Length}  members {r.Members.Length}");
int probeFailures = 0;
void Probe(string label, bool ok) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}"); if (!ok) probeFailures++; }
try
{
    var tbl = (await engine.GetModelGraphAsync()).Tables.FirstOrDefault();
    if (tbl != null)
    {
        await engine.CreateRoleAsync("__probe_role", "Read", "agent");
        var setRes = await engine.SetTablePermissionAsync("__probe_role", "table:" + tbl.Name, "1 = 1", "agent");
        var probed = (await engine.ListRolesAsync()).FirstOrDefault(r => r.Name == "__probe_role");
        Probe($"create role + RLS filter on '{tbl.Name}' (compat/governance allows it); perm now {setRes.ModelPermission}",
            probed != null && probed.TableFilters.Length == 1);
        await engine.DeleteRoleAsync("__probe_role", "agent");
        Probe("delete role (net-zero)", (await engine.ListRolesAsync()).All(r => r.Name != "__probe_role"));
    }
}
catch (Exception ex) { Probe($"RLS edit blocked: {ex.GetType().Name}: {ex.Message}", false); }

// OLS (object-level security) — happy path on a CL>=1400 model (Finance is 1606); gracefully skipped below 1400.
Console.WriteLine("\n== OLS ==");
try
{
    var tbl = (await engine.GetModelGraphAsync()).Tables.FirstOrDefault(t => !t.IsHidden) ?? (await engine.GetModelGraphAsync()).Tables.FirstOrDefault();
    if (tbl != null)
    {
        await engine.CreateRoleAsync("__ols_probe", "Read", "agent");
        try
        {
            var setOls = await engine.SetTableObjectPermissionAsync("__ols_probe", "table:" + tbl.Name, "None", "agent");
            var surfaced = (await engine.ListRolesAsync()).First(r => r.Name == "__ols_probe")
                .ObjectPermissions.Any(op => op.Table == tbl.Name && op.MetadataPermission == "None");
            Probe($"hide table '{tbl.Name}' (None) surfaces in list_roles", setOls.Changed && surfaced);
            var clrOls = await engine.SetTableObjectPermissionAsync("__ols_probe", "table:" + tbl.Name, "Default", "agent");
            Probe("clear table OLS (Default) is net-zero (empty permission removed)",
                clrOls.Changed && (await engine.ListRolesAsync()).First(r => r.Name == "__ols_probe").ObjectPermissions.Length == 0);
        }
        catch (Exception ex) when (ex.Message.Contains("compatibility level")) { Console.WriteLine("  [skip] OLS gated: model CL < 1400"); }
        await engine.DeleteRoleAsync("__ols_probe", "agent");
    }
}
catch (Exception ex) { Probe($"OLS edit failed: {ex.GetType().Name}: {ex.Message}", false); }

// Optional full-findings dump (UTF-8 file) so messages with unicode (→) survive the cp1252 console: probe <model> <outFile>
if (args.Length > 1)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"# Full AI-readiness findings for: {path}");
    sb.AppendLine($"# GRADE {card.Grade}  overall {card.Overall}  rawOverall {card.RawOverall}  findings {card.Findings.Count()}");
    foreach (var g in card.Findings.GroupBy(f => f.RuleId).OrderBy(g => g.Key))
    {
        sb.AppendLine($"\n## {g.Key}  ({g.Count()})  [{g.First().Category}/{g.First().Severity}]");
        foreach (var f in g.OrderBy(f => f.ObjectName))
            sb.AppendLine($"  - {f.ObjectName}\t{f.Message}");
    }
    System.IO.File.WriteAllText(args[1], sb.ToString(), new System.Text.UTF8Encoding(false));
    Console.WriteLine($"\nFull findings written to {args[1]}");
}
if (probeFailures > 0) Console.WriteLine($"\n{probeFailures} RLS probe check(s) FAILED.");
return probeFailures == 0 ? 0 : 1;

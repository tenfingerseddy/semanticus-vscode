// The matrix is built on rows and diffs whose fields are null until a path fills them, and it reads the analyzers own null-tolerant DTOs;
// nullable annotations here would only annotate that, so the file opts out rather than add a warning per field (round 7 low L8).
#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// The rule-clash matrix (m11/rule-clashes round 8): a CLOSED table generated from the bundled rule set, not a list somebody
    /// remembered. For every bundled readiness rule and Model quality rule the person can accept, it takes a model where the
    /// rule fires, accepts one finding of it (and, for a rule that declines a change, the whole rule), then drives every path
    /// that writes or queues: the Model Plan's seeded rows (Run the plan, and each row on its own), apply_safe_fixes,
    /// make_model_ai_ready, BPA Fix all, and the change plan with the assistant's bpa_fix rows. It asserts two things, and each
    /// is checked against what the model and the pages say, never against the table under test:
    ///   1. nothing the acceptance declines is written or queued, and
    ///   2. no OTHER finding reads as accepted or passes the deploy gate.
    /// The oracle for 1 is the diff of the serialized model before and after the paths, so a path nobody thought of is caught
    /// the same way as one somebody did. The changes that are not a property of the finding's own object (a date table marked,
    /// a measure added over a column) are named in <see cref="StructuralAsks"/>, a second statement of the same fact.
    /// </summary>
    internal static class RuleClashMatrix
    {
        internal const string Why = "Accepted on purpose (rule clash matrix).";

        // ---- the rules ----------------------------------------------------------------------------------------------------

        internal sealed class RuleRef
        {
            public string System;          // "air" | "bpa"
            public string RuleId;
            public string Title;
            public RuleAbout About;        // null: the rule declines nothing the table knows
            public bool Live;              // a readiness rule that reads live data: never evaluated offline
            public bool Fallback;          // a Model quality rule only the fallback rule set has: in force only if the bundled set fails to load
            public string Key => System + ":" + RuleId;
        }

        internal static IEnumerable<BpaRule> ModelQualityRules() =>
            BpaRuleSet.Standard().Concat(BpaDefaultRules.Rules).GroupBy(r => r.ID, StringComparer.OrdinalIgnoreCase).Select(g => g.First());

        internal static IReadOnlyList<RuleRef> Catalog()
        {
            var list = new List<RuleRef>();
            var live = new HashSet<string>(ReadinessRuleSet.LiveRules(new ReadinessLiveStats()).Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var r in ReadinessRuleSet.Default().Concat(ReadinessRuleSet.LiveRules(new ReadinessLiveStats())).GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
                list.Add(new RuleRef { System = "air", RuleId = r.Id, Title = r.Title, About = RuleAboutTable.ForReadiness(r.Id), Live = live.Contains(r.Id) });
            var bundled = new HashSet<string>(BpaRuleSet.Standard().Select(r => r.ID), StringComparer.OrdinalIgnoreCase);
            foreach (var r in ModelQualityRules())
                list.Add(new RuleRef { System = "bpa", RuleId = r.ID, Title = r.Name, About = RuleAboutTable.ForBpa(r), Fallback = !bundled.Contains(r.ID) });
            return list;
        }

        // ---- the fixtures -------------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, string> Built = new();
        private static readonly object BuiltLock = new();
        private static string _scratch;

        private static string Scratch()
        {
            if (_scratch != null) return _scratch;
            // The process id leads the folder's name. NO CODE RUNS AS THE TEST PROCESS EXITS (NoBrowserSignInGuardTests, state-home
            // lane): the folder is removed by the NEXT process instead, which sweeps the folders whose process is gone.
            _scratch = Path.Combine(Path.GetTempPath(), "sem-matrix-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_scratch);
            SweepDeadProcessScratch(Path.GetTempPath());
            return _scratch;
        }

        private static void SweepDeadProcessScratch(string parent)
        {
            try
            {
                foreach (var old in Directory.GetDirectories(parent, "sem-matrix-*"))
                {
                    var parts = Path.GetFileName(old).Split('-');
                    if (parts.Length < 4 || !int.TryParse(parts[2], out var pid) || pid == Environment.ProcessId) continue;
                    bool alive;
                    try { System.Diagnostics.Process.GetProcessById(pid).Dispose(); alive = true; } catch (ArgumentException) { alive = false; }
                    if (alive) continue;
                    try { Directory.Delete(old, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The model file of a named fixture, built once per process.</summary>
        internal static async Task<string> FixtureFileAsync(string name)
        {
            lock (BuiltLock) if (Built.TryGetValue(name, out var have)) return have;
            var path = Path.Combine(Scratch(), name + ".bim");
            if (name == "adventureworks") File.Copy(TestModels.FindBim(), path, true);
            else
            {
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await RuleClashFixtures.BuildAsync(f, name);
                await f.SaveAsAsync(name + ".bim");
                File.Copy(Path.Combine(f.Workspace, name + ".bim"), path, true);
            }
            lock (BuiltLock) Built[name] = path;
            return path;
        }

        internal static readonly string[] FixtureNames = { "kitchen", "schema", "instructions", "adventureworks" };

        /// <summary>A fresh engine holding a copy of the fixture, saved in its own workspace (the Model Plan keeps its memory beside a saved model).</summary>
        internal static async Task<DecisionFixture> OpenAsync(string fixture)
        {
            var src = await FixtureFileAsync(fixture);
            var f = new DecisionFixture(TestEntitlements.Pro);
            var dest = Path.Combine(f.Workspace, fixture + ".bim");
            File.Copy(src, dest, true);
            await f.Engine.OpenAsync(dest);
            return f;
        }

        // ---- reading the model as a flat list of (object, property) -------------------------------------------------------

        private static readonly Dictionary<string, string> Collections = new(StringComparer.Ordinal)
        {
            ["tables"] = "table", ["columns"] = "column", ["measures"] = "measure", ["hierarchies"] = "hierarchy", ["levels"] = "level",
            ["partitions"] = "partition", ["relationships"] = "relationship", ["roles"] = "role", ["perspectives"] = "perspective",
            ["cultures"] = "culture", ["expressions"] = "expression", ["dataSources"] = "datasource", ["calculationItems"] = "calcitem",
            ["functions"] = "function",
        };

        private const char Sep = '␟';

        /// <summary>The serialized model as (ref, property) to value. An object that exists is the entry (ref, "@exists"); a
        /// property the file omits (its default) is absent, so a change to or from the default shows either way.</summary>
        internal static Dictionary<string, string> Flatten(string bim)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(bim);
            var root = doc.RootElement;
            var model = root.TryGetProperty("model", out var mm) ? mm : root;
            Emit(model, "obj:Model", null!, null!, d);
            return d;
        }

        private static string Text(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText();

        private static void Emit(JsonElement obj, string @ref, string table, string hierarchy, Dictionary<string, string> into)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (p.Name == "annotations" || p.Name == "extendedProperties" || p.Name == "changedProperties") continue;
                if (p.Value.ValueKind == JsonValueKind.Array && Collections.TryGetValue(p.Name, out var kind) && p.Value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Object))
                {
                    foreach (var child in p.Value.EnumerateArray())
                    {
                        var name = child.TryGetProperty("name", out var n) ? Text(n) : "?";
                        var childTable = kind == "table" ? name : table;
                        var childHier = kind == "hierarchy" ? name : hierarchy;
                        var childRef = kind switch
                        {
                            "table" => "table:" + name,
                            "column" or "measure" or "partition" or "calcitem" => kind + ":" + table + "/" + name,
                            "hierarchy" => "hierarchy:" + table + "/" + name,
                            "level" => "level:" + table + "/" + hierarchy + "/" + name,
                            _ => kind + ":" + name,
                        };
                        into[childRef + Sep + "@exists"] = "1";
                        Emit(child, childRef, childTable, childHier, into);
                    }
                    continue;
                }
                if (p.Value.ValueKind == JsonValueKind.Object && p.Name == "calculationGroup") { Emit(p.Value, @ref, table, hierarchy, into); continue; }
                if (p.Name == "name") continue;   // a rename is a new object here; the paths never rename
                into[@ref + Sep + p.Name] = p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)
                    ? string.Join("\n", p.Value.EnumerateArray().Select(x => x.GetString()))
                    : Text(p.Value);
            }
        }

        internal sealed class Change
        {
            public string Ref, Property, Before, After;
            public bool Added, Removed;
            public override string ToString() => Added ? "+ " + Ref : Removed ? "- " + Ref : Ref + " " + Property + ": " + Short(Before) + " -> " + Short(After);
            private static string Short(string s) => s == null ? "(none)" : s.Length > 60 ? s.Substring(0, 57) + "..." : s;
        }

        internal static List<Change> Diff(Dictionary<string, string> before, Dictionary<string, string> after)
        {
            var changes = new List<Change>();
            foreach (var k in before.Keys.Union(after.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                before.TryGetValue(k, out var b);
                after.TryGetValue(k, out var a);
                if (b == a) continue;
                var at = k.IndexOf(Sep);
                var r = k.Substring(0, at);
                var prop = k.Substring(at + 1);
                if (prop == "@exists") changes.Add(new Change { Ref = r, Added = b == null, Removed = a == null });
                else changes.Add(new Change { Ref = r, Property = prop, Before = b, After = a });
            }
            return changes;
        }

        internal static Task<Dictionary<string, string>> SnapshotAsync(DecisionFixture f) =>
            f.Sessions.Require().ReadAsync(m => Flatten(TOM.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase)));

        // ---- the paths ----------------------------------------------------------------------------------------------------

        internal static readonly string[] Paths = { "run-the-plan", "run-each-row", "safe-fixes", "make-ai-ready", "bpa-fix-all", "change-plan", "assistant-rows" };

        /// <summary>A change something offered to make (queued) or was told to make: an object, the property, and the value when it is named.</summary>
        internal sealed class Queued
        {
            public string Where, Ref, Property, After, RuleId, Kind;
            public override string ToString() => Where + ": " + Kind + " " + Ref + (Property != null ? " " + Property : "") + (After != null ? " = " + After : "") + (RuleId != null ? " (" + RuleId + ")" : "");
        }

        private static async Task<List<Queued>> ChangePlanQueueAsync(DecisionFixture f, string where)
        {
            var plan = await f.Engine.GetPlanAsync();
            return plan.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.RuleId) && i.Status is not ("skipped" or "rejected" or "applied" or "failed"))
                .Select(i => new Queued { Where = where, Ref = i.ObjectRef, Property = i.Target, After = i.After, RuleId = i.RuleId, Kind = i.Kind })
                .ToList();
        }

        /// <summary>Press Run the plan until nothing Semanticus can do is ready.</summary>
        private static async Task RunThePlanAsync(DecisionFixture f)
        {
            for (var press = 0; press < 6; press++)
            {
                var plan = await f.Engine.GetModelPlanAsync();
                foreach (var r in plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.ChangeSet?.Stale == true))
                    await f.Engine.ModelPlanActionAsync(r.Id, "propose", "human");
                plan = await f.Engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)) break;
                var result = await new EngineRpcTarget(f.Engine).applyPlanItems(null!, "human", plan.ReadyDigest);
                if (!result.Performed) break;
            }
        }

        /// <summary>Drive one path on a fresh engine and say what it queued. The writes are read from the model afterwards.</summary>
        internal static async Task<List<Queued>> DriveAsync(DecisionFixture f, string path)
        {
            var queued = new List<Queued>();
            switch (path)
            {
                case "run-the-plan":
                {
                    var plan = await f.Engine.GetModelPlanAsync();
                    foreach (var r in plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready))
                        await f.Engine.ModelPlanActionAsync(r.Id, "propose", "human");
                    queued.AddRange(await ChangePlanQueueAsync(f, path));
                    await RunThePlanAsync(f);
                    break;
                }
                case "run-each-row":
                {
                    var plan = await f.Engine.GetModelPlanAsync();
                    foreach (var id in plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held).Select(i => i.Id).ToList())
                    {
                        await f.Engine.ModelPlanActionAsync(id, "propose", "human");
                        var now = (await f.Engine.GetModelPlanAsync()).Items.FirstOrDefault(i => i.Id == id);
                        if (now?.ChangeSet?.Digest == null) continue;
                        queued.AddRange(await ChangePlanQueueAsync(f, path + "/" + id));
                        await f.Engine.ModelPlanActionAsync(id, "approve", "human", now.ChangeSet.Digest);
                    }
                    break;
                }
                case "safe-fixes":
                    await f.Engine.ApplySafeFixesAsync("agent");
                    break;
                case "make-ai-ready":
                {
                    var ready = await f.Engine.MakeAiReadyAsync("agent", 400);
                    foreach (var w in ready.AiQueue)
                        queued.Add(new Queued { Where = path, Ref = w.Finding.ObjectRef, RuleId = w.Finding.RuleId, Kind = "queue" });
                    break;
                }
                case "bpa-fix-all":
                    await f.Engine.BpaFixAllAsync("agent");
                    break;
                case "change-plan":
                {
                    await f.Engine.ProposePlanAsync(null, true, 400, "agent");
                    queued.AddRange(await ChangePlanQueueAsync(f, path));
                    await new EngineRpcTarget(f.Engine).applyPlan();
                    break;
                }
                case "assistant-rows":
                {
                    await f.Engine.ProposePlanAsync(null, true, 400, "agent");
                    queued.AddRange(await ChangePlanQueueAsync(f, path));
                    var plan = await f.Engine.GetModelPlanAsync();
                    foreach (var row in plan.Items.Where(i => i.Id.StartsWith("asst/", StringComparison.Ordinal)).ToList())
                    {
                        var digest = row.ChangeSet?.Digest;
                        if (digest == null) continue;
                        try { await f.Engine.ModelPlanActionAsync(row.Id, "approve", "agent", digest); } catch { /* a refused approval writes nothing */ }
                    }
                    break;
                }
                default: throw new ArgumentException(path);
            }
            return queued;
        }

        /// <summary>What every path writes when nothing is accepted: the model the paths leave, one fresh engine per path, in the order of <see cref="Paths"/>.</summary>
        internal sealed class Baseline
        {
            public Dictionary<string, List<Change>> Diffs = new();
            public Dictionary<string, Dictionary<string, string>> After = new();
            public Dictionary<string, List<Queued>> Queued = new();
        }

        internal static async Task<Baseline> BaselineAsync(string fixture)
        {
            var result = new Baseline();
            foreach (var p in Paths)
            {
                using var f = await OpenAsync(fixture);
                var before = await SnapshotAsync(f);
                result.Queued[p] = await DriveAsync(f, p);
                var after = await SnapshotAsync(f);
                result.After[p] = after;
                result.Diffs[p] = Diff(before, after);
            }
            return result;
        }

        /// <summary>Would some path, with nothing accepted, have written or queued something this acceptance declines?</summary>
        internal static bool WouldBeCarriedOut(Acceptance a, Baseline b) =>
            Paths.Any(p => b.Diffs[p].Any(c => !c.Added && !c.Removed && Declines(a, c.Ref, c.Property, c.After, b.After[p]))
                || b.Diffs[p].Any(c => c.Added && a.Rule.RuleId == "DAC-IMPLICIT-MEASURE" && Declines(a, c.Ref, "expression", null!, b.After[p]))
                || b.Diffs[p].Any(c => c.Removed && Declines(a, c.Ref, "@removed", null!, b.After[p]))
                || b.Queued[p].Any(q => QueueDeclined(a, q, b.After[p])));

        /// <summary>Every line a path queues names a rule, and that rule's row names the property the line writes. A line whose rule's row says
        /// it declines nothing, or something else, would slip past an acceptance of that rule.</summary>
        internal static IEnumerable<string> LineProblems(Baseline b, IReadOnlyList<RuleRef> catalog)
        {
            var byId = catalog.GroupBy(r => r.RuleId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var q in b.Queued.SelectMany(kv => kv.Value.Where(x => x.Kind != "queue")).GroupBy(x => x.Kind + Sep + x.Ref + Sep + x.Property + Sep + x.RuleId).Select(g => g.First()))
            {
                if (string.IsNullOrEmpty(q.RuleId)) continue;   // a line added by hand is an explicit request and is never declined
                if (q.RuleId == "STARTER-MEASURE") { if (q.Kind is not ("create_measure" or "set_measure_format")) yield return q + ": the starter rule id on a line that is not a measure"; continue; }
                if (!byId.TryGetValue(q.RuleId, out var rule)) { yield return q + ": names a rule no bundled set has"; continue; }
                if (rule.About == null || !rule.About.DeclinesSomething) { yield return q + ": writes " + q.Property + ", but the row of " + rule.Key + " says it declines nothing"; continue; }
                if (!SameProperty(rule.About.Property, q.Property)) yield return q + ": writes " + q.Property + ", but the row of " + rule.Key + " is about " + rule.About.Property;
            }
        }

        // ---- the acceptances ----------------------------------------------------------------------------------------------

        /// <summary>One acceptance the matrix makes: a finding of a rule on one object, or the whole rule (ObjectRef null).</summary>
        internal sealed class Acceptance
        {
            public RuleRef Rule;
            public string ObjectRef;       // null = the whole rule
            public bool Blocker;           // the finding is a deploy-gate blocker (Model quality, severity 2 or 3, no fix)
            public bool WholeRule => ObjectRef == null;
            public string Family;          // what it is about, for packing: a batch holds one acceptance per (object, family)
            public string Kind;            // the kind of object the finding sits on ("measure", "column", ...)
            public override string ToString() => Rule.Key + (ObjectRef == null ? " (whole rule)" : " @ " + ObjectRef);
        }

        /// <summary>The changes a path can make that are not one property of the finding's own object. A second statement of the
        /// same fact the rule table makes, kept here so a row the table leaves out is still caught: the person who accepted
        /// "Model should have a date table" (or "No marked date table") does not want one marked, and the person who accepted
        /// "relies on implicit aggregation" for a column does not want a measure over it. Rules that decline nothing are absent.</summary>
        internal static readonly Dictionary<string, string> StructuralAsks = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MODEL_SHOULD_HAVE_A_DATE_TABLE"] = "dateTable",
            ["DATE-MARK"] = "dateTable",
            ["DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE"] = "dateTable",
            ["DAC-IMPLICIT-MEASURE"] = "measureOver",
            // The rules whose change is to remove or merge the object the finding sits on: accepting one says "keep it".
            ["UNNECESSARY_MEASURES"] = "keepObject",
            ["UNNECESSARY_COLUMNS"] = "keepObject",
            ["REMOVE_ROLES_WITH_NO_MEMBERS"] = "keepObject",
            ["PERSPECTIVES_WITH_NO_OBJECTS"] = "keepObject",
            ["REMOVE_AUTO-DATE_TABLE"] = "keepObject",
            ["REMOVE_REDUNDANT_COLUMNS_IN_RELATED_TABLES"] = "keepObject",
            ["AVOID_DUPLICATE_MEASURES"] = "keepObject",
            ["MEAS-DUP-EXPR"] = "keepObject",
        };

        private static string Norm(string s) => (s ?? "").Trim().Trim('"').ToLowerInvariant();

        private static bool SameProperty(string ruleProperty, string changed)
        {
            var a = Norm(ruleProperty);
            var b = Norm(changed);
            if (a == b) return true;
            if (a == "cardinality") return b == "fromcardinality" || b == "tocardinality";
            return false;
        }

        private static string KindOf(string @ref) { var i = (@ref ?? "").IndexOf(':'); return i < 0 ? "" : @ref.Substring(0, i); }

        /// <summary>Is a write of (ref, property, value) something this acceptance declines? Read from the rule's own row, and from the
        /// structural asks above; a value-specific (own-ask) acceptance declines only the value its rule names, when it names one.</summary>
        internal static bool Declines(Acceptance a, string @ref, string property, string after, Dictionary<string, string> model)
        {
            var about = a.Rule.About;
            if (about != null && about.DeclinesSomething && SameProperty(about.Property, property) && about.Kinds.Contains(KindOf(@ref)))
            {
                var reach = a.WholeRule
                    || WaiverStore.SameRef(a.ObjectRef, @ref)
                    || (about.FromModel && WaiverStore.SameRef(a.ObjectRef, WaiverStore.ModelRef));
                if (reach)
                {
                    if (!about.OwnAsk) return true;
                    var own = about.OwnValue?.Invoke(@ref);
                    if (own != null && after != null && string.Equals(Norm(own), Norm(after), StringComparison.Ordinal)) return true;
                }
            }
            if (StructuralAsks.TryGetValue(a.Rule.RuleId, out var tag))
            {
                if (tag == "keepObject" && property == "@removed")
                    return a.WholeRule ? KindOf(@ref) == a.Kind : WaiverStore.SameRef(a.ObjectRef, @ref);
                if (tag == "dateTable" && Norm(property) == "datacategory" && Norm(after) == "time" && KindOf(@ref) == "table")
                    return a.WholeRule || KindOf(a.ObjectRef) == "obj" || WaiverStore.SameRef(a.ObjectRef, @ref);
                if (tag == "measureOver" && KindOf(@ref) == "measure" && model != null && model.TryGetValue(@ref + Sep + "expression", out var expr))
                {
                    // column:Table/Name; the expression names it as 'Table'[Name] or Table[Name]
                    var col = (a.ObjectRef ?? "").StartsWith("column:", StringComparison.Ordinal) ? a.ObjectRef.Substring("column:".Length) : null;
                    if (col == null) return a.WholeRule && ReferencesAnyColumn(expr, model);
                    var slash = col.IndexOf('/');
                    var t = col.Substring(0, slash);
                    var c = col.Substring(slash + 1);
                    return expr.Contains("[" + c + "]", StringComparison.OrdinalIgnoreCase) && (expr.Contains("'" + t + "'", StringComparison.OrdinalIgnoreCase) || expr.Contains(t + "[", StringComparison.OrdinalIgnoreCase));
                }
            }
            return false;
        }

        private static bool ReferencesAnyColumn(string expr, Dictionary<string, string> model) =>
            model.Keys.Any(k => k.EndsWith(Sep + "@exists", StringComparison.Ordinal) && k.StartsWith("column:", StringComparison.Ordinal)
                && expr.Contains("[" + k.Substring(k.IndexOf('/') + 1, k.IndexOf(Sep) - k.IndexOf('/') - 1) + "]", StringComparison.OrdinalIgnoreCase));

        // ---- one batch ----------------------------------------------------------------------------------------------------

        internal sealed class Outcome
        {
            public List<string> Problems = new();
            public Dictionary<string, int> Writes = new();      // per path: how many changes it made
            public Dictionary<string, int> Queued = new();
        }

        /// <summary>Whether the person accepted this finding: by its own rule and object, or (plan statement check 2, item 3b) through the
        /// one problem two pages show under two rules (PairedAcceptances: an inactive relationship, a snowflake table). The pair is ONE
        /// decision read by both pages, so accepting either counts as accepting both; no other finding ever rides along.</summary>
        private static bool Own(IReadOnlyList<Acceptance> batch, string system, string ruleId, string objectRef)
        {
            if (OwnDirect(batch, system, ruleId, objectRef)) return true;
            var twin = system == "air" ? PairedAcceptances.BpaTwinOf(ruleId) : PairedAcceptances.AirTwinOf(ruleId);
            return twin != null && OwnDirect(batch, system == "air" ? "bpa" : "air", twin, objectRef);
        }

        private static bool OwnDirect(IReadOnlyList<Acceptance> batch, string system, string ruleId, string objectRef) =>
            batch.Any(a => string.Equals(a.Rule.System, system, StringComparison.Ordinal) && string.Equals(a.Rule.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)
                && (a.WholeRule || WaiverStore.SameRef(a.ObjectRef, objectRef)));

        /// <summary>Accept the batch on a fresh copy of the fixture, then read the pages and drive every path on a fresh copy of that.</summary>
        internal static async Task<Outcome> CheckAsync(string fixture, IReadOnlyList<Acceptance> batch, IReadOnlyList<string> paths = null, DecisionFixture reuse = null)
        {
            paths ??= Paths;
            var outcome = new Outcome();
            var accepted = Path.Combine(Scratch(), "acc-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim");
            // The sample hands over the engine it already scanned on, so it does not open the same fixture twice.
            using (var f = reuse ?? await OpenAsync(fixture))
            {
                foreach (var a in batch)
                    await f.Engine.WaiveFindingAsync(a.Rule.System, a.Rule.RuleId, a.WholeRule ? null : a.ObjectRef, Why, "human");
                await CheckPagesAsync(f, batch, outcome.Problems);
                await f.Engine.SaveAsync(accepted, "bim", false, "human");
            }
            foreach (var p in paths)
            {
                using var f = await OpenCopyAsync(accepted);
                var before = await SnapshotAsync(f);
                var queued = await DriveAsync(f, p);
                var after = await SnapshotAsync(f);
                var diff = Diff(before, after);
                outcome.Writes[p] = diff.Count;
                outcome.Queued[p] = queued.Count;
                var added = new HashSet<string>(diff.Where(x => x.Added).Select(x => x.Ref), StringComparer.Ordinal);
                foreach (var a in batch)
                {
                    foreach (var c in diff.Where(x => !x.Added && !x.Removed && !added.Contains(x.Ref) && Declines(a, x.Ref, x.Property, x.After, after)))
                        outcome.Problems.Add(a + ": the path " + p + " wrote " + c);
                    foreach (var c in diff.Where(x => x.Added && Declines(a, x.Ref, "expression", null!, after) && a.Rule.RuleId == "DAC-IMPLICIT-MEASURE"))
                        outcome.Problems.Add(a + ": the path " + p + " wrote " + c + " over the accepted column");
                    foreach (var c in diff.Where(x => x.Removed && Declines(a, x.Ref, "@removed", null!, after)))
                        outcome.Problems.Add(a + ": the path " + p + " removed " + c.Ref + ", which the person accepted as it is");
                    foreach (var q in queued.Where(x => QueueDeclined(a, x, after)))
                        outcome.Problems.Add(a + ": the path " + p + " queued " + q);
                }
            }
            try { File.Delete(accepted); } catch { }
            return outcome;
        }

        private static async Task<DecisionFixture> OpenCopyAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var dest = Path.Combine(f.Workspace, "matrix.bim");
            File.Copy(file, dest, true);
            await f.Engine.OpenAsync(dest);
            return f;
        }

        private static bool QueueDeclined(Acceptance a, Queued q, Dictionary<string, string> model)
        {
            var property = q.Property;
            if (property == null && q.Kind == "queue") property = RuleAboutTable.ForReadiness(q.RuleId)?.Property;
            if (q.Kind == "delete_if_unused") return Declines(a, q.Ref, "@removed", null!, model);
            if (q.Kind == "create_measure") return Declines(a, q.Ref, "expression", q.After, new Dictionary<string, string> { [q.Ref + Sep + "expression"] = q.After ?? "" }) && a.Rule.RuleId == "DAC-IMPLICIT-MEASURE";
            return property != null && Declines(a, q.Ref, property, q.After, model);
        }

        /// <summary>What the pages and the gate say once the batch is accepted: only what the person accepted reads as accepted.</summary>
        private static async Task CheckPagesAsync(DecisionFixture f, IReadOnlyList<Acceptance> batch, List<string> problems)
        {
            var bpa = await f.Engine.BpaScanAsync();
            var air = await f.Engine.AiReadinessScanAsync();
            var gate = await f.Engine.DeployGateAsync(null, "human");
            var ownBpa = 0; var ownAir = 0; var ownBlockers = 0;
            foreach (var v in bpa.Violations)
            {
                var own = Own(batch, "bpa", v.RuleId, v.ObjectRef);
                var accepted = v.Waived && !v.WaivedAsTwin;
                var blocker = v.Severity >= 2 && !v.CanAutoFix && !v.Advice;   // format advice never blocks the gate (THE FORMAT FILL RULE)
                if (own) { ownBpa++; if (blocker && !v.NoLongerFires && !v.WholeRuleRow) ownBlockers++; }
                if (!own && accepted) problems.Add("bpa " + v.RuleId + " @ " + v.ObjectRef + " reads as accepted (\"" + v.WaiverReason + "\") but nobody accepted it");
                if (!own && blocker && v.Waived) problems.Add("bpa " + v.RuleId + " @ " + v.ObjectRef + " is a deploy-gate blocker that reads as waived (\"" + v.WaiverReason + "\")");
                if (own && !accepted) problems.Add("bpa " + v.RuleId + " @ " + v.ObjectRef + " was accepted but reads open");
            }
            foreach (var x in air.Findings)
            {
                var own = Own(batch, "air", x.RuleId, x.ObjectRef);
                var accepted = x.Waived && !x.WaivedAsTwin;
                if (own) ownAir++;
                if (!own && accepted) problems.Add("air " + x.RuleId + " @ " + x.ObjectRef + " reads as accepted (\"" + x.WaiverReason + "\") but nobody accepted it");
                if (own && !accepted) problems.Add("air " + x.RuleId + " @ " + x.ObjectRef + " was accepted but reads open");
            }
            if (bpa.WaivedCount != ownBpa) problems.Add("Model quality counts " + bpa.WaivedCount + " accepted; the person accepted " + ownBpa);
            if (air.WaivedCount != ownAir) problems.Add("AI readiness counts " + air.WaivedCount + " accepted; the person accepted " + ownAir);
            if (gate.BpaWaivedBlocking != ownBlockers) problems.Add("the deploy gate counts " + gate.BpaWaivedBlocking + " blockers as accepted; the person accepted " + ownBlockers);
        }

        // ---- choosing what to accept, and the table -----------------------------------------------------------------------

        internal sealed class Row
        {
            public RuleRef Rule;
            public string Fixture;             // null: no bundled fixture fires the rule
            public string ObjectRef;
            public bool Blocker;
            public bool CheckedWholeRule;
            public bool CarriedOutWithout;      // a path would have written or queued what this acceptance declines, if it were not accepted
            public bool CarriedOutWithoutWhole; // the same, for the whole rule
            public string Ask;
            public int Findings;
        }

        internal static bool DeclinesAnything(RuleRef r) => (r.About != null && r.About.DeclinesSomething) || StructuralAsks.ContainsKey(r.RuleId);

        internal static string AskOf(RuleRef r)
        {
            var a = r.About;
            var tag = StructuralAsks.TryGetValue(r.RuleId, out var t) ? "; also " + t : "";
            if (a == null) return "unknown" + tag;
            if (!a.DeclinesSomething) return "nothing" + tag;
            return (a.OwnAsk ? "own " : "keep ") + a.Property + " (" + string.Join(",", a.Kinds) + (a.FromModel ? ", finding on the model" : "") + ")" + tag;
        }

        private static string FamilyOf(RuleRef r, string objectRef)
        {
            if (StructuralAsks.TryGetValue(r.RuleId, out var tag)) return tag;
            var a = r.About;
            return a != null && a.DeclinesSomething ? Norm(a.Property) + (a.OwnAsk ? ":" + r.RuleId : "") : "rule:" + r.Key;
        }

        internal sealed class Run
        {
            public List<Row> Rows = new();
            public List<string> Problems = new();
            public List<string> Notes = new();
            public int Batches, Acceptances, PathRuns;
        }

        private static Task<Run> _run;
        internal static Task<Run> GetRunAsync() => _run ??= RunAllAsync();

        /// <summary>The default-tier sample of the matrix (lean suite): ONE fixture, two rules that decline something, one batch of
        /// those findings, and only the path that carries out the most (Run the plan). It checks the same two promises as the full run (nothing the acceptance declines is
        /// written or queued; no other finding reads as accepted or passes the deploy gate) on a small slice of the table. The full
        /// run, every rule on every fixture on every path, is the sweep.</summary>
        private static Task<Run> _sample;
        internal static Task<Run> GetSampleRunAsync() => _sample ??= RunAllAsync(new[] { "kitchen" }, 2, new[] { "run-the-plan" });

        private static async Task<Run> RunAllAsync(IReadOnlyList<string> fixtures = null, int sampleRules = 0, IReadOnlyList<string> paths = null)
        {
            var sample = sampleRules > 0;
            var run = new Run();
            var catalog = Catalog();
            var pending = catalog.Where(r => !r.Live && !r.Fallback).ToDictionary(r => r.Key, r => r);
            foreach (var fixture in fixtures ?? FixtureNames)
            {
                // What fires here, for the rules no earlier fixture reached.
                var fired = new Dictionary<string, List<(string Ref, bool Blocker)>>();
                var scanned = await OpenAsync(fixture);   // the sample keeps it: the accepting batch reuses this engine
                try
                {
                    var f = scanned;
                    var bpa = await f.Engine.BpaScanAsync();
                    var air = await f.Engine.AiReadinessScanAsync();
                    foreach (var v in bpa.Violations.Where(v => !v.WholeRuleRow && !v.NoLongerFires))
                    {
                        var k = "bpa:" + v.RuleId;
                        if (!fired.TryGetValue(k, out var l)) fired[k] = l = new();
                        l.Add((v.ObjectRef, v.Severity >= 2 && !v.CanAutoFix));
                    }
                    foreach (var x in air.Findings.Where(x => !x.WholeRuleRow && !x.NoLongerFires))
                    {
                        var k = "air:" + x.RuleId;
                        if (!fired.TryGetValue(k, out var l)) fired[k] = l = new();
                        l.Add((x.ObjectRef, false));
                    }
                }
                finally { if (!sample) scanned.Dispose(); }
                var here = pending.Values.Where(r => fired.ContainsKey(r.Key)).OrderBy(r => fired[r.Key].Select(x => x.Ref).Distinct().Count()).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
                // The sample takes the first few rules that decline something (the fewest findings first, so the pick is small and stable).
                if (sample) here = here.Where(DeclinesAnything).Take(sampleRules).ToList();
                foreach (var r in here) pending.Remove(r.Key);

                // One finding of each rule, spread over objects so one object carries as few acceptances as it can.
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var picks = new List<Acceptance>();
                foreach (var r in here)
                {
                    var candidates = fired[r.Key].OrderBy(x => x.Ref, StringComparer.Ordinal).ToList();
                    var family0 = FamilyOf(r, null!);
                    var pick = candidates.FirstOrDefault(c => !taken.Contains(c.Ref + Sep + family0));
                    if (pick.Ref == null) pick = candidates[0];
                    taken.Add(pick.Ref + Sep + family0);
                    picks.Add(new Acceptance { Rule = r, ObjectRef = pick.Ref, Blocker = pick.Blocker, Family = family0, Kind = KindOf(pick.Ref) });
                    run.Rows.Add(new Row { Rule = r, Fixture = fixture, ObjectRef = pick.Ref, Blocker = pick.Blocker, Ask = AskOf(r), Findings = candidates.Count });
                }

                // A batch holds one acceptance per (object, family), so a finding read as accepted can only be read through a twin.
                var batches = new List<List<Acceptance>>();
                foreach (var a in picks)
                {
                    var batch = batches.FirstOrDefault(b => !b.Any(x => WaiverStore.SameRef(x.ObjectRef, a.ObjectRef) && x.Family == a.Family));
                    if (batch == null) batches.Add(batch = new List<Acceptance>());
                    batch.Add(a);
                }
                // Whole rules, for the rules that decline something.
                var whole = here.Where(DeclinesAnything)
                    .Select(r => new Acceptance { Rule = r, ObjectRef = null!, Family = FamilyOf(r, null!), Kind = KindOf(run.Rows.First(x => x.Rule == r).ObjectRef) }).ToList();
                foreach (var w in whole) run.Rows.First(x => x.Rule == w.Rule).CheckedWholeRule = true;
                if (whole.Count > 0 && !sample) batches.Add(whole);

                // The baseline only feeds the committed table and two extra checks of the full run; the sample skips it.
                if (!sample)
                {
                    var baseline = await BaselineAsync(fixture);
                    foreach (var kv in baseline.Diffs)
                        foreach (var c in kv.Value.Where(c => !c.Added && !c.Removed && c.Before != null && Norm(c.Property) == "expression"))
                            run.Problems.Add(fixture + ": the path " + kv.Key + " rewrote an expression (" + c + "), which an own-ask expression rule assumes no path does");
                    foreach (var lp in LineProblems(baseline, catalog)) run.Problems.Add(fixture + ": " + lp);
                    foreach (var a in picks) run.Rows.First(x => x.Rule == a.Rule).CarriedOutWithout = WouldBeCarriedOut(a, baseline);
                    foreach (var w in whole) run.Rows.First(x => x.Rule == w.Rule).CarriedOutWithoutWhole = WouldBeCarriedOut(w, baseline);
                }

                var n = 0;
                foreach (var batch in batches)
                {
                    n++;
                    var outcome = await CheckAsync(fixture, batch, paths, sample && n == 1 ? scanned : null);
                    run.Batches++;
                    run.Acceptances += batch.Count;
                    run.PathRuns += (paths ?? Paths).Count;
                    foreach (var p in outcome.Problems) run.Problems.Add(fixture + " batch " + n + (batch[0].WholeRule ? " (whole rules)" : "") + ": " + p);
                }
            }
            foreach (var r in pending.Values.OrderBy(r => r.Key, StringComparer.Ordinal))
                run.Rows.Add(new Row { Rule = r, Ask = AskOf(r) });
            foreach (var r in catalog.Where(r => r.Live || r.Fallback))
                run.Rows.Add(new Row { Rule = r, Ask = AskOf(r) });
            return run;
        }

        /// <summary>Rules no bundled fixture makes fire, each with the reason. A rule that does fire must not be listed.</summary>
        internal static readonly Dictionary<string, string> NotExercised = BuildExcuses();

        private static Dictionary<string, string> BuildExcuses()
        {
            const string advice = "advice about a setting the fixtures do not have (";
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string reason, params string[] keys) { foreach (var k in keys) d[k] = reason; }
            // Each of these declines nothing (its row says so), so no path has a change of its own to carry out and no other rule's
            // acceptance can cover it; what is left to check is that accepting it lifts nothing, which every advice rule that does fire shows.
            Add(advice + "an AI data schema, authored in the service)", "air:AISCHEMA-DEP-MISSING");
            Add(advice + "the automatic date and time setting of a Power BI Desktop file)", "air:BP-AUTO-DATETIME");
            Add(advice + "calendars, which need compatibility level 1701 or later)", "air:CAL-MAPPING", "air:CAL-TI-NO-CALENDAR");
            Add(advice + "a Direct Lake model)", "air:CFG-PREP-MODE");
            Add(advice + "the Copilot tooling format)", "air:DAC-COPILOT-TOOLING-FORMAT");
            Add(advice + "field parameter tables)", "air:DAC-FIELD-PARAM-COMPLEXITY");
            Add(advice + "a model Copilot cannot index, or a DirectQuery model)", "air:DAC-QNA-DISABLED", "air:DAC-QNA-STORAGE");
            Add(advice + "verified answers, authored in the service)", "air:DAC-VERIFIED-ANSWERS");
            Add(advice + "more than 25 tables, more than 1,000 indexed fields, or the Copilot scale ceiling; a fixture that big slows every path)", "air:LIMIT-DATAAGENT-TABLES", "air:LIMIT-QNA-INDEX", "air:LIMIT-SCALE");
            Add(advice + "object-level security)", "air:SEC-OLS-AWARENESS");
            Add("a legacy XML synonym store, which no op of the engine builds (it only reads one)", "air:SYN-LSDL-XML");
            Add("its own pattern doubles the backslash of the word boundary, so it never matches any expression (found by this matrix; reported, not changed here)", "bpa:SEM_UNFINISHED_MARKER_IN_EXPRESSION");
            Add(advice + "a DirectQuery model)", "bpa:MODEL_USING_DIRECT_QUERY_AND_NO_AGGREGATIONS", "bpa:MEASURES_USING_TIME_INTELLIGENCE_AND_MODEL_IS_USING_DIRECT_QUERY");
            Add(advice + "a structured data source with a query partition)", "bpa:AVOID_STRUCTURED_DATA_SOURCES_WITH_PROVIDER_PARTITIONS");
            return d;
        }

        internal static string TableOf(Run run)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Rule clash matrix");
            sb.AppendLine();
            sb.AppendLine("Generated by `Semanticus.Tests/RuleClashMatrix.cs` (run by `RuleClashMatrixTests`); do not edit by hand. Regenerate with");
            sb.AppendLine("`SEMANTICUS_MATRIX_WRITE=1 dotnet test Semanticus.Tests -c Release --filter FullyQualifiedName~RuleClashMatrixTests`.");
            sb.AppendLine();
            sb.AppendLine("Every bundled readiness rule and Model quality rule. Where a fixture makes the rule fire, one finding of it is accepted (and, for a rule that");
            sb.AppendLine("declines a change, the whole rule), then every path that writes or queues is driven: Run the plan, each plan row on its own, apply_safe_fixes,");
            sb.AppendLine("make_model_ai_ready, BPA Fix all, and the change plan with the assistant's rows. Checked: nothing the acceptance declines is written or queued, and no");
            sb.AppendLine("other finding reads as accepted or passes the deploy gate. A row that fails prints in the test, never here.");
            sb.AppendLine();
            sb.AppendLine("\"Carried out without it\" says whether some path, with nothing accepted, would have written or queued what the acceptance declines: where it says no,");
            sb.AppendLine("the acceptance was checked against the pages and the gate but no path had anything of that kind to do on this fixture.");
            sb.AppendLine();
            sb.AppendLine("| Page | Rule | What accepting it declines | Fixture | Finding accepted | Blocker | Whole rule checked | Carried out without it | Result |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var r in run.Rows.OrderBy(r => r.Rule.System, StringComparer.Ordinal).ThenBy(r => r.Rule.RuleId, StringComparer.Ordinal))
            {
                var page = r.Rule.System == "air" ? "AI readiness" : "Model quality";
                var result = r.Fixture != null ? "pass"
                    : r.Rule.Live ? "not exercised: reads live data, which offline has none"
                    : r.Rule.Fallback ? "not exercised: only in the fallback rule set, in force only if the bundled set fails to load"
                    : NotExercised.TryGetValue(r.Rule.Key, out var why) ? "not exercised: " + why
                    : "NOT EXERCISED";
                sb.AppendLine("| " + page + " | " + r.Rule.RuleId.Replace("|", "/") + " | " + r.Ask + " | " + (r.Fixture ?? "") + " | " + (r.ObjectRef ?? "").Replace("|", "/") + " | " + (r.Fixture == null ? "" : r.Blocker ? "yes" : "no")
                    + " | " + (r.Fixture == null ? "" : r.CheckedWholeRule ? "yes" : "no")
                    + " | " + (r.Fixture == null ? "" : !DeclinesAnything(r.Rule) ? "n/a" : (r.CarriedOutWithout ? "yes" : "no") + (r.CheckedWholeRule ? "; whole rule " + (r.CarriedOutWithoutWhole ? "yes" : "no") : ""))
                    + " | " + result + " |");
            }
            sb.AppendLine();
            var exercised = run.Rows.Count(r => r.Fixture != null);
            sb.AppendLine("Rules: " + run.Rows.Count + ". Exercised: " + exercised + ". Not exercised: " + (run.Rows.Count - exercised) + ".");
            sb.AppendLine();
            sb.AppendLine("Accepted in " + run.Batches + " batches (" + run.Acceptances + " acceptances, per finding and whole rule). Each batch drove " + Paths.Length
                + " paths, each on a fresh copy of the accepted model (" + run.PathRuns + " path runs), after a baseline of the same paths on each of the " + FixtureNames.Length + " fixtures.");
            return sb.ToString();
        }
    }
}

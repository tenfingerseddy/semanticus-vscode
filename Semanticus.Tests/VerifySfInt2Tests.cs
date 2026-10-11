// Verifier, integration check 2 of m11/sf-int (the spec-first start after the wiring step).
//
// THE GRADE QUESTION. The wiring step reports that a Pro start on the J8 schema ends at AI readiness B and Best practice D after the build, with no
// ready Plan row. The old path (the Plan statement check, 90716ced) reached A and A. These tests build the same Pro start the end-to-end proof builds
// (same offline seam, same policy oracle, same words), then say exactly what still fails, and walk the Plan after the build the way the person and their
// assistant would: Run the plan while a Semanticus row is ready, the words queue through the tools it names, the person's rows. The dump goes to the file
// named by SEMANTICUS_VSFI2_DUMP when it is set.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class VerifySfInt2GradeTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _origHome;
        private readonly List<IDisposable> _owned = new List<IDisposable>();
        private readonly StringBuilder _dump = new StringBuilder();

        public VerifySfInt2GradeTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "vg-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home"));
            ConnectionRegistry.RootOverride = Path.Combine(_root, "user");
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
            Directory.CreateDirectory(Path.Combine(_root, "policy"));
        }

        public void Dispose()
        {
            var path = Environment.GetEnvironmentVariable("SEMANTICUS_VSFI2_DUMP");
            if (!string.IsNullOrWhiteSpace(path)) File.AppendAllText(path, _dump.ToString());
            foreach (var o in _owned) { try { o.Dispose(); } catch { } }
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            ConnectionRegistry.RootOverride = _safeRoot; AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private void Say(string line) => _dump.AppendLine(line);

        private const string Server = "wh-j8.datawarehouse.fabric.microsoft.com", Db = "ContosoConsulting";

        /// <summary>The end-to-end proof's own J8 rows (its private J8Source), so the sample is the same one.</summary>
        private static Func<string, string, string, int, Task<ResultSet>> J8Rows(FabricSchema schema)
        {
            var t = typeof(StartPlanEndToEndTests).GetNestedType("J8Source", BindingFlags.NonPublic);
            var src = Activator.CreateInstance(t, schema);
            var m = t.GetMethod("Execute");
            return (Func<string, string, string, int, Task<ResultSet>>)Delegate.CreateDelegate(typeof(Func<string, string, string, int, Task<ResultSet>>), src, m);
        }

        private static StartWord[] WordsFor(StartWordsPage page) => page.Items.Select(i => new StartWord
        {
            Ref = i.Ref, Kind = i.Kind,
            Text = i.Kind == "synonyms" ? "alias one, alias two"
                 : i.Kind == "display_name" ? NewName(i.Ref)
                 : i.Kind == "instructions" ? "This model answers questions about consulting work: hours, projects, clients and invoices."
                 : "What " + Humanise(i.Ref) + " means for the business, in plain words.",
        }).ToArray();

        private static string NewName(string r)
        {
            var raw = (r ?? "").Substring((r ?? "").LastIndexOf('/') + 1);
            var h = Humanise(r);
            return string.Equals(h, raw, StringComparison.OrdinalIgnoreCase) ? h + " Value" : h;
        }

        private static string Humanise(string r)
        {
            var name = (r ?? "").Substring((r ?? "").LastIndexOf('/') + 1);
            var colon = name.IndexOf(':'); if (colon >= 0) name = name.Substring(colon + 1);
            return string.Join(" ", name.Split('_', ' ').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1).ToLowerInvariant()));
        }

        /// <summary>The Pro start on the J8 schema, UI door, to a built model: the end-to-end proof's path (recording run).</summary>
        private async Task<(LocalEngine Engine, SessionManager Sessions)> BuiltProAsync(bool pro = true, bool mcpDoor = false)
        {
            var schema = SqlDraftBuildTests.J8Schema();
            AgentPolicyStore.SetPreset("open", "human");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N").Substring(0, 5)));
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            engine.SourceSampleExecutorForTest = J8Rows(schema);
            engine.UseDecisionsForTest(new DecisionService(new StartPlanDecideTests.PolicyOracle()));

            var folder = Path.Combine(_root, "model-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            var view = mcpDoor ? await McpTools.StartModelFromSql(engine, Server, Db, folder, "Uplift")
                               : await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = folder, Name = "Uplift" }, "human");
            Assert.True(view.Exists);
            await engine.StartSampleSettledForTest();
            view = await engine.GetStartPlanAsync();
            var fy = view.NeedsYou.SingleOrDefault(x => x.Kind == "fiscal_year");
            if (fy != null) view = mcpDoor ? await McpTools.EditStartPlan(engine, "answer", "fiscal_year", fy.Recommended)
                                           : await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = "fiscal_year", Value = fy.Recommended }, "human");
            if (pro)
            {
                await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
                if (mcpDoor)
                {
                    // the source grant is the person's question in every preset (join 6, Sol item 1): the assistant's call asks, the person says yes, the same call goes through
                    Assert.True((await McpTools.GrantStartDisclosure(engine, view.Digest)).Asked);
                    await engine.ApproveAgentActionAsync((await engine.ListPendingApprovalsAsync()).Single().Id, "human");
                    view = await McpTools.GrantStartDisclosure(engine, view.Digest);
                }
                else view = await engine.GrantStartDisclosureAsync(view.Digest, "human");
                await engine.StartDecideSettledForTest();
            }
            for (var pass = 0; pass < 60; pass++)
            {
                var page = await engine.GetStartWordsAsync(40, 0);
                if (page.Items.Count == 0) break;
                await engine.WriteStartWordsAsync(WordsFor(page), "agent");
            }
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            Say($"START PLAN at approval: decide {view.Decide.State}, preview AI {view.Grades.AfterBuildGrade} ({view.Grades.AfterBuild}) BP {view.Grades.AfterBuildBestPractice}; needs-you {view.NeedsYou.Count}:");
            foreach (var x in view.NeedsYou) Say("   needs-you " + x.Kind + ": " + x.Text);
            foreach (var s in view.Sections) Say($"  section {s.Id}: {s.Summary}");
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = mcpDoor ? await McpTools.BuildStartPlan(engine, view.Digest) : await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            return (engine, sessions);
        }

        private async Task<(string Ai, string Bp)> DumpGradesAsync(LocalEngine engine, string when)
        {
            var ai = await engine.AiReadinessScanAsync();
            var bpa = await engine.BpaScanAsync();
            Say($"== {when}: AI readiness {ai.Grade} ({ai.Overall:0.0}, raw {ai.RawOverall:0.0}) gated by [{string.Join(", ", ai.GatedBy)}]; Best practice {bpa.BestPractice} ({bpa.BestPracticeWhy})");
            foreach (var c in ai.Categories.Where(c => c.HasRules)) Say($"   AI category {c.Category}: {c.Score:0.0} weight {c.Weight} applicable {c.Applicable} open {c.Violations} accepted {c.Waived}");
            foreach (var g in ai.Findings.Where(f => !f.IsAccepted()).GroupBy(f => f.RuleId + " [" + f.Severity + "] " + f.RuleTitle))
                Say($"   AI open {g.Key} x{g.Count()}: " + string.Join("; ", g.Take(12).Select(f => f.ObjectRef)));
            foreach (var g in bpa.Violations.Where(v => !v.IsAccepted() && !v.WholeRuleRow).GroupBy(v => v.RuleId + " sev" + v.Severity + " " + v.RuleName))
                Say($"   BPA open {g.Key} x{g.Count()}: " + string.Join("; ", g.Take(12).Select(v => v.ObjectRef + (v.Advice ? " (advice)" : ""))));
            return (ai.Grade, bpa.BestPractice);
        }

        private void DumpPlan(ModelPlanView plan, string when)
        {
            Say($"-- Plan {when}: {plan.Items.Length} rows, readyDigest {(plan.ReadyDigest == null ? "none" : "set")}");
            foreach (var i in plan.Items)
                Say($"   [{i.Doer}/{i.StateWord}{(i.Held ? "/held" : "")}{(i.Optional ? "/optional" : "")}] {i.Id}: {i.Title} / {i.Sub}");
        }

        /// <summary>The assistant writes one words-queue item with the tool the queue names.</summary>
        private async Task<string> WriteWordAsync(LocalEngine engine, WordsQueueItem it)
        {
            try
            {
                switch (it.Tool)
                {
                    case "set_description": return (await McpTools.SetDescription(engine, it.ObjectRef, "What " + Humanise(it.ObjectRef) + " means for the business, in plain words.")).ToString();
                    case "rename_object": return (await McpTools.RenameObject(engine, it.ObjectRef, NewName(it.ObjectRef))).ToString();
                    case "set_synonyms": return (await McpTools.SetSynonyms(engine, it.ObjectRef, new[] { Humanise(it.ObjectRef).ToLowerInvariant() + " figure", "the " + Humanise(it.ObjectRef).ToLowerInvariant() })).ToString();
                    case "create_linguistic_schema": return (await McpTools.CreateLinguisticSchema(engine)).ToString();
                    case "set_ai_instructions": return (await McpTools.SetAiInstructions(engine, "This model answers questions about consulting work: hours, projects, clients and invoices.")).ToString();
                    case "set_measure_format":
                        var n = (it.ObjectRef ?? "").ToLowerInvariant();
                        var fmt = n.Contains('%') || n.Contains("yoy") ? "#,0.0%" : n.Contains("amt") || n.Contains("rate") ? "#,0.00" : "#,0";
                        return (await McpTools.SetMeasureFormat(engine, it.ObjectRef, fmt)).ToString();
                    default: return "NO TOOL WRITER for " + it.Tool;
                }
            }
            catch (Exception e) { return "THREW " + e.GetType().Name + ": " + e.Message; }
        }

        /// <summary>Walk the Plan after the build: Run the plan while a Semanticus row is ready, then the words queue through its tools. Returns the final letters.</summary>
        private async Task<(string Ai, string Bp)> WalkAsync(LocalEngine engine, string origin, int rounds = 12)
        {
            for (var round = 1; round <= rounds; round++)
            {
                var plan = await engine.GetModelPlanAsync();
                await engine.JevSettledForTest();
                plan = await engine.GetModelPlanAsync();
                DumpPlan(plan, "walk round " + round);
                var ready = plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held).ToList();
                if (ready.Count > 0 && plan.ReadyDigest != null)
                {
                    var r = await engine.ApplyPlanItemsAsync(Array.Empty<PlanItemDigest>(), origin, plan.ReadyDigest);
                    Say($"   Run the plan ({origin}): performed {r.Performed}, applied {r.Applied}, {r.Sentence}; skipped {r.Skipped.Length}");
                    if (r.Performed && r.Applied > 0) continue;
                }
                var q = await engine.GetWordsQueueAsync(400, 0);
                Say($"   words queue: {q.Total} open [{string.Join(", ", q.Kinds.Select(k => k.Key + " " + k.Value))}]");
                if (q.Items.Length == 0) break;
                var wrote = 0;
                var failed = new List<string>();
                foreach (var it in q.Items)
                {
                    var res = await WriteWordAsync(engine, it);
                    if (res.StartsWith("THREW") || res.StartsWith("NO TOOL")) failed.Add(it.Tool + " " + it.ObjectRef + ": " + res); else wrote++;
                }
                Say($"   assistant wrote {wrote} words-queue items; failed {failed.Count}: " + string.Join(" | ", failed.Take(8)));
                if (wrote == 0) break;
            }
            return await DumpGradesAsync(engine, "after walking the Plan (" + origin + ")");
        }

        private sealed class Act { public string Rule, Ref, Who, Route, Result; }

        /// <summary>Which Plan row (or the words queue) offers this object, or null when no row does.</summary>
        private static readonly HashSet<string> BpaRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "PROVIDE_FORMAT_STRING_FOR_MEASURES", "SEM_COLUMN_NEEDS_FORMAT_STRING", "INACTIVE_RELATIONSHIPS_THAT_ARE_NEVER_ACTIVATED", "SNOWFLAKE_SCHEMA_ARCHITECTURE" };

        /// <summary>Which Plan row (or the words queue) offers THIS finding, or null when no row does. A best-practice finding counts as offered only by
        /// the words queue naming its object and rule or by the best-practice row; an AI readiness finding by a row that lists its object (the proposal
        /// rows are built from the readiness findings). Accepting a readiness finding on a row does not close its best-practice twin.</summary>
        private static string RouteFor(ModelPlanView plan, WordsQueue words, string objRef, string ruleId)
        {
            var bpa = BpaRules.Contains(ruleId ?? "");
            if (words.Items.Any(i => string.Equals(i.ObjectRef, objRef, StringComparison.OrdinalIgnoreCase) && (!bpa || string.Equals(i.RuleId, ruleId, StringComparison.OrdinalIgnoreCase) || i.Kind == "format")))
                return "words/model (assistant)";
            var row = plan.Items.FirstOrDefault(i => (bpa ? i.Id == "bpafix/model" : true)
                && (i.Objects ?? new string[0]).Any(o => string.Equals(o, objRef, StringComparison.OrdinalIgnoreCase)));
            return row == null ? null : row.Id + " (" + row.Doer + ")";
        }

        /// <summary>Close what is left, finding by finding, the way a person and their assistant would: words and renames by the assistant,
        /// acceptances with a reason by the person. Each act says whether a Plan row offered it. Returns the acts.</summary>
        private async Task<List<Act>> FinishAsync(LocalEngine engine, SessionManager sessions, bool mcpDoor)
        {
            var acts = new List<Act>();
            var person = mcpDoor ? "agent" : "human";
            for (var pass = 1; pass <= 6; pass++)
            {
                var ai = await engine.AiReadinessScanAsync();
                var bpa = await engine.BpaScanAsync();
                if (ai.Grade == "A" && bpa.BestPractice == "A") break;
                var plan = await engine.GetModelPlanAsync();
                await engine.JevSettledForTest();
                plan = await engine.GetModelPlanAsync();
                var words = await engine.GetWordsQueueAsync(400, 0);
                var renamed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                async Task Do(string rule, string objRef, string who, Func<Task<object>> act)
                {
                    var a = new Act { Rule = rule, Ref = objRef, Who = who, Route = RouteFor(plan, words, objRef, rule) ?? "NO PLAN ROW" };
                    try { a.Result = (await act())?.ToString(); } catch (Exception e) { a.Result = "THREW " + e.Message; }
                    acts.Add(a);
                }
                foreach (var f in ai.Findings.Where(f => !f.IsAccepted() && !f.WholeRuleRow).ToList())
                {
                    var name = f.ObjectRef.Substring(f.ObjectRef.LastIndexOf('/') + 1);
                    switch (f.RuleId)
                    {
                        case "NAME-TECH-PREFIX":
                            var bare = System.Text.RegularExpressions.Regex.Replace(f.ObjectRef.Substring(6), "^(Dim|Fact)[ _]", "");
                            if (renamed.Add(f.ObjectRef)) await Do(f.RuleId, f.ObjectRef, "assistant", async () => mcpDoor ? await McpTools.RenameObject(engine, f.ObjectRef, bare) : await engine.RenameObjectAsync(f.ObjectRef, bare, "agent"));
                            break;
                        case "NAME-DUP": case "DAC-QNA-NAME-COLLISIONS":
                            var table = f.ObjectRef.Substring(f.ObjectRef.IndexOf(':') + 1, f.ObjectRef.IndexOf('/') - f.ObjectRef.IndexOf(':') - 1);
                            if (renamed.Add(f.ObjectRef)) await Do(f.RuleId, f.ObjectRef, "assistant", async () => await engine.RenameObjectAsync(f.ObjectRef, table + " " + name, "agent"));
                            break;
                        case "SYN-COLLIDE": case "SYN-MISSING": case "SYN-THIN":
                            var term = (f.ObjectRef.Contains('/') ? f.ObjectRef.Substring(f.ObjectRef.IndexOf(':') + 1).Replace('/', ' ') : name.Substring(name.IndexOf(':') + 1)).ToLowerInvariant() + " figure";
                            await Do(f.RuleId, f.ObjectRef, "assistant", async () => mcpDoor ? await McpTools.SetSynonyms(engine, f.ObjectRef, new[] { term }) : await engine.SetSynonymsAsync(f.ObjectRef, new[] { term }, null, "agent"));
                            break;
                        case "DAC-AI-DATA-SCHEMA":
                            var hidden = await sessions.Current.ReadAsync(m => m.AllColumns.Where(c => c.IsHidden).Select(c => "column:" + c.Table.Name + "/" + c.Name).FirstOrDefault());
                            await Do(f.RuleId, f.ObjectRef, "assistant", async () => await engine.SetAiDataSchemaAsync(hidden, false, null, "agent"));
                            break;
                        case "REL-SNOWFLAKE": case "REL-DISCONNECTED": case "REL-INACTIVE":
                            await Do(f.RuleId, f.ObjectRef, "person", async () => await engine.WaiveFindingAsync("air", f.RuleId, f.ObjectRef, "By design: a second date role, a second path, or a code lookup.", person));
                            break;
                        default:
                            acts.Add(new Act { Rule = f.RuleId, Ref = f.ObjectRef, Who = "?", Route = RouteFor(plan, words, f.ObjectRef, f.RuleId) ?? "NO PLAN ROW", Result = "UNHANDLED " + f.RuleTitle });
                            break;
                    }
                }
                foreach (var v in bpa.Violations.Where(v => !v.IsAccepted() && !v.WholeRuleRow && v.Severity >= 2).ToList())
                {
                    switch (v.RuleId)
                    {
                        case "PROVIDE_FORMAT_STRING_FOR_MEASURES":
                            var lower = v.ObjectRef.ToLowerInvariant();
                            var fmt = lower.Contains("yoy") || lower.Contains('%') ? "#,0.0%" : lower.Contains("amt") || lower.Contains("rate") ? "#,0.00" : "#,0";
                            await Do(v.RuleId, v.ObjectRef, "assistant", async () => mcpDoor ? await McpTools.SetMeasureFormat(engine, v.ObjectRef, fmt) : await engine.SetMeasureFormatAsync(v.ObjectRef, fmt, "agent"));
                            break;
                        case "SEM_COLUMN_NEEDS_FORMAT_STRING":
                            var isDate = await sessions.Current.ReadAsync(m => m.AllColumns.Any(c => "column:" + c.Table.Name + "/" + c.Name == v.ObjectRef && c.DataType == TabularEditor.TOMWrapper.DataType.DateTime));
                            await Do(v.RuleId, v.ObjectRef, "assistant", async () => await engine.SetObjectPropertyAsync(v.ObjectRef, "FormatString", isDate ? "yyyy-mm-dd" : "#,0", "agent"));
                            break;
                        case "INACTIVE_RELATIONSHIPS_THAT_ARE_NEVER_ACTIVATED": case "SNOWFLAKE_SCHEMA_ARCHITECTURE":
                            await Do(v.RuleId, v.ObjectRef, "person", async () => await engine.WaiveFindingAsync("bpa", v.RuleId, v.ObjectRef, "By design: a second date role, or a second path to the client.", person));
                            break;
                        default:
                            acts.Add(new Act { Rule = v.RuleId, Ref = v.ObjectRef, Who = "?", Route = RouteFor(plan, words, v.ObjectRef, v.RuleId) ?? "NO PLAN ROW", Result = "UNHANDLED sev" + v.Severity });
                            break;
                    }
                }
                Say($"-- finish pass {pass}: {acts.Count} acts so far");
            }
            foreach (var g in acts.GroupBy(a => a.Rule + " by " + a.Who + " via " + a.Route))
                Say($"   ACT {g.Key} x{g.Count()}: " + string.Join("; ", g.Take(4).Select(a => a.Ref + " => " + (a.Result ?? "").Replace("\n", " ").Substring(0, Math.Min(90, (a.Result ?? "").Length)))));
            return acts;
        }

        private async Task<string> GradeWalkAsync(bool mcpDoor)
        {
            Say("######## Pro, " + (mcpDoor ? "MCP door" : "UI door"));
            var (engine, sessions) = await BuiltProAsync(mcpDoor: mcpDoor);
            var after = await DumpGradesAsync(engine, "right after the build");
            await engine.GetModelPlanAsync();
            await engine.JevSettledForTest();
            DumpPlan(await engine.GetModelPlanAsync(), "right after the build");
            var walked = await WalkAsync(engine, mcpDoor ? "agent" : "human", rounds: 3);
            var acts = await FinishAsync(engine, sessions, mcpDoor);
            var final = await DumpGradesAsync(engine, "after the rest, row by row");
            var offPlan = acts.Where(a => a.Route == "NO PLAN ROW").Select(a => a.Rule).Distinct().OrderBy(x => x).ToList();
            Say($"RESULT {(mcpDoor ? "MCP" : "UI")}: after build {after.Ai}/{after.Bp}; after the Plan walk {walked.Ai}/{walked.Bp}; final {final.Ai}/{final.Bp}; acts {acts.Count} ({acts.Count(x => x.Result != null && !x.Result.StartsWith("THREW") && !x.Result.StartsWith("UNHANDLED"))} landed: person {acts.Count(x => x.Who == "person" && !(x.Result ?? "").StartsWith("THREW"))}, assistant {acts.Count(x => x.Who == "assistant" && !(x.Result ?? "").StartsWith("THREW"))}; off-plan landed {acts.Count(x => x.Route == "NO PLAN ROW" && !(x.Result ?? "").StartsWith("THREW"))}); rules closed with NO PLAN ROW: [{string.Join(", ", offPlan)}]");
            Assert.Equal("A", final.Ai);
            Assert.Equal("A", final.Bp);
            return final.Ai;
        }

        [Fact] public async Task Grade_walk_to_A_and_A_after_the_Pro_build_UI_door() => Assert.Equal("A", await GradeWalkAsync(false));
        [Fact] public async Task Grade_walk_to_A_and_A_after_the_Pro_build_MCP_door() => Assert.Equal("A", await GradeWalkAsync(true));

        // ================================================================== attacks on the wiring's claims (W1, W3, W6, W7)

        private (LocalEngine Engine, SessionManager Sessions, StartPlanDecideTests.PolicyOracle Oracle) NewEngine(bool pro, string workspace = null)
        {
            AgentPolicyStore.SetPreset("open", "human");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, pro ? TestEntitlements.Pro : TestEntitlements.Free, workspace ?? Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N").Substring(0, 5)));
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            engine.SourceSampleExecutorForTest = J8Rows(SqlDraftBuildTests.J8Schema());
            var oracle = new StartPlanDecideTests.PolicyOracle();
            engine.UseDecisionsForTest(new DecisionService(oracle));
            return (engine, sessions, oracle);
        }

        private async Task<StartPlanView> StartedAsync(LocalEngine engine, string folder = null)
        {
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = folder ?? Path.Combine(_root, "model-" + Guid.NewGuid().ToString("N").Substring(0, 5)), Name = "Uplift" }, "human");
            await engine.StartSampleSettledForTest();
            view = await engine.GetStartPlanAsync();
            var fy = view.NeedsYou.SingleOrDefault(x => x.Kind == "fiscal_year");
            if (fy != null) view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = "fiscal_year", Value = fy.Recommended }, "human");
            return view;
        }

        private static async Task WordsAsync(LocalEngine engine)
        {
            for (var pass = 0; pass < 60; pass++)
            {
                var page = await engine.GetStartWordsAsync(40, 0);
                if (page.Items.Count == 0) break;
                await engine.WriteStartWordsAsync(WordsFor(page), "agent");
            }
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
        }

        private static string AllSent(StartPlanDecideTests.PolicyOracle o) =>
            string.Join("\n", o.Snapshots.Select(x => string.Join("|", x.QuestionNames) + "\n" + x.SnapshotJson)).ToLowerInvariant();

        /// <summary>W1, attacked end to end: the person leaves dim_staff out (and one column of an included table) before the grant. Nothing about them may
        /// reach Jev, by table name or by the names of the left-out table's own columns, through the decide rounds, the words rounds, the build, and the
        /// Plan's own Jev run on the built model.</summary>
        [Fact]
        public async Task Verify_a_left_out_table_and_its_columns_never_reach_Jev_by_name_from_start_to_the_plan_after_the_build()
        {
            var (engine, sessions, oracle) = NewEngine(pro: true);
            var view = await StartedAsync(engine);
            view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "false" }, "human");
            view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_column", Target = "fact_invoice/TAX_AMT", Value = "false" }, "human");
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            view = await engine.GrantStartDisclosureAsync(view.Digest, "human");
            await engine.StartDecideSettledForTest();
            await WordsAsync(engine);
            view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            oracle.Phase = 2;
            await engine.GetModelPlanAsync();
            await engine.JevSettledForTest();
            await engine.GetModelPlanAsync();
            Assert.True(oracle.Asks > 0, "nothing was asked at all, so this proves nothing");
            var sent = AllSent(oracle);
            var leaks = new[] { "dim_staff", "dim staff", "emp_nm", "grade_cd", "std_rate", "hire_dt_key", "term_flg", "tax_amt" }
                .Where(n => StartPlanConsent.ContainsPhrase(sent, n)).ToList();
            Say($"W1 left-out names: {oracle.Asks} asks ({oracle.Requests.Count(r => r.Phase == 2)} after the build); leaked [{string.Join(", ", leaks)}]");
            Assert.True(leaks.Count == 0, "left-out names reached Jev: " + string.Join(", ", leaks));
        }

        /// <summary>W1, the sample: the person gives the model's permission but says Not now to the source grant (which names "counts and patterns from a
        /// sample of rows"). Do facts worked out from the sample reach Jev anyway, after the build, under the model's permission alone?</summary>
        [Fact]
        public async Task Verify_after_not_now_on_the_source_grant_what_the_plan_after_the_build_sends_from_the_sample()
        {
            var (engine, sessions, oracle) = NewEngine(pro: true);
            var view = await StartedAsync(engine);
            Assert.True(view.SampleFacts, "no sample was read");
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = "grant", Value = "Not now" }, "human");
            Assert.True(view.JevDeclined);
            await WordsAsync(engine);
            Assert.Equal(0, oracle.Asks);
            view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            oracle.Phase = 2;
            await engine.GetModelPlanAsync();
            await engine.JevSettledForTest();
            await engine.GetModelPlanAsync();
            var points = oracle.Requests.Where(r => r.Phase == 2).GroupBy(r => r.Point).Select(g => g.Key + " x" + g.Count());
            Say("   points after the build: " + string.Join(", ", points));
            var withSample = oracle.Snapshots.Count(x => System.Text.RegularExpressions.Regex.IsMatch(x.SnapshotJson ?? "", "\"(blank_share|blankShare|distinct_share)\":\\s*[0-9]"));
            var sampledTrue = oracle.Snapshots.Count(x => (x.SnapshotJson ?? "").Contains("\"sampled\":true"));
            Say($"W1 not-now: asks after the build {oracle.Asks}, requests carrying sample-derived shares {withSample}, carrying sampled:true {sampledTrue}; sidecar {(engine.LoadSourceFacts(sessions.Current) != null ? "present" : "absent")}");
            var one = oracle.Snapshots.FirstOrDefault(x => System.Text.RegularExpressions.Regex.IsMatch(x.SnapshotJson ?? "", "\"(blank_share|blankShare|distinct_share)\":\\s*[0-9]"));
            if (one.SnapshotJson != null) Say("   a request with sample facts: " + string.Join("|", one.QuestionNames) + " :: " + System.Text.RegularExpressions.Regex.Match(one.SnapshotJson, "\"(blank_share|blankShare|distinct_share)\".{0,200}").Value);
            // The person was shown "It also sees counts and patterns worked out from a sample of rows" on the source card and said Not now. Facts worked out
            // from that sample must not go to Jev on the model's permission alone.
            Assert.True(withSample == 0, withSample + " requests after the build carried facts worked out from the sample of rows the person said Not now to");
        }

        /// <summary>W3, attacked: a line the person dropped stays out of the build; two builds pressed at once build once; one undo step; undo, redo, undo and
        /// build again all land the same model; the person's drop is carried into the Plan and Run the plan does not write it.</summary>
        [Fact]
        public async Task Verify_the_build_honours_a_drop_builds_once_when_pressed_twice_and_rebuilds_after_undo()
        {
            var (engine, sessions, oracle) = NewEngine(pro: true);
            var view = await StartedAsync(engine);
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            view = await engine.GrantStartDisclosureAsync(view.Digest, "human");
            await engine.StartDecideSettledForTest();
            await WordsAsync(engine);
            var doc = engine.StartPlanDocForTest();
            var victim = doc.Lines.FirstOrDefault(l => l.Kept && l.Held == null && l.DecidedBy == StartDecidedBy.Jev && l.Item.Kind == "set_format_string" && (l.Item.ObjectRef ?? "").StartsWith("column:"))
                      ?? doc.Lines.First(l => l.Kept && l.Held == null && l.DecidedBy == StartDecidedBy.Jev && (l.Item.Target ?? "") == "formatString");
            var key = StartPlanDecide.LineKey(victim);
            view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "drop_line", Target = key, Value = "" }, "human");
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            doc = engine.StartPlanDocForTest();
            var line = doc.Lines.Single(l => StartPlanDecide.LineKey(l) == StartPlanDecide.LineKey(victim) || (l.Item.ObjectRef == victim.Item.ObjectRef && l.Item.Target == victim.Item.Target));
            Assert.False(line.Kept);
            var objRef = line.Item.ObjectRef;
            view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);

            var steps0 = await sessions.Current.RunAsync(() => sessions.Current.UndoStateNow().Steps);
            var both = await Task.WhenAll(engine.BuildStartPlanAsync(view.Digest, "human"), McpTools.BuildStartPlan(engine, view.Digest));
            Say($"W3 double press: built [{string.Join(", ", both.Select(b => b.Built + ":" + (b.Refused ?? "")))}]");
            Assert.Equal(1, both.Count(b => b.Built));
            int Tables() => sessions.Current.ReadAsync(m => m.Tables.Count).GetAwaiter().GetResult();
            string Fmt() => sessions.Current.ReadAsync(m =>
                m.AllColumns.Where(c => string.Equals(Semanticus.Engine.ObjectRefs.For(c), objRef, StringComparison.OrdinalIgnoreCase)).Select(c => c.FormatString)
                 .Concat(m.AllMeasures.Where(x => string.Equals(Semanticus.Engine.ObjectRefs.For(x), objRef, StringComparison.OrdinalIgnoreCase)).Select(x => x.FormatString))
                 .FirstOrDefault() ?? "(object not found)").GetAwaiter().GetResult();
            Assert.Equal(7, Tables());
            Assert.Equal(1, await sessions.Current.RunAsync(() => sessions.Current.UndoStateNow().Steps) - steps0);
            var droppedFmt = Fmt();
            await engine.UndoAsync("human");
            Assert.Equal(0, Tables());
            await engine.RedoAsync("human");
            Assert.Equal(7, Tables());
            Assert.Equal(droppedFmt, Fmt());
            await engine.UndoAsync("human");
            view = await engine.GetStartPlanAsync();
            var again = await engine.BuildStartPlanAsync(view.Approval?.Digest ?? view.Digest, "human");
            if (!again.Built)
            {
                view = await engine.ApproveStartPlanAsync(view.Digest);
                again = await engine.BuildStartPlanAsync(view.Digest, "human");
            }
            Assert.True(again.Built, again.Refused);
            Assert.Equal(7, Tables());
            Say($"W3 dropped line {key} on {objRef}: format after build '{droppedFmt}', after rebuild '{Fmt()}'");
            Assert.Equal(droppedFmt, Fmt());
            var plan = await engine.GetModelPlanAsync();
            await engine.JevSettledForTest();
            plan = await engine.GetModelPlanAsync();
            if (plan.ReadyDigest != null && plan.Items.Any(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held))
                await engine.ApplyPlanItemsAsync(Array.Empty<PlanItemDigest>(), "human", plan.ReadyDigest);
            Say($"W3 after Run the plan: format '{Fmt()}'");
            Assert.Equal(droppedFmt, Fmt());
        }

        private static List<string> HandMade(StartPlanDoc d, string table) => d.Lines
            .Where(l => StartPlanDecide.IsHandMade(l.DecidedBy) && ((l.Item.ObjectRef ?? "").Contains(":" + table + "/") || string.Equals(l.Item.ObjectRef, "table:" + table, StringComparison.OrdinalIgnoreCase) ))
            .Select(l => l.DecidedBy + " " + l.Item.Kind + " " + l.Item.ObjectRef + " " + l.Item.Target + " kept=" + l.Kept + " word=" + l.Word + " after=" + l.Item.After)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        /// <summary>W6, attacked as a class: on ONE table the assistant writes words (table, column, measure), the person picks how a column adds up,
        /// pins a value, keeps an unsure line and drops a kept one. The person then leaves a column out, then the whole table, the engine restarts, and
        /// the table comes back, then the column. Every hand-made line, the pick and the pin must be exactly what they were.</summary>
        [Fact]
        public async Task Verify_every_hand_made_thing_on_a_table_survives_leaving_it_out_and_a_restart_and_returns_with_it()
        {
            var ws = Path.Combine(_root, "ws-w6");
            var (engine, sessions, oracle) = NewEngine(pro: true, workspace: ws);
            var view = await StartedAsync(engine);
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            view = await engine.GrantStartDisclosureAsync(view.Digest, "human");
            await engine.StartDecideSettledForTest();
            const string T = "dim_project";
            // the assistant's words, only on dim_project
            var page = await engine.GetStartWordsAsync(400, 0);
            var mine = page.Items.Where(i => (i.Ref ?? "").Contains(T)).ToList();
            Assert.True(mine.Count >= 3, "too few words open on " + T + ": " + mine.Count);
            var wrote = await McpTools.WriteStartWords(engine, mine.Select(i => new StartWord { Ref = i.Ref, Kind = i.Kind, Text = i.Kind == "synonyms" ? "proj word " + i.Ref.Length : i.Kind == "display_name" ? "Proj " + Humanise(i.Ref) : "Words for " + i.Ref }).ToArray());
            await engine.StartDecideSettledForTest();
            // the person's pick, keep, drop and pin
            var m0 = engine.StartPlanDocForTest().Manifest.Tables.Single(t => t.SourceName == T);
            await engine.EditStartPlanAsync(new StartPlanEdit { Op = "set_pick", Target = m0.Name + "/" + m0.Columns.Single(c => c.SourceColumn == "BUDGET_HRS").Name, Value = "average" }, "human");
            await engine.StartDecideSettledForTest();
            var d = engine.StartPlanDocForTest();
            var TN = d.Manifest.Tables.Single(t => t.SourceName == T).Name;
            bool On(StartLine l) => (l.Item.ObjectRef ?? "").Contains(":" + TN + "/") || string.Equals(l.Item.ObjectRef, "table:" + TN, StringComparison.OrdinalIgnoreCase);
            var unsure = d.Lines.FirstOrDefault(l => !l.Kept && l.DecidedBy == StartDecidedBy.Jev && On(l));
            if (unsure != null) await engine.EditStartPlanAsync(new StartPlanEdit { Op = "keep_line", Target = StartPlanDecide.LineKey(unsure) }, "human");
            var kept = d.Lines.Where(l => l.Kept && l.DecidedBy == StartDecidedBy.Jev && On(l) && l.Item.Kind != "create_measure").ToList();
            Assert.True(kept.Count >= 2, "too few Jev lines on " + TN + ": " + string.Join(" | ", d.Lines.Where(On).Select(l => l.DecidedBy + " " + l.Item.Kind + " " + l.Item.ObjectRef + " kept=" + l.Kept)));
            await engine.EditStartPlanAsync(new StartPlanEdit { Op = "drop_line", Target = StartPlanDecide.LineKey(kept[0]) }, "human");
            var pinTarget = kept.Skip(1).FirstOrDefault(l => (l.Item.Target ?? "") == "formatString") ?? kept[1];
            await engine.EditStartPlanAsync(new StartPlanEdit { Op = "pin", Target = StartPlanDecide.LineKey(pinTarget), Value = (pinTarget.Item.Target ?? "") == "formatString" ? "#,0.000" : pinTarget.Item.After }, "human");
            await engine.StartDecideSettledForTest();
            var before = engine.StartPlanDocForTest();
            var handBefore = HandMade(before, TN);
            var pickBefore = before.Manifest.BaseMeasures.Where(b => b.Table.Contains("roject")).Select(b => b.Column + ":" + b.Pick + ":" + b.Name + ":" + b.DecidedBy).OrderBy(x => x).ToList();
            var pinsBefore = before.Pins.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value).ToList();
            Say($"W6 before: {handBefore.Count} hand-made lines on {T} (wrote {wrote.Written}); picks [{string.Join(", ", pickBefore)}]; pins {pinsBefore.Count}; unsure kept {(unsure != null)}");

            var tableName = before.Manifest.Tables.Single(t => t.SourceName == T).Name;
            await engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_column", Target = tableName + "/" + before.Manifest.Tables.Single(t => t.SourceName == T).Columns.Single(c => c.SourceColumn == "BUDGET_HRS").Name, Value = "false" }, "human");
            await engine.StartDecideSettledForTest();
            await engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = tableName, Value = "false" }, "human");
            await engine.StartDecideSettledForTest();
            var outDoc = engine.StartPlanDocForTest();
            var modelPath = outDoc.Destination.ModelPath;
            engine.Dispose(); sessions.Dispose();

            // a new process on the same state
            var (second, sessions2, oracle2) = NewEngine(pro: true, workspace: ws);
            await second.OpenAsync(modelPath);
            await second.GetStartPlanAsync();
            await second.StartDecideSettledForTest();
            var tn = second.StartPlanDocForTest().Manifest.Tables.Single(t => t.SourceName == T);
            await second.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = tn.Name, Value = "true" }, "human");
            await second.StartDecideSettledForTest();
            tn = second.StartPlanDocForTest().Manifest.Tables.Single(t => t.SourceName == T);
            await second.EditStartPlanAsync(new StartPlanEdit { Op = "include_column", Target = tn.Name + "/" + tn.Columns.Single(c => c.SourceColumn == "BUDGET_HRS").Name, Value = "true" }, "human");
            await second.StartDecideSettledForTest();
            var after = second.StartPlanDocForTest();
            var handAfter = HandMade(after, TN);
            var pickAfter = after.Manifest.BaseMeasures.Where(b => b.Table.Contains("roject")).Select(b => b.Column + ":" + b.Pick + ":" + b.Name + ":" + b.DecidedBy).OrderBy(x => x).ToList();
            var pinsAfter = after.Pins.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value).ToList();
            var lost = handBefore.Except(handAfter).ToList();
            var lostPicks = pickBefore.Except(pickAfter).ToList();
            var lostPins = pinsBefore.Except(pinsAfter).ToList();
            Say($"W6 after restart and return: lost lines [{string.Join(" || ", lost)}]; new [{string.Join(" || ", handAfter.Except(handBefore))}]; lost picks [{string.Join(", ", lostPicks)}]; lost pins [{string.Join(", ", lostPins)}]; held now {after.Lines.Count(l => l.Held != null)}");
            foreach (var h in after.Lines.Where(l => l.Held != null)) Say("   still held: " + h.DecidedBy + " " + h.Item.Kind + " " + h.Item.ObjectRef + " " + h.Item.Target + " kept=" + h.Kept + " held=" + h.Held + " word=" + h.Word);
            foreach (var h in before.Lines.Where(l => l.Held != null)) Say("   held before leaving out: " + h.DecidedBy + " " + h.Item.Kind + " " + h.Item.ObjectRef + " held=" + h.Held);
            Assert.Empty(lost);
            Assert.Empty(lostPicks);
            Assert.Empty(lostPins);
            // A line held before the table went out (a kept unsure format, see the keep defect below) may stay held; nothing else may.
            var heldBefore = before.Lines.Where(l => l.Held != null).Select(StartPlanDecide.LineKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Empty(after.Lines.Where(l => l.Held != null && !heldBefore.Contains(StartPlanDecide.LineKey(l))).Select(l => StartPlanDecide.LineKey(l) + " " + l.Held));
        }

        /// <summary>W7, attacked: the plan waits for a grant when a table is added after it; a second edit that puts the table back out ends the wait with
        /// no new grant; the same after an engine restart in the middle of the wait.</summary>
        [Fact]
        public async Task Verify_the_wait_for_a_grant_clears_itself_also_across_a_restart()
        {
            var ws = Path.Combine(_root, "ws-w7");
            var (engine, sessions, oracle) = NewEngine(pro: true, workspace: ws);
            var view = await StartedAsync(engine);
            view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "ref_codes", Value = "false" }, "human");
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            view = await engine.GrantStartDisclosureAsync(view.Digest, "human");
            await engine.StartDecideSettledForTest();
            view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "ref_codes", Value = "true" }, "human");
            await engine.StartDecideSettledForTest();
            view = await engine.GetStartPlanAsync();
            Assert.Equal(StartDecideState.WaitingGrant, view.Decide.State);
            var asks = oracle.Asks;
            var modelPath = engine.StartPlanDocForTest().Destination.ModelPath;
            engine.Dispose(); sessions.Dispose();
            var (second, sessions2, oracle2) = NewEngine(pro: true, workspace: ws);
            await second.OpenAsync(modelPath);
            view = await second.GetStartPlanAsync();
            await second.StartDecideSettledForTest();
            view = await second.GetStartPlanAsync();
            Say($"W7 after restart while waiting: {view.Decide.State} ({view.Decide.Reason}); asks on the new engine {oracle2.Asks}");
            Assert.Equal(StartDecideState.WaitingGrant, view.Decide.State);
            Assert.Equal(0, oracle2.Asks);
            view = await second.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "ref_codes", Value = "false" }, "human");
            await second.StartDecideSettledForTest();
            view = await second.GetStartPlanAsync();
            Say($"W7 after taking it back out: {view.Decide.State} ({view.Decide.Reason}); asks {oracle2.Asks}");
            Assert.NotEqual(StartDecideState.WaitingGrant, view.Decide.State);
            Assert.NotEqual(StartDecideState.Deciding, view.Decide.State);
            Assert.DoesNotContain(StartPlanConsent.ContainsPhrase(AllSent(oracle2), "ref_codes") ? "leak" : "", new[] { "leak" });
        }

        /// <summary>DEFECT (found by the W6 attack). Jev was unsure of a format, so the line is left out "until you say" in Needs you. The person says keep. The
        /// start plan records the keep (Kept, decided by the person), but the executor's format gate only honours a tick made the Plan's way (KeptByPerson with
        /// the object's digest), which keep_line never sets. So the person's answer is not built, and the line is held with the words "nobody ticked the line",
        /// which is untrue: the person did.</summary>
        [Fact]
        public async Task Verify_keeping_a_format_line_Jev_was_unsure_of_builds_it_and_never_says_nobody_ticked_it()
        {
            var (engine, sessions, oracle) = NewEngine(pro: true);
            var view = await StartedAsync(engine);
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            view = await engine.GrantStartDisclosureAsync(view.Digest, "human");
            await engine.StartDecideSettledForTest();
            await WordsAsync(engine);
            view = await engine.GetStartPlanAsync();
            var need = view.NeedsYou.FirstOrDefault(n => n.Kind == "unsure_line" && n.Text.StartsWith("Format as"));
            Assert.True(need != null, "no unsure format line to answer: " + string.Join(" | ", view.NeedsYou.Select(n => n.Kind)));
            view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = need.Id, Value = "keep" }, "human");
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            var doc = engine.StartPlanDocForTest();
            var key = need.Id.Substring(need.Id.IndexOf(':') + 1);
            var line = doc.Lines.Single(l => string.Equals(StartPlanDecide.LineKey(l), key, StringComparison.OrdinalIgnoreCase));
            Say($"unsure keep: {need.Id} -> kept={line.Kept} by {line.DecidedBy} held=[{line.Held}] after={line.Item.After}");
            Assert.True(line.Kept && line.DecidedBy == StartDecidedBy.Person, "the keep did not land on the line");
            Assert.True(line.Held == null || !line.Held.Contains("nobody ticked", StringComparison.OrdinalIgnoreCase), "the person kept this line, and the plan says nobody ticked it: " + line.Held);
            view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var objRef = line.Item.ObjectRef;
            var fmt = await sessions.Current.ReadAsync(m =>
                m.AllColumns.Where(c => string.Equals(Semanticus.Engine.ObjectRefs.For(c), objRef, StringComparison.OrdinalIgnoreCase)).Select(c => c.FormatString)
                 .Concat(m.AllMeasures.Where(x => string.Equals(Semanticus.Engine.ObjectRefs.For(x), objRef, StringComparison.OrdinalIgnoreCase)).Select(x => x.FormatString))
                 .FirstOrDefault() ?? "(object not found)");
            Say($"unsure keep built: {objRef} format '{fmt}' (the line said '{line.Item.After}')");
            var afterDoc = engine.StartPlanDocForTest();
            var la = afterDoc.Lines.FirstOrDefault(l => string.Equals(StartPlanDecide.LineKey(l), key, StringComparison.OrdinalIgnoreCase));
            Say($"   after the build the start plan line: kept={la?.Kept} held=[{la?.Held}] why=[{la?.Why}]");
            var plan = await engine.GetModelPlanAsync();
            await engine.JevSettledForTest();
            plan = await engine.GetModelPlanAsync();
            foreach (var i in plan.Items.Where(i => (i.Sub ?? "").Contains("Mtd") || (i.Objects ?? new string[0]).Any(o => o.Contains("Mtd"))))
                Say($"   Plan row: [{i.Doer}/{i.StateWord}] {i.Id}: {i.Title} / {i.Sub}");
            var snap = sessions.CurrentContext.Plans.Current?.Snapshot().Where(i => (i.ObjectRef ?? "").Contains("Mtd")).Select(i => i.Kind + " " + i.ObjectRef + " status=" + i.Status + " droppedBy=" + i.DroppedBy + " keptByPerson=" + i.KeptByPerson);
            Say("   Plan items on it: " + string.Join(" | ", snap ?? new string[0]));
            Assert.Equal(line.Item.After, fmt);
        }

        /// <summary>W1, the stale grant, attacked on both doors: the person looks while the sample is still being read (digest D0, no sample in it). The
        /// sample lands. A grant on D0 must be refused on either door, and once a grant on the current digest is given it must record the sample.</summary>
        [Fact]
        public async Task Verify_a_grant_on_the_digest_from_before_the_sample_landed_is_refused_on_both_doors()
        {
            var (engine, sessions, oracle) = NewEngine(pro: true);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rows = J8Rows(SqlDraftBuildTests.J8Schema());
            engine.SourceSampleExecutorForTest = async (srv, db, sql, max) => { await gate.Task; return await rows(srv, db, sql, max); };
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = Path.Combine(_root, "model-st"), Name = "Uplift" }, "human");
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            var d0 = (await engine.GetStartPlanAsync()).Digest;
            var early = await Record(() => engine.GrantStartDisclosureAsync(d0, "human"));
            gate.SetResult(true);
            await engine.StartSampleSettledForTest();
            var now = await engine.GetStartPlanAsync();
            Assert.True(now.SampleFacts, "the sample did not land");
            var human = await Record(() => engine.GrantStartDisclosureAsync(d0, "human"));
            var agent = await Record(() => McpTools.GrantStartDisclosure(engine, d0));
            await engine.StartDecideSettledForTest();
            Say($"W1 stale grant: digest moved {d0 != now.Digest}; while reading [{early}]; human on D0 [{human}]; MCP on D0 [{agent}]; asks {oracle.Asks}");
            Assert.NotEqual(d0, now.Digest);
            Assert.StartsWith("refused", human);
            Assert.StartsWith("refused", agent);
            Assert.Equal(0, oracle.Asks);
            var fy = now.NeedsYou.SingleOrDefault(x => x.Kind == "fiscal_year");
            var ok = await engine.GrantStartDisclosureAsync(now.Digest, "human");
            Assert.True(engine.StartPlanDocForTest().Disclosure?.SampleFacts == true, "the grant on the current digest did not record the sample");
        }

        private static async Task<string> Record(Func<Task<StartPlanView>> act)
        {
            try { var v = await act(); return "granted (sample=" + v.SampleFacts + ")"; }
            catch (Exception e) { return "refused: " + e.Message; }
        }
    }
}

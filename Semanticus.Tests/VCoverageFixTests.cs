// The verifier's tests for the coverage lane (v-coverage-fix, 13 Oct 2026), kept as the lane's outcome tests (fix-coverage, 13 Oct).
// The outcome drive: Run the plan, then a scripted assistant takes every request through the MCP methods until the queue is empty,
// and the test reads back both grades and the Plan's open rows. The scripted assistant writes no words and no formulas, so it cannot
// reach grade A. What it must prove: both grades rise, nothing is refused, and every finding still lowering a grade is on an open row.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class VCoverageFixTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        private readonly ITestOutputHelper _out;
        public VCoverageFixTests(ModelTemplates templates, ITestOutputHelper output) { _templates = templates; _out = output; }

        private static async Task BuildGappyAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Batch Id", "String", null, "human");
            await e.CreateColumnAsync("table:Sales", "Load Note", "String", null, "human");
            await e.SetObjectPropertyAsync("column:Sales/Batch Id", "IsHidden", "true", "human");
            await e.SetObjectPropertyAsync("column:Sales/Load Note", "IsHidden", "true", "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", "BothDirections", true, "human");
            var total = await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Safe Total", "IFERROR ( SUM ( Sales[Amount] ), 0 )", "human");
            await e.CreateFieldParameterAsync("Discount Scenario", new[] { new FieldParameterItem { ObjectRef = total } }, "human");
        }

        private async Task<DecisionFixture> OpenAsync(string which)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            if (which == "gappy") await _templates.OpenCopyAsync(f, "v-gappy-coverage", BuildGappyAsync, "VGappy.bim");
            else
            {
                var bim = Path.Combine(f.Workspace, "AW.bim");
                File.Copy(TestModels.FindBim(), bim);
                await f.Engine.OpenAsync(bim);
            }
            f.UseNull();
            return f;
        }

        private static bool Scored(Scorecard air, ReadinessFinding x) =>
            !x.Waived && (air.ScoredRuleIds.Contains(x.RuleId ?? "") || x.RuleId is "LIMIT-SCALE" or "DAX-REFERENCE" or "DESC-MEASURE");

        private async Task<(BpaScorecard bpa, Scorecard air, ModelPlanView plan)> ReadAsync(LocalEngine e, string label)
        {
            var bpa = await e.BpaScanAsync();
            var air = await e.AiReadinessScanAsync();
            var plan = await e.GetModelPlanAsync();
            _out.WriteLine($"==== {label}: BPA {bpa.BestPractice} {bpa.BestPracticeScore} (AutoFixable {bpa.AutoFixable}) | AI {air.Grade} {air.Overall:0.0} | strip {plan.Strip?.TasksDone}/{plan.Strip?.TasksAll}");
            foreach (var i in plan.Items)
            {
                _out.WriteLine($"  ROW {i.Id} | doer={i.Doer} | {i.StateWord} | opt={i.Optional} | {i.Title} | rules={string.Join(",", i.Rules ?? Array.Empty<string>())} | objs={(i.Objects ?? Array.Empty<string>()).Length}:{string.Join(",", (i.Objects ?? Array.Empty<string>()).Take(4))} | note={i.StateNote}");
                if (LocalEngine.IsCoverageItem(i.Id) || i.Id == LocalEngine.CompatItemId) foreach (var w in i.Why ?? Array.Empty<string>()) _out.WriteLine("       WHY " + w);
            }
            foreach (var g in bpa.Violations.Where(v => !v.Waived && v.Severity >= 2).GroupBy(v => v.RuleId))
                _out.WriteLine($"  BPA {g.First().Severity} {g.Key} x{g.Count()} fix={g.Count(v => v.CanAutoFix)} withheld={g.Count(v => v.FixWithheld)} [{string.Join(",", g.Take(3).Select(v => v.ObjectRef))}]");
            foreach (var g in air.Findings.Where(x => Scored(air, x)).GroupBy(x => x.RuleId))
                _out.WriteLine($"  AIR {g.First().Severity} {g.Key} x{g.Count()} fix={g.First().Fix} [{string.Join(",", g.Take(3).Select(x => x.ObjectRef))}]");
            return (bpa, air, plan);
        }

        private static readonly Dictionary<string, string> TwinOf = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AVOID_USING_THE_IFERROR_FUNCTION"] = "BP-DAX-IFERROR", ["PROVIDE_FORMAT_STRING_FOR_MEASURES"] = "FMT-MEASURE",
        };

        private static bool RowNames(ModelPlanItem i, string rule, string obj)
        {
            var rules = i.Rules ?? Array.Empty<string>();
            var objs = i.Objects ?? Array.Empty<string>();
            var twin = PairedAcceptances.AirTwinOf(rule) ?? (TwinOf.TryGetValue(rule ?? "", out var t) ? t : null);
            var ruleOk = rules.Contains(rule, StringComparer.OrdinalIgnoreCase) || (twin != null && rules.Contains(twin, StringComparer.OrdinalIgnoreCase));
            return ruleOk && (objs.Length == 0 || objs.Contains(obj, StringComparer.OrdinalIgnoreCase));
        }

        private static async Task StripIfErrorAsync(LocalEngine e, string objRef)
        {
            var dax = await e.GetDaxAsync(objRef) ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(dax, @"^\s*IFERROR\s*\((.*),([^,]*)\)\s*$", System.Text.RegularExpressions.RegexOptions.Singleline);
            var inner = m.Success ? m.Groups[1].Value.Trim() : dax;
            // A second measure with the same formula would be a new finding; the scripted assistant keeps them apart.
            await e.SetDaxAsync(objRef, inner + " + 0", "agent", null);
        }

        /// <summary>The scripted assistant works ONE plan row: only the findings that row names, with the tool its brief names.</summary>
        private async Task<List<string>> WorkRowAsync(LocalEngine e, ModelPlanItem row)
        {
            var unhandled = new List<string>();
            if (row.Id == LocalEngine.CompatItemId) { await McpTools.SetCompatibilityLevel(e, 1701); return unhandled; }
            if (row.Id == "cleanup/model")
            {
                foreach (var o in row.Objects ?? Array.Empty<string>()) try { await e.DeleteObjectsAsync(new[] { o }, "agent"); } catch (Exception ex) { unhandled.Add("cleanup " + o + " " + ex.Message); }
                return unhandled;
            }
            if (row.Id is "calendar/model" or "datetable/model")
            {
                var r = await McpTools.DefineCalendarFromTemplate(e, "gregorian", "Date");
                _out.WriteLine("   calendar: " + System.Text.Json.JsonSerializer.Serialize(r));
                try { await e.MarkDateTableAsync("table:Date", "Date", "agent"); } catch (Exception ex) { _out.WriteLine("   mark: " + ex.Message); }
                return unhandled;
            }
            foreach (var v in (await e.BpaScanAsync()).Violations.Where(v => !v.Waived && v.Severity >= 2 && RowNames(row, v.RuleId, v.ObjectRef)).ToArray())
            {
                try
                {
                    switch (v.RuleId)
                    {
                        case "UNNECESSARY_COLUMNS": case "UNNECESSARY_MEASURES": await e.DeleteObjectsAsync(new[] { v.ObjectRef }, "agent"); break;
                        case "PROVIDE_FORMAT_STRING_FOR_MEASURES": await e.SetMeasureFormatAsync(v.ObjectRef, "#,0.00", "agent"); break;
                        case "AVOID_USING_THE_IFERROR_FUNCTION": await StripIfErrorAsync(e, v.ObjectRef); break;
                        case "MODEL_SHOULD_HAVE_A_DATE_TABLE":
                            await McpTools.DefineCalendarFromTemplate(e, "gregorian", "Date");
                            await e.MarkDateTableAsync("table:Date", "Date", "agent");
                            break;
                        case "AVOID_EXCESSIVE_BI-DIRECTIONAL_OR_MANY-TO-MANY_RELATIONSHIPS":
                            foreach (var rel in (await e.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == "REL-BIDI"))
                                await e.SetObjectPropertyAsync(rel.ObjectRef, "CrossFilteringBehavior", "OneDirection", "agent");
                            break;
                        default:
                            if (v.CanAutoFix) await e.BpaFixAsync(v.RuleId, v.ObjectRef, "agent");
                            else unhandled.Add("bpa " + v.RuleId);
                            break;
                    }
                }
                catch (Exception ex) { unhandled.Add("bpa-throw " + v.RuleId + " " + ex.Message); }
            }
            var air = await e.AiReadinessScanAsync();
            foreach (var x in air.Findings.Where(x => !x.Waived && RowNames(row, x.RuleId, x.ObjectRef)).ToArray())
            {
                try
                {
                    if (x.Fix == nameof(FixKind.SafeFix)) { await e.ApplyFixAsync(x.RuleId, x.ObjectRef, "agent"); continue; }
                    if ((x.RuleId ?? "").StartsWith("DESC-", StringComparison.OrdinalIgnoreCase))
                    { await e.SetDescriptionAsync(x.ObjectRef, "The " + x.ObjectName + " value for each row, used in sales reports by region and year.", "agent"); continue; }
                    if (x.RuleId == "FMT-MEASURE") { await e.SetMeasureFormatAsync(x.ObjectRef, "#,0.00", "agent"); continue; }
                    if (x.RuleId == "BP-DAX-IFERROR") { await StripIfErrorAsync(e, x.ObjectRef); continue; }
                    if (x.RuleId == "REL-BIDI") { await e.SetObjectPropertyAsync(x.ObjectRef, "CrossFilteringBehavior", "OneDirection", "agent"); continue; }
                    unhandled.Add("air " + x.RuleId + " (" + x.Fix + ")");
                }
                catch (Exception ex) { unhandled.Add("air-throw " + x.RuleId + " " + ex.Message); }
            }
            return unhandled;
        }

        private async Task DriveAsync(string which)
        {
            using var f = await OpenAsync(which);
            var e = f.Engine;
            var problems = new List<string>();
            var inv = new PlanInvariant(e, _out);
            // The Plan invariant (no job twice, the strip is the list, no finding on no row) after EVERY step, hard: "INVARIANT".
            async Task Step(string label) { foreach (var p in await inv.ProblemsAsync(await e.GetModelPlanAsync(), which + " " + label)) problems.Add("INVARIANT " + label + ": " + p); }
            var (bpa0, air0, plan0) = await ReadAsync(e, which + " BEFORE");
            await Step("before");

            // 1. Every scored finding is on some row (rule or twin) naming the object, or on a rule-level row.
            var cover = plan0.Items.Where(i => PlanStateWords.IsOpen(i.StateWord)).ToArray();
            foreach (var v in bpa0.Violations.Where(v => !v.Waived && v.Severity >= 2))
            {
                var twin = PairedAcceptances.AirTwinOf(v.RuleId) ?? (v.RuleId == "AVOID_USING_THE_IFERROR_FUNCTION" ? "BP-DAX-IFERROR" : v.RuleId == "PROVIDE_FORMAT_STRING_FOR_MEASURES" ? "FMT-MEASURE" : null);
                if (!cover.Any(i => ((i.Rules ?? Array.Empty<string>()).Contains(v.RuleId, StringComparer.OrdinalIgnoreCase) || (twin != null && (i.Rules ?? Array.Empty<string>()).Contains(twin, StringComparer.OrdinalIgnoreCase)))
                                    && ((i.Objects ?? Array.Empty<string>()).Length == 0 || (i.Objects ?? Array.Empty<string>()).Contains(v.ObjectRef, StringComparer.OrdinalIgnoreCase)))
                    && !cover.Any(i => i.Id == "cleanup/model" && (i.Objects ?? Array.Empty<string>()).Contains(v.ObjectRef, StringComparer.OrdinalIgnoreCase)))
                    problems.Add("UNCOVERED bpa " + v.RuleId + " " + v.ObjectRef);
            }
            foreach (var x in air0.Findings.Where(x => Scored(air0, x)))
                if (!cover.Any(i => (i.Rules ?? Array.Empty<string>()).Contains(x.RuleId, StringComparer.OrdinalIgnoreCase)
                                    && ((i.Objects ?? Array.Empty<string>()).Length == 0 || (i.Objects ?? Array.Empty<string>()).Contains(x.ObjectRef, StringComparer.OrdinalIgnoreCase)))
)
                    problems.Add("UNCOVERED air " + x.RuleId + " " + x.ObjectRef);
            // 4. Only person-only rows on You.
            foreach (var i in cover.Where(i => i.Doer == PlanDoers.You)) problems.Add("ON-YOU before: " + i.Id + " " + i.Title);

            // Run the plan.
            var run = await McpTools.ApplyPlanItems(e, null, plan0.ReadyDigest);
            _out.WriteLine("RUN: " + System.Text.Json.JsonSerializer.Serialize(run));
            await Step("after Run the plan");

            // The assistant works the queue until it is empty.
            var seen = new HashSet<string>();
            for (var round = 0; round < 8; round++)
            {
                var q = await McpTools.ListAssistantRequests(e);
                _out.WriteLine($"-- round {round}: {q.Requests.Length} offered, {q.Held.Length} held, {q.NotOffered.Length} not offered: " +
                               string.Join(" ; ", q.NotOffered.Select(n => n.Id + " waits " + n.WaitsFor)));
                if (q.Requests.Length == 0) break;
                foreach (var r in q.Requests)
                {
                    _out.WriteLine($"   REQ {r.RequestId} | {r.Need}");
                    if (!seen.Add(r.RequestId)) problems.Add("OFFERED AGAIN: " + r.RequestId + " " + r.Need);
                    var c = await McpTools.ClaimRequest(e, r.RequestId);
                    if (!c.Ok) { problems.Add("CLAIM refused " + r.RequestId + ": " + c.Message); continue; }
                    await Step("claimed " + r.Need);
                    var planNow = await e.GetModelPlanAsync();
                    // A queued task carries no rule list; the row it came from (same title) does.
                    var row = plan0.Items.FirstOrDefault(i => i.Title == r.Need && (i.Rules?.Length > 0 || i.Id == "cleanup/model" || i.Id == LocalEngine.CompatItemId || i.Id.StartsWith("calendar/") || i.Id.StartsWith("datetable/")))
                              ?? planNow.Items.FirstOrDefault(i => i.Title == r.Need && i.Rules?.Length > 0)
                              ?? planNow.Items.FirstOrDefault(i => i.Id == r.RequestId);
                    var packet = (c.Request?.Brief ?? "") + " || " + (c.Request?.FullText ?? "");
                    _out.WriteLine("      packet: " + (packet.Length > 900 ? packet.Substring(0, 900) : packet));
                    _out.WriteLine("      row: " + (row?.Id ?? "(none)") + " objs=" + string.Join(",", r.Objects ?? Array.Empty<string>()));
                    if (row != null)
                    {
                        var un = await WorkRowAsync(e, row);
                        foreach (var g in un.GroupBy(u => u)) problems.Add("UNHANDLED x" + g.Count() + " on " + row.Id + ": " + g.Key);
                    }
                    var d = await McpTools.CompleteRequest(e, r.RequestId, c.Request.ClaimToken, "Done.");
                    if (!d.Ok) problems.Add("COMPLETE refused " + r.RequestId + ": " + d.Message);
                    await Step("done " + r.Need);
                }
            }

            var (bpa1, air1, plan1) = await ReadAsync(e, which + " AFTER");
            await Step("end");
            // The numbers counted by hand from the rows the Plan lists (fix-coverage3; the build before it said 7 of 19 and 12 of 18).
            // The gappy model is built in memory, so its list also carries the Save row, which the strip leaves out (hero 26 N9): 16 rows, 15 tasks.
            // The aw run now ends with unsaved edits too, so its list carries the Save row as well: 17 rows, 16 tasks.
            var (wantDone, wantAll) = which == "aw" ? (6, 16) : (10, 15);
            if (plan1.Strip?.TasksDone != wantDone || plan1.Strip?.TasksAll != wantAll)
                problems.Add($"INVARIANT strip: Plan done {plan1.Strip?.TasksDone} of {plan1.Strip?.TasksAll}, counted by hand from the rows {wantDone} of {wantAll}");
            if (!(bpa1.BestPracticeScore > bpa0.BestPracticeScore)) problems.Add($"BPA did not rise: {bpa0.BestPracticeScore} -> {bpa1.BestPracticeScore}");
            if (!(air1.Overall > air0.Overall)) problems.Add($"AI did not rise: {air0.Overall} -> {air1.Overall}");
            var open1 = plan1.Items.Where(i => PlanStateWords.IsOpen(i.StateWord)).ToArray();
            foreach (var i in open1) problems.Add("STILL OPEN: " + i.Id + " doer=" + i.Doer + " " + i.StateWord + " " + i.Title + " note=" + i.StateNote);
            if (plan1.Strip != null && plan1.Strip.TasksDone != plan1.Strip.TasksAll) problems.Add($"Plan done {plan1.Strip.TasksDone}/{plan1.Strip.TasksAll}");

            // 2. Grades and Plan tell the same story.
            var bpaOpen = bpa1.Violations.Where(v => !v.Waived && v.Severity >= 2).ToArray();
            var airOpen = air1.Findings.Where(x => Scored(air1, x)).ToArray();
            if (open1.Length == 0 && (bpaOpen.Length > 0 || airOpen.Length > 0))
                problems.Add("NO OPEN ROWS but scored findings: " + string.Join("; ", bpaOpen.Select(v => v.RuleId + " " + v.ObjectRef).Concat(airOpen.Select(x => x.RuleId + " " + x.ObjectRef))));
            if ((bpa1.BestPractice != "A" || air1.Grade != "A") && open1.Length == 0)
                problems.Add($"grade below A ({bpa1.BestPractice}/{air1.Grade}) with no open row");

            // Second build and second run: nothing comes back.
            await McpTools.ApplyPlanItems(e, null, plan1.ReadyDigest);
            var (_, _, plan2) = await ReadAsync(e, which + " SECOND");
            await Step("second run");
            foreach (var i in plan2.Items.Where(i => PlanStateWords.IsOpen(i.StateWord) && !open1.Any(o => o.Id == i.Id)))
                problems.Add("CAME BACK: " + i.Id + " " + i.Title);

            // Every finding still lowering a grade, and the row that holds it.
            foreach (var v in bpaOpen) _out.WriteLine($"LEFT bpa {v.RuleId} {v.ObjectRef} -> " + (open1.FirstOrDefault(i => RowNames(i, v.RuleId, v.ObjectRef) || (i.Id == "cleanup/model" && (i.Objects ?? Array.Empty<string>()).Contains(v.ObjectRef, StringComparer.OrdinalIgnoreCase)))?.Id ?? "NO ROW"));
            foreach (var x in airOpen) _out.WriteLine($"LEFT air {x.RuleId} {x.ObjectRef} -> " + (open1.FirstOrDefault(i => RowNames(i, x.RuleId, x.ObjectRef))?.Id ?? "NO ROW"));
            foreach (var v in bpaOpen.Where(v => !open1.Any(i => RowNames(i, v.RuleId, v.ObjectRef) || (i.Id == "cleanup/model" && (i.Objects ?? Array.Empty<string>()).Contains(v.ObjectRef, StringComparer.OrdinalIgnoreCase)))))
                problems.Add("LEFT-ON-NO-ROW bpa " + v.RuleId + " " + v.ObjectRef);
            foreach (var x in airOpen.Where(x => !open1.Any(i => RowNames(i, x.RuleId, x.ObjectRef))))
                problems.Add("LEFT-ON-NO-ROW air " + x.RuleId + " " + x.ObjectRef);

            // One job is one row on the Plan (Kane, 13 Oct): a finished task is not listed again as the row it took over, and a task
            // that said done beside its reopened row is the row. A later finding of the same kind is a new job and may share a title.
            if (which == "aw")
                foreach (var g in plan1.Items.GroupBy(i => i.Title).Where(g => g.Count() > 1))
                    problems.Add("LISTED-TWICE " + g.Key + " :: " + string.Join(" | ", g.Select(i => i.Id + " " + i.StateWord)));
            foreach (var g in problems.GroupBy(p => p.Length > 60 ? p.Substring(0, 60) : p)) _out.WriteLine("PROBLEM x" + g.Count() + " " + g.First());
            // The scripted assistant writes no words and no formulas, so rows it cannot work stay open (UNHANDLED, STILL OPEN, Plan done, CAME BACK
            // when the second Run hands a still-open row over again). Those are the point: an open row, not a missing one. What fails the test is a
            // refusal, a finding on no row, a person's row before the run, or a grade that did not rise.
            var hard = problems.Where(p => p.StartsWith("UNCOVERED") || p.StartsWith("ON-YOU") || p.StartsWith("CLAIM refused") || p.StartsWith("COMPLETE refused")
                || p.StartsWith("OFFERED AGAIN") || p.StartsWith("BPA did not rise") || p.StartsWith("AI did not rise") || p.StartsWith("NO OPEN ROWS")
                || p.StartsWith("grade below A") || p.StartsWith("LEFT-ON-NO-ROW") || p.StartsWith("LISTED-TWICE") || p.StartsWith("INVARIANT")).ToArray();
            Assert.True(hard.Length == 0, string.Join("\n", hard));
        }

        [Fact] public Task V_gappy_outcome() => DriveAsync("gappy");

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact] public Task V_adventureworks_outcome() => DriveAsync("aw");

        /// <summary>Review page: the Fix N count equals what Fix all changes, on both models.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory, InlineData("gappy"), InlineData("aw")]
        public async Task V_fix_n_equals_fix_all(string which)
        {
            using var f = await OpenAsync(which);
            var e = f.Engine;
            var before = await e.BpaScanAsync();
            var r = await e.BpaFixAllAsync("human");
            _out.WriteLine($"{which}: Fix {before.AutoFixable}; Fix all applied {r.Applied}; after AutoFixable {r.Scorecard.AutoFixable}; withheld left {r.Scorecard.Violations.Count(v => v.FixWithheld && !v.Waived)}");
            Assert.True(before.AutoFixable == 0 || r.Applied > 0, "a Fix N button with N>0 changed nothing");
            Assert.True(r.Applied >= before.AutoFixable, $"Fix {before.AutoFixable} but Fix all made {r.Applied}");
        }
            /// <summary>The second model: every open scored best-practice finding is on an open row (or a twin row), before anything runs.</summary>
        [Fact]
        public async Task V_every_scored_bpa_finding_on_adventureworks_is_on_a_row()
        {
            using var f = await OpenAsync("aw");
            var e = f.Engine;
            var bpa = await e.BpaScanAsync();
            var plan = await e.GetModelPlanAsync();
            var open = plan.Items.Where(i => PlanStateWords.IsOpen(i.StateWord)).ToArray();
            var orphan = bpa.Violations.Where(v => !v.Waived && v.Severity >= 2)
                .Where(v => !open.Any(i => RowNames(i, v.RuleId, v.ObjectRef))
                            && !open.Any(i => i.Id == "cleanup/model" && (i.Objects ?? Array.Empty<string>()).Contains(v.ObjectRef, StringComparer.OrdinalIgnoreCase)))
                .Select(v => $"{v.RuleId} {v.ObjectRef} (severity {v.Severity})").ToArray();
            Assert.True(orphan.Length == 0, "scored findings on no row and with no reason: " + string.Join("; ", orphan));
        }

        // ---- focused probes of what the drive found ------------------------------------------------------------

        private async Task<(LocalEngine e, DecisionFixture f)> GappyRunAsync()
        {
            var f = await OpenAsync("gappy");
            var e = f.Engine;
            var plan = await e.GetModelPlanAsync();
            await McpTools.ApplyPlanItems(e, null, plan.ReadyDigest);
            return (e, f);
        }

        /// <summary>The cleanup row is the assistant's now. The assistant claims it, removes the column it names, and says done.</summary>
        [Fact]
        public async Task V_cleanup_task_can_be_completed_after_the_column_is_removed()
        {
            var (e, f) = await GappyRunAsync();
            using var _ = f;
            var inv = new PlanInvariant(e, _out);
            await inv.CheckAsync("cleanup: after Run the plan");
            var req = (await McpTools.ListAssistantRequests(e)).Requests.Single(r => r.Need.StartsWith("Remove ", StringComparison.Ordinal));
            _out.WriteLine("objects: " + string.Join(",", req.Objects));
            var c = await McpTools.ClaimRequest(e, req.RequestId);
            Assert.True(c.Ok, c.Message);
            await e.DeleteObjectsAsync(req.Objects, "agent");
            var d = await McpTools.CompleteRequest(e, req.RequestId, c.Request.ClaimToken, "Removed Sales[Load Note]; nothing used it.");
            var row = (await inv.CheckAsync("cleanup: after done")).Items.FirstOrDefault(i => i.Id == req.RequestId);
            _out.WriteLine($"complete ok={d.Ok} msg={d.Message} | row {row?.StateWord} note={row?.StateNote}");
            Assert.True(d.Ok, "complete_request refused after the removal the task asked for: " + d.Message);
            Assert.False(PlanStateWords.IsOpen(row?.StateWord ?? PlanStateWords.Done), "row still open: " + row?.StateWord);
        }

        private static async Task FixCoverageFourAsync(LocalEngine e, bool template)
        {
            await e.BpaFixAsync("AVOID_FLOATING_POINT_DATA_TYPES", "column:Sales/Amount", "agent");
            foreach (var rel in (await e.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == "REL-BIDI"))
                await e.SetObjectPropertyAsync(rel.ObjectRef, "CrossFilteringBehavior", "OneDirection", "agent");
            await e.DeleteObjectsAsync(new[] { "column:Sales/Batch Id" }, "agent");
            if (template) { await McpTools.SetCompatibilityLevel(e, 1701); await McpTools.DefineCalendarFromTemplate(e, "gregorian", "Date"); }
            else
            {
                await e.CreateTableAsync("Date", "agent");
                await e.CreateColumnAsync("table:Date", "Date", "DateTime", null, "agent");
            }
            await e.MarkDateTableAsync("table:Date", "Date", "agent");
        }

        /// <summary>A new scored finding appears while the coverage task is with the assistant (the person adds a column in Studio, or the
        /// assistant's own fix adds one). After the assistant says done, the Plan must still show an open row for it.</summary>
        [Theory, InlineData("person-adds-column"), InlineData("calendar-template"), InlineData("added-after-done")]
        public async Task V_a_new_finding_after_the_coverage_handoff_still_has_an_open_row(string how)
        {
            var (e, f) = await GappyRunAsync();
            using var _ = f;
            var inv = new PlanInvariant(e, _out);
            await inv.CheckAsync(how + ": after Run the plan");
            var req = (await McpTools.ListAssistantRequests(e)).Requests.Single(r => r.Need.Contains("best-practice"));
            var c = await McpTools.ClaimRequest(e, req.RequestId);
            Assert.True(c.Ok, c.Message);
            await inv.CheckAsync(how + ": claimed");
            await FixCoverageFourAsync(e, how == "calendar-template");
            if (how == "person-adds-column") await e.CreateColumnAsync("table:Sales", "Discount", "Double", null, "human");
            await inv.CheckAsync(how + ": fixed, not yet said done");
            var d = await McpTools.CompleteRequest(e, req.RequestId, c.Request.ClaimToken, "Fixed the four.");
            Assert.True(d.Ok, d.Message);
            await inv.CheckAsync(how + ": said done");
            if (how == "added-after-done")
            {
                var mid = (await e.GetModelPlanAsync()).Items.FirstOrDefault(i => i.Id == req.RequestId);
                _out.WriteLine("task after complete: " + mid?.StateWord);
                await e.CreateColumnAsync("table:Sales", "Discount", "Double", null, "human");
            }
            var (bpa, air, plan) = await ReadAsync(e, "after " + how);
            await inv.CheckAsync(how + ": end");
            var open = plan.Items.Where(i => PlanStateWords.IsOpen(i.StateWord)).ToArray();
            var scored = bpa.Violations.Where(v => !v.Waived && v.Severity >= 2).ToArray();
            // Only the findings on objects that did not exist at hand-off (other rows' queued tasks carry no rule list to read).
            var fresh = new[] { "column:Sales/Discount", "column:Date/", "table:Date", "obj:Model/Model" };
            var orphan = scored.Where(v => fresh.Any(p => (v.ObjectRef ?? "").StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .Where(v => !open.Any(i => RowNames(i, v.RuleId, v.ObjectRef))).Select(v => v.RuleId + " " + v.ObjectRef).ToArray();
            var queue = await McpTools.ListAssistantRequests(e);
            _out.WriteLine("queue offers: " + string.Join(" ; ", queue.Requests.Select(r => r.Need)));
            Assert.True(orphan.Length == 0, $"Model quality {bpa.BestPractice} {bpa.BestPracticeScore}: scored findings on no open row: " + string.Join("; ", orphan));
        }
    }
}

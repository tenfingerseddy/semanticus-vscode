// Verifier tests for the ai.schema family (M4 point 5, what Copilot sees), lane m11/m4-aischema.
//
// Each test attacks one promise the lane makes: a person's own choice is not reversed by "the rule" when Jev is not
// sure; no value or formula text reaches a request; consent gates every ask; one undo puts back a whole approved row;
// and a set of lines checked together across tables is never approved in an order that breaks the schema's own rule.
//
// THE FAMILY ASKS ONE TABLE AT A TIME (m11/jev-batch). A request holds a table's objects (in chunks) and one question for each,
// named "ai_schema:<ref>". So a recorded answer is keyed by the object's ref (ByObjectRecordedOracle), a fake service answers
// question by question, and what the tests count is REQUESTS (one per table chunk) and objects (what the cap and the progress
// line count). The shared model has 2 tables (Customer holds 4 objects, Sales 6), so the tests that need "some chunks asked,
// some not" add small tables (MoreTables) to have more chunks than answers.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class AiSchemaVerifyTests
    {
        private const string Point = "ai.schema";
        private const string Prefix = "jev.ai-schema.";

        /// <summary>The checkout this test run came from, for the one test here that reads an engine source. Its own,
        /// as every other source-reading test in this suite has its own, and that is not duplication for its own
        /// sake: the coverage oracle credits an MCP operation to a test file by looking for the operation's name
        /// anywhere in that file's text, and one operation's name hides inside the name of another test class here,
        /// so calling that class from this file would credit an operation this file never exercises.</summary>
        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Semanticus.sln"))
                    && System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Semanticus.Engine", "Decisions", "DecisionAnswerStore.cs")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException(
                "This test reads the engine's own source and could not find the checkout above " + AppContext.BaseDirectory);
        }

        private static async Task<DecisionFixture> ModelAsync(string file, bool pro = true, Func<LocalEngine, Task> extra = null)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            var e = f.Engine;
            // These tests count requests, refusals and budgets request by request, and were written for one request a table. The model's
            // whole-model packing is JevBatchAcrossTablesTests'.
            e.JevPerTableForTest = true;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "Customer Name", "String", null, "human");
            await e.CreateColumnAsync("table:Customer", "Region", "String", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "LineKey", "Int64", null, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Order Lines", "COUNTROWS ( Sales )", "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
            if (extra != null) await extra(e);
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        /// <summary>Extra small tables (a table and one column each: 2 objects, one chunk), so a model has more chunks than a test's
        /// service answers.</summary>
        private static Func<LocalEngine, Task> MoreTables(int count, string prefix = "Extra") => async e =>
        {
            for (var i = 1; i <= count; i++)
            {
                var name = prefix + i.ToString(CultureInfo.InvariantCulture);
                await e.CreateTableAsync(name, "human");
                await e.CreateColumnAsync("table:" + name, "Note", "String", null, "human");
            }
        };

        /// <summary>The batches the family sends for the model as it stands: (objects, request estimate for the direct vendor route).</summary>
        private static async Task<List<TableBatch<AiSchemaFamily.Ask>>> BatchesAsync(DecisionFixture f)
        {
            var prepared = await f.Sessions.Current.ReadAsync(m => AiSchemaFamily.Prepare(m));
            return AiSchemaFamily.Batches(prepared, perTable: true);   // these tests count requests one a table (ModelAsync switches the engine to it)
        }

        private static int EstimateOf(TableBatch<AiSchemaFamily.Ask> b, bool relay = false)
        {
            var r = b.RequestFor(DecisionRegistry.Require(Point));
            var snapshot = DecisionDigest.CanonicalSnapshotJson(r.Snapshot);
            return JevDecisionOracle.EstimateInputTokens(relay
                ? JevDecisionOracle.BuildRelayBody(r, snapshot, new string('0', 64))
                : JevDecisionOracle.BuildBody(r, snapshot));
        }

        /// <summary>The answers a fake vendor or relay gives to a batched request body: "leave it out, sure" for every ai.schema
        /// question in it (the ranking's questions get nothing, as before). Also how many objects it answered.</summary>
        private static (string Answers, int Objects) ExcludeAnswers(string requestBody)
        {
            var sb = new StringBuilder("{");
            var n = 0;
            using var doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Object)
                foreach (var q in qs.EnumerateObject())
                {
                    if (!q.Name.StartsWith("ai_schema:", StringComparison.Ordinal)) continue;
                    if (n++ > 0) sb.Append(',');
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(":{\"type\":\"choice\",\"choice\":\"exclude\",\"confidence\":0.95}");
                }
            return (sb.Append('}').ToString(), n);
        }

        private sealed class CapturingOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public CapturingOracle(IDecisionOracle inner) { _inner = inner; }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> Seen { get { lock (_seen) return _seen.ToList(); } }
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
                return _inner.AskAsync(request, ct);
            }
        }

        /// <summary>Answers EVERY ai.schema request, whatever its digest, so a snapshot that changes after an edit is
        /// answered too. pick sees each object's table name and own name (from the request's `objects`, one question for each,
        /// m11/jev-batch) and returns the choice.</summary>
        private sealed class AnyDigestOracle : IDecisionOracle
        {
            private readonly Func<string, string, (string Choice, double Confidence)> _pick;
            public AnyDigestOracle(Func<string, string, (string, double)> pick) { _pick = pick; }
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PointName != Point) return Task.FromResult(DecisionAnswers.NotDecided(request, "not asked in this test"));
                using var objects = JsonDocument.Parse(JsonSerializer.Serialize(request.Snapshot["objects"]));
                var names = objects.RootElement.EnumerateArray().Select(o => o.GetProperty("name").GetString()).ToArray();
                var tables = objects.RootElement.EnumerateArray().Select(o => o.GetProperty("table").GetString()).ToArray();   // each entry names its table
                var body = new StringBuilder("{");
                for (var i = 0; i < request.Questions.Count; i++)
                {
                    var (choice, confidence) = _pick(tables[i], names[i]);   // the question at i is about `objects[i]`
                    if (i > 0) body.Append(',');
                    body.Append(JsonSerializer.Serialize(request.Questions[i].Name)).Append(":{\"type\":\"choice\",\"choice\":")
                        .Append(JsonSerializer.Serialize(choice)).Append(",\"confidence\":")
                        .Append(confidence.ToString(CultureInfo.InvariantCulture)).Append('}');
                }
                body.Append('}');
                return Task.FromResult(DecisionAnswerReader.Read(request, JsonDocument.Parse(body.ToString()).RootElement, "jev-test-1", 500, 20));
            }
        }

        /// <summary>A service that is set up but gives no answer (a timeout, an error, an exhausted allowance).</summary>
        private sealed class SilentOracle : IDecisionOracle
        {
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) =>
                Task.FromResult(DecisionAnswers.NotDecided(request, "the service did not answer"));
        }

        private static async Task UseAnyDigestAsync(DecisionFixture f, Func<string, string, (string, double)> pick)
        {
            f.Engine.AiSchemaJevDecidesForTest = true;
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnCategory, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");
            f.Engine.UseDecisionsForTest(new DecisionService(new AnyDigestOracle(pick), new DecisionBudget()));
        }

        private static string Fixture(IEnumerable<(string Digest, string Choice, double Confidence)> rows)
        {
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (digest, choice, confidence) in rows)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
                  .Append(",\"point\":\"").Append(Point).Append('"')
                  .Append(",\"usage\":{\"input_tokens\":500,\"output_tokens\":20}")
                  .Append(",\"answers\":{\"ai_schema\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                  .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append("}}}");
            }
            return sb.Append("]}").ToString();
        }

        /// <summary>Record Jev's answers for the family's real requests (everything not named: include, sure), switch the
        /// other families off, and hand back the capturing oracle. Consent is the caller's.</summary>
        private static async Task<CapturingOracle> AnswerAsync(DecisionFixture f,
            IReadOnlyDictionary<string, (string Choice, double Confidence)> picks)
        {
            f.Engine.AiSchemaJevDecidesForTest = true;
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnCategory, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");
            var prepared = await f.Sessions.Current.ReadAsync(m => AiSchemaFamily.Prepare(m));
            // One entry for each object, keyed by its ref: the family asks a table's objects at once (m11/jev-batch).
            var rows = prepared.Asks.Select(a =>
                picks.TryGetValue(a.Ref, out var p) ? (a.Ref, p.Choice, p.Confidence) : (a.Ref, "include", 0.95)).ToList();
            var capturing = new CapturingOracle(new ByObjectRecordedOracle(Fixture(rows), "jev-test-1"));
            f.Engine.UseDecisionsForTest(new DecisionService(capturing, new DecisionBudget()));
            return capturing;
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        private static Task<string[]> ExcludedAsync(DecisionFixture f) =>
            f.Sessions.Current.ReadAsync(m => ReadinessRuleSet.AiSchemaExcluded(m)
                .Select(ObjectRefs.For).OrderBy(r => r, StringComparer.Ordinal).ToArray());

        private static IEnumerable<ModelPlanChangeLine> Lines(ModelPlanView plan) =>
            plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>());

        // ---- 1. the rule has no opinion on what a person already left out --------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_persons_own_exclusion_is_not_put_back_by_the_rule_when_Jev_is_not_sure()
        {
            using var f = await ModelAsync("VerifyOwnChoice.bim");
            // The person left Region out of what Copilot sees. No readiness rule calls Region noise, and none says
            // to put a field back: today's rules have no opinion on it.
            await f.Engine.SetAiDataSchemaAsync("column:Customer/Region", false, null, "human");
            await f.ConsentAsync();
            // Jev leans the person's way but is only "likely", so the Pro path hands Region to the rule.
            await AnswerAsync(f, new Dictionary<string, (string, double)> { ["column:Customer/Region"] = ("exclude", 0.7) });

            var plan = await SettledPlanAsync(f);

            var region = Lines(plan).Where(l => l.Target == "column:Customer/Region").ToArray();
            Assert.True(region.Length == 0,
                "The person's own exclusion of Region comes back as a line: " + string.Join(" | ", region.Select(l =>
                    $"{l.Before}->{l.After} kept={l.Kept} provenance={l.Provenance?.Kind}/{l.Provenance?.Rule} reason=\"{l.Reason}\"")));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_row_whose_every_line_is_the_rules_does_not_say_Jev_chose_it()
        {
            using var f = await ModelAsync("VerifyRuleOnly.bim");
            await f.ConsentAsync();
            // Jev is not sure about the join key, so the rule decides it; everything else Jev keeps as it is.
            await AnswerAsync(f, new Dictionary<string, (string, double)> { ["column:Sales/CustomerKey"] = ("include", 0.5) });

            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == Prefix + "table:Sales");

            var line = Assert.Single(sales.ChangeSet.Lines);
            Assert.Equal(ModelPlanChangeKinds.Rule, line.Provenance.Kind);
            Assert.DoesNotContain(sales.Why, w => w.StartsWith("Jev chose each setting", StringComparison.Ordinal));
        }

        [Fact]
        public void The_low_confidence_behaviour_the_agent_door_reads_matches_the_Pro_path()
        {
            // get_decision_points hands this sentence to the assistant. On the Pro path a pick below the bar is not a
            // dropped line: the rule's answer replaces it and arrives kept (AiSchemaFamily.ProPick).
            var said = DecisionRegistry.Require(Point).LowConfidenceBehaviour;
            Assert.DoesNotContain("arrives dropped", said, StringComparison.Ordinal);
        }

        // ---- 2. no value and no formula text in any request ----------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task No_literal_formula_text_description_or_folder_reaches_a_request()
        {
            using var f = await ModelAsync("VerifyNoValues.bim", extra: async e =>
            {
                await e.CreateMeasureAsync("table:Sales", "Big Order Flag",
                    "IF ( SUM ( Sales[Amount] ) > 7654321, \"ZQXLITERALONE\", \"ZQXLITERALTWO\" )", "human", "ZQXFOLDER");
                await e.CreateCalculatedColumnAsync("table:Customer", "Customer Label", "\"ZQXCALC\" & Customer[Region]", "human");
                await e.SetDescriptionAsync("column:Customer/Customer Name", "ZQXDESCRIPTION the name on the invoice", "human");
            });
            await f.ConsentAsync();
            var oracle = await AnswerAsync(f, new Dictionary<string, (string, double)>());

            await SettledPlanAsync(f);

            var sent = oracle.Seen.Where(r => r.PointName == Point).ToArray();
            Assert.NotEmpty(sent);
            Assert.Contains(sent, r => JsonSerializer.Serialize(r.Snapshot).Contains("Big Order Flag", StringComparison.Ordinal));
            foreach (var r in sent)
            {
                var json = JsonSerializer.Serialize(r.Snapshot);
                Assert.DoesNotContain("ZQX", json, StringComparison.Ordinal);
                Assert.DoesNotContain("7654321", json, StringComparison.Ordinal);
                Assert.DoesNotContain("IF (", json, StringComparison.Ordinal);
            }
        }

        // ---- 3. consent ------------------------------------------------------------------------------------------

        [Fact]
        public async Task Without_consent_nothing_is_asked_and_there_is_no_row()
        {
            using var f = await ModelAsync("VerifyNoConsent.bim");
            var oracle = await AnswerAsync(f, new Dictionary<string, (string, double)>());

            var plan = await SettledPlanAsync(f);

            Assert.DoesNotContain(oracle.Seen, r => r.PointName == Point);
            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith(Prefix, StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_permission_given_before_ai_schema_existed_does_not_cover_it()
        {
            using var f = await ModelAsync("VerifyOldConsent.bim");
            var before = DecisionRegistry.DeclaredFields.Where(x => !x.StartsWith(Point + ":", StringComparison.Ordinal)).ToArray();
            Assert.True(before.Length < DecisionRegistry.DeclaredFields.Count);
            await f.Engine.SetConsentAsync(before, DecisionRegistry.ConsentSchemaVersion, "human");
            var oracle = await AnswerAsync(f, new Dictionary<string, (string, double)>());

            var plan = await SettledPlanAsync(f);
            var info = await f.Engine.GetConsentAsync();

            Assert.DoesNotContain(oracle.Seen, r => r.PointName == Point);
            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith(Prefix, StringComparison.Ordinal));
            Assert.False(info.Current);
            Assert.Contains(Point + ":", info.Reason ?? "", StringComparison.Ordinal);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Withdrawing_consent_takes_the_rows_away()
        {
            using var f = await ModelAsync("VerifyWithdraw.bim");
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)> { ["column:Sales/LineKey"] = ("exclude", 0.95) });
            var plan = await SettledPlanAsync(f);
            Assert.Contains(plan.Items, i => i.Id.StartsWith(Prefix, StringComparison.Ordinal));

            await f.Engine.WithdrawConsentAsync("human");
            var after = await SettledPlanAsync(f);

            Assert.DoesNotContain(after.Items, i => i.Id.StartsWith(Prefix, StringComparison.Ordinal));
        }

        // ---- 4. one undo, one redo, and a second approve of the same digest ---------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task One_undo_puts_back_a_whole_approved_row_redo_reapplies_and_the_old_digest_is_refused()
        {
            using var f = await ModelAsync("VerifyUndo.bim");
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)>
            {
                ["column:Sales/CustomerKey"] = ("exclude", 0.95),
                ["column:Sales/LineKey"] = ("exclude", 0.95),
                ["measure:Sales/Order Lines"] = ("exclude", 0.95),
            });
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == Prefix + "table:Sales");
            Assert.Equal(3, sales.ChangeSet.Lines.Count(l => l.Kept));

            var approved = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest);
            Assert.True(approved.Performed, approved.Sentence);
            var want = new[] { "column:Sales/CustomerKey", "column:Sales/LineKey", "measure:Sales/Order Lines" };
            Assert.Equal(want, await ExcludedAsync(f));

            var again = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest);
            Assert.False(again.Performed, "a second approve of the same digest applied again");

            await f.Engine.UndoAsync("human");
            Assert.Empty(await ExcludedAsync(f));

            await f.Engine.RedoAsync("human");
            Assert.Equal(want, await ExcludedAsync(f));
        }

        // ---- 5. lines checked together across tables, approved row by row ---------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Putting_things_back_across_two_tables_never_leaves_a_seen_measure_using_a_hidden_field()
        {
            using var f = await ModelAsync("VerifyAcross.bim", extra: async e =>
            {
                await e.CreateTableAsync("Product", "human");
                await e.CreateColumnAsync("table:Product", "List Price", "Double", null, "human");
                await e.CreateColumnAsync("table:Product", "Product Name", "String", null, "human");
                await e.CreateMeasureAsync("table:Sales", "Average List Price", "AVERAGE ( Product[List Price] )", "human");
            });
            // The person left the measure and the field it uses out together, which the schema's rule allows.
            await f.Engine.SetAiDataSchemaAsync("measure:Sales/Average List Price", false, null, "human");
            await f.Engine.SetAiDataSchemaAsync("column:Product/List Price", false, null, "human");
            await f.ConsentAsync();
            // Jev is sure both should be seen again (and answers again after any edit). Resolve checks them as one
            // set, so both are lines.
            await UseAnyDigestAsync(f, (table, name) => ("include", 0.95));

            var plan = await SettledPlanAsync(f);
            var rows = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
            Assert.Contains(rows, i => i.ChangeSet.Lines.Any(l => l.Target == "measure:Sales/Average List Price"));
            Assert.Contains(rows, i => i.ChangeSet.Lines.Any(l => l.Target == "column:Product/List Price"));

            // AMENDED BY THE COORDINATOR PER D1 (26 Sep). This test was written before D1: a person's own exclusions are
            // their intent, so a put-back of either arrives DROPPED and applies only once the person keeps it. The person
            // here wants both back, so they keep both lines first, as they would on the page. The assertion is unchanged.
            foreach (var line in rows.SelectMany(i => i.ChangeSet.Lines)
                         .Where(l => l.Target == "measure:Sales/Average List Price" || l.Target == "column:Product/List Price"))
            {
                Assert.False(line.Kept, "a put-back of the person's own exclusion arrived kept: " + line.Target);
                await f.Engine.SetPlanItemAsync(line.PlanItemId, null, true, "human");
            }
            plan = await SettledPlanAsync(f);

            // A person works the Plan in the order it offers: whatever Semanticus row is Ready goes first (Run the
            // plan does the same), until a Copilot row is Ready. Then they approve that one, as the page lets them.
            ModelPlanItem first = null;
            var done = new List<string>();
            for (var step = 0; step < 12 && first == null; step++)
            {
                first = plan.Items.FirstOrDefault(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal) && i.StateWord == "ready");
                if (first != null) break;
                var next = plan.Items.FirstOrDefault(i => i.Doer == "semanticus" && i.StateWord == "ready");
                if (next == null)
                {
                    // A person's own row goes first and every Semanticus row waits for it (m11/plan-waits): this one wants the person's
                    // choice about relationships, so the person sets it aside for now, as they may, and the plan moves on.
                    // Rows that wait on an assistant to write words (describe and words rows) stand in front of the rules' format rows and
                    // the Copilot rows too; a person who has no assistant here sets those aside as well.
                    var yours = plan.Items.FirstOrDefault(i => !i.Id.StartsWith(Prefix, StringComparison.Ordinal)
                        && (i.StateWord == "needs_you" || (i.StateWord == "ready" && i.Doer == "assistant")));
                    if (yours != null)
                    {
                        await f.Engine.SkipModelPlanItemAsync(yours.Id, "not now", "human");
                        done.Add(yours.Id + " (set aside by the person)");
                        plan = await SettledPlanAsync(f);
                        continue;
                    }
                }
                Assert.True(next != null, "Nothing is Ready and no Copilot row can be reached: " + string.Join(" | ", plan.Items.Select(i =>
                    i.Id + " " + i.StateWord + " waits[" + string.Join(",", (i.WaitsFor ?? Array.Empty<PlanWaitRef>()).Select(w => w.Id)) + "]")));
                var r = next.ChangeSet?.Digest != null && next.ChangeSet.State == ModelPlanChangeStates.Proposed
                    ? await f.Engine.ModelPlanActionAsync(next.Id, "approve", "human", next.ChangeSet.Digest)
                    : await f.Engine.ModelPlanActionAsync(next.Id, "start", "human");
                done.Add(next.Id + (r.Performed ? "" : " (not performed: " + r.Sentence + ")"));
                plan = await SettledPlanAsync(f);
            }
            Assert.True(first != null, "No Copilot row became Ready after: " + string.Join(", ", done));
            // Read it again once Jev has settled on this revision, as the page does before it offers Approve.
            for (var i = 0; i < 5 && (first.ChangeSet.Stale || first.ChangeSet.StaleAtProposal); i++)
            {
                plan = await SettledPlanAsync(f);
                first = plan.Items.Single(x => x.Id == first.Id);
            }
            var result = await f.Engine.ModelPlanActionAsync(first.Id, "approve", "human", first.ChangeSet.Digest);
            Assert.True(result.Performed, result.Sentence + " (after: " + string.Join(", ", done) + "; row " + first.Id
                + " stale " + first.ChangeSet.Stale + "/" + first.ChangeSet.StaleAtProposal + " " + first.ChangeSet.StaleReason + ")");

            var scan = await f.Engine.AiReadinessScanAsync();
            var broken = scan.Findings.Where(x => x.RuleId == "AISCHEMA-DEP-MISSING").ToArray();
            Assert.True(broken.Length == 0,
                "Approving the Ready row " + first.Id + " first left the schema breaking its own rule: "
                + string.Join(" | ", broken.Select(x => x.ObjectRef + " " + x.Message)));
        }

        // ---- 6. Jev gives no answer after the table changes ------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task When_Jev_gives_no_answer_after_an_edit_a_Copilot_row_is_not_left_Ready_with_lines_that_cannot_be_approved()
        {
            using var f = await ModelAsync("VerifySilent.bim");
            await f.ConsentAsync();
            await UseAnyDigestAsync(f, (table, name) => table == "Sales" && name == "LineKey" ? ("exclude", 0.95) : ("include", 0.95));
            var before = (await SettledPlanAsync(f)).Items.Single(i => i.Id == Prefix + "table:Sales");
            Assert.False(before.ChangeSet.Stale);

            // The service stops answering (an exhausted allowance looks the same), then a person adds a measure to
            // Sales. Every Sales field's request changes, because each one names the table's other fields.
            f.Engine.UseDecisionsForTest(new DecisionService(new SilentOracle(), new DecisionBudget()));
            await f.Engine.CreateMeasureAsync("table:Sales", "Average Amount", "AVERAGE ( Sales[Amount] )", "human");
            var plan = await SettledPlanAsync(f);
            plan = await SettledPlanAsync(f);

            var sales = plan.Items.SingleOrDefault(i => i.Id == Prefix + "table:Sales");
            if (sales == null) return;   // the row went away: nothing is offered that cannot be done
            var result = sales.StateWord == "ready"
                ? await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest)
                : null;
            Assert.True(sales.StateWord != "ready" || result.Performed,
                "The Sales row reads Ready with an Approve the engine refuses: stale=" + sales.ChangeSet.Stale
                + " (" + sales.ChangeSet.StaleReason + "); approve said: " + result?.Sentence);
        }

        // ---- 7. round 2 (26 Sep): what the answer store forgets, what the batches say, whose answer a late reply is,
        //      and what a dropped line is called. The first four fail on m11/m4-aischema 5369e884; the last two pass
        //      and stay as evidence (the generation tie and known answers after the budget runs out). ----

        /// <summary>A service that SPENDS the way the live client does: every ai.schema ask it sends is charged to the
        /// session's budget (500 input tokens each), so a small budget runs out part way through the batches.</summary>
        private sealed class SpendingOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private readonly DecisionBudget _budget;
            private int _asked;
            public SpendingOracle(IDecisionOracle inner, DecisionBudget budget) { _inner = inner; _budget = budget; }
            public bool Available => true;
            public int Asked => Volatile.Read(ref _asked);
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PointName == Point)
                {
                    Interlocked.Increment(ref _asked);
                    _budget.Record(500, 20);
                }
                return _inner.AskAsync(request, ct);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Withdrawing_permission_right_after_a_restart_forgets_the_answers_kept_for_that_model()
        {
            // Jev's answers about a model are kept on this computer (DecisionAnswerStore) and must be forgotten when the
            // person withdraws permission for that model (DecisionService.OnConsentWithdrawn calls Answers.Forget). It
            // forgets the model the service last ASKED about (_cacheScope), not the model whose permission is being
            // withdrawn, so after a restart, before Jev has been asked anything, a withdrawal forgets nothing.
            using var f = await ModelAsync("VerifyForgetRestart.bim");
            await f.ConsentAsync();
            var capturing = await AnswerAsync(f, new Dictionary<string, (string, double)> { ["column:Sales/LineKey"] = ("exclude", 0.95) });
            f.Engine.UseDecisionsForTest(new DecisionService(capturing, new DecisionBudget(), answers: new DecisionAnswerStore()));
            await SettledPlanAsync(f);
            var path = System.IO.Path.Combine(f.Workspace, "VerifyForgetRestart.bim");
            await f.Engine.SaveAsync(path, "bim", false, "human");   // the permission marker travels in the saved model
            var folder = System.IO.Path.Combine(ConnectionRegistry.Root(), "decision-answers");
            string[] Kept() => System.IO.Directory.Exists(folder)
                ? System.IO.Directory.GetFiles(folder, "*.jsonl").Where(p => new System.IO.FileInfo(p).Length > 0).ToArray()
                : Array.Empty<string>();
            Assert.NotEmpty(Kept());   // precondition: Jev's answers were kept on this computer

            // Restart: a new engine on this computer opens the same file, and the person withdraws permission at once.
            var sessions = new SessionManager();
            var again = new LocalEngine(sessions, TestEntitlements.Pro, f.Workspace);
            try
            {
                await again.OpenAsync(path);
                Assert.True((await again.GetConsentAsync()).Current, "precondition: the saved model still has its permission");
                await again.WithdrawConsentAsync("human");

                Assert.True(Kept().Length == 0,
                    "Permission was withdrawn, but the answers kept for this model are still on this computer: "
                    + string.Join(", ", Kept().Select(p => System.IO.Path.GetFileName(p) + " (" + System.IO.File.ReadAllLines(p).Length + " answers)")));
            }
            finally
            {
                again.Dispose();
                sessions.Dispose();
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task When_the_session_budget_runs_out_between_batches_every_row_still_says_Jev_has_not_looked_at_everything()
        {
            // What Copilot sees is asked in batches (AiSchemaFamily.MaxObjectsPerRun), and while objects are still to be
            // asked every row says how far Jev has got, "so a row is never read as the whole answer when it is not"
            // (LocalEngine.StampJevProgress). A large model runs out of the session's budget part way (200k input
            // tokens is about 200 asks). The next batch then asks nothing, the run is no longer partial, and the
            // progress line goes: the rows read as the whole answer although Jev never looked at the rest.
            // One request per table now (m11/jev-batch), so the model has four table chunks (Customer, Sales and two small tables)
            // and the budget is two requests at 500 input tokens each: it runs out before the third chunk.
            using var f = await ModelAsync("VerifyBudgetBatches.bim", extra: MoreTables(2));
            await f.ConsentAsync();
            var capturing = await AnswerAsync(f, new Dictionary<string, (string, double)> { ["column:Customer/CustomerKey"] = ("exclude", 0.95) });
            var budget = new DecisionBudget(capInputTokens: 1000);   // two requests at 500 input tokens each
            var spending = new SpendingOracle(capturing, budget);
            f.Engine.UseDecisionsForTest(new DecisionService(spending, budget));
            f.Engine.AiSchemaCapForTest = 3;
            var total = (await BatchesAsync(f)).Count;

            ModelPlanView plan = null;
            for (var i = 0; i < 8; i++) plan = await SettledPlanAsync(f);

            // Preconditions: the budget ran out before every chunk was asked, and Jev's first answers are on the plan.
            Assert.True(budget.Exhausted, "precondition: the session's budget ran out");
            Assert.True(spending.Asked < total, "precondition: Jev was asked " + spending.Asked + " of " + total + " requests");
            var rows = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(rows);

            foreach (var row in rows)
                Assert.True((row.Why ?? Array.Empty<string>()).Any(w => w.StartsWith("Jev has looked at", StringComparison.Ordinal)),
                    "Jev sent " + spending.Asked + " of " + total + " requests, but the row " + row.Id
                    + " reads as the whole answer: " + string.Join(" | ", row.Why ?? Array.Empty<string>()));
        }

        [Fact]
        public async Task A_reply_that_lands_after_another_model_was_opened_is_not_an_answer_for_that_model()
        {
            // b8b4e4c7 keeps a reply that lands after the model moved, for the same evidence. The session cache is one
            // per session, and "an answer bought for one model is not an answer for the next one" (DecisionService: a
            // change of model empties the cache). A reply for model A that lands after model B was opened, and B asked
            // once, now goes into B's cache, so B's first ask about the same evidence (a copy of A, say) is answered
            // with A's reply and never reaches the service.
            var request = DecisionFixture.SampleRequest();
            var point = DecisionRegistry.Require(request.PointName);
            var other = new DecisionRequest(point.Name, point.SchemaVersion, DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("measure_name", "Order Count"),
                new KeyValuePair<string, object>("measure_formula_facts", DecisionFixture.SampleFormulaFacts("COUNTROWS")),
                new KeyValuePair<string, object>("table_name", "Sales"),
                new KeyValuePair<string, object>("table_columns", new List<object>()),
                new KeyValuePair<string, object>("sibling_measures", new List<object>()),
            }), request.Questions);
            var oracle = new RecordedDecisionOracle(
                DecisionFixture.ProofFixture(request.Digest, "Volume", 0.93, "currency", 0.97), "jev-test-1");
            var scope = "disk:model-a";
            var session = "s-a";
            using var service = new DecisionService(oracle, readConsent: _ =>
                DecisionConsent.Grant(DecisionRegistry.DeclaredFields, DecisionRegistry.ConsentSchemaVersion, DateTimeOffset.UtcNow))
            { ReadScope = () => Task.FromResult(scope) };

            service.BeforeReturnForTest = () =>
            {
                // While A's ask is on its way, the person opens model B, and B asks about something first.
                service.BeforeReturnForTest = null;
                scope = "disk:model-b";
                session = "s-b";
                service.AskAsync(other.PointName, other.Snapshot, other.Questions, isPro: true, enabled: true,
                    fence: new DecisionSessionFence("s-b", 1, () => (session, 1)), ct: CancellationToken.None).GetAwaiter().GetResult();
            };
            var late = await service.AskAsync(request.PointName, request.Snapshot, request.Questions, isPro: true, enabled: true,
                fence: new DecisionSessionFence("s-a", 1, () => (session, 1)), ct: CancellationToken.None);
            Assert.False(late.Asked);   // dropped for A: another model was opened while it was on its way
            Assert.Equal(2, oracle.AskCount);

            // Model B holds an object with the same evidence. B never bought an answer for it.
            await service.AskAsync(request.PointName, request.Snapshot, request.Questions, isPro: true, enabled: true,
                fence: new DecisionSessionFence("s-b", 1, () => (session, 1)), ct: CancellationToken.None);
            Assert.True(oracle.AskCount == 3,
                "Model B's question was answered with the reply bought for model A, which landed after B was opened: the service was asked "
                + oracle.AskCount + " times, not 3");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task After_the_budget_runs_out_an_edit_elsewhere_keeps_the_answers_Jev_already_gave()
        {
            // "The answers already known (the session's cache, or kept on this computer) are read first and cost nothing"
            // (AiSchemaFamily.MaxObjectsPerRun). Once the session's budget is spent, an edit to another table leaves the
            // Customer objects' evidence as it was, so their answers are known and should still stand.
            using var f = await ModelAsync("VerifyBudgetEdit.bim");
            await f.ConsentAsync();
            var capturing = await AnswerAsync(f, new Dictionary<string, (string, double)> { ["column:Customer/CustomerKey"] = ("exclude", 0.95) });
            var budget = new DecisionBudget(capInputTokens: 1000);   // exactly the two requests this model needs (one per table, m11/jev-batch)
            var spending = new SpendingOracle(capturing, budget);
            f.Engine.UseDecisionsForTest(new DecisionService(spending, budget));
            var plan = await SettledPlanAsync(f);
            Assert.Contains(plan.Items, i => i.Id == Prefix + "table:Customer");
            Assert.True(budget.Exhausted, "precondition: the budget is spent");

            await f.Engine.CreateMeasureAsync("table:Sales", "Average Amount", "AVERAGE ( Sales[Amount] )", "human");
            plan = await SettledPlanAsync(f);
            plan = await SettledPlanAsync(f);

            var customer = plan.Items.SingleOrDefault(i => i.Id == Prefix + "table:Customer");
            Assert.True(customer != null && customer.ChangeSet.Lines.Any(l => l.Target == "column:Customer/CustomerKey"),
                "The Customer row went away after an edit to Sales, although Jev's answer for Customer[CustomerKey] is known: rows "
                + string.Join(", ", plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).Select(i => i.Id)));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Answers_kept_under_one_permission_are_not_served_after_another_engine_withdraws_and_grants_again()
        {
            using var f = await ModelAsync("VerifyGeneration.bim");
            await f.ConsentAsync();
            var capturing = await AnswerAsync(f, new Dictionary<string, (string, double)>());
            var store = new DecisionAnswerStore();
            f.Engine.UseDecisionsForTest(new DecisionService(capturing, new DecisionBudget(), answers: store));
            await SettledPlanAsync(f);
            var first = capturing.Seen.Count(r => r.PointName == Point);
            Assert.True(first > 0);
            var firstDigests = capturing.Seen.Where(r => r.PointName == Point).Select(r => r.Digest).ToHashSet(StringComparer.Ordinal);
            // Another engine on this computer (another window) withdraws permission for the same model and gives it again.
            // Its writes go to the one permission file every engine here reads (one process holds one TOM model, so the
            // other window's writes are made through the store itself).
            var scope = await f.Engine.DecisionConsentScopeAsync();
            DecisionConsentStore.Withdraw(scope, DateTimeOffset.UtcNow);
            DecisionConsentStore.Grant(scope, DecisionRegistry.DeclaredFields, DecisionRegistry.ConsentSchemaVersion, DateTimeOffset.UtcNow);

            // This engine's service still holds the old answers in memory and on disk. A fresh service with the same store
            // (the session cache is empty) must not serve them under the new permission.
            f.Engine.UseDecisionsForTest(new DecisionService(capturing, new DecisionBudget(), answers: store));
            await f.Engine.CreateColumnAsync("table:Customer", "Segment", "String", null, "human");
            await f.Engine.UndoAsync("human");
            await SettledPlanAsync(f);
            // Every question asked under the old permission is asked again under the new one: none is served from what
            // was kept before. Compared as the set of evidence asked about, not a count: the edit above starts a run for
            // the in-between revision, and how much of it goes out before the undo cancels it is timing (it was 10 or
            // 15 asks from run to run, fix-aischema-r2), while the set asked about the original evidence is not.
            var second = capturing.Seen.Where(r => r.PointName == Point).Skip(first).Select(r => r.Digest).ToHashSet(StringComparer.Ordinal);
            var served = firstDigests.Where(d => !second.Contains(d)).ToArray();
            Assert.True(served.Length == 0, served.Length + " of " + firstDigests.Count
                + " answers bought under the old permission were served under the new one, not asked again");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_line_the_person_dropped_is_not_described_as_putting_back_something_they_left_out()
        {
            // The row's why now names why a line arrives dropped: "Jev was unsure", or "puts back something you left out"
            // (JevFamily.Items, D1). It counts every dropped line that is not unsure as a put-back, so a line the PERSON
            // dropped is described as a put-back of their own exclusion. That shared sentence is on every Jev row.
            using var f = await ModelAsync("VerifyDroppedWhy.bim");
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)> { ["column:Sales/LineKey"] = ("exclude", 0.95) });
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == Prefix + "table:Sales");
            var line = sales.ChangeSet.Lines.Single(l => l.Target == "column:Sales/LineKey");
            Assert.True(line.Kept);
            Assert.Equal("Excluded", line.After);

            // The person wants Copilot to keep seeing LineKey, so they drop the line.
            await f.Engine.SetPlanItemAsync(line.PlanItemId, null, false, "human");
            sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == Prefix + "table:Sales");
            Assert.False(sales.ChangeSet.Lines.Single(l => l.Target == "column:Sales/LineKey").Kept);

            Assert.True(!sales.Why.Any(w => w.Contains("back something you left out", StringComparison.Ordinal)),
                "The only dropped line leaves LineKey OUT and the person dropped it, but the row says: " + string.Join(" | ", sales.Why));
        }

        [Fact]
        public async Task The_answers_kept_on_this_computer_hold_no_name_from_the_model()
        {
            // DecisionAnswerStore's header says the file holds "the answers as option ids and confidences. No name, no
            // formula, no model content". An option id can BE model content: the measure point's folder options are
            // "folder:" and the folder's own name (MeasureDefaultsPoint.FolderOptionId), and the store writes the chosen
            // id and every probability's id. On lane p (verify-r2) the kept answers held "folder:Sales" 23 times, and a
            // withdrawal that forgets the wrong model (the test above) leaves them there.
            using var f = new DecisionFixture();
            var request = DecisionFixture.SampleRequest();   // its folder option is the model's own folder, "Volume"
            var answers = await new RecordedDecisionOracle(
                DecisionFixture.ProofFixture(request.Digest, "Volume", 0.93, "currency", 0.97), "jev-test-1")
                .AskAsync(request, CancellationToken.None);
            Assert.True(answers.Decided);
            new DecisionAnswerStore().Put("disk:contoso#marker", 1, request, answers);

            var folder = System.IO.Path.Combine(ConnectionRegistry.Root(), "decision-answers");
            var kept = string.Concat(System.IO.Directory.GetFiles(folder, "*.jsonl").Select(System.IO.File.ReadAllText));
            Assert.NotEmpty(kept);
            Assert.True(!kept.Contains("Volume", StringComparison.Ordinal),
                "The answer kept on this computer holds the model's folder name: " + kept);
        }

        // ---- 8. round 3 (27 Sep): the budget the live client enforces, and the ranking the store keeps. Both fail on
        //      m11/m4-aischema a8399e95. ----

        /// <summary>A vendor that answers every ai.schema ask "leave it out, sure" and says each answer cost
        /// <c>charge</c> input tokens. It stands where the network is, so the real client (JevDecisionOracle) reserves,
        /// sends, settles and refuses exactly as it does live.</summary>
        private sealed class ExcludeVendor : System.Net.Http.HttpMessageHandler
        {
            private readonly int _charge;
            private int _sends;
            public ExcludeVendor(int charge) { _charge = charge; }
            public int Sends => Volatile.Read(ref _sends);
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref _sends);
                var (answers, _) = ExcludeAnswers(request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
                var body = "{\"model\":\"jev-test-1\",\"usage\":{\"input_tokens\":" + _charge.ToString(CultureInfo.InvariantCulture)
                    + ",\"output_tokens\":10},\"answers\":" + answers + "}";
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task When_the_live_client_refuses_an_ask_the_budget_cannot_fit_every_row_still_says_Jev_has_not_looked_at_everything()
        {
            // R2-2's fix reads a spent budget from the service's own gate only: Budget.Exhausted, skipped with
            // DecisionService.BudgetSpentReason (LocalEngine.JevFamilies.cs, "spent"). The live client stops sooner. It
            // RESERVES each ask's estimate before it sends (JevDecisionOracle, Budget.TryReserve), so once what is left is
            // smaller than the next ask's estimate every ask comes back ANSWERED but not decided ("this session has used
            // its advice budget"), while Budget.Exhausted stays false (used plus reserved is still under the cap). That
            // is how the 200k session budget normally ends, since an ask rarely lands it exactly on the cap. The batch
            // then counts nothing as spent, the run is neither partial nor stopped short, and the progress line goes: the
            // rows read as the whole answer although Jev never looked at the rest.
            // One request per table (m11/jev-batch): four chunks (Customer 4 objects, Sales 6, two small tables of 2), so the live
            // client's own refusal of the THIRD request leaves two answered and two chunks unasked.
            using var f = await ModelAsync("VerifyBudgetReserve.bim", extra: MoreTables(2));
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)>());   // switches the other families off
            var batches = await BatchesAsync(f);
            var asks = batches.SelectMany(b => b.Asks).ToList();
            var estimates = batches.Select(b => EstimateOf(b)).ToArray();
            var lookedAt = batches[0].Asks.Count + batches[1].Asks.Count;   // the two chunks the run's first batches ask and hear back about
            // Each answer costs more than any estimate, and the cap leaves room for two answers and then less than the
            // smallest estimate: the third reservation is refused while the budget does not read as exhausted.
            var charge = estimates.Max() + 100;
            var budget = new DecisionBudget(capInputTokens: 2L * charge + estimates.Min() - 1);
            using var vendor = new ExcludeVendor(charge);
            using var client = new JevDecisionOracle("not-a-real-key", budget, vendor, TimeSpan.FromSeconds(10), maxConcurrent: 1);
            f.Engine.UseDecisionsForTest(new DecisionService(client, budget));
            f.Engine.AiSchemaCapForTest = 3;

            ModelPlanView plan = null;
            for (var i = 0; i < 8; i++) plan = await SettledPlanAsync(f);

            // Preconditions: the live client stopped part way, and the budget does not read as exhausted.
            Assert.Equal(2, vendor.Sends);
            Assert.True(vendor.Sends < batches.Count, "precondition: Jev was sent " + vendor.Sends + " of " + batches.Count + " requests");
            Assert.False(budget.Exhausted, "precondition: the live client's own refusal, not the service's exhausted gate");
            // And it stops: more reads start no more runs and send nothing more.
            var runs = f.Engine.JevRunsStartedForTest;
            for (var i = 0; i < 3; i++) plan = await SettledPlanAsync(f);
            Assert.Equal(runs, f.Engine.JevRunsStartedForTest);
            Assert.Equal(2, vendor.Sends);
            var rows = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(rows);

            // What R2-2 promised the rows say once the budget stops the batches (AiSchemaFamily.ProgressLine).
            const string Rest = "This session has used its suggestion budget, so the rest wait for the next session.";
            foreach (var row in rows)
                Assert.True((row.Why ?? Array.Empty<string>()).Any(w => w.StartsWith("Jev has looked at " + lookedAt + " of " + asks.Count + " things", StringComparison.Ordinal)
                                                                      && w.EndsWith(Rest, StringComparison.Ordinal)),
                    "Jev was asked about " + vendor.Sends + " of " + batches.Count + " requests before the session's budget stopped it, but the row "
                    + row.Id + " reads as the whole answer: " + string.Join(" | ", row.Why ?? Array.Empty<string>()));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_ranking_kept_on_this_computer_holds_no_name_from_the_model()
        {
            // R2-5 hashed every option id, but the store still writes each QUESTION's name as it is (DecisionAnswerStore,
            // Wire: o[q.Name]). plan.rank names its questions after the plan items it scores, "value:" and the item's id
            // (PlanRankPoint.QuestionFor), and an item's id carries the model's own names ("check/measure:Sales/...",
            // "jev.ai-schema.table:Sales"). The engine asks plan.rank through the same service that keeps every decided
            // answer on this computer, so a decided ranking puts the model's names in the file. Live on this branch the
            // vendor refuses plan.rank (HTTP 422), which is the only reason the lane p store held none; m11/m4-relationships
            // e03216bf makes the vendor answer it.
            //
            // The item that carries the names here is the starter row, "starter/table:Sales", and it needs a Sales number
            // column that no measure reads and whose name does not say it is an id. The shared model's only such column
            // used to be LineKey, and since m11/plan-harm 4c4187c7 a column whose last word is Key is never totalled
            // (StarterMeasureTemplates.NeverSummed), rightly, so the row went away and the precondition below could no
            // longer be met. Quantity is a count a person does total, so the starter row offers "Total Quantity" and the
            // ranking scores an item whose id names the model again.
            using var f = await ModelAsync("VerifyRankKept.bim", extra: async e =>
                await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human"));
            var probe = f.UseEmptyRecorded();
            await f.ConsentAsync();
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnCategory, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.AiSchema, false, "human");
            // Learn the ranking's real digest and the items it scores, as ModelPlanCacheTests does.
            var plan = await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            var digest = Assert.Single(probe.AskedDigests);
            var open = plan.Items.Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed).ToArray();
            var names = new[] { "Sales", "Customer", "Total Sales", "Order Lines", "Amount", "Region", "Quantity" };
            Assert.True(open.Any(i => names.Any(n => i.Id.Contains(n, StringComparison.Ordinal))),
                "precondition: an item the ranking scores names the model: " + string.Join(", ", open.Select(i => i.Id)));

            // The ranking is answered through the service the engine builds for a real session, which keeps it.
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":");
            sb.Append(JsonSerializer.Serialize(digest)).Append(",\"point\":\"").Append(DecisionRegistry.PlanRank).Append('"');
            sb.Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":40},\"answers\":{");
            for (var i = 0; i < open.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonSerializer.Serialize(PlanRankPoint.QuestionFor(open[i].Id)))
                  .Append(":{\"type\":\"score\",\"score\":\"useful\",\"confidence\":0.95}");
            }
            sb.Append("}}]}");
            var oracle = new RecordedDecisionOracle(sb.ToString(), "jev-test-1");
            f.Engine.UseDecisionsForTest(f.Engine.BuildDecisions(oracle, new DecisionBudget()));
            var ranked = await f.Engine.GetModelPlanAsync();
            await f.Engine.PlanRankSettledForTest();
            ranked = await f.Engine.GetModelPlanAsync();
            Assert.Single(oracle.AskedDigests);
            Assert.True(ranked.Ranked, "precondition: the ranking was answered: " + ranked.OrderedBy);

            var folder = System.IO.Path.Combine(ConnectionRegistry.Root(), "decision-answers");
            var kept = System.IO.Directory.Exists(folder)
                ? string.Concat(System.IO.Directory.GetFiles(folder, "*.jsonl").Select(System.IO.File.ReadAllText))
                : "";
            Assert.True(kept.Length > 0, "precondition: the ranking was kept on this computer");
            var held = names.Where(n => kept.Contains(n, StringComparison.Ordinal)).ToArray();
            Assert.True(held.Length == 0, "The ranking kept on this computer holds the model's names ("
                + string.Join(", ", held) + "): " + kept);
        }

        // ---- 9. round 4 (27 Sep): the rows say how far Jev has got whenever the advice stops part way, not only when this
        //      session's own budget stops it. Fails on m11/m4-aischema 354c05e9. ----

        /// <summary>The hosted relay as a licensed Pro session meets it (docs/redesign/contracts/relay.md): it answers the
        /// first <c>answered</c> asks "leave it out, sure", then refuses every ask with one of its own refusals, in its
        /// own words.</summary>
        private sealed class StoppingRelay : System.Net.Http.HttpMessageHandler
        {
            private readonly int _answered;
            private readonly int _status;
            private readonly string _refusal;
            private int _decides;
            private int _answeredAiSchema;
            public StoppingRelay(int answered, int status, string error, string reason)
            {
                _answered = answered;
                _status = status;
                _refusal = "{\"error\":" + JsonSerializer.Serialize(error) + ",\"reason\":" + JsonSerializer.Serialize(reason) + "}";
            }
            public int Decides => Volatile.Read(ref _decides);

            /// <summary>How many OBJECTS the answers it gave were about (one question each, m11/jev-batch). The session's own ranking
            /// (plan.rank) goes to the SAME relay and may take one of the answers, so this, and not the relay's answer
            /// budget, is how many of the model's objects Jev looked at.</summary>
            public int AnsweredAiSchema => Volatile.Read(ref _answeredAiSchema);

            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Method != System.Net.Http.HttpMethod.Post || !request.RequestUri.AbsolutePath.EndsWith("/v1/decide", StringComparison.Ordinal))
                    return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
                var answered = Interlocked.Increment(ref _decides) <= _answered;
                var (answers, objects) = ExcludeAnswers(request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
                if (answered) Interlocked.Add(ref _answeredAiSchema, objects);
                var body = answered
                    ? "{\"model\":\"jev-test-1\",\"usage\":{\"input_tokens\":900,\"output_tokens\":10},\"answers\":" + answers + "}"
                    : _refusal;
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(answered ? System.Net.HttpStatusCode.OK : (System.Net.HttpStatusCode)_status)
                {
                    Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(402, "licence_budget_spent", "This licence has used its cloud advice for the month. It resets on 2026-10-01.")]
        [InlineData(503, "paused", "Cloud advice is paused right now. Nothing else has changed.")]
        public async Task When_the_relay_stops_answering_part_way_every_row_still_says_how_far_Jev_has_got(int status, string error, string reason)
        {
            // The design (docs/redesign/jev-ai-schema-replay.md, "What the product does beyond the replay"): the product asks
            // at most 80 new objects per run and the rest in the runs after, and "each row says how far Jev has got". The
            // engine keeps that line only while a batch heard back ("more") or when THIS session's budget stopped the run
            // ("spent", DecisionService.BudgetSpentReason, which R3-1 now also reads off the client's refused hold). A
            // licensed Pro session asks through the relay, and the relay stops answering part way for reasons of its own:
            // this licence's monthly allowance (402, licence_budget_spent), the house cap or a pause (503). Each comes back
            // asked and not decided, so the next batch hears nothing, is neither "more" nor "spent", and the line goes. The
            // rows Jev did answer then read as the whole answer, and a table Jev never looked at has no row at all, although
            // Jev looked at only part of the model and nothing more is asked in this session.
            // One request per table (m11/jev-batch): the relay answers the first two requests (the session's ranking may take one),
            // and the model has four chunks, so the relay refuses the rest.
            using var f = await ModelAsync("VerifyRelayStop" + status.ToString(CultureInfo.InvariantCulture) + ".bim", extra: MoreTables(2));
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)>());   // switches the other families off
            var asks = (await f.Sessions.Current.ReadAsync(m => AiSchemaFamily.Prepare(m))).Asks;
            var budget = new DecisionBudget();
            using var relay = new StoppingRelay(answered: 2, status, error, reason);
            using var client = new JevDecisionOracle("not-a-real-licence", budget, relay, TimeSpan.FromSeconds(10), maxConcurrent: 1,
                                                     relayBaseUrl: "https://relay.invalid");
            Assert.True(client.IsRelay, "precondition: the production route, through the relay");
            f.Engine.UseDecisionsForTest(new DecisionService(client, budget));
            f.Engine.AiSchemaCapForTest = 3;

            ModelPlanView plan = null;
            for (var i = 0; i < 8; i++) plan = await SettledPlanAsync(f);

            // Preconditions: Jev answered some but not all, the relay refused after that, and this session's own
            // budget did not stop it. How many of the relay's four answers were Jev's is counted rather than assumed:
            // the session's ranking asks the same relay and takes one of them.
            var looked = relay.AnsweredAiSchema;
            Assert.True(relay.Decides > 2, "precondition: the relay refused (" + error + ") after two answers; it was asked " + relay.Decides + " times");
            Assert.True(0 < looked && looked < asks.Count, "precondition: Jev answered " + looked + " of " + asks.Count + " things");
            Assert.False(budget.Exhausted, "precondition: this session's budget is not what stopped the batches");
            // And it stops: more reads start no more runs and ask nothing more in this session.
            var runs = f.Engine.JevRunsStartedForTest;
            var decides = relay.Decides;
            for (var i = 0; i < 3; i++) plan = await SettledPlanAsync(f);
            Assert.Equal(runs, f.Engine.JevRunsStartedForTest);
            Assert.Equal(decides, relay.Decides);
            var rows = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(rows);

            foreach (var row in rows)
            {
                var line = (row.Why ?? Array.Empty<string>())
                    .FirstOrDefault(w => w.StartsWith("Jev has looked at " + looked + " of " + asks.Count + " things", StringComparison.Ordinal));
                Assert.True(line != null,
                    "Jev answered " + looked + " of " + asks.Count + " things before the relay stopped answering (" + status.ToString(CultureInfo.InvariantCulture)
                    + " " + error + "), and nothing more is asked in this session, but the row " + row.Id + " reads as the whole answer: "
                    + string.Join(" | ", row.Why ?? Array.Empty<string>()));
                // And the line says nothing untrue about why: the rest do not follow in this session, and it was not this
                // session's budget that stopped them.
                Assert.False(line.EndsWith("The rest follow.", StringComparison.Ordinal), "The rest do not follow in this session: " + line);
                Assert.DoesNotContain("This session has used its suggestion budget", line, StringComparison.Ordinal);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_stop_before_Jev_answers_anything_still_puts_what_Copilot_sees_on_the_plan()
        {
            // Rows for what Copilot sees are built from the LINES Jev's answers produced (JevFamily.Items), so a run
            // that stopped before Jev answered a single object has no row to carry "Jev has looked at 0 of N". The
            // Trust section then says nothing at all about Copilot, and a person reads that as nothing to change
            // (verify round 4, r4-11, session B1: the whole session's allowance went on one ranking and all 68 asks
            // were refused). Jev looked at none of this model, and the plan has to say so.
            using var f = await ModelAsync("VerifyRelayStopFirst.bim");
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)>());   // switches the other families off
            var asks = (await f.Sessions.Current.ReadAsync(m => AiSchemaFamily.Prepare(m))).Asks;
            var budget = new DecisionBudget();
            using var relay = new StoppingRelay(answered: 0, 503, "paused", "Cloud advice is paused right now. Nothing else has changed.");
            using var client = new JevDecisionOracle("not-a-real-licence", budget, relay, TimeSpan.FromSeconds(10), maxConcurrent: 1,
                                                     relayBaseUrl: "https://relay.invalid");
            f.Engine.UseDecisionsForTest(new DecisionService(client, budget));
            f.Engine.AiSchemaCapForTest = 3;

            ModelPlanView plan = null;
            for (var i = 0; i < 4; i++) plan = await SettledPlanAsync(f);

            Assert.True(relay.Decides > 0, "precondition: the relay was asked and refused every ask");
            Assert.False(budget.Exhausted, "precondition: this session's own budget is not what stopped it");
            var rows = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
            Assert.True(rows.Length > 0,
                "Jev answered none of " + asks.Count + " things and the plan says nothing about what Copilot sees: "
                + string.Join(", ", plan.Items.Select(i => i.Id)));
            var row = Assert.Single(rows);
            var line = Assert.Single((row.Why ?? Array.Empty<string>())
                .Where(w => w.StartsWith("Jev has looked at 0 of " + asks.Count + " things", StringComparison.Ordinal)));
            Assert.Contains("Cloud advice is paused right now.", line, StringComparison.Ordinal);
            Assert.False(line.EndsWith("The rest follow.", StringComparison.Ordinal), line);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_batch_that_heard_back_and_hit_the_budget_does_not_promise_that_the_rest_follow()
        {
            // "more" used to win over "spent" (LocalEngine.JevFamilies.cs:750), so a batch that heard back about some
            // of its objects AND had one ask refused by the budget promised "The rest follow." and started one more
            // run that could only be refused. A spent budget sends nothing else in this session (verify round 4,
            // item 3), so the rows say that and nothing more starts.
            // One request per table (m11/jev-batch): three chunks (Customer 4 objects, Sales 6, one small table of 2), all taken by one
            // run (a cap of 12 objects), so that one run both heard back and hit the budget.
            using var f = await ModelAsync("VerifyBudgetWinsOverMore.bim", extra: MoreTables(1));
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)>());   // switches the other families off
            var batches = await BatchesAsync(f);
            var asks = batches.SelectMany(b => b.Asks).ToList();
            var estimates = batches.Select(b => EstimateOf(b)).ToArray();
            Assert.Equal(3, batches.Count);
            // Room for two answers and then less than the smallest estimate: the THIRD request of the first batch of
            // three is refused, so that one batch both heard back and hit the budget.
            var charge = estimates.Max() + 100;
            var budget = new DecisionBudget(capInputTokens: 2L * charge + estimates.Min() - 1);
            using var vendor = new ExcludeVendor(charge);
            using var client = new JevDecisionOracle("not-a-real-key", budget, vendor, TimeSpan.FromSeconds(10), maxConcurrent: 1);
            f.Engine.UseDecisionsForTest(new DecisionService(client, budget));
            f.Engine.AiSchemaCapForTest = asks.Count;   // the three chunks are one batch (whole chunks are asked up to the cap)
            var lookedAt = batches[0].Asks.Count + batches[1].Asks.Count;

            var plan = await SettledPlanAsync(f);   // ONE batch: two answers and one request the budget refused

            Assert.Equal(2, vendor.Sends);
            Assert.True(lookedAt < asks.Count, "precondition: objects are still to be asked");
            Assert.Equal(1, f.Engine.JevRunsStartedForTest);
            var rows = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(rows);
            const string Rest = "This session has used its suggestion budget, so the rest wait for the next session.";
            foreach (var row in rows)
            {
                var line = Assert.Single((row.Why ?? Array.Empty<string>())
                    .Where(w => w.StartsWith("Jev has looked at " + lookedAt + " of " + asks.Count + " things", StringComparison.Ordinal)));
                Assert.EndsWith(Rest, line, StringComparison.Ordinal);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_run_whose_answers_changed_no_line_does_not_say_Jev_updated_the_plan()
        {
            // The strip says "Jev updated this after your change" whenever a run heard back at all
            // (LocalEngine.Plan.cs, JevRan). A run can hear back about every object and agree with all of them,
            // which puts no line on the plan and leaves it exactly as the rules left it. Then Jev updated nothing
            // and the strip must say so (verify round 4, r4-11).
            using var f = await ModelAsync("VerifyStripNoLines.bim");
            await f.ConsentAsync();
            var capturing = await AnswerAsync(f, new Dictionary<string, (string, double)>());   // agrees with every object

            var plan = await SettledPlanAsync(f);

            Assert.True(capturing.Seen.Count > 0, "precondition: Jev was asked");
            Assert.Empty(plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)));
            Assert.False(plan.Ranked, "precondition: the ranking is not what updated the plan");
            Assert.Equal(PlanStripUpdatedBy.Semanticus, plan.Strip.Updated.By);
        }

        [Fact]
        public async Task An_answer_kept_on_this_computer_is_not_cached_for_the_model_that_now_owns_the_cache()
        {
            // The session cache belongs to ONE model at a time, and an ask claims it for its own model before it
            // looks anything up. A kept answer found after another model has claimed the cache must not go into it:
            // the other model would then be answered about the same evidence with an answer bought for this one
            // (verify round 2, R2-3b). The claim and the lookups are a few lines apart, so only an ask that
            // re-scopes the cache between them can tell the guard is there. BeforeLookupForTest is that window.
            var request = DecisionFixture.SampleRequest();
            var point = DecisionRegistry.Require(request.PointName);
            var other = new DecisionRequest(point.Name, point.SchemaVersion, DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("measure_name", "Order Count"),
                new KeyValuePair<string, object>("measure_formula_facts", DecisionFixture.SampleFormulaFacts("COUNTROWS")),
                new KeyValuePair<string, object>("table_name", "Sales"),
                new KeyValuePair<string, object>("table_columns", new List<object>()),
                new KeyValuePair<string, object>("sibling_measures", new List<object>()),
            }), request.Questions);

            // Model A bought this answer in an earlier session and it is kept on this computer.
            var bought = await new RecordedDecisionOracle(
                DecisionFixture.ProofFixture(request.Digest, "Volume", 0.93, "currency", 0.97), "jev-test-1")
                .AskAsync(request, CancellationToken.None);
            Assert.True(bought.Decided);
            var store = new DecisionAnswerStore();
            store.Put("disk:model-a#m", 0, request, bought);

            // The live service can answer the OTHER question and nothing else, so anything decided for this
            // evidence later came from a cache rather than from the service.
            var oracle = new RecordedDecisionOracle(
                DecisionFixture.ProofFixture(other.Digest, "Volume", 0.93, "currency", 0.97), "jev-test-1");
            var scope = "disk:model-a#m";
            using var service = new DecisionService(oracle, answers: store, readConsent: _ =>
                DecisionConsent.Grant(DecisionRegistry.DeclaredFields, DecisionRegistry.ConsentSchemaVersion, DateTimeOffset.UtcNow))
            { ReadScope = () => Task.FromResult(scope) };

            service.BeforeLookupForTest = () =>
            {
                // Model B is opened and asks something of its own, which takes the cache over.
                service.BeforeLookupForTest = null;
                scope = "disk:model-b#m";
                service.AskAsync(other.PointName, other.Snapshot, other.Questions, isPro: true, enabled: true,
                    fence: null, ct: CancellationToken.None).GetAwaiter().GetResult();
            };
            var hit = await service.AskAsync(request.PointName, request.Snapshot, request.Questions, isPro: true,
                enabled: true, fence: null, ct: CancellationToken.None);
            Assert.True(hit.Asked && hit.Answers.Decided, "precondition: model A was served its own kept answer");

            // Model B now asks the same question. It bought nothing for it and nothing is kept for it.
            var before = oracle.AskCount;
            await service.AskAsync(request.PointName, request.Snapshot, request.Questions, isPro: true,
                enabled: true, fence: null, ct: CancellationToken.None);
            Assert.True(oracle.AskCount > before,
                "Model B was answered from the cache with an answer kept for model A: the service was asked "
                + oracle.AskCount + " times, still " + before);
        }

        [Fact]
        public void This_installs_key_is_owner_only_from_the_moment_it_is_made()
        {
            // The key that hashes every question name and option id is 32 random bytes kept beside the answer files.
            // It used to be created at whatever the default mode is and set to owner only AFTER the bytes were
            // written, so there was a window, however short, in which this install's key sat on disk readable by
            // anyone on the machine (verify round 3). The mode is part of creating the file now.
            using var f = new DecisionFixture();
            var request = DecisionFixture.SampleRequest();
            var answers = new RecordedDecisionOracle(
                DecisionFixture.ProofFixture(request.Digest, "Volume", 0.93, "currency", 0.97), "jev-test-1")
                .AskAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            new DecisionAnswerStore().Put("disk:contoso#marker", 1, request, answers);

            var key = System.IO.Path.Combine(ConnectionRegistry.Root(), "decision-answers", "install.key");
            Assert.True(System.IO.File.Exists(key), "the store kept an answer without making a key");
            if (!OperatingSystem.IsWindows())
                Assert.Equal(System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite,
                             System.IO.File.GetUnixFileMode(key));

            // And there is no window before that. A mode set after the write cannot be seen from here once it has
            // run, so what this holds is the creating call itself: the key's FileStream carries the mode.
            var source = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "Semanticus.Engine", "Decisions", "DecisionAnswerStore.cs"));
            var made = source.IndexOf("var fresh = RandomNumberGenerator.GetBytes(32);", StringComparison.Ordinal);
            var written = source.IndexOf("fs.Write(fresh, 0, fresh.Length);", StringComparison.Ordinal);
            Assert.True(made > 0 && written > made, "the key is not made where this test thinks it is");
            Assert.Contains("UnixCreateMode", source.Substring(made, written - made), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_cache_that_now_belongs_to_another_model_is_not_read_for_this_one()
        {
            // The other half of the same rule (verify round 4, the coordinator, 27 Sep). The store hit was PUT under a
            // scope guard and the cache was READ without one, and the two are the same window: an ask claims the cache
            // for its own model, and while it is building its request another model opens, asks, and takes the cache
            // over. The read that follows then serves whatever the other model bought. It needs the same digest, and
            // the digest is the point, the schema version, the snapshot and the questions, so two models with a
            // same-named table and the same facts hash the same. An answer bought for one model is not an answer for
            // the next one, whichever end of the cache it is read from.
            var request = DecisionFixture.SampleRequest();
            var oracle = new RecordedDecisionOracle(
                DecisionFixture.ProofFixture(request.Digest, "Volume", 0.93, "currency", 0.97), "jev-test-1");
            var scope = "disk:model-a#m";
            using var service = new DecisionService(oracle, readConsent: _ =>
                DecisionConsent.Grant(DecisionRegistry.DeclaredFields, DecisionRegistry.ConsentSchemaVersion, DateTimeOffset.UtcNow))
            { ReadScope = () => Task.FromResult(scope) };

            service.BeforeLookupForTest = () =>
            {
                // Model B is opened and asks the same question of its own. That buys B an answer and takes the cache.
                service.BeforeLookupForTest = null;
                scope = "disk:model-b#m";
                service.AskAsync(request.PointName, request.Snapshot, request.Questions, isPro: true, enabled: true,
                    fence: null, ct: CancellationToken.None).GetAwaiter().GetResult();
            };
            var first = await service.AskAsync(request.PointName, request.Snapshot, request.Questions, isPro: true,
                enabled: true, fence: null, ct: CancellationToken.None);

            Assert.True(first.Asked && first.Answers.Decided, "precondition: model A was answered at all");
            Assert.True(oracle.AskCount == 2,
                "Model A was served the answer model B bought, out of a cache that had become B's: the service was asked "
                + oracle.AskCount + " time(s), not 2");
        }

        // ---- 10. verify round 5 (27 Sep, targeted): what the rows say after the service stops answering and the person
        //      keeps working. Each fails on m11/m4-aischema 323875a2. ----

        private const string Month = "This licence has used its cloud advice for the month. It resets on 2026-10-01.";
        private const string Paused = "Cloud advice is paused right now. Nothing else has changed.";

        /// <summary>The hosted relay as a licensed session meets it, with three things a real one does that the round 4
        /// relay did not: it keeps refusing for as long as it is told to, it can drop one connection without answering, and
        /// it can start answering again (a pause lifts, a month resets). It answers "leave it out, sure".</summary>
        private sealed class ScriptedRelay : System.Net.Http.HttpMessageHandler
        {
            private readonly int _status;
            private readonly string _refusal;
            private int _answersLeft;
            private int _decides;
            private int _aiSchemaAsks;
            private int _answeredAiSchema;
            private int _refused;
            private string _dropOnceFor;
            private int _dropped;
            private volatile bool _answerEverything;

            private readonly HashSet<string> _dropAlways;

            /// <summary>dropOnceFor and dropAlways are TABLE names: a request is one table's objects (m11/jev-batch).</summary>
            public ScriptedRelay(int answered, int status, string error, string reason, string dropOnceFor = null, IEnumerable<string> dropAlways = null)
            {
                _answersLeft = answered;
                _status = status;
                _refusal = "{\"error\":" + JsonSerializer.Serialize(error) + ",\"reason\":" + JsonSerializer.Serialize(reason) + "}";
                _dropOnceFor = dropOnceFor;
                _dropAlways = new HashSet<string>(dropAlways ?? Array.Empty<string>(), StringComparer.Ordinal);
            }

            private readonly List<string> _order = new List<string>();

            /// <summary>The tables the relay was asked about, in the order the requests arrived.</summary>
            public string Order { get { lock (_order) return string.Join(",", _order); } }

            public int DroppedCount => Volatile.Read(ref _dropped);

            public int Decides => Volatile.Read(ref _decides);
            public int AiSchemaAsks => Volatile.Read(ref _aiSchemaAsks);
            /// <summary>The OBJECTS the answers it gave were about (one question each).</summary>
            public int AnsweredAiSchema => Volatile.Read(ref _answeredAiSchema);
            public int Refused => Volatile.Read(ref _refused);
            public bool Dropped => Volatile.Read(ref _dropped) > 0;

            /// <summary>From now on every ask is answered: the pause has lifted, or the month has reset.</summary>
            public void AnswerFromNowOn() => _answerEverything = true;

            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Method != System.Net.Http.HttpMethod.Post || !request.RequestUri.AbsolutePath.EndsWith("/v1/decide", StringComparison.Ordinal))
                    return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
                Interlocked.Increment(ref _decides);
                string point = null, objectName = null;
                var requestBody = request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                using (var doc = JsonDocument.Parse(requestBody))
                {
                    if (doc.RootElement.TryGetProperty("point", out var p) && p.ValueKind == JsonValueKind.String) point = p.GetString();
                    // A request is one table's objects here (the tests' own per-table switch): its table is the table its first object names.
                    if (doc.RootElement.TryGetProperty("snapshot", out var s) && s.ValueKind == JsonValueKind.Object
                        && s.TryGetProperty("objects", out var objs) && objs.ValueKind == JsonValueKind.Array && objs.GetArrayLength() > 0
                        && objs[0].TryGetProperty("table", out var o) && o.ValueKind == JsonValueKind.String) objectName = o.GetString();
                }
                if (point == Point) Interlocked.Increment(ref _aiSchemaAsks);
                if (point == Point) lock (_order) _order.Add(objectName);
                if (point == Point && objectName != null && _dropAlways.Contains(objectName))
                {
                    Interlocked.Increment(ref _dropped);
                    throw new System.Net.Http.HttpRequestException("the connection was reset before any reply (a test relay dropping this object every time)");
                }
                var drop = Volatile.Read(ref _dropOnceFor);
                if (point == Point && drop != null && string.Equals(objectName, drop, StringComparison.Ordinal)
                    && Interlocked.CompareExchange(ref _dropOnceFor, null, drop) == drop)
                {
                    Interlocked.Increment(ref _dropped);
                    throw new System.Net.Http.HttpRequestException("the connection was reset before any reply (a test relay dropping one ask)");
                }
                var answered = _answerEverything || Interlocked.Decrement(ref _answersLeft) >= 0;
                var (answers, objects) = ExcludeAnswers(requestBody);
                if (answered && point == Point) Interlocked.Add(ref _answeredAiSchema, objects);
                if (!answered) Interlocked.Increment(ref _refused);
                var body = answered
                    ? "{\"model\":\"jev-test-1\",\"usage\":{\"input_tokens\":900,\"output_tokens\":10},\"answers\":" + answers + "}"
                    : _refusal;
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(answered ? System.Net.HttpStatusCode.OK : (System.Net.HttpStatusCode)_status)
                {
                    Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
        }

        private static string ProgressLineOf(ModelPlanItem row) =>
            (row.Why ?? Array.Empty<string>()).SingleOrDefault(w => w.StartsWith("Jev has looked at ", StringComparison.Ordinal));

        [Fact]
        public async Task After_the_licences_month_stops_Jev_the_refusals_that_follow_do_not_become_this_sessions_budget()
        {
            // R4-1 keeps "Jev has looked at X of N" with the relay's own words when the licence's month runs out (402). But
            // the person keeps working, and every change starts a run that asks the objects Jev has not looked at again.
            // The relay refuses each one BEFORE it forwards or spends anything (docs/redesign/contracts/relay.md, 402
            // licence_budget_spent: "the reservation would pass the per-licence cap"), yet the client settles every refused
            // attempt at its estimate, as spend that may have been charged (JevDecisionOracle, spend.SettleUnknown in the
            // attempt's finally). A few ordinary edits later this session's budget reads as used, the next refused hold
            // becomes the budget skip, "spent" wins, and every row says "This session has used its suggestion budget, so the
            // rest wait for the next session." That is untrue twice over: the service charged this session for four answers
            // and nothing else, and the next session meets the same month. Lane h on the Yoga showed it live on 27 Sep: 17
            // answers, 194 refusals, 199,962 of 200,000 counted as used, and every Copilot row flipped to the budget.
            // One request per table (the tests' own switch, JevPerTableForTest): four chunks, the relay answers the first two requests.
            using var f = await ModelAsync("VerifyMonthThenEdits.bim", extra: MoreTables(2));
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)>());   // switches the other families off
            var batches = await BatchesAsync(f);
            var asks = batches.SelectMany(b => b.Asks).ToList();
            var largest = batches.Max(b => EstimateOf(b, relay: true));
            // The session's allowance, scaled down so a handful of edits does what a working afternoon does to 200,000:
            // room for the two answers the relay gives and about a dozen refused requests at their estimates.
            var budget = new DecisionBudget(capInputTokens: 2L * 900 + 12L * largest);
            using var relay = new ScriptedRelay(answered: 2, 402, "licence_budget_spent", Month);
            using var client = new JevDecisionOracle("not-a-real-licence", budget, relay, TimeSpan.FromSeconds(10), maxConcurrent: 1,
                                                     relayBaseUrl: "https://relay.invalid");
            f.Engine.UseDecisionsForTest(new DecisionService(client, budget));
            f.Engine.AiSchemaCapForTest = 3;

            ModelPlanView plan = null;
            for (var i = 0; i < 8; i++) plan = await SettledPlanAsync(f);
            var looked = relay.AnsweredAiSchema;
            Assert.True(0 < looked && looked < asks.Count, "precondition: Jev answered " + looked + " of " + asks.Count + " things");
            Assert.True(plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).Select(ProgressLineOf)
                            .Any(l => l != null && l.Contains(Month, StringComparison.Ordinal)),
                "precondition: before the edits the rows say the licence's month stopped Jev (round 4's fix)");

            // The person keeps working: ordinary edits that change nothing Jev is asked about.
            for (var edit = 0; edit < 6; edit++)
            {
                await f.Engine.SetDescriptionAsync("table:Customer",
                    "Everyone who has bought from the company, note " + edit.ToString(CultureInfo.InvariantCulture) + ".", "human");
                for (var i = 0; i < 2; i++) plan = await SettledPlanAsync(f);
            }

            Assert.Equal(looked, relay.AnsweredAiSchema);   // the month still stops it: nothing more was answered
            var rows = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                var line = ProgressLineOf(row);
                Assert.True(line != null && line.Contains(Month, StringComparison.Ordinal)
                            && !line.Contains("This session has used its suggestion budget", StringComparison.Ordinal),
                    "The relay answered " + looked + " asks and refused every other one for the licence's month, before spending anything, "
                    + "but after " + relay.Refused + " refusals this session counts " + budget.UsedInputTokens.ToString(CultureInfo.InvariantCulture)
                    + " of " + (budget.CapInputTokens?.ToString(CultureInfo.InvariantCulture) ?? "no cap") + " as used and the row " + row.Id
                    + " blames that instead: " + (line ?? string.Join(" | ", row.Why ?? Array.Empty<string>())));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task One_dropped_connection_is_not_named_as_the_reason_while_the_service_itself_is_refusing()
        {
            // R4-1 names why the batches stopped with the FIRST ask that did not come back decided, in the model's own
            // order (LocalEngine.JevFamilies.cs, stopReason). So when the first object's ask is the one whose connection
            // dropped, a one-off transport failure outranks the service's own refusal, although the service refused every
            // other ask and is still refusing. The row then sends a person to look at their network ("The advice service
            // could not be reached.") while cloud advice is paused, and "the rest wait until Jev can answer again" is
            // pinned to the wrong cause. Lane h on the Yoga showed it live on 27 Sep: most asks came back paused, a few
            // lost their connection, and the row said "could not be reached".
            // One request per table (m11/jev-batch): the FIRST table's connection drops once, and the other table's request is refused
            // with the pause. Both chunks are one batch (a cap of every object), as the two asks were.
            using var f = await ModelAsync("VerifyDropThenPause.bim");
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)>());   // switches the other families off
            var asks = (await f.Sessions.Current.ReadAsync(m => AiSchemaFamily.Prepare(m))).Asks;
            var name = asks[0].TableName;
            Assert.False(string.IsNullOrEmpty(name), "precondition: the first object's table has a name");
            var budget = new DecisionBudget();
            using var relay = new ScriptedRelay(answered: 0, 503, "paused", Paused, dropOnceFor: name);
            using var client = new JevDecisionOracle("not-a-real-licence", budget, relay, TimeSpan.FromSeconds(10), maxConcurrent: 1,
                                                     relayBaseUrl: "https://relay.invalid");
            f.Engine.UseDecisionsForTest(new DecisionService(client, budget));
            f.Engine.AiSchemaCapForTest = asks.Count;

            ModelPlanView plan = null;
            for (var i = 0; i < 4; i++) plan = await SettledPlanAsync(f);

            Assert.True(relay.Dropped, "precondition: the first table's connection dropped once");
            // At least one ask reached the relay and was refused with its pause. Since the refusal hold-off (JevDecisionOracle.RefusedHoldOff,
            // ai.schema final verify low item 5) the asks after that first pause are not sent at all; each comes back with the relay's
            // own pause sentence, so the rows still see the pause more often than the one dropped connection. This precondition said
            // ">= 2 refused by the relay" before the hold-off existed.
            Assert.True(relay.Refused >= 1, "precondition: the relay refused an ask with its pause (" + relay.Refused + ")");
            Assert.False(budget.Exhausted, "precondition: this session's budget is not what stopped it");
            var lines = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).Select(ProgressLineOf).Where(l => l != null).ToArray();
            Assert.NotEmpty(lines);
            foreach (var line in lines)
                Assert.True(line.Contains(Paused, StringComparison.Ordinal),
                    "The relay refused every ask it received with its pause and is still paused, and one connection dropped; the row names "
                    + "the dropped connection as why the rest wait: " + line);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task When_Jev_can_answer_again_the_rows_do_not_say_the_rest_are_only_waiting_for_that()
        {
            // The tail R4-1 gives every stop but this session's budget is "The rest wait until Jev can answer again." But
            // once a stop is remembered no batch starts on a read (by design, R4-1: "no further batch starts"), and nothing
            // asks again until the person changes the model or the next session opens. So when the pause lifts, or the
            // month resets, Jev CAN answer again, read after read asks nothing, and every row still promises the rest are
            // only waiting for Jev. The coordinator's ruling was one plain sentence for the reason and WHEN the rest
            // happen; this names a "when" the product does not keep (docs/product-copy-style.md, rule 9: never overclaim).
            // Either the rest are asked once Jev can answer, or the line says when they really are.
            using var f = await ModelAsync("VerifyPauseLifts.bim", extra: MoreTables(2));   // four chunks, the relay answers two requests
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)>());   // switches the other families off
            var asks = (await f.Sessions.Current.ReadAsync(m => AiSchemaFamily.Prepare(m))).Asks;
            var budget = new DecisionBudget();
            using var relay = new ScriptedRelay(answered: 2, 503, "paused", Paused);
            using var client = new JevDecisionOracle("not-a-real-licence", budget, relay, TimeSpan.FromSeconds(10), maxConcurrent: 1,
                                                     relayBaseUrl: "https://relay.invalid");
            f.Engine.UseDecisionsForTest(new DecisionService(client, budget));
            f.Engine.AiSchemaCapForTest = 3;

            ModelPlanView plan = null;
            for (var i = 0; i < 8; i++) plan = await SettledPlanAsync(f);
            var looked = relay.AnsweredAiSchema;
            Assert.True(0 < looked && looked < asks.Count, "precondition: Jev answered " + looked + " of " + asks.Count + " things");

            relay.AnswerFromNowOn();   // the pause lifts: Jev can answer again
            var asked = relay.AiSchemaAsks;
            for (var i = 0; i < 3; i++) plan = await SettledPlanAsync(f);

            if (relay.AiSchemaAsks > asked) return;   // the rest were asked once Jev could answer: the promise was kept
            var rows = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                var line = ProgressLineOf(row);
                Assert.True(line == null || !line.Contains("until Jev can answer again", StringComparison.Ordinal),
                    "Jev can answer again and no ask about what Copilot sees went out over three reads, yet the row "
                    + row.Id + " still says the rest are only waiting for Jev: " + line);
            }
        }

        // ---- join fixes (M11 join part 1, ai.schema final verify low items) --------------------------------------

        /// <summary>Low item 1, "most common reason wins": among the refusals the service gave, the one most asks came back
        /// with names the stop, not the first in the model's order. Nothing pinned it.</summary>
        [Fact]
        public void The_reason_most_asks_came_back_with_names_why_the_batches_stopped()
        {
            const string Crowded = "Too many advice requests are already running for this licence. Try again in a moment.";
            Assert.Equal(Paused, LocalEngine.BatchStopReason(new[] { Crowded, null, Paused, Paused }));
            // A tie goes to the model's own order, so the choice never depends on which reply landed first.
            Assert.Equal(Crowded, LocalEngine.BatchStopReason(new[] { Crowded, Paused }));
            Assert.Null(LocalEngine.BatchStopReason(new string[] { null, null }));
        }

        /// <summary>Low item 1, "heard beats unheard": one refusal the service gave outranks any number of asks that heard
        /// nothing (a lost connection, a deadline). Only the verifier's throwaway attack T7 caught it; this is T7 lifted,
        /// with the rule itself pinned beside it.</summary>
        [Fact]
        public void A_refusal_the_service_gave_outranks_more_asks_that_heard_nothing()
        {
            Assert.Equal(Paused, LocalEngine.BatchStopReason(new[]
            {
                JevDecisionOracle.UnreachableReason, JevDecisionOracle.UnreachableReason, JevDecisionOracle.TimedOutReason, Paused,
            }));
            // With nothing heard, the most common silence is named.
            Assert.Equal(JevDecisionOracle.UnreachableReason, LocalEngine.BatchStopReason(new[]
            {
                JevDecisionOracle.TimedOutReason, JevDecisionOracle.UnreachableReason, JevDecisionOracle.UnreachableReason,
            }));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_refusal_the_service_gave_wins_over_more_asks_in_the_same_batch_that_heard_nothing()
        {
            // One request per table (m11/jev-batch): Customer's (4 objects) and Sales' (6) connections drop every time, and the
            // small third table's request is refused with the pause. The refusal is in ONE request and the silence in two, and it
            // still wins: the silent objects (10) outnumber the refused ones (2), as the three dropped asks outnumbered the one.
            using var f = await ModelAsync("VerifyHeardWins.bim", extra: MoreTables(1, "Zz"));   // tables go in name order: Zz1 is asked last, after the two drops
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)>());   // switches the other families off
            var asks = (await f.Sessions.Current.ReadAsync(m => AiSchemaFamily.Prepare(m))).Asks;
            var names = new[] { "Customer", "Sales" };
            var budget = new DecisionBudget();
            using var relay = new ScriptedRelay(answered: 0, 503, "paused", Paused, dropAlways: names);
            using var client = new JevDecisionOracle("not-a-real-licence", budget, relay, TimeSpan.FromSeconds(10), maxConcurrent: 1,
                                                     relayBaseUrl: "https://relay.invalid");
            f.Engine.UseDecisionsForTest(new DecisionService(client, budget));
            f.Engine.AiSchemaCapForTest = asks.Count;   // two requests that hear nothing and one the service refuses, in one batch

            ModelPlanView plan = null;
            for (var i = 0; i < 3; i++) plan = await SettledPlanAsync(f);

            Assert.True(relay.DroppedCount >= 2 && relay.Refused >= 1, "precondition: dropped " + relay.DroppedCount + ", refused " + relay.Refused + ", order " + relay.Order);
            var lines = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).Select(ProgressLineOf).Where(l => l != null).ToArray();
            Assert.NotEmpty(lines);
            foreach (var line in lines)
                Assert.True(line.Contains(Paused, StringComparison.Ordinal),
                    "The service refused with its pause and two requests heard nothing; the row names the silence instead: " + line);
        }

        /// <summary>Low item 5, NO BRAKE AFTER A SPENT MONTH OR A PAUSE. A refusal before the relay forwards is free, so every
        /// model change used to ask every object Jev had not looked at again (up to AiSchemaFamily.MaxObjectsPerRun, plus the
        /// other families), and the relay refused all of it, until the month reset. Now a model change inside the hold-off
        /// sends nothing and the rows keep naming the month; a change after it asks again.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task After_the_relay_refuses_for_the_month_a_model_change_inside_the_hold_off_sends_nothing_and_one_after_it_asks_again()
        {
            using var f = await ModelAsync("VerifyHoldOff.bim", extra: MoreTables(2));   // four chunks, the relay answers two requests
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)>());   // switches the other families off
            var asks = (await f.Sessions.Current.ReadAsync(m => AiSchemaFamily.Prepare(m))).Asks;
            var now = new DateTime(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc);
            var budget = new DecisionBudget();
            using var relay = new ScriptedRelay(answered: 2, 402, "licence_budget_spent", Month);
            using var client = new JevDecisionOracle("not-a-real-licence", budget, relay, TimeSpan.FromSeconds(10), maxConcurrent: 1,
                                                     relayBaseUrl: "https://relay.invalid");
            client.UtcNowForTest = () => now;
            f.Engine.UseDecisionsForTest(new DecisionService(client, budget));
            f.Engine.AiSchemaCapForTest = 3;

            ModelPlanView plan = null;
            for (var i = 0; i < 6; i++) plan = await SettledPlanAsync(f);
            Assert.True(relay.Refused > 0, "precondition: the relay refused for the month");
            Assert.True(relay.AnsweredAiSchema < asks.Count, "precondition: objects are still to be asked");

            // A change a minute later: nothing goes out, and the rows still name the month.
            now = now.AddMinutes(1);
            var decides = relay.Decides;
            await f.Engine.SetDescriptionAsync("table:Customer", "Everyone who has bought from the company.", "human");
            for (var i = 0; i < 3; i++) plan = await SettledPlanAsync(f);
            Assert.True(relay.Decides == decides,
                "the relay refused for the licence's month a minute ago and a model change asked it " + (relay.Decides - decides) + " more times");
            var lines = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).Select(ProgressLineOf).Where(l => l != null).ToArray();
            Assert.NotEmpty(lines);
            Assert.All(lines, l => Assert.Contains(Month, l, StringComparison.Ordinal));

            // After the hold-off, the month has reset: the next change asks again.
            now = now.Add(JevDecisionOracle.RefusedHoldOff);
            relay.AnswerFromNowOn();
            var asked = relay.AiSchemaAsks;
            await f.Engine.SetDescriptionAsync("table:Customer", "Everyone who has bought from the company, and why.", "human");
            for (var i = 0; i < 3; i++) plan = await SettledPlanAsync(f);
            Assert.True(relay.AiSchemaAsks > asked, "the hold-off had passed and a model change asked nothing");
        }

        // ---- Kane, 8 Oct: calm. When Jev cannot be reached and the person cannot act, ONE line, in one place. ----

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task When_Jev_refuses_without_a_reason_and_looked_at_nothing_the_Plan_says_so_once_with_no_row_and_no_count()
        {
            // A 401 with no sentence of its own (the case on Kane's Yoga, 8 Oct): the Plan used to grow a row "Jev has not finished
            // choosing what Copilot sees" with "Jev has looked at 0 of N things", blame "this session's sign-in" and promise a retry.
            using var f = await ModelAsync("CalmNothingLooked.bim", extra: MoreTables(1));
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)>());   // switches the other families off
            var budget = new DecisionBudget();
            using var relay = new ScriptedRelay(answered: 0, 401, "", "");
            using var client = new JevDecisionOracle("not-a-real-licence", budget, relay, TimeSpan.FromSeconds(10), maxConcurrent: 1,
                                                     relayBaseUrl: "https://relay.invalid");
            f.Engine.UseDecisionsForTest(new DecisionService(client, budget));

            ModelPlanView plan = null;
            for (var i = 0; i < 3; i++) plan = await SettledPlanAsync(f);
            await f.Engine.PlanRankSettledForTest();
            plan = await f.Engine.GetModelPlanAsync();

            Assert.True(relay.Refused > 0, "precondition: the service refused");
            Assert.DoesNotContain(plan.Items, i => i.Id == AiSchemaFamily.NotFinishedItemId);
            Assert.DoesNotContain(plan.Items, i => (i.Why ?? Array.Empty<string>()).Any(w => w.Contains("Jev looked at", StringComparison.Ordinal)));
            Assert.Equal("Jev could not be reached this time, so the tasks are in the order Semanticus found them.", plan.OrderedBy);
            var everything = string.Join(" | ", plan.Items.SelectMany(i => i.Why ?? Array.Empty<string>()).Append(plan.OrderedBy));
            foreach (var old in new[] { "sign-in", "tries", "try again", "401", "advice service" })
                Assert.DoesNotContain(old, everything, StringComparison.OrdinalIgnoreCase);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task When_Jev_refuses_without_a_reason_after_looking_at_some_the_rows_keep_the_count_and_drop_the_retry_promise()
        {
            using var f = await ModelAsync("CalmSomeLooked.bim", extra: MoreTables(2));   // four chunks, the service answers two
            await f.ConsentAsync();
            await AnswerAsync(f, new Dictionary<string, (string, double)>());
            var budget = new DecisionBudget();
            using var relay = new ScriptedRelay(answered: 2, 401, "", "");
            using var client = new JevDecisionOracle("not-a-real-licence", budget, relay, TimeSpan.FromSeconds(10), maxConcurrent: 1,
                                                     relayBaseUrl: "https://relay.invalid");
            f.Engine.UseDecisionsForTest(new DecisionService(client, budget));
            f.Engine.AiSchemaCapForTest = 3;

            ModelPlanView plan = null;
            for (var i = 0; i < 8; i++) plan = await SettledPlanAsync(f);

            Assert.True(relay.AnsweredAiSchema > 0, "precondition: Jev looked at some");
            var lines = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).Select(ProgressLineOf).Where(l => l != null).ToArray();
            Assert.NotEmpty(lines);
            foreach (var line in lines)
            {
                Assert.EndsWith(" things in this model so far.", line, StringComparison.Ordinal);
                foreach (var old in new[] { "sign-in", "tries", "again", "next session" })
                    Assert.DoesNotContain(old, line, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}

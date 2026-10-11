using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// m11/rule-clashes round 6. The promise: AN ACCEPTED FINDING IS NEVER CARRIED OUT, BY ANY RULE OR ANY PATH. An acceptance
    /// declines the (object, property) its rule is ABOUT, whatever the rule's fix kind; it is read from the stored acceptance
    /// and outlives the finding; one set is asked by every path that writes or queues a change; and where several findings'
    /// asks meet on one text property, or sibling rules write different values, an acceptance declines its own ask only.
    /// The verifier's round 5 file (VerifyRuleClashesRound5Tests) proves N1 to N3 on both doors; these cover the rest of the
    /// ruling: the rule-to-property table, the narrower rule (H1, H2, A3), and the six lows.
    /// </summary>
    public sealed class RuleClashesRound6Tests
    {
        private const string Why = "Declined on purpose; reports depend on it as it is.";
        private const string Other = "Left out because you accepted this change under another rule.";
        private const string After = "Left out because you accepted this finding after it was planned.";

        // ---- the rule-to-property table ------------------------------------------------------------------------------

        private static IReadOnlyList<ReadinessRule> AllReadinessRules() =>
            ReadinessRuleSet.Default().Concat(ReadinessRuleSet.LiveRules(new ReadinessLiveStats())).ToList();

        private static IEnumerable<BpaRule> AllModelQualityRules() =>
            BpaRuleSet.Standard().Concat(BpaRuleSet.Native()).Concat(BpaDefaultRules.Rules).GroupBy(r => r.ID, StringComparer.OrdinalIgnoreCase).Select(g => g.First());

        [Fact]
        public void Every_bundled_readiness_rule_maps_to_a_property_or_says_why_it_declines_nothing()
        {
            var problems = new List<string>();
            foreach (var r in AllReadinessRules())
            {
                var about = RuleAboutTable.ForReadiness(r.Id);
                if (about == null) problems.Add(r.Id + ": not in the table");
                else if (about.DeclinesSomething && about.Kinds.Length == 0) problems.Add(r.Id + ": names a property but no object kind");
                else if (!about.DeclinesSomething && string.IsNullOrWhiteSpace(about.Reason)) problems.Add(r.Id + ": declines nothing and gives no reason");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void Every_bundled_model_quality_rule_maps_to_a_property_or_says_why_it_declines_nothing()
        {
            var problems = new List<string>();
            foreach (var r in AllModelQualityRules())
            {
                var about = RuleAboutTable.ForBpa(r);
                if (about == null) problems.Add(r.ID + ": not readable from its fix and not in the table");
                else if (about.DeclinesSomething && about.Kinds.Length == 0) problems.Add(r.ID + ": names a property but no object kind");
                else if (!about.DeclinesSomething && string.IsNullOrWhiteSpace(about.Reason)) problems.Add(r.ID + ": declines nothing and gives no reason");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void The_table_names_no_rule_that_is_not_bundled_and_reads_a_fix_rule_off_its_definition()
        {
            var readiness = AllReadinessRules().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Empty(RuleAboutTable.Readiness.Keys.Where(k => !readiness.Contains(k)));
            var bpa = AllModelQualityRules().ToDictionary(r => r.ID, StringComparer.OrdinalIgnoreCase);
            Assert.Empty(RuleAboutTable.ModelQuality.Keys.Where(k => !bpa.ContainsKey(k)));
            // A rule with a FixExpression is read off it, so the table never restates (and can never contradict) it.
            var restated = RuleAboutTable.ModelQuality.Keys.Where(k => bpa[k].FixExpression != null && System.Text.RegularExpressions.Regex.IsMatch(bpa[k].FixExpression, @"^\s*\w+\s*=(?!=)")).ToList();
            Assert.True(restated.Count == 0, "restated in the table although the rule's fix names its target: " + string.Join(", ", restated));
        }

        [Theory]
        [InlineData("VIS-KEY", "isHidden")]
        [InlineData("VIS-TECH-COLUMN", "isHidden")]
        [InlineData("VIS-FK", "isHidden")]
        [InlineData("FMT-COLUMN", "formatString")]
        [InlineData("FMT-MEASURE", "formatString")]
        [InlineData("DAC-IMPLICIT-MEASURE", "summarizeBy")]
        [InlineData("FMT-SUMMARIZE", "summarizeBy")]
        [InlineData("CAT-URL", "dataCategory")]
        [InlineData("NAME-COLUMN", "name")]
        [InlineData("NAME-TECH-PREFIX", "name")]
        [InlineData("DESC-MEASURE", "description")]
        public void A_readiness_rule_is_about_the_property_its_own_ask_names(string ruleId, string property)
        {
            var about = RuleAboutTable.ForReadiness(ruleId);
            Assert.Equal(property, about.Property, ignoreCase: true);
            Assert.False(about.OwnAsk);
        }

        [Theory]
        [InlineData("HIDE_FOREIGN_KEYS", "isHidden", false)]
        [InlineData("NUMERIC_COLUMN_SUMMARIZE_BY", "summarizeBy", false)]
        [InlineData("INTEGER_FORMATTING", "formatString", false)]
        [InlineData("MARK_PRIMARY_KEYS", "isKey", false)]
        [InlineData("DATECOLUMN_FORMATSTRING", "formatString", true)]
        [InlineData("MONTHCOLUMN_FORMATSTRING", "formatString", true)]
        [InlineData("AVOID_INVALID_NAME_CHARACTERS", "name", false)]
        public void A_model_quality_rule_is_about_the_property_its_fix_writes(string ruleId, string property, bool own)
        {
            var rule = AllModelQualityRules().First(r => r.ID == ruleId);
            var about = RuleAboutTable.ForBpa(rule);
            Assert.Equal(property, about.Property, ignoreCase: true);
            Assert.Equal(own, about.OwnAsk);
        }

        [Fact]
        public void A_rule_loaded_later_is_read_off_its_own_fix_with_no_edit_to_any_table()
        {
            var custom = new BpaRule { ID = "ORG_HIDE_TECH", Scope = "DataColumn", Expression = "true", FixExpression = "IsHidden = true" };
            var about = RuleAboutTable.ForBpa(custom);
            Assert.Equal("isHidden", about.Property, ignoreCase: true);
            Assert.Contains("column", about.Kinds);
            Assert.Null(RuleAboutTable.ForBpa(new BpaRule { ID = "ORG_NO_FIX_UNLISTED", Scope = "Measure", Expression = "true" }));   // not known: declines nothing
        }

        // ---- the model ------------------------------------------------------------------------------------------------

        /// <summary>A small star. Customer[CustomerKey] is the marked one-side key sharing the foreign key's name; Sales[ProductKey]
        /// sums; Sales[Month Start Date] is a date column with no format whose name trips both the date and the month format
        /// rule; Product[ProductName] trips a cryptic-name rule and the label-column rule; Order Count has no format.</summary>
        private static async Task<DecisionFixture> StarAsync(bool free = false)
        {
            var f = new DecisionFixture(free ? TestEntitlements.Free : TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Round6", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Month Start Date", "DateTime", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await e.CreateTableAsync("Product", "human");
            await e.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Product", "ProductName", "String", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
            await e.SetColumnMetadataAsync("column:Sales/ProductKey", null, "Sum", null, null, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            await f.Sessions.Require().MutateAsync("human", "setup", m =>
            {
                m.Tables["Customer"].Columns["CustomerKey"].IsKey = true;
                m.Tables["Sales"].Columns["Month Start Date"].FormatString = "";
            });
            return f;
        }

        private static Task<SetResult> WaiveAsync(DecisionFixture f, string door, string system, string ruleId, string objRef) => door == "studio"
            ? new EngineRpcTarget(f.Engine).waiveFinding(system, ruleId, objRef, Why)
            : McpTools.WaiveFinding(f.Engine, system, ruleId, objRef, Why);

        private static Task<SetResult> UnwaiveAsync(DecisionFixture f, string door, string system, string ruleId, string objRef) => door == "studio"
            ? new EngineRpcTarget(f.Engine).unwaiveFinding(system, ruleId, objRef)
            : McpTools.UnwaiveFinding(f.Engine, system, ruleId, objRef);

        private static Task<ChangePlanView> ProposeAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).proposePlan()
            : McpTools.ProposePlan(f.Engine);

        private static Task<ApplyPlanReport> ApplyAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).applyPlan()
            : McpTools.ApplyPlan(f.Engine);

        private static Task<BpaFixAllResult> FixAllAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).bpaFixAll()
            : McpTools.ApplySafeFixes(f.Engine, "bpa").ContinueWith(t => (BpaFixAllResult)t.Result);

        private static Task<T> Read<T>(DecisionFixture f, Func<Model, T> read) => f.Sessions.Require().ReadAsync(read);

        private static bool Writes(ChangeItem i, string objRef, string property) =>
            i.ObjectRef == objRef && string.Equals(i.Target, property, StringComparison.OrdinalIgnoreCase);

        // ---- N2, the acceptance outlives the finding, on the paths the round 5 file does not reach ---------------------

        /// <summary>Rule-level and Tabular Editor forms of the round 5 case: the acceptance is not a per-object Semanticus record,
        /// and the key still sums after the plan's own Visibility step hides it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("rule-level")]
        [InlineData("te-ignore")]
        public async Task An_acceptance_of_every_kind_keeps_the_key_summing_once_its_finding_stops_firing(string kind)
        {
            using var f = await StarAsync();
            if (kind == "rule-level") await WaiveAsync(f, "mcp", "bpa", "NUMERIC_COLUMN_SUMMARIZE_BY", null);
            else await f.Sessions.Require().MutateAsync("human", "a Tabular Editor ignore", m =>
                WaiverStore.WriteTeIgnore(m.Tables["Sales"].Columns["ProductKey"], "NUMERIC_COLUMN_SUMMARIZE_BY", true));

            for (var press = 0; press < 3; press++) await McpTools.ApplySafeFixes(f.Engine, "air");

            Assert.Equal((true, AggregateFunction.Sum), await Read(f, m => (m.Tables["Sales"].Columns["ProductKey"].IsHidden, m.Tables["Sales"].Columns["ProductKey"].SummarizeBy)));
        }

        /// <summary>The acceptance is still shown when its finding no longer fires, so it is never invisible while it declines.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_acceptance_whose_finding_stopped_firing_is_still_listed_as_accepted_on_both_pages()
        {
            using var f = await StarAsync();
            await WaiveAsync(f, "studio", "bpa", "NUMERIC_COLUMN_SUMMARIZE_BY", "column:Sales/ProductKey");
            await McpTools.ApplySafeFixes(f.Engine, "air");   // hides the key, so the accepted finding stops firing
            Assert.True(await Read(f, m => m.Tables["Sales"].Columns["ProductKey"].IsHidden));

            var bpa = await f.Engine.BpaScanAsync();
            Assert.DoesNotContain(bpa.Violations, v => !v.Waived && v.RuleId == "NUMERIC_COLUMN_SUMMARIZE_BY" && v.ObjectRef == "column:Sales/ProductKey");
            var row = Assert.Single(bpa.Violations, v => v.RuleId == "NUMERIC_COLUMN_SUMMARIZE_BY" && v.ObjectRef == "column:Sales/ProductKey");
            Assert.True(row.Waived);
            Assert.True(row.NoLongerFires);
            Assert.Equal(Why, row.WaiverReason);
            Assert.Equal(bpa.Violations.Count(v => v.IsAccepted()), bpa.WaivedCount);

            await WaiveAsync(f, "mcp", "air", "FMT-SUMMARIZE", "column:Product/ProductKey");
            await f.Engine.ApplyFixAsync("FMT-SUMMARIZE", "column:Product/ProductKey", "human");   // the finding stops firing
            var ready = await f.Engine.AiReadinessScanAsync();
            var quiet = Assert.Single(ready.Findings, x => x.RuleId == "FMT-SUMMARIZE" && x.ObjectRef == "column:Product/ProductKey");
            Assert.True(quiet.Waived);
            Assert.True(quiet.NoLongerFires);
            Assert.Equal(ready.Findings.Count(x => x.IsAccepted()), ready.WaivedCount);
        }

        // ---- N1 and N3 on the Free tier and the assistant's change set --------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task On_the_free_tier_an_accepted_proposal_finding_still_declines_its_change_on_every_path(string door)
        {
            using var f = await StarAsync(free: true);
            await WaiveAsync(f, door, "air", "VIS-KEY", "column:Customer/CustomerKey");
            await ProposeAsync(f, door);
            await ApplyAsync(f, door);
            await FixAllAsync(f, door);
            if (door == "studio") await new EngineRpcTarget(f.Engine).applySafeFixes(); else await McpTools.ApplySafeFixes(f.Engine, "air");
            var ready = door == "studio" ? await new EngineRpcTarget(f.Engine).makeAiReady() : await McpTools.MakeModelAiReady(f.Engine);

            Assert.False(await Read(f, m => m.Tables["Customer"].Columns["CustomerKey"].IsHidden));
            Assert.DoesNotContain(ready.AiQueue, w => w.Finding.ObjectRef == "column:Customer/CustomerKey" && w.Finding.Fix == "AiContent" && w.Finding.RuleId.StartsWith("VIS"));
        }

        /// <summary>N2 and N3 on the Free tier (Run the plan is Pro; apply_safe_fixes, the change plan and make_model_ai_ready are free):
        /// the acceptance outlives its finding, and the assistant's queue leaves out both a format and a rename that were declined.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task On_the_free_tier_the_acceptance_outlives_its_finding_and_the_queue_leaves_declined_changes_out(string door)
        {
            using var f = await StarAsync(free: true);
            await WaiveAsync(f, door, "bpa", "NUMERIC_COLUMN_SUMMARIZE_BY", "column:Sales/ProductKey");
            await WaiveAsync(f, door, "bpa", "INTEGER_FORMATTING", "measure:Sales/Order Count");
            await WaiveAsync(f, door, "air", "NAME-COLUMN", "column:Product/ProductName");
            for (var press = 0; press < 3; press++)
                if (door == "studio") await new EngineRpcTarget(f.Engine).applySafeFixes(); else await McpTools.ApplySafeFixes(f.Engine, "air");
            await ProposeAsync(f, door);
            await ApplyAsync(f, door);
            await ProposeAsync(f, door);
            await ApplyAsync(f, door);
            var ready = door == "studio" ? await new EngineRpcTarget(f.Engine).makeAiReady() : await McpTools.MakeModelAiReady(f.Engine);

            Assert.Equal((true, AggregateFunction.Sum, ""), await Read(f, m => (m.Tables["Sales"].Columns["ProductKey"].IsHidden, m.Tables["Sales"].Columns["ProductKey"].SummarizeBy, m.Tables["Sales"].Measures["Order Count"].FormatString ?? "")));
            Assert.True(await Read(f, m => m.Tables["Product"].Columns.Any(c => c.Name == "ProductName")));
            Assert.DoesNotContain(ready.AiQueue, w => w.Finding.ObjectRef == "measure:Sales/Order Count" && w.Finding.RuleId == "FMT-MEASURE");
            Assert.DoesNotContain(ready.AiQueue, w => w.Finding.ObjectRef == "column:Product/ProductName" && w.Finding.RuleId == "DIM-PRIMARY-NAME");
        }

        /// <summary>The assistant's own Model quality change set (asst/bpa_fix) leaves a declined change out, including one
        /// declined by a readiness Proposal finding.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_assistants_model_quality_change_set_leaves_a_proposal_declined_change_out()
        {
            using var f = await StarAsync();
            await WaiveAsync(f, "mcp", "air", "VIS-KEY", "column:Customer/CustomerKey");
            var plan = await ProposeAsync(f, "mcp");
            Assert.All(plan.Items.Where(i => Writes(i, "column:Customer/CustomerKey", "isHidden") && !string.IsNullOrEmpty(i.RuleId)),
                i => { Assert.Equal("skipped", i.Status); Assert.Equal(Other, i.Note); });
            await ApplyAsync(f, "mcp");
            Assert.False(await Read(f, m => m.Tables["Customer"].Columns["CustomerKey"].IsHidden));
        }

        // ---- the narrower rule: one line, several asks (H1) ---------------------------------------------------------------

        /// <summary>Instructions over the 10,000-character cap with an undefined code: one plan line carries both findings.</summary>
        private static async Task<DecisionFixture> InstructionsAsync()
        {
            var f = await StarAsync();
            await f.Engine.CreateMeasureAsync("table:Sales", "QRX Revenue", "1", "human");
            await f.Engine.CreateLinguisticSchemaAsync(null, "agent");
            await f.Engine.SetAiInstructionsAsync("MARKER-TEXT use fiscal years and report currency.", null, "agent");
            await f.Sessions.Require().MutateAsync("human", "setup: long instructions", m =>
            {
                var cult = m.Cultures.First(c => c.Content != null && c.Content.Contains("MARKER-TEXT"));
                cult.Content = cult.Content.Replace("MARKER-TEXT", new string('a', 10600));
            });
            return f;
        }

        private static ChangeItem InstructionsLine(ChangePlanView plan) => plan.Items.Single(i => i.Target == "aiInstructions");

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Accepting_one_instructions_finding_keeps_the_line_for_the_other_and_takes_its_words_out()
        {
            using var f = await InstructionsAsync();
            var card = await f.Engine.AiReadinessScanAsync();
            var len = card.Findings.Single(x => x.RuleId == "DAC-AI-INSTRUCTIONS-LEN");
            var gap = card.Findings.Single(x => x.RuleId == "DAC-GLOSSARY-GAP");
            var whole = InstructionsLine(await ProposeAsync(f, "mcp"));
            Assert.Contains(len.Message, whole.Rationale);
            Assert.Contains(gap.Message, whole.Rationale);

            // accepted BEFORE the plan: the glossary finding is accepted, the length finding still stands
            await WaiveAsync(f, "mcp", "air", "DAC-GLOSSARY-GAP", gap.ObjectRef);
            var plan = await ProposeAsync(f, "mcp");
            var line = InstructionsLine(plan);
            Assert.NotEqual("skipped", line.Status);
            Assert.Contains(len.Message, line.Rationale);
            Assert.DoesNotContain(gap.Message, line.Rationale);

            // accepted AFTER the plan: the same, with the words taken out of the line that is already there
            await UnwaiveAsync(f, "mcp", "air", "DAC-GLOSSARY-GAP", gap.ObjectRef);
            plan = await ProposeAsync(f, "mcp");
            Assert.Contains(gap.Message, InstructionsLine(plan).Rationale);
            await WaiveAsync(f, "studio", "air", "DAC-AI-INSTRUCTIONS-LEN", len.ObjectRef);
            line = InstructionsLine(await f.Engine.GetPlanAsync());
            Assert.NotEqual("skipped", line.Status);
            Assert.Contains(gap.Message, line.Rationale);
            Assert.DoesNotContain(len.Message, line.Rationale);

            // both accepted: nothing left to ask, so the line is left out; undo the second and it is back with the first's words
            await WaiveAsync(f, "studio", "air", "DAC-GLOSSARY-GAP", gap.ObjectRef);
            line = InstructionsLine(await f.Engine.GetPlanAsync());
            Assert.Equal("skipped", line.Status);
            await UnwaiveAsync(f, "studio", "air", "DAC-GLOSSARY-GAP", gap.ObjectRef);
            line = InstructionsLine(await f.Engine.GetPlanAsync());
            Assert.NotEqual("skipped", line.Status);
            Assert.Contains(gap.Message, line.Rationale);
        }

        // ---- the narrower rule: sibling rules write different values (A3, H2) --------------------------------------------

        // ---- the lows ---------------------------------------------------------------------------------------------------

        /// <summary>L1: the note names the right reason, before and after the plan and after undo and redo.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_note_names_the_acceptance_that_left_the_line_out()
        {
            using var f = await StarAsync();
            const string key = "column:Sales/CustomerKey";
            var plan = await ProposeAsync(f, "studio");
            Assert.Contains(plan.Items, i => Writes(i, key, "isHidden") && i.Status == "approved");

            await WaiveAsync(f, "studio", "bpa", "HIDE_FOREIGN_KEYS", key);   // another rule's acceptance, after the plan
            var line = (await f.Engine.GetPlanAsync()).Items.Single(i => Writes(i, key, "isHidden") && !string.IsNullOrEmpty(i.RuleId));
            Assert.Equal((("skipped"), Other), (line.Status, line.Note));

            await f.Engine.UndoAsync("human");
            line = (await f.Engine.GetPlanAsync()).Items.Single(i => Writes(i, key, "isHidden") && !string.IsNullOrEmpty(i.RuleId));
            Assert.Equal("approved", line.Status);
            await f.Engine.RedoAsync("human");
            line = (await f.Engine.GetPlanAsync()).Items.Single(i => Writes(i, key, "isHidden") && !string.IsNullOrEmpty(i.RuleId));
            Assert.Equal((("skipped"), Other), (line.Status, line.Note));   // after redo: still another rule's acceptance, not "after it was planned"

            await WaiveAsync(f, "studio", "air", "VIS-FK", key);   // and now the line's own finding is accepted too
            line = (await f.Engine.GetPlanAsync()).Items.Single(i => Writes(i, key, "isHidden") && !string.IsNullOrEmpty(i.RuleId));
            Assert.Equal(("skipped", After), (line.Status, line.Note)) ;
        }

        /// <summary>L2: Fix all lists a declined twin under Unfixed with its reason, so remaining 0 is only said when true.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Fix_all_lists_a_declined_twin_with_its_reason_instead_of_claiming_nothing_remains(string door)
        {
            using var f = await StarAsync();
            await WaiveAsync(f, door, "air", "VIS-KEY", "column:Customer/CustomerKey");
            var fixAll = await FixAllAsync(f, door);
            var left = Assert.Single(fixAll.Unfixed, u => u.RuleId == "HIDE_FOREIGN_KEYS" && u.ObjectRef == "column:Customer/CustomerKey");
            Assert.Contains("related finding", left.Reason);
            Assert.True(fixAll.Remaining >= 1);
            Assert.False(await Read(f, m => m.Tables["Customer"].Columns["CustomerKey"].IsHidden));
        }

        /// <summary>L3, as round 8 rules it: a related finding is left alone by every path, but it is not accepted: both pages keep it open,
        /// and the grade and the counts include it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_declined_twin_stays_open_on_both_findings_pages_and_the_grade_counts_it()
        {
            using var f = await StarAsync();
            const string key = "column:Sales/CustomerKey";
            var before = await f.Engine.AiReadinessScanAsync();
            await WaiveAsync(f, "mcp", "bpa", "HIDE_FOREIGN_KEYS", key);

            var ready = await f.Engine.AiReadinessScanAsync();
            var twin = ready.Findings.Single(x => x.RuleId == "VIS-FK" && x.ObjectRef == key);
            Assert.True(twin.Waived);   // every path leaves its change alone
            Assert.True(twin.WaivedAsTwin);   // and nobody accepted it
            Assert.Equal(before.WaivedCount, ready.WaivedCount);
            Assert.Equal(before.Overall, ready.Overall);   // an open finding still counts against the grade

            var bpa = await f.Engine.BpaScanAsync();
            Assert.Equal(bpa.Violations.Count(v => !v.IsAccepted()), bpa.ViolationCount);
        }

        /// <summary>L5: a line an acceptance left out does not read approved when it is ticked; unwaive puts it back ticked.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Ticking_a_left_out_line_does_not_make_it_read_approved(string door)
        {
            using var f = await StarAsync();
            const string key = "column:Sales/CustomerKey";
            await WaiveAsync(f, door, "bpa", "HIDE_FOREIGN_KEYS", key);
            var plan = await ProposeAsync(f, door);
            var line = plan.Items.Single(i => Writes(i, key, "isHidden") && !string.IsNullOrEmpty(i.RuleId));
            Assert.Equal("skipped", line.Status);

            var ticked = door == "studio" ? await new EngineRpcTarget(f.Engine).setPlanItem(line.Id, null, true) : await McpTools.SetPlanItem(f.Engine, line.Id, null, true);
            line = ticked.Items.Single(i => i.Id == line.Id);
            Assert.Equal("skipped", line.Status);
            Assert.Equal(Other, line.Note);

            await UnwaiveAsync(f, door, "bpa", "HIDE_FOREIGN_KEYS", key);
            line = (await f.Engine.GetPlanAsync()).Items.Single(i => i.Id == line.Id);
            Assert.Equal("approved", line.Status);
        }

        /// <summary>L6: a Tabular Editor ignore on any object kind is seen, so a model with only that ignore is not answered "none".</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_tabular_editor_ignore_on_any_object_kind_is_seen()
        {
            using var f = await StarAsync();
            await f.Sessions.Require().MutateAsync("human", "objects of every kind", m =>
            {
                var t = m.Tables["Sales"];
                var h = t.AddHierarchy("Geo"); h.AddLevel(t.Columns["CustomerKey"], "Lvl");
                m.AddPerspective("Everything");
                m.AddRole("Reader");
            });
            var kinds = new Dictionary<string, Func<Model, ITabularObject>>
            {
                ["level"] = m => m.Tables["Sales"].Hierarchies["Geo"].Levels.First(),
                ["hierarchy"] = m => m.Tables["Sales"].Hierarchies["Geo"],
                ["perspective"] = m => m.Perspectives["Everything"],
                ["role"] = m => m.Roles["Reader"],
                ["partition"] = m => m.Tables["Sales"].Partitions.First(),
                ["relationship"] = m => m.Relationships.First(),
                ["model"] = m => m,
            };
            foreach (var (kind, pick) in kinds)
            {
                Assert.False(await Read(f, m => WaiverStore.AnyTeIgnore(m)), kind + ": nothing yet");
                await f.Sessions.Require().MutateAsync("human", "ignore " + kind, m => WaiverStore.WriteTeIgnore(pick(m), "HIDE_FOREIGN_KEYS", true));
                Assert.True(await Read(f, m => WaiverStore.AnyTeIgnore(m)), kind + ": the ignore is seen");
                await f.Sessions.Require().MutateAsync("human", "clear " + kind, m => WaiverStore.WriteTeIgnore(pick(m), "HIDE_FOREIGN_KEYS", false));
            }
        }
    }
}

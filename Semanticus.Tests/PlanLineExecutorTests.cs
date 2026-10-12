using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The Plan's synchronous line loop, extracted into <see cref="PlanLineExecutor"/> so the Plan and the start plan's one build step
    /// share it (design-spec-first-start-v2, S5 and S12). Each test below states the same list of lines two ways: through a NAIVE loop
    /// (ApplyOrder plus ApplyOneItem, which is what a first cut of the build would be) and through the executor.
    /// The naive loop is kept on purpose: it is the proof of what the executor adds, and it must stay wrong.
    /// </summary>
    public sealed class PlanLineExecutorTests
    {
        private const string Item = "jev.column.summarize.table:Sales";

        /// <summary>Sales[Qty] (a whole number column) and one measure over it.</summary>
        private static async Task<DecisionFixture> ModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Exec", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Qty", "Int64", "Qty", "human");
            return f;
        }

        private static ChangeItem Line(string kind, string objectRef, string after, string before = "(none)", string point = null, string needs = null) =>
            new ChangeItem
            {
                Id = "t-" + Guid.NewGuid().ToString("N").Substring(0, 6), ObjectRef = objectRef, Kind = kind, Source = "jev", JevItem = Item,
                Target = kind, Before = before, After = after, Status = "approved", DecisionPoint = point, Needs = needs, Risk = "safe",
            };

        private static ChangeItem Rename(string objectRef, string to) =>
            new ChangeItem
            {
                Id = "t-" + Guid.NewGuid().ToString("N").Substring(0, 6), ObjectRef = objectRef, Kind = "rename", Source = "deterministic", Target = "name",
                Before = objectRef.Substring(objectRef.LastIndexOfAny(new[] { ':', '/' }) + 1), After = to, Status = "approved", Risk = "rename",
            };

        /// <summary>A person's own tick on a column's format, taken against the object as it is now.</summary>
        private static async Task<ChangeItem> KeptColumnFormatAsync(DecisionFixture f, string columnRef, string format)
        {
            var line = Line("set_format_string", columnRef, format, point: DecisionRegistry.ColumnFormat);
            line.KeptByPerson = true;
            line.KeptAgainst = "(none)";
            line.KeptDigest = await f.Sessions.Require().ReadAsync(m => FormatFill.ComputeDigest(ObjectRefs.Resolve(m, columnRef)));
            return line;
        }

        /// <summary>What a first cut of a build would do: order the lines the way the Plan does, then write each one.</summary>
        private static Task NaiveApplyAsync(DecisionFixture f, IEnumerable<ChangeItem> lines)
        {
            var s = f.Sessions.Require();
            return s.MutateAsync("human", "naive build", m =>
            {
                foreach (var it in lines.OrderBy(LocalEngine.ApplyOrder).ToList())
                    f.Engine.ApplyOneItem(m, it, s);
            });
        }

        // ---- 1. a kept Jev format follows its column through the renames of the same list ------------------------------------

        [Fact]
        public async Task Naive_loop_loses_a_kept_format_whose_column_is_renamed_in_the_same_list()
        {
            using var f = await ModelAsync();
            var lines = new List<ChangeItem>
            {
                await KeptColumnFormatAsync(f, "column:Sales/Qty", "#,##0"),
                Rename("column:Sales/Qty", "Quantity"),
                Rename("table:Sales", "Sales Orders"),
            };
            // The format is ranked after the renames, and its ref still says Sales/Qty: nothing is there to write to.
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => NaiveApplyAsync(f, lines));
            Assert.Contains("column:Sales/Qty", ex.Message, StringComparison.Ordinal);
        }

        // ---- 2. a hide whose measure line is not being applied --------------------------------------------------------------------

        private const string TotalRef = "measure:Sales/Total Qty";
        private static readonly string NeedsTotal = JevFamily.NeedsKey("create_measure", TotalRef);

        private static List<ChangeItem> BaseMeasureLines(string status = "approved") => new List<ChangeItem>
        {
            Line("create_measure", TotalRef, "SUM ( Sales[Qty] )"),
            Line("set_description", TotalRef, "Everything sold, added up.", needs: NeedsTotal),
            Line("set_display_folder", TotalRef, "Totals", needs: NeedsTotal),
        }.Select(l => { l.Status = status; return l; }).ToList();

        [Fact]
        public async Task Naive_loop_hides_a_column_whose_measure_line_is_absent()
        {
            using var f = await ModelAsync();
            var hide = Line("set_column_hidden", "column:Sales/Qty", "Hidden", before: "Visible", needs: NeedsTotal);
            await NaiveApplyAsync(f, new[] { hide });
            Assert.True(await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Columns["Qty"].IsHidden),
                "the naive loop applied a hide that no measure stands behind, which is what the Needs link exists to stop");
        }

        // ---- 3. dropping a base measure ------------------------------------------------------------------------------------

        [Fact]
        public async Task Naive_loop_after_a_drop_of_the_measure_still_writes_its_followers_and_fails()
        {
            using var f = await ModelAsync();
            var lines = BaseMeasureLines();
            lines[0].Status = "rejected";   // dropped on its own: nothing moves the followers with it
            var kept = lines.Where(l => l.Status == "approved").ToList();
            Assert.Equal(2, kept.Count);
            await Assert.ThrowsAnyAsync<Exception>(() => NaiveApplyAsync(f, kept));
        }

        // ---- the executor: the same three lists, written the way the Plan writes them -------------------------------------------

        private static async Task<PlanLineExecutor> RunAsync(DecisionFixture f, List<ChangeItem> lines, PlanLineOptions options = null, IJevModelFacts facts = null)
        {
            PlanLineExecutor ran = null;
            var s = f.Sessions.Require();
            await s.MutateAsync("human", "executor build", m => ran = PlanLineExecutor.ApplyInBatch(f.Engine, m, s, lines, options, facts));
            return ran;
        }

        private static Task<T> ReadAsync<T>(DecisionFixture f, Func<Model, T> read) => f.Sessions.Require().ReadAsync(read);

        [Fact]
        public async Task Executor_lands_a_kept_format_on_the_column_that_was_renamed_in_the_same_list()
        {
            using var f = await ModelAsync();
            var format = await KeptColumnFormatAsync(f, "column:Sales/Qty", "#,##0");
            var lines = new List<ChangeItem> { format, Rename("column:Sales/Qty", "Quantity"), Rename("table:Sales", "Sales Orders") };

            var ran = await RunAsync(f, lines);

            Assert.True(ran.Applied == 3, string.Join(" | ", lines.Select(l => l.Kind + " " + l.ObjectRef + " " + l.Status + ": " + l.Note)));
            Assert.Equal(0, ran.Failed);
            Assert.All(lines, l => Assert.Equal("applied", l.Status));
            Assert.Equal("#,##0", await ReadAsync(f, m => m.Tables["Sales Orders"].Columns["Quantity"].FormatString));
            Assert.Equal("column:Sales Orders/Quantity", format.ObjectRef);   // the ref was refreshed to where the column is now
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Executor_does_not_write_a_format_onto_a_different_column_that_took_the_old_name()
        {
            using var f = await ModelAsync();
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Int64", "Amount", "human");
            var format = await KeptColumnFormatAsync(f, "column:Sales/Qty", "#,##0");
            // Qty goes away to Quantity, and Amount takes the name Qty: the line is about the object, not the name.
            var lines = new List<ChangeItem> { format, Rename("column:Sales/Qty", "Quantity"), Rename("column:Sales/Amount", "Qty") };

            await RunAsync(f, lines);

            Assert.Equal("#,##0", await ReadAsync(f, m => m.Tables["Sales"].Columns["Quantity"].FormatString));
            Assert.True(string.IsNullOrEmpty(await ReadAsync(f, m => m.Tables["Sales"].Columns["Qty"].FormatString)));
        }

        [Fact]
        public async Task Executor_skips_a_hide_whose_measure_line_is_absent_and_says_why()
        {
            using var f = await ModelAsync();
            var hide = Line("set_column_hidden", "column:Sales/Qty", "Hidden", before: "Visible", needs: NeedsTotal);
            var measure = BaseMeasureLines("proposed")[0];   // on the plan, but not being applied with the hide

            var ran = await RunAsync(f, new List<ChangeItem> { hide }, new PlanLineOptions { CloseNeedsAgainst = new[] { hide, measure } });

            Assert.Equal(0, ran.Applied);
            Assert.Equal("skipped", hide.Status);
            Assert.StartsWith("not applied: it needs", hide.Note, StringComparison.Ordinal);
            Assert.Contains(TotalRef, hide.Note, StringComparison.Ordinal);
            Assert.False(await ReadAsync(f, m => m.Tables["Sales"].Columns["Qty"].IsHidden));
        }

        [Fact]
        public void The_needs_closure_holds_back_what_needs_a_skipped_line_in_turn()
        {
            var measure = BaseMeasureLines("proposed")[0];
            var hide = Line("set_column_hidden", "column:Sales/Qty", "Hidden", before: "Visible", needs: NeedsTotal);
            var excel = Line("set_is_available_in_mdx", "column:Sales/Qty", "false", before: "true", needs: JevFamily.NeedsKey("set_column_hidden", "column:Sales/Qty"));
            var toApply = new List<ChangeItem> { hide, excel };

            PlanLineExecutor.CloseNeeds(new[] { measure, hide, excel }, toApply);

            Assert.Empty(toApply);
            Assert.Equal("skipped", hide.Status);
            Assert.Equal("skipped", excel.Status);
        }

        [Fact]
        public async Task Executor_applies_a_hide_together_with_the_measure_it_needs()
        {
            using var f = await ModelAsync();
            var lines = BaseMeasureLines();
            var hide = Line("set_column_hidden", "column:Sales/Qty", "Hidden", before: "Visible", needs: NeedsTotal);
            lines.Add(hide);

            var ran = await RunAsync(f, lines, new PlanLineOptions { CloseNeedsAgainst = lines.ToList() });

            Assert.Equal(0, ran.Failed);
            Assert.All(lines, l => Assert.Equal("applied", l.Status));
            Assert.True(await ReadAsync(f, m => m.Tables["Sales"].Columns["Qty"].IsHidden));
            Assert.Equal("Totals", await ReadAsync(f, m => m.Tables["Sales"].Measures["Total Qty"].DisplayFolder));
        }

        [Fact]
        public async Task Dropping_a_base_measure_drops_its_followers_and_keeping_one_brings_the_measure_back()
        {
            using var f = await ModelAsync();
            var lines = BaseMeasureLines();
            var hide = Line("set_column_hidden", "column:Sales/Qty", "Hidden", before: "Visible", needs: NeedsTotal);
            lines.Add(hide);

            lines[0].Status = "rejected";
            PlanLineExecutor.FollowKeep(lines[0], lines, keep: false);
            Assert.All(lines, l => Assert.Equal("rejected", l.Status));

            var ran = await RunAsync(f, lines.Where(l => l.Status == "approved").ToList());
            Assert.Equal(0, ran.Applied + ran.Failed);
            Assert.Empty(await ReadAsync(f, m => m.Tables["Sales"].Measures.Select(x => x.Name).ToList()));

            // Keeping the hide keeps what it needs, and what needs that.
            hide.Status = "approved";
            PlanLineExecutor.FollowKeep(hide, lines, keep: true);
            Assert.All(lines, l => Assert.Equal("approved", l.Status));
        }

        [Fact]
        public void A_drop_that_reaches_nobody_else_leaves_other_items_alone()
        {
            var lines = BaseMeasureLines();
            var other = Line("set_description", "measure:Sales/Other", "Another.", needs: JevFamily.NeedsKey("create_measure", "measure:Sales/Other"));
            other.JevItem = "jev.column.summarize.table:Elsewhere";
            lines.Add(other);

            PlanLineExecutor.FollowKeep(lines[0], lines, keep: false);

            Assert.Equal("approved", other.Status);
        }

        // ---- the facts the two data checks read ------------------------------------------------------------------------------------

        private sealed class OneToOne : IJevModelFacts
        {
            public IReadOnlyList<string> OneToOneWith(string columnRef) => columnRef == "column:Sales/Month" ? new[] { "column:Sales/MonthNo" } : null;
            public bool? WholeNumbers(string columnRef) => null;
        }

        private static async Task<DecisionFixture> SortModelAsync()
        {
            var f = await ModelAsync();
            await f.Engine.CreateColumnAsync("table:Sales", "Month", "String", "Month", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "MonthNo", "Int64", "MonthNo", "human");
            return f;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Null_facts_are_the_engines_own_so_an_unmeasured_sort_is_refused_as_it_always_was()
        {
            using var f = await SortModelAsync();
            var sort = Line("set_sort_by_column", "column:Sales/Month", "MonthNo", before: "(none)");

            await RunAsync(f, new List<ChangeItem> { sort }, facts: null);

            Assert.Equal("failed", sort.Status);
            Assert.Contains("have not been measured in full", sort.Note, StringComparison.Ordinal);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Injected_facts_reach_the_sort_check()
        {
            using var f = await SortModelAsync();
            var sort = Line("set_sort_by_column", "column:Sales/Month", "MonthNo", before: "(none)");

            await RunAsync(f, new List<ChangeItem> { sort }, facts: new OneToOne());

            Assert.Equal("applied", sort.Status);
            Assert.Equal("MonthNo", await ReadAsync(f, m => m.Tables["Sales"].Columns["Month"].SortByColumn?.Name));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_engines_own_test_facts_still_answer_when_none_are_injected()
        {
            using var f = await SortModelAsync();
            f.Engine.JevFactsForTest = () => new OneToOne();
            var sort = Line("set_sort_by_column", "column:Sales/Month", "MonthNo", before: "(none)");

            await RunAsync(f, new List<ChangeItem> { sort });

            Assert.Equal("applied", sort.Status);
        }

        // ---- an approval stays all of the kept lines or none -----------------------------------------------------------------------

        [Fact]
        public async Task Under_an_approval_one_line_that_cannot_be_made_puts_the_whole_batch_back()
        {
            using var f = await ModelAsync();
            var s = f.Sessions.Require();
            var good = Line("set_description", "column:Sales/Qty", "How many.");
            good.Source = "deterministic"; good.JevItem = null;
            var bad = Line("set_description", "column:Sales/Nope", "Nothing is here.");
            bad.Source = "deterministic"; bad.JevItem = null;
            bad.Before = "(none)";
            var lines = new List<ChangeItem> { good, bad };
            var guard = new PlanApprovalGuard
            {
                Digest = "d", Revision = s.Revision, SessionId = s.Id, ItemId = "row",
                ReadSourceVersion = () => null,
            };
            foreach (var l in lines) { l.Id = "ci-" + (lines.IndexOf(l) + 1); guard.Lines[l.Id] = new ApprovedLine { Op = l.Kind, ObjectRef = l.ObjectRef, Property = l.Target, Before = l.Before, After = l.After, Status = l.Status }; }
            var revisionBefore = s.Revision;

            var ex = await Assert.ThrowsAsync<PlanApprovalRefusedException>(() =>
                RunAsync(f, lines, new PlanLineOptions { Guards = new[] { guard } }));

            Assert.Contains("the whole set was put back", ex.Message, StringComparison.Ordinal);
            Assert.Equal(bad.Id, ex.Line?.Id);
            Assert.Equal(revisionBefore, s.Revision);
            Assert.True(string.IsNullOrEmpty(await ReadAsync(f, m => m.Tables["Sales"].Columns["Qty"].Description)), "the good line was kept although the set was refused");
        }

        [Fact]
        public async Task Without_an_approval_the_same_two_lines_apply_what_they_can()
        {
            using var f = await ModelAsync();
            var good = Line("set_description", "column:Sales/Qty", "How many.");
            good.Source = "deterministic"; good.JevItem = null;
            var bad = Line("set_description", "column:Sales/Nope", "Nothing is here.");
            bad.Source = "deterministic"; bad.JevItem = null;

            var ran = await RunAsync(f, new List<ChangeItem> { good, bad });

            Assert.Equal(1, ran.Applied);
            Assert.Equal(1, ran.Failed);
            Assert.Equal("How many.", await ReadAsync(f, m => m.Tables["Sales"].Columns["Qty"].Description));
        }
    }
}

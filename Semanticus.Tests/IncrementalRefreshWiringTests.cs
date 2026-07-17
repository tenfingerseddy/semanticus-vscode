using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The POLICY auto-wire door (set_incremental_refresh_policy + the UI Save) — distinct from the
    /// static wiring kernel below. M filters the PARTITION OUTPUT, so the authored field must be the column's
    /// SourceColumn (which can differ from the model Name), and a calculated column (no source-side field) must
    /// be rejected honestly, on BOTH doors, engine-side.</summary>
    public sealed class IncrementalRefreshAutoWirePolicyTests : IAsyncLifetime
    {
        private SessionManager _sessions = null!;
        private LocalEngine _engine = null!;
        private string _tableRef = null!;

        public async Task InitializeAsync()
        {
            _sessions = new SessionManager();
            _engine = new LocalEngine(_sessions);
            await _engine.OpenAsync(TestModels.FindBim());
            await _engine.SetCompatibilityLevelAsync(1604, "human");
            _tableRef = await _engine.CreateImportTableAsync("IR_Wire", "let\n    Source = #table(type table [order_dt = datetime], {})\nin\n    Source", "agent");
        }

        public Task DisposeAsync() { _engine.Dispose(); return Task.CompletedTask; }

        [Fact]
        public async Task Policy_auto_wire_emits_the_SourceColumn_name_in_M_not_the_model_name()
        {
            // Model name 'OrderDate' over source field 'order_dt' — the wired filter must use the SOURCE name;
            // [OrderDate] would filter a field the partition query never yields (dead M, empty refreshes).
            await _engine.CreateColumnAsync(_tableRef, "OrderDate", "DateTime", "order_dt", "agent");
            await _engine.SetIncrementalRefreshPolicyAsync(_tableRef, "OrderDate", 5, "Year", 10, "Day", 0, "Import", null, autoWire: true, "agent");

            var partition = (await _engine.ListPartitionsAsync(_tableRef)).Single();
            var m = await _engine.GetPartitionMAsync($"partition:IR_Wire/{partition.Name}");
            Assert.Contains("[order_dt] >= RangeStart and [order_dt] < RangeEnd", m);
            Assert.DoesNotContain("[OrderDate]", m);
        }

        [Fact]
        public async Task Policy_auto_wire_rejects_a_calculated_date_column_with_the_honest_reason()
        {
            await _engine.CreateCalculatedColumnAsync(_tableRef, "CalcDate", "TODAY()", "agent");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _engine.SetIncrementalRefreshPolicyAsync(_tableRef, "CalcDate", 5, "Year", 10, "Day", 0, "Import", null, autoWire: true, "agent"));
            Assert.Contains("needs a data column loaded from the source", ex.Message);
            Assert.Contains("'CalcDate' is calculated", ex.Message);
        }

        [Fact]
        public async Task Policy_rejects_a_date_column_that_disagrees_with_the_already_wired_field()
        {
            // Wire on OrderDate (source order_dt). The engine never rewrites an existing filter, so a later save
            // claiming a DIFFERENT column would leave the M on order_dt while the policy claims ship_dt — the exact
            // silent-disagreement the guard must refuse (MAJOR 2).
            await _engine.CreateColumnAsync(_tableRef, "OrderDate", "DateTime", "order_dt", "agent");
            await _engine.CreateColumnAsync(_tableRef, "ShipDate", "DateTime", "ship_dt", "agent");
            await _engine.SetIncrementalRefreshPolicyAsync(_tableRef, "OrderDate", 5, "Year", 10, "Day", 0, "Import", null, autoWire: true, "agent");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _engine.SetIncrementalRefreshPolicyAsync(_tableRef, "ShipDate", null, null, null, null, null, null, null, autoWire: true, "agent"));
            Assert.Contains("[order_dt]", ex.Message);   // names the wired source field
            Assert.Contains("[ShipDate]", ex.Message);   // and the column the policy tried to claim

            // Re-submitting the wired column (OrderDate → order_dt) matches and proceeds; the DTO reports the field.
            await _engine.SetIncrementalRefreshPolicyAsync(_tableRef, "OrderDate", null, null, null, null, null, null, null, autoWire: true, "agent");
            var policy = await _engine.GetIncrementalRefreshPolicyAsync(_tableRef);
            Assert.True(policy.Enabled);
            Assert.Equal("order_dt", policy.WiredDateField);
        }

        [Fact]
        public async Task Policy_is_not_convicted_by_an_unrelated_nested_range_comparison()
        {
            // A nested SelectRows over ANOTHER query bounds [ship_dt] with RangeStart only (textually FIRST). A
            // first-match parser would read ship_dt and falsely convict the real filter on order_dt; the pair rule
            // ignores the half comparison, so submitting the truly-wired column proceeds clean.
            await _engine.CreateColumnAsync(_tableRef, "OrderDate", "DateTime", "order_dt", "agent");
            await _engine.CreateColumnAsync(_tableRef, "ShipDate", "DateTime", "ship_dt", "agent");
            var partition = (await _engine.ListPartitionsAsync(_tableRef)).Single();
            await _engine.SetPartitionMAsync($"partition:IR_Wire/{partition.Name}",
                "let\n    Other = Table.SelectRows(#table(type table [ship_dt = datetime], {}), each [ship_dt] >= RangeStart),\n"
                + "    Source = #table(type table [order_dt = datetime], {}),\n"
                + "    Filtered = Table.SelectRows(Source, each [order_dt] >= RangeStart and [order_dt] < RangeEnd)\nin\n    Filtered", "agent");

            var result = await _engine.SetIncrementalRefreshPolicyAsync(_tableRef, "OrderDate", 5, "Year", 10, "Day", 0, "Import", null, autoWire: true, "agent");

            Assert.Null(result.Warning);
            Assert.True((await _engine.GetIncrementalRefreshPolicyAsync(_tableRef)).Enabled);
        }

        [Fact]
        public async Task Policy_warns_without_rejecting_when_the_wired_field_matches_no_columns_source()
        {
            // The filter is on the PRE-RENAME field [order_dt_raw]; a Table.RenameColumns step downstream makes
            // that legitimate M whose output field IS order_dt. No column claims order_dt_raw, so the mismatch is
            // unprovable: the save proceeds with the advisory instead of a hard rejection.
            await _engine.CreateColumnAsync(_tableRef, "OrderDate", "DateTime", "order_dt", "agent");
            var partition = (await _engine.ListPartitionsAsync(_tableRef)).Single();
            await _engine.SetPartitionMAsync($"partition:IR_Wire/{partition.Name}",
                "let\n    Source = #table(type table [order_dt_raw = datetime], {}),\n"
                + "    Filtered = Table.SelectRows(Source, each [order_dt_raw] >= RangeStart and [order_dt_raw] < RangeEnd),\n"
                + "    Renamed = Table.RenameColumns(Filtered, {{\"order_dt_raw\", \"order_dt\"}})\nin\n    Renamed", "agent");

            var result = await _engine.SetIncrementalRefreshPolicyAsync(_tableRef, "OrderDate", 5, "Year", 10, "Day", 0, "Import", null, autoWire: true, "agent");

            Assert.Contains("[order_dt_raw]", result.Warning);
            Assert.Contains("verify the policy's date column", result.Warning);
            Assert.True((await _engine.GetIncrementalRefreshPolicyAsync(_tableRef)).Enabled);
        }

        [Fact]
        public async Task Policy_auto_wire_authors_a_real_filter_when_the_only_range_mention_is_inside_a_string()
        {
            // The prerequisite detector shares the parser's non-code masking: a range "filter" living inside a
            // STRING literal is not a filter, so auto-wire must author the real one instead of enabling the
            // policy on a fake (which the parser would then read as unknown and wave through).
            await _engine.CreateColumnAsync(_tableRef, "OrderDate", "DateTime", "order_dt", "agent");
            var partition = (await _engine.ListPartitionsAsync(_tableRef)).Single();
            await _engine.SetPartitionMAsync($"partition:IR_Wire/{partition.Name}",
                "let\n    Source = #table(type table [order_dt = datetime], {}),\n"
                + "    Diagnostic = \"[order_dt] >= RangeStart and [order_dt] < RangeEnd\"\nin\n    Source", "agent");

            await _engine.SetIncrementalRefreshPolicyAsync(_tableRef, "OrderDate", 5, "Year", 10, "Day", 0, "Import", null, autoWire: true, "agent");

            var m = await _engine.GetPartitionMAsync($"partition:IR_Wire/{partition.Name}");
            Assert.Contains("Table.SelectRows(Source, each [order_dt] >= RangeStart and [order_dt] < RangeEnd)", m);
            Assert.True((await _engine.GetIncrementalRefreshPolicyAsync(_tableRef)).Enabled);
        }

        [Fact]
        public async Task Policy_rejects_a_calculated_date_column_even_when_a_range_filter_already_exists()
        {
            // With an existing filter the authoring path is skipped, but a calculated column is DEFINITIVELY
            // unavailable to M whatever the filter says — the validator rejects it before any parse, on every path.
            await _engine.CreateColumnAsync(_tableRef, "OrderDate", "DateTime", "order_dt", "agent");
            await _engine.SetIncrementalRefreshPolicyAsync(_tableRef, "OrderDate", 5, "Year", 10, "Day", 0, "Import", null, autoWire: true, "agent");
            await _engine.CreateCalculatedColumnAsync(_tableRef, "CalcDate", "TODAY()", "agent");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _engine.SetIncrementalRefreshPolicyAsync(_tableRef, "CalcDate", null, null, null, null, null, null, null, autoWire: true, "agent"));
            Assert.Contains("'CalcDate' is calculated", ex.Message);
        }
    }

    public sealed class IncrementalRefreshWiringTests
    {
        [Fact]
        public void Bare_source_is_wrapped_then_filtered_without_replacing_it()
        {
            var source = "Sql.Database(\"server\", \"warehouse\"){[Schema=\"dbo\",Item=\"Sales\"]}[Data]";

            var result = IncrementalRefreshWiring.AppendRangeFilter(source, "Order Date");

            Assert.Contains("Source = " + source, result);
            Assert.Contains("Table.SelectRows(Source, each [Order Date] >= RangeStart and [Order Date] < RangeEnd)", result);
            Assert.EndsWith("#\"Filtered for incremental refresh\"", result);
        }

        [Fact]
        public void Existing_let_chain_filters_the_step_named_by_in_not_an_unrelated_last_binding()
        {
            var source = "let\n    Source = #table(type table [Date = datetime], {}),\n    Kept = Table.FirstN(Source, 10),\n    Diagnostic = \"in, RangeStart\"\nin\n    Kept";

            var result = IncrementalRefreshWiring.AppendRangeFilter(source, "Date");

            Assert.Contains("Table.SelectRows(Kept, each [Date] >= RangeStart and [Date] < RangeEnd)", result);
            Assert.DoesNotContain("Table.SelectRows(Diagnostic", result);
            Assert.Contains("Diagnostic = \"in, RangeStart\"", result);
        }

        [Fact]
        public void Generated_step_name_is_unique_and_field_brackets_are_escaped()
        {
            var source = "let\n    Source = #table(type table [#\"Closed]At\" = datetime], {}),\n    #\"Filtered for incremental refresh\" = Source\nin\n    #\"Filtered for incremental refresh\"";

            var result = IncrementalRefreshWiring.AppendRangeFilter(source, "Closed]At");

            Assert.Contains("#\"Filtered for incremental refresh1\" = Table.SelectRows(#\"Filtered for incremental refresh\", each [Closed]]At] >= RangeStart and [Closed]]At] < RangeEnd)", result);
            Assert.EndsWith("#\"Filtered for incremental refresh1\"", result);
        }

        [Theory]
        [InlineData("", "Date", "no source")]
        [InlineData("Source", "", "date column")]
        public void Missing_inputs_fail_before_any_M_is_returned(string source, string column, string message)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => IncrementalRefreshWiring.AppendRangeFilter(source, column));
            Assert.Contains(message, ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        // The field the predicate compares against RangeStart/RangeEnd — parsed from either side, ']]' un-escaped.
        [InlineData("let Source = x, Filtered = Table.SelectRows(Source, each [order_dt] >= RangeStart and [order_dt] < RangeEnd) in Filtered", "order_dt")]
        [InlineData("Table.SelectRows(Source, each RangeStart <= [Closed]]At] and [Closed]]At] < RangeEnd)", "Closed]At")]
        public void TryParseRangeField_reads_the_field_the_filter_compares(string m, string expected)
        {
            Assert.True(IncrementalRefreshWiring.TryParseRangeField(m, out var field));
            Assert.Equal(expected, field);
        }

        [Fact]
        public void TryParseRangeField_ignores_a_comment_only_mention()
        {
            // A comment that merely names RangeStart is not a real predicate — no field to parse.
            Assert.False(IncrementalRefreshWiring.TryParseRangeField("let Source = x /* [d] >= RangeStart */ in Source", out _));
        }

        [Fact]
        public void TryParseRangeField_returns_unknown_for_a_mixed_field_pair()
        {
            // [A] >= RangeStart and [B] < RangeEnd bounds no single field — reporting A would be a guess strong
            // enough to hard-reject on; the parser must say UNKNOWN instead.
            Assert.False(IncrementalRefreshWiring.TryParseRangeField(
                "Table.SelectRows(Source, each [A] >= RangeStart and [B] < RangeEnd)", out _));
        }

        [Fact]
        public void TryParseRangeField_returns_unknown_when_two_predicates_pair_different_fields()
        {
            // Two full pairs on different fields (e.g. the real filter plus a nested query's own window): no
            // single field can be THE wired one, so the parse stands down rather than pick a winner.
            Assert.False(IncrementalRefreshWiring.TryParseRangeField(
                "let A = Table.SelectRows(X, each [a] >= RangeStart and [a] < RangeEnd),"
                + " B = Table.SelectRows(Y, each [b] >= RangeStart and [b] < RangeEnd) in B", out _));
        }

        [Fact]
        public void TryParseRangeField_ignores_a_half_comparison_beside_the_real_pair()
        {
            // A nested predicate bounding another field with ONE parameter never forms a pair; only the
            // same-field RangeStart+RangeEnd pair counts, wherever it sits textually.
            Assert.True(IncrementalRefreshWiring.TryParseRangeField(
                "let Other = Table.SelectRows(Q, each [ship_dt] >= RangeStart),"
                + " Filtered = Table.SelectRows(Source, each [order_dt] >= RangeStart and [order_dt] < RangeEnd) in Filtered", out var field));
            Assert.Equal("order_dt", field);
        }

        [Fact]
        public void TryParseRangeField_does_not_pair_across_predicates()
        {
            // RangeStart bounds [d] in one lambda and RangeEnd bounds [d] in ANOTHER: the same-predicate rule
            // refuses to stitch them into a pair (each comparison may govern a different query).
            Assert.False(IncrementalRefreshWiring.TryParseRangeField(
                "let A = Table.SelectRows(X, each [d] >= RangeStart), B = Table.SelectRows(Y, each [d] < RangeEnd) in B", out _));
        }

        [Fact]
        public void TryParseRangeField_does_not_leak_a_lambda_segment_across_a_comma()
        {
            // The each lambda ends at its argument's comma; the NEXT argument's comparison must not share its
            // segment (without the comma-pop these two would falsely pair on [d]).
            Assert.False(IncrementalRefreshWiring.TryParseRangeField(
                "f(x, each [d] >= RangeStart, [d] < RangeEnd)", out _));
        }

        [Fact]
        public void TryParseRangeField_pairs_inside_an_explicit_arrow_lambda()
        {
            // (r) => ... is a predicate scope exactly like each: a pair inside it counts…
            Assert.True(IncrementalRefreshWiring.TryParseRangeField(
                "Table.SelectRows(X, (r) => r[d] >= RangeStart and r[d] < RangeEnd)", out var field));
            Assert.Equal("d", field);
        }

        [Fact]
        public void TryParseRangeField_does_not_pair_across_explicit_arrow_lambdas()
        {
            // …and two arrow lambdas are two scopes — their half comparisons never stitch into a pair.
            Assert.False(IncrementalRefreshWiring.TryParseRangeField(
                "let A = Table.SelectRows(X, (r) => r[d] >= RangeStart), B = Table.SelectRows(Y, (r) => r[d] < RangeEnd) in B", out _));
        }

        [Fact]
        public void TryParseRangeField_returns_unknown_for_a_quoted_identifier_field()
        {
            // [#"Order Date"] is masked before matching; decoding it would mean a second escape grammar, so the
            // parse stands down as UNKNOWN (safe under the tier table) instead of pairing a name it cannot read.
            Assert.False(IncrementalRefreshWiring.TryParseRangeField(
                "Table.SelectRows(S, each [#\"Order Date\"] >= RangeStart and [#\"Order Date\"] < RangeEnd)", out _));
        }
    }
}

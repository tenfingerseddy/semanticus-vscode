// Join 9: the fixes the join itself makes on top of the merged lanes. Each test here failed first on the merge of psc4, uatj8 and fixup.
//
// J1  probe_measure sends a date member as a date, as Explain does since U5 (a text literal against a Date column is refused by the engine).
// J3  load_rules and reset_rules (bpa) report a standard base that adds up with a scan: the bundled set plus the built-in rule psc4 added (the merged McpSmoke caught the one-off).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class Join9ProbeDateTests
    {
        [Fact]
        public async Task Probe_filters_send_a_member_of_a_date_column_as_a_date_and_other_members_as_they_were()
        {
            using var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Free);
            await e.OpenAsync(TestModels.FindBim());
            var scenario = new ProbeScenario
            {
                Name = "single-select date",
                Filters = new[]
                {
                    new ProbeFilter { Column = "'Date'[Date]", Members = new[] { "2018-06-25T00:00:00" } },
                    new ProbeFilter { Column = "'Customer'[Customer Id]", Members = new[] { "2018-06-25T00:00:00" } },
                    new ProbeFilter { Column = "'Date'[Date]", Empty = true },
                },
            };
            var args = await e.ProbeFilterArgsAsync(scenario);
            Assert.Equal("TREATAS ( { DATE ( 2018, 6, 25 ) }, 'Date'[Date] )", args[0]);
            Assert.StartsWith("TREATAS ( { \"2018-06-25T00:00:00\" }", args[1]);   // a column that is not a date keeps the text
            Assert.StartsWith("FILTER ( ALL (", args[2]);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_rules_counts_a_person_reads_after_load_and_reset_add_up_with_a_scan()
        {
            using var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Free);
            await e.OpenAsync(TestModels.FindBim());
            var scanned = (await e.BpaScanAsync()).RuleCount;
            const string custom = "[{\"ID\":\"J9_CUSTOM_RULE\",\"Name\":\"join 9 custom\",\"Category\":\"Test\",\"Severity\":1,\"Scope\":\"Model\",\"Expression\":\"Name <> null\"}]";
            var loaded = await e.LoadBpaRulesAsync(custom, replace: false, "human");
            Assert.Equal(scanned, loaded.StandardRules);                 // the base is what a scan runs with no custom rule
            Assert.Equal(loaded.StandardRules + 1, loaded.ActiveRules);   // base plus the one loaded
            var reset = await e.ResetBpaRulesAsync("human");
            Assert.Equal(scanned, reset.StandardRules);
            Assert.Equal(reset.StandardRules, reset.ActiveRules);
        }

        [Fact]
        public void A_member_the_live_query_returned_as_a_date_is_handed_on_in_a_form_that_reads_back_as_the_same_date()
        {
            var text = LocalEngine.ProbeMemberText(new DateTime(2018, 6, 25, 13, 5, 9, DateTimeKind.Unspecified));
            Assert.Equal("TREATAS ( { DATE ( 1899, 12, 30 ) + TIME ( 0, 0, 0 ) + 3739093509000 / 86400000 }, 'Date'[Date] )", DaxBench.CompileMemberFilter("'Date'[Date]", new[] { text }, true));
            Assert.Equal("Stonebridge", LocalEngine.ProbeMemberText("Stonebridge"));
            Assert.Equal("12.5", LocalEngine.ProbeMemberText(12.5));
        }

        // ---- S3 (Sol, join 10): a DateTime member keeps its fractional seconds. ----
        // Microsoft documents DateTime precision (https://learn.microsoft.com/en-us/power-bi/connect-data/desktop-data-types: Date/Time keeps fractional seconds) and
        // TREATAS member matching (https://learn.microsoft.com/en-us/dax/treatas-function-dax: a member must equal a value of the column). DATE and TIME take whole numbers,
        // so the fraction of the second is added as a fraction of a day: ms / 86400000.

        /// <summary>The DAX the compiler wrote, evaluated the way DAX evaluates it: DATE is a serial day, TIME is a fraction of a day, and "+" and "/" are doubles.</summary>
        private static double EvalDateExpression(string dax)
        {
            var m = System.Text.RegularExpressions.Regex.Match(dax, @"^TREATAS \( \{ DATE \( (\d+), (\d+), (\d+) \) \+ TIME \( (\d+), (\d+), (\d+) \)(?: \+ (\d+) / (\d+))? \}, ");
            Assert.True(m.Success, dax);
            int G(int i) => int.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture);
            var value = new DateTime(G(1), G(2), G(3)).ToOADate() + (G(4) * 3600.0 + G(5) * 60.0 + G(6)) / 86400.0;
            if (m.Groups[7].Success) value += double.Parse(m.Groups[7].Value, System.Globalization.CultureInfo.InvariantCulture) / double.Parse(m.Groups[8].Value, System.Globalization.CultureInfo.InvariantCulture);
            return value;
        }

        [Theory]
        [InlineData("2026-10-05T12:34:56.500", 500)]
        [InlineData("2026-10-05T12:34:56.001", 1)]
        [InlineData("2026-10-05T23:59:59.999", 999)]
        [InlineData("2026-10-05T00:00:00.250", 250)]
        public void A_member_with_fractional_seconds_is_filtered_as_that_exact_instant_and_not_the_whole_second(string text, int milliseconds)
        {
            var stored = DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            Assert.Equal(milliseconds, stored.Millisecond);
            var dax = DaxBench.CompileMemberFilter("'Sales'[Timestamp]", new[] { text }, true);
            Assert.Contains("/ 86400000", dax);                     // the fraction is in the expression
            var shown = DateTime.FromOADate(EvalDateExpression(dax));   // FromOADate rounds to the millisecond, the member's own precision
            Assert.Equal(stored, shown);
            Assert.NotEqual(new DateTime(stored.Year, stored.Month, stored.Day, stored.Hour, stored.Minute, stored.Second), shown);   // the .000 row is not the one selected
        }

        [Fact]
        public void A_member_in_whole_seconds_is_written_as_the_stored_number_and_a_trailing_zero_fraction_changes_nothing()
        {
            Assert.Equal("TREATAS ( { DATE ( 1899, 12, 30 ) + TIME ( 0, 0, 0 ) + 4000365296000 / 86400000 }, 'Sales'[Timestamp] )", DaxBench.CompileMemberFilter("'Sales'[Timestamp]", new[] { "2026-10-05T12:34:56" }, true));
            Assert.Equal("TREATAS ( { DATE ( 1899, 12, 30 ) + TIME ( 0, 0, 0 ) + 4000365296000 / 86400000 }, 'Sales'[Timestamp] )", DaxBench.CompileMemberFilter("'Sales'[Timestamp]", new[] { "2026-10-05T12:34:56.000" }, true));
        }

        [Fact]
        public void A_member_finer_than_a_millisecond_keeps_its_ticks()
        {
            var dax = DaxBench.CompileMemberFilter("'Sales'[Timestamp]", new[] { "2026-10-05T12:34:56.1234567" }, true);
            var stored = new DateTime(2026, 10, 5, 12, 34, 56).AddTicks(1234567);
            Assert.True(Math.Abs(EvalDateExpression(dax) - (stored.Date.ToOADate() + stored.TimeOfDay.Ticks / 864000000000.0)) < 1e-9, dax);   // ToOADate rounds to the millisecond, so the day fraction is taken from the ticks
            Assert.Contains("1234567 / 864000000000", dax);
        }

        [Fact]
        public void The_text_of_a_member_the_live_query_returned_keeps_fractional_seconds()
        {
            var d = new DateTime(2026, 10, 5, 12, 34, 56, 500, DateTimeKind.Unspecified);
            Assert.Equal("2026-10-05T12:34:56.5", LocalEngine.ProbeMemberText(d));
            Assert.Equal("2026-10-05T12:34:56.5", LocalEngine.ProbeMemberText(new DateTimeOffset(d)));
            Assert.Equal("2026-10-05T12:34:56", LocalEngine.ProbeMemberText(new DateTime(2026, 10, 5, 12, 34, 56)));   // whole seconds read as before
            Assert.Equal("2026-10-05T12:34:56.1234567", LocalEngine.ProbeMemberText(new DateTime(2026, 10, 5, 12, 34, 56).AddTicks(1234567)));
            // and it reads back as the same instant through the compiler
            var text = LocalEngine.ProbeMemberText(d);
            Assert.Equal(d, DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
        }
    }
}

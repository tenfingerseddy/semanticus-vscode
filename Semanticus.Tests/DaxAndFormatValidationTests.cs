using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>C5.5 DAX and format-string validation: static format refusal, calc-item format round trip,
    /// offline arity diagnostics, and Analysis Services markup stripped from query errors.</summary>
    public sealed class DaxAndFormatValidationTests
    {
        private static LocalEngine NewEngine() => new LocalEngine(new SessionManager());

        [Fact]
        public async Task Dax_writes_refuse_bad_text_arity_and_unknown_references_without_mutation()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.CreateModelAsync("DaxFence", 1604);
            var table = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(table, "Amount", "Decimal", "Amount", "human");
            var measure = await engine.CreateMeasureAsync(table, "M", "1", "human");
            var before = await engine.GetDaxAsync(measure);

            var sameRevision = sessions.Current.Revision;
            var noOp = await engine.SetDaxAsync(measure, before, "agent");
            Assert.False(noOp.Changed);
            Assert.Equal(sameRevision, noOp.Revision);

            foreach (var bad in new[] { "THIS IS NOT DAX AT ALL", "SUMX(Sales)", "SUM(Sales[Ghost])" })
            {
                var revision = sessions.Current.Revision;
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SetDaxAsync(measure, bad, "agent"));
                Assert.Contains("DAX", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(revision, sessions.Current.Revision);
                Assert.Equal(before, await engine.GetDaxAsync(measure));
            }
        }

        [Fact]
        public async Task Dynamic_format_and_function_body_writes_use_the_same_fence()
        {
            using var engine = NewEngine();
            await engine.CreateModelAsync("DaxFenceExtra", 1702);
            var table = await engine.CreateTableAsync("Sales", "human");
            var measure = await engine.CreateMeasureAsync(table, "M", "1", "human");

            var format = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SetMeasureFormatExpressionAsync(measure, "THIS IS NOT DAX AT ALL", "agent"));
            Assert.Contains("format", format.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null((await engine.GetObjectAsync(measure)).Properties["formatStringExpression"] as string);

            var function = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.CreateFunctionAsync("Bad", "1 + 1", "agent"));
            Assert.Contains("lambda", function.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain((await engine.ListFunctionsAsync()), f => f.Name == "Bad");
        }

        [Fact]
        public void Unclosed_colour_bracket_is_a_format_string_problem()
        {
            Assert.Contains("[", FormatStringRules.Problem("#,##0.00;;;[Red"));
            Assert.Null(FormatStringRules.Problem("#,##0.00;;;[Red]"));
            Assert.Null(FormatStringRules.Problem("0.0%"));
            Assert.Null(FormatStringRules.Problem(""));
        }

        [Fact]
        public async Task Malformed_static_format_string_is_refused_on_both_write_paths()
        {
            using var engine = NewEngine();
            await engine.CreateModelAsync("Fmt", 1604);
            await engine.CreateTableAsync("Sales", "human");
            var mRef = await engine.CreateMeasureAsync("table:Sales", "M", "1", "human");

            var viaFormat = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.SetMeasureFormatAsync(mRef, "#,##0.00;;;[Red", "human"));
            Assert.Contains("[", viaFormat.Message);
            Assert.DoesNotContain("malformed", viaFormat.Message, StringComparison.OrdinalIgnoreCase);

            var viaProp = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.SetObjectPropertyAsync(mRef, "FormatString", "#,##0.00;;;[Red", "human"));
            Assert.Contains("[", viaProp.Message);

            await engine.SetMeasureFormatAsync(mRef, "#,##0.00;;;[Red]", "human");
            Assert.Equal("#,##0.00;;;[Red]", (await engine.GetObjectAsync(mRef)).Properties["formatString"]);

            var revision = (await engine.SessionInfoAsync()).Revision;
            var noOp = await engine.SetMeasureFormatAsync(mRef, "#,##0.00;;;[Red]", "human");
            Assert.False(noOp.Changed);
            Assert.Equal(revision, noOp.Revision);
        }

        [Fact]
        public async Task Calc_item_dynamic_format_round_trips_unquoted_percent()
        {
            using var engine = NewEngine();
            await engine.CreateModelAsync("CG", 1604);
            var gRef = await engine.CreateCalculationGroupAsync("Time", "human");
            var item = await engine.CreateCalculationItemAsync(gRef, "YTD", "SELECTEDMEASURE()", "human");
            await engine.SetCalcItemFormatStringAsync(item, "0.0%", "human");

            var groups = await engine.ListCalculationGroupsAsync();
            var stored = Assert.Single(Assert.Single(groups).Items).FormatStringExpression;
            Assert.Equal("0.0%", stored);
        }

        [Fact]
        public async Task Sumx_with_one_argument_is_diagnosed_offline()
        {
            using var engine = NewEngine();
            await engine.CreateModelAsync("Dax", 1604);
            await engine.CreateTableAsync("Sales", "human");

            var v = await engine.ValidateDaxAsync("SUMX(Sales)");
            Assert.Contains(v.Diagnostics, d =>
                d.Message.IndexOf("SUMX", StringComparison.OrdinalIgnoreCase) >= 0
                && d.Message.Contains("2")
                && d.Message.Contains("1"));

            var ok = await engine.ValidateDaxAsync("SUMX(Sales, 1)");
            Assert.DoesNotContain(ok.Diagnostics, d => d.Message.IndexOf("SUMX", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void Query_error_markup_tags_are_stripped()
        {
            var raw = "The value for column <olii>UAT Missing Measure</olii> cannot be determined.";
            Assert.Equal("The value for column UAT Missing Measure cannot be determined.", DaxErrorText.Plain(raw));
            Assert.Equal("ok", DaxErrorText.Plain("ok"));
            Assert.Equal("The value for column UAT Missing Measure cannot be determined.", ResultSet.FromError(raw).Error);
        }
    }
}

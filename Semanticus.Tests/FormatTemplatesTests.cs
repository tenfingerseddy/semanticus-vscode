using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Format-string template library (docs scratchpad format-string-template-library.md). Proves the catalog is
    /// served from engine-side data (offline, no session needed) with the ratified shape (~86 templates / 17
    /// categories / ~20 common-pinned, static+dynamic tagged), that filters narrow it, and that the NEW measure
    /// DYNAMIC format-string op (set_measure_format_expression — the confirmed gap that set_measure_format is
    /// static-only) sets + round-trips + is undoable, auto-clears the static format, and is gated on CL 1601+.
    /// </summary>
    public sealed class FormatTemplatesTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static LocalEngine NewEngine() => new LocalEngine(new SessionManager(), new Fake(false));

        [Fact]
        public async Task Catalog_is_served_offline_with_the_ratified_shape()
        {
            using var engine = NewEngine();   // NO model opened — the library is static data, usable before an open

            var all = await engine.ListFormatTemplatesAsync(null, null);
            Assert.True(all.Length >= 86, $"expected the full curated catalog, got {all.Length}");
            Assert.Equal(17, all.Select(t => t.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // Every row is well-formed: id/category/name/kind/formatString/example all present; kind is static|dynamic.
            Assert.All(all, t =>
            {
                Assert.False(string.IsNullOrWhiteSpace(t.Id));
                Assert.False(string.IsNullOrWhiteSpace(t.Category));
                Assert.False(string.IsNullOrWhiteSpace(t.Name));
                Assert.False(string.IsNullOrWhiteSpace(t.FormatString));
                Assert.False(string.IsNullOrWhiteSpace(t.ExampleOutput));
                Assert.Contains(t.Kind, new[] { "static", "dynamic" });
            });
            Assert.Equal(all.Length, all.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());   // ids unique

            // A tighter "Common" default view is pinned (~20), and BOTH static and dynamic families are present.
            var common = all.Count(t => t.Common);
            Assert.InRange(common, 15, 30);
            Assert.Contains(all, t => t.Kind == "static");
            Assert.Contains(all, t => t.Kind == "dynamic");

            // The star category ships as ZERO-DEPENDENCY static strings (not a forced DaxLib UDF dependency), and the
            // canonical triangle change-% carries its glyphs + credit.
            var tri = all.Single(t => t.Id == "chg-triangle-pct");
            Assert.Equal("static", tri.Kind);
            Assert.Contains("▲", tri.FormatString);   // ▲
            Assert.Contains("▼", tri.FormatString);   // ▼
            Assert.False(string.IsNullOrEmpty(tri.Credit));

            // The magnitude-adaptive scaler is dynamic (impossible as a static string).
            var auto = all.Single(t => t.Id == "scale-auto");
            Assert.Equal("dynamic", auto.Kind);
            Assert.Contains("SELECTEDMEASURE", auto.FormatString);

            // Optional DaxLib "power-pack" rows are tagged with a pack id (so the UI can offer a one-click install) but
            // the library never HARD-depends on them.
            Assert.Contains(all, t => t.Pack == "DaxLib.FormatString");
        }

        [Fact]
        public async Task Category_and_search_filters_narrow_the_catalog()
        {
            using var engine = NewEngine();
            var all = await engine.ListFormatTemplatesAsync(null, null);

            var pct = await engine.ListFormatTemplatesAsync("Percentages", null);
            Assert.NotEmpty(pct);
            Assert.All(pct, t => Assert.Equal("Percentages", t.Category));
            Assert.True(pct.Length < all.Length);

            // 'contains' match, case-insensitive.
            var change = await engine.ListFormatTemplatesAsync("change", null);
            Assert.NotEmpty(change);
            Assert.All(change, t => Assert.Contains("change", t.Category, StringComparison.OrdinalIgnoreCase));

            // Fuzzy search hits the format string itself (finds the K-scaling template by its literal).
            var byString = await engine.ListFormatTemplatesAsync(null, "\"K\"");
            Assert.Contains(byString, t => t.Id == "scale-k-1");

            var none = await engine.ListFormatTemplatesAsync(null, "zzz_no_such_template_zzz");
            Assert.Empty(none);
        }

        [Fact]
        public async Task Dynamic_measure_format_expression_sets_round_trips_and_undoes()
        {
            using var engine = NewEngine();
            await engine.CreateModelAsync("FmtDyn", 1604);          // CL 1604 supports dynamic measure format strings (needs 1601+)
            await engine.CreateTableAsync("Sales", "human");
            var mRef = await engine.CreateMeasureAsync("table:Sales", "YoY %", "0.1", "human");

            // Start from a STATIC format, then switch to a dynamic expression.
            await engine.SetMeasureFormatAsync(mRef, "0.0%", "human");
            Assert.Equal("0.0%", (await engine.GetObjectAsync(mRef)).Properties["formatString"]);

            const string expr = "\"+0.0%;-0.0%;0.0%\"";
            var set = await engine.SetMeasureFormatExpressionAsync(mRef, expr, "human");
            Assert.True(set.Changed);

            var afterSet = (await engine.GetObjectAsync(mRef)).Properties;
            Assert.Equal(expr, afterSet["formatStringExpression"]);           // the dynamic expression is stored…
            Assert.True(string.IsNullOrEmpty(afterSet["formatString"] as string));   // …and the static format was auto-cleared (AS forbids both)

            // Re-applying the identical expression is a net-zero no-op.
            Assert.False((await engine.SetMeasureFormatExpressionAsync(mRef, expr, "human")).Changed);

            // Undo restores the prior state (the static format returns, dynamic cleared) — one step on the shared timeline.
            await engine.UndoAsync("human");
            var afterUndo = (await engine.GetObjectAsync(mRef)).Properties;
            Assert.True(string.IsNullOrEmpty(afterUndo["formatStringExpression"] as string));
            Assert.Equal("0.0%", afterUndo["formatString"]);

            // Redo re-applies it; then clearing (empty) removes the dynamic format again.
            await engine.RedoAsync("human");
            var cleared = await engine.SetMeasureFormatExpressionAsync(mRef, "", "human");
            Assert.True(cleared.Changed);
            Assert.True(string.IsNullOrEmpty((await engine.GetObjectAsync(mRef)).Properties["formatStringExpression"] as string));
        }

        [Fact]
        public async Task Dynamic_measure_format_expression_is_gated_on_compatibility_level_and_type()
        {
            using var engine = NewEngine();
            await engine.OpenAsync(TestModels.FindBim());   // AdventureWorks = CL 1200, below the 1601 floor
            var mRef = (await engine.ListMeasuresAsync()).First().Ref;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.SetMeasureFormatExpressionAsync(mRef, "\"0.0%\"", "human"));
            Assert.Contains("compatibility level", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Clearing on a below-floor model is a safe no-op (the property can't exist there), not a throw.
            Assert.False((await engine.SetMeasureFormatExpressionAsync(mRef, "", "human")).Changed);

            // A non-measure ref is refused with a clear, corrective message.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.SetMeasureFormatExpressionAsync("table:Nope", "\"0.0%\"", "human"));
        }
    }
}

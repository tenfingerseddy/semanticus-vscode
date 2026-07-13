using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The BestPractice AI-readiness category (the model-scored layer over the DaxLint token-path rules) plus the
    /// TI-gating retrofit of DATE-MARK. Mirrors the WaiverTests harness: build a model through the LocalEngine
    /// (dual-drive path) and scan it with ai_readiness_scan. The load-bearing invariants: EVERY enum category scores
    /// without throwing (the Weights KeyNotFound hazard); the category is DORMANT (never inflates to an always-pass
    /// 100) on a clean model; advisory rules surface findings but never activate/score the category alone; and a
    /// date-less model isn't dinged for a missing date table.
    /// </summary>
    public sealed class BestPracticeRulesTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // A fresh model with one fact table + a couple of measures whose DAX trips NO scored BestPractice rule
        // (no IF / CALCULATE / IFERROR / BLANK / SUMMARIZE / ERROR / VAR): a genuinely clean baseline.
        private static async Task<LocalEngine> CleanModelAsync(bool pro = false)
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(pro));
            await engine.CreateModelAsync("BP", 1604);
            var t = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            await engine.CreateColumnAsync(t, "Qty", "Int64", "Qty", "human");
            await engine.CreateMeasureAsync(t, "Total Amount", "SUM ( Facts[Amount] )", "human");
            await engine.CreateMeasureAsync(t, "Total Qty", "SUM ( Facts[Qty] )", "human");
            return engine;
        }

        private static CategoryScore Cat(Scorecard c, ReadinessCategory cat) =>
            c.Categories.First(x => x.Category == cat.ToString());

        // ---- the Weights KeyNotFound hazard: every enum category scores without throwing ---------------------------

        [Fact]
        public async Task Every_readiness_category_scores_without_throwing()
        {
            using var engine = await CleanModelAsync();
            var card = await engine.AiReadinessScanAsync();
            // A CategoryScore is produced for EVERY enum value (the scoring loop indexes Weights[cat] for each — a
            // missing weight would have thrown KeyNotFoundException before we ever got here).
            foreach (ReadinessCategory cat in Enum.GetValues(typeof(ReadinessCategory)))
                Assert.Contains(card.Categories, x => x.Category == cat.ToString());
            Assert.Contains(card.Categories, x => x.Category == ReadinessCategory.BestPractice.ToString());
        }

        // ---- clean model: BestPractice is dormant + no BP-DAX-* findings -------------------------------------------

        [Fact]
        public async Task Clean_model_leaves_best_practice_dormant()
        {
            using var engine = await CleanModelAsync();
            var card = await engine.AiReadinessScanAsync();
            Assert.False(Cat(card, ReadinessCategory.BestPractice).HasRules);   // no BP rule reported Applicable>0
            Assert.DoesNotContain(card.Findings, f => f.RuleId.StartsWith("BP-", StringComparison.Ordinal));
        }

        // ---- an IFERROR measure: the scored rule fires and presents the category ----------------------------------

        [Fact]
        public async Task Iferror_measure_activates_best_practice_and_docks_the_score()
        {
            using var engine = await CleanModelAsync();
            var t = "table:Facts";
            await engine.CreateMeasureAsync(t, "Guarded Ratio", "IFERROR ( SUM(Facts[Amount]) / SUM(Facts[Qty]), 0 )", "human");

            var card = await engine.AiReadinessScanAsync();
            Assert.Contains(card.Findings, f => f.RuleId == "BP-DAX-IFERROR" && !f.Waived);
            var bp = Cat(card, ReadinessCategory.BestPractice);
            Assert.True(bp.HasRules);           // a scored rule reported Applicable>0 → the category presents
            Assert.True(bp.Score < 100);        // …and a present violation docks it
        }

        // ---- an advisory rule surfaces its finding but NEVER activates the category on its own ---------------------

        [Fact]
        public async Task Advisory_distinctcount_surfaces_but_does_not_activate_the_category()
        {
            using var engine = await CleanModelAsync();
            var t = "table:Facts";
            // COUNTROWS(DISTINCT(col)) → the distinctcount-over-countrows idiom (advisory). Note it trips NO scored
            // rule (no IF/CALCULATE/IFERROR/BLANK/SUMMARIZE/ERROR/VAR), so BestPractice must stay dormant.
            await engine.CreateMeasureAsync(t, "Distinct Custs", "COUNTROWS ( DISTINCT ( Facts[Qty] ) )", "human");

            var card = await engine.AiReadinessScanAsync();
            Assert.Contains(card.Findings, f => f.RuleId == "BP-DAX-DISTINCTCOUNT");   // surfaced…
            Assert.False(Cat(card, ReadinessCategory.BestPractice).HasRules);          // …but the category is still dormant
        }

        // ---- BP-AUTO-DATETIME: dormant without a footprint; fires on a GUID-suffixed local date table --------------

        [Fact]
        public async Task Auto_datetime_dormant_without_footprint()
        {
            using var engine = await CleanModelAsync();
            var card = await engine.AiReadinessScanAsync();
            Assert.DoesNotContain(card.Findings, f => f.RuleId == "BP-AUTO-DATETIME");
        }

        [Fact]
        public async Task Auto_datetime_fires_on_a_hidden_local_date_table()
        {
            using var engine = await CleanModelAsync();
            // A PBI-generated hidden local date table: the GUID-suffixed name is the footprint the rule matches.
            await engine.CreateTableAsync("LocalDateTable_" + Guid.NewGuid().ToString("D"), "human");

            var card = await engine.AiReadinessScanAsync();
            Assert.Contains(card.Findings, f => f.RuleId == "BP-AUTO-DATETIME");
            Assert.True(Cat(card, ReadinessCategory.BestPractice).HasRules);   // Applicable=1 → the category presents
        }

        [Fact]
        public async Task Auto_datetime_safe_on_a_user_table_named_datetabletemplate_without_a_guid()
        {
            using var engine = await CleanModelAsync();
            await engine.CreateTableAsync("DateTableTemplate", "human");   // no GUID suffix → NOT the engine footprint

            var card = await engine.AiReadinessScanAsync();
            Assert.DoesNotContain(card.Findings, f => f.RuleId == "BP-AUTO-DATETIME");
        }

        // ---- DATE-MARK is now TI-gated: no TI DAX ⇒ not dinged; TI DAX ⇒ dinged unless a date table is marked ------

        [Fact]
        public async Task Date_mark_dormant_without_time_intelligence()
        {
            using var engine = await CleanModelAsync();   // no TI DAX, no marked date table
            var card = await engine.AiReadinessScanAsync();
            Assert.DoesNotContain(card.Findings, f => f.RuleId == "DATE-MARK");
        }

        // ---- adversarial-review fixes (2026-07-03): Applicable = the VIOLATION population, never the trigger ------

        [Fact]
        public async Task Benign_if_leaves_best_practice_dormant()
        {
            using var engine = await CleanModelAsync();
            // Any IF used to trigger BP-DAX-DIV-GUARD's applicability and mint an always-pass score-100 category —
            // the exact inflation the review caught. A benign IF must leave the category dormant.
            await engine.CreateMeasureAsync("table:Facts", "Has Sales", "IF ( SUM(Facts[Qty]) > 0, 1, 0 )", "human");

            var card = await engine.AiReadinessScanAsync();
            Assert.False(Cat(card, ReadinessCategory.BestPractice).HasRules);
            Assert.DoesNotContain(card.Findings, f => f.RuleId.StartsWith("BP-DAX-", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Iferror_without_division_scores_zero_with_no_sibling_dilution()
        {
            using var engine = await CleanModelAsync();
            // Plain IFERROR (no division/search): only BP-DAX-IFERROR may present. Under the old trigger-population
            // design, -DIV and -SEARCH also presented as always-passes and diluted the category to 66.7.
            await engine.CreateMeasureAsync("table:Facts", "Guarded", "IFERROR ( SUM(Facts[Amount]), 0 )", "human");

            var card = await engine.AiReadinessScanAsync();
            var bp = Cat(card, ReadinessCategory.BestPractice);
            Assert.True(bp.HasRules);
            Assert.Equal(0, bp.Score);          // presence design: every present rule IS a violation
            Assert.Equal(1, bp.Applicable);     // exactly the violating object — no triggered-but-passing padding
        }

        [Fact]
        public async Task Column_op_measure_surfaces_only_the_advisory()
        {
            using var engine = await CleanModelAsync();
            // The contested direction (col > [Measure]) — BP-DAX-MEASURE-PREDICATE is advisory: surfaced, never scored.
            await engine.CreateMeasureAsync("table:Facts", "Big Only", "CALCULATE ( SUM(Facts[Amount]), Facts[Qty] > [Total Qty] )", "human");

            var card = await engine.AiReadinessScanAsync();
            Assert.Contains(card.Findings, f => f.RuleId == "BP-DAX-MEASURE-PREDICATE");
            Assert.False(Cat(card, ReadinessCategory.BestPractice).HasRules);
        }

        [Fact]
        public async Task Date_mark_fires_once_time_intelligence_is_used()
        {
            using var engine = await CleanModelAsync();
            // Build a Date table (unmarked) so TOTALYTD resolves, then add a TI measure.
            var dt = await engine.CreateTableAsync("Date", "human");
            await engine.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await engine.CreateMeasureAsync("table:Facts", "YTD Amount", "TOTALYTD ( SUM(Facts[Amount]), 'Date'[Date] )", "human");

            var card = await engine.AiReadinessScanAsync();
            Assert.Contains(card.Findings, f => f.RuleId == "DATE-MARK");   // TI in use + no marked table ⇒ finding

            // Mark the Date table (DataCategory='Time') → the finding clears.
            await engine.MarkDateTableAsync(dt, "Date", "human");
            var after = await engine.AiReadinessScanAsync();
            Assert.DoesNotContain(after.Findings, f => f.RuleId == "DATE-MARK");
        }
    }
}

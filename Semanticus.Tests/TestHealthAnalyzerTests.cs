using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>E3 (docs/tests-tab-spec.md): the three invariants pinned on the pure analyzer —
    /// I1 unknown never moves the grade · I2 grade+coverage are one unit · I3 root causes, not counts —
    /// plus the dormant-or-dock category rule and the integrity hard gate.</summary>
    public sealed class TestHealthAnalyzerTests
    {
        // ---- builders --------------------------------------------------------------------------------

        // Drive each relationship to the requested verdict through the REAL evaluator (so these tests can't
        // drift from E4's actual behavior): orphans>0 = Fail, 0 = Pass. The NotVerifiable case withholds the
        // probe AND the column types — DataTypeMatch is decided STATICALLY from the types alone, so a typed,
        // unprobed relationship would still carry one decisive check (measured; the first draft assumed not).
        private static RelationshipIntegrityReport Rels(params Verdict[] riVerdicts)
            => RelationshipIntegrity.Evaluate(riVerdicts.Select((v, i) => new RelationshipCheckInput
            {
                Name = "r" + i,
                ManyTable = "F", ManyColumn = "K", OneTable = "D", OneColumn = "K",
                Cardinality = "manyToOne", IsActive = true,
                ManyColumnType = v == Verdict.NotVerifiable ? null : "Int64",
                OneColumnType = v == Verdict.NotVerifiable ? null : "Int64",
                Probe = v == Verdict.NotVerifiable ? null : new RelationshipProbeResult
                {
                    OrphanRows = v == Verdict.Fail ? 7 : 0,
                    DuplicateKeys = 0, BlankForeignKeys = 0, BlankKeys = 0, ManyRowCount = 100, OneRowCount = 10,
                },
            }));

        private static ReconcileOutcome Recon(Verdict v, bool missing = false)
            => new ReconcileOutcome { DefId = Guid.NewGuid().ToString("N"), Title = "t", Verdict = v, Missing = missing };

        // ---- I1: NotVerifiable / Suspect move the grade in NEITHER direction ----
        [Fact]
        public void I1_notverifiable_is_excluded_from_the_grade()
        {
            // 1 passing RI probe + 2 unprobed relationships: the unknowns must not dilute (or inflate) the grade.
            var h = TestHealthAnalyzer.Analyze(Rels(Verdict.Pass, Verdict.NotVerifiable, Verdict.NotVerifiable), null);
            Assert.Equal(100.0, h.Categories.Single(c => c.Category == "Integrity").Score);
            Assert.True(h.NotVerifiable > 0);
            Assert.True(h.CoveragePct < 100.0);
            Assert.True(h.Overall <= h.CoveragePct + 0.05);
            Assert.NotEqual("A", h.Grade);
        }

        // A category that decided NOTHING is dormant — it must not average in at 100 (unknown-as-healthy).
        [Fact]
        public void I1_allunknown_category_is_dormant_not_perfect()
        {
            var allUnknown = TestHealthAnalyzer.Analyze(Rels(Verdict.NotVerifiable, Verdict.NotVerifiable), null);
            var withFail = TestHealthAnalyzer.Analyze(Rels(Verdict.NotVerifiable, Verdict.NotVerifiable),
                new[] { Recon(Verdict.Fail) });   // one decided (failing) correctness check
            Assert.False(allUnknown.Categories.Single(c => c.Category == "Integrity").HasChecks);
            // The failing correctness check must own the whole grade: the dormant Integrity can't prop it up.
            Assert.Equal(0.0, withFail.Categories.Single(c => c.Category == "Correctness").Score);
            Assert.Equal("F", withFail.Grade);
        }

        // ---- I2: grade and coverage travel together, in one object ----
        [Fact]
        public void I2_grade_and_coverage_are_one_unit()
        {
            var h = TestHealthAnalyzer.Analyze(
                Rels(Verdict.Pass, Verdict.NotVerifiable, Verdict.NotVerifiable, Verdict.NotVerifiable), null);
            Assert.True(h.CoveragePct <= 25.0);
            Assert.True(h.Overall <= h.CoveragePct + 0.05);
            Assert.NotEqual("A", h.Grade);
            Assert.True(h.Checked >= 12);               // 4 relationships × 3 checks each
        }

        // ---- I3: Suspect is cascade, not cause — root failures count only the real defect ----
        [Fact]
        public void I3_suspect_does_not_count_as_a_root_failure()
        {
            // Duplicate keys on the one side: E4 fails KeyUniqueness and demotes THIS relationship's RI check
            // to Suspect naming the root cause — the analyzer must count ONE root failure, not two.
            var report = RelationshipIntegrity.Evaluate(new[]
            {
                new RelationshipCheckInput
                {
                    Name = "dup", ManyTable = "F", ManyColumn = "K", OneTable = "D", OneColumn = "K",
                    Cardinality = "manyToOne", IsActive = true, ManyColumnType = "Int64", OneColumnType = "Int64",
                    Probe = new RelationshipProbeResult { OrphanRows = 5, DuplicateKeys = 3, BlankForeignKeys = 0, BlankKeys = 0, ManyRowCount = 100, OneRowCount = 10 },
                },
            });
            var rel = report.Relationships.Single();
            Assert.Equal(Verdict.Fail, rel.KeyUniqueness.Verdict);
            Assert.Equal(Verdict.Suspect, rel.ReferentialIntegrity.Verdict);   // demoted, root cause named
            Assert.NotNull(rel.ReferentialIntegrity.RootCause);

            var h = TestHealthAnalyzer.Analyze(report, null);
            Assert.Equal(1, h.RootFailures);            // the duplicate-key defect — ONE row that matters
            Assert.Equal(1, h.Suspect);
        }

        // ---- the integrity hard gate: a mostly-green suite can never present an A over silent corruption ----
        [Fact]
        public void Integrity_failure_caps_the_grade_at_D()
        {
            var h = TestHealthAnalyzer.Analyze(
                Rels(Verdict.Fail, Verdict.Pass, Verdict.Pass, Verdict.Pass, Verdict.Pass, Verdict.Pass,
                     Verdict.Pass, Verdict.Pass, Verdict.Pass, Verdict.Pass),
                new[] { Recon(Verdict.Pass) });
            Assert.True(h.Overall <= 60.0);
            Assert.Equal("D", h.Grade);
            Assert.NotEmpty(h.GatedBy);
        }

        // ---- reconcile outcomes: pass/fail decisive; a MISSING binding is loud but never graded ----
        [Fact]
        public void Missing_binding_is_surfaced_not_graded()
        {
            var h = TestHealthAnalyzer.Analyze(Rels(),
                new[] { Recon(Verdict.Pass), Recon(Verdict.NotVerifiable, missing: true) });
            Assert.Equal(1, h.Missing);
            Assert.Equal("A", h.Grade);                 // the missing test is not a pass AND not a fail
            Assert.Equal(1, h.Categories.Single(c => c.Category == "Correctness").Checked);
        }

        [Fact]
        public void Empty_suite_is_honest_zero_coverage()
        {
            var h = TestHealthAnalyzer.Analyze(Rels(), null);
            Assert.Equal(0.0, h.CoveragePct);
            Assert.Equal(0, h.Checked);
            Assert.Equal("F", h.Grade);
        }

        // ---- E5 wiring: the Performance category exists only when budgets were DECLARED ----
        [Fact]
        public void Performance_stays_dormant_without_timing_verdicts()
        {
            // No timing (no budgets set) ⇒ the pre-declared 0.10 weight must not dilute or prop up the grade.
            var h = TestHealthAnalyzer.Analyze(Rels(Verdict.Pass), null);
            var perf = h.Categories.Single(c => c.Category == "Performance");
            Assert.False(perf.HasChecks);
            Assert.Equal(0, perf.Checked);
            Assert.Equal("A", h.Grade);
        }

        [Fact]
        public void Timing_verdicts_activate_performance_and_move_the_grade()
        {
            // Same reports, plus one over-budget timing Fail: Performance activates (weight 0.10 vs
            // Integrity 0.30, normalized) and the declared-budget failure drags the average down.
            var without = TestHealthAnalyzer.Analyze(Rels(Verdict.Pass), null);
            var with = TestHealthAnalyzer.Analyze(Rels(Verdict.Pass), null, new[] { Verdict.Fail });
            var perf = with.Categories.Single(c => c.Category == "Performance");
            Assert.True(perf.HasChecks);
            Assert.Equal(0.0, perf.Score);
            Assert.True(with.Overall < without.Overall);
            // An over-budget measure is a user-declared failure: it counts among the root causes (I3 —
            // over-reporting roots is honest; E5 already names the cause in its detail).
            Assert.Equal(1, with.RootFailures);
        }

        [Fact]
        public void Timing_notverifiable_shrinks_coverage_never_the_grade()
        {
            // A budgeted measure whose cache-clear was refused arrives NotVerifiable: Performance must stay
            // DORMANT (nothing decided), the grade must not move, and the unknown must still show in coverage.
            var h = TestHealthAnalyzer.Analyze(Rels(Verdict.Pass), null, new[] { Verdict.NotVerifiable });
            var perf = h.Categories.Single(c => c.Category == "Performance");
            Assert.False(perf.HasChecks);
            Assert.Equal(1, perf.NotVerifiable);
            Assert.NotEqual("A", h.Grade);
            Assert.True(h.CoveragePct < 100.0);
            Assert.True(h.Overall <= h.CoveragePct + 0.05);
        }
    }
}

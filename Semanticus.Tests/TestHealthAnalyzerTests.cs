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

        private static SecurityStaticReport Sec(params (string filter, string err)[] filters)
            => SecurityStaticChecks.Evaluate(filters.Select((f, i) => new RoleFilterInput
            { Role = "role" + i, Table = "T", FilterExpression = f.filter, ErrorMessage = f.err }));

        private static ReconcileOutcome Recon(Verdict v, bool missing = false)
            => new ReconcileOutcome { DefId = Guid.NewGuid().ToString("N"), Title = "t", Verdict = v, Missing = missing };

        private static readonly SecurityStaticReport NoSec = SecurityStaticChecks.Evaluate(Array.Empty<RoleFilterInput>());

        // ---- I1: NotVerifiable / Suspect move the grade in NEITHER direction ----
        [Fact]
        public void I1_notverifiable_is_excluded_from_the_grade()
        {
            // 1 passing RI probe + 2 unprobed relationships: the unknowns must not dilute (or inflate) the grade.
            var h = TestHealthAnalyzer.Analyze(Rels(Verdict.Pass, Verdict.NotVerifiable, Verdict.NotVerifiable), NoSec, null);
            Assert.Equal("A", h.Grade);
            Assert.True(h.NotVerifiable > 0);
            // ...and coverage discloses exactly how thin the decisive base is (I2's other half).
            Assert.True(h.CoveragePct < 100.0);
        }

        // A category that decided NOTHING is dormant — it must not average in at 100 (unknown-as-healthy).
        [Fact]
        public void I1_allunknown_category_is_dormant_not_perfect()
        {
            var allUnknown = TestHealthAnalyzer.Analyze(Rels(Verdict.NotVerifiable, Verdict.NotVerifiable), NoSec, null);
            var withFail = TestHealthAnalyzer.Analyze(Rels(Verdict.NotVerifiable, Verdict.NotVerifiable),
                Sec(("1=1", null)), null);   // one decided (failing) security check
            Assert.False(allUnknown.Categories.Single(c => c.Category == "Integrity").HasChecks);
            // The failing security check must own the whole grade — the dormant Integrity can't prop it up.
            Assert.Equal(0.0, withFail.Categories.Single(c => c.Category == "Security").Score);
            Assert.Equal("F", withFail.Grade);
        }

        // ---- I2: grade and coverage travel together, in one object ----
        [Fact]
        public void I2_grade_and_coverage_are_one_unit()
        {
            var h = TestHealthAnalyzer.Analyze(
                Rels(Verdict.Pass, Verdict.NotVerifiable, Verdict.NotVerifiable, Verdict.NotVerifiable), NoSec, null);
            Assert.Equal("A", h.Grade);                 // the one decisive check passed…
            Assert.True(h.CoveragePct <= 25.0);         // …and the same object says how little that proves
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

            var h = TestHealthAnalyzer.Analyze(report, NoSec, null);
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
                Sec(("[Region] = USERNAME()", null)), new[] { Recon(Verdict.Pass) });
            Assert.True(h.Overall <= 60.0);
            Assert.Equal("D", h.Grade);
            Assert.NotEmpty(h.GatedBy);
        }

        // ---- reconcile outcomes: pass/fail decisive; a MISSING binding is loud but never graded ----
        [Fact]
        public void Missing_binding_is_surfaced_not_graded()
        {
            var h = TestHealthAnalyzer.Analyze(Rels(), NoSec,
                new[] { Recon(Verdict.Pass), Recon(Verdict.NotVerifiable, missing: true) });
            Assert.Equal(1, h.Missing);
            Assert.Equal("A", h.Grade);                 // the missing test is not a pass AND not a fail
            Assert.Equal(1, h.Categories.Single(c => c.Category == "Correctness").Checked);
        }

        [Fact]
        public void Empty_suite_is_honest_zero_coverage()
        {
            var h = TestHealthAnalyzer.Analyze(Rels(), NoSec, null);
            Assert.Equal(0.0, h.CoveragePct);
            Assert.Equal(0, h.Checked);
        }

        // ---- static security semantics ----
        [Fact]
        public void Tautology_and_error_filters_fail_a_real_filter_passes()
        {
            var rep = Sec(("1 = 1", null), ("TRUE()", null), ("[Region] = \"AU\"", null), ("[X] ==", "syntax error"));
            Assert.Equal(3, rep.Summary.Failed);
            Assert.Equal(1, rep.Summary.Passed);
            Assert.Equal(100.0, rep.Summary.CoveragePct);   // all four were decidable statically
        }

        [Fact]
        public void Filterless_permissions_are_not_tests()
        {
            var rep = SecurityStaticChecks.Evaluate(new[]
            { new RoleFilterInput { Role = "viewer", Table = "T", FilterExpression = "  " } });
            Assert.Empty(rep.Filters);                   // metadata-only permission — nothing to test, no fake coverage
        }

        // ---- E5 wiring: the Performance category exists only when budgets were DECLARED ----
        [Fact]
        public void Performance_stays_dormant_without_timing_verdicts()
        {
            // No timing (no budgets set) ⇒ the pre-declared 0.10 weight must not dilute or prop up the grade.
            var h = TestHealthAnalyzer.Analyze(Rels(Verdict.Pass), NoSec, null);
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
            var without = TestHealthAnalyzer.Analyze(Rels(Verdict.Pass), NoSec, null);
            var with = TestHealthAnalyzer.Analyze(Rels(Verdict.Pass), NoSec, null, new[] { Verdict.Fail });
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
            var h = TestHealthAnalyzer.Analyze(Rels(Verdict.Pass), NoSec, null, new[] { Verdict.NotVerifiable });
            var perf = h.Categories.Single(c => c.Category == "Performance");
            Assert.False(perf.HasChecks);
            Assert.Equal(1, perf.NotVerifiable);
            Assert.Equal("A", h.Grade);
            Assert.True(h.CoveragePct < 100.0);
        }
    }
}

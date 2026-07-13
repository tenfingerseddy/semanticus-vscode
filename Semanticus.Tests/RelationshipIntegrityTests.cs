using System;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Tests-tab E4 (relationship integrity), the pure offline core. Proves the three tab invariants on this engine:
    /// I1 — a probe-less check is <c>NotVerifiable</c>, never a silent Pass; I2 — the summary always pairs the
    /// tallies with a coverage %; I3 — a one-side uniqueness failure demotes the SAME relationship's referential-
    /// integrity check to <c>Suspect</c> (with the duplicate as the named root cause), not a second Fail. Every
    /// verdict state is reached, blank-FK separation from orphans is pinned, and the DAX identifier escaping is
    /// asserted string-for-string on space / apostrophe / bracket edge cases.
    /// </summary>
    public sealed class RelationshipIntegrityTests
    {
        // A star relationship: Sales[CustomerKey] (many) -> Customer[CustomerKey] (one), types matching by default.
        private static RelationshipCheckInput Rel(
            string card = "manyToOne", bool active = true,
            string manyType = "Int64", string oneType = "Int64",
            RelationshipProbeResult? probe = null) => new RelationshipCheckInput
            {
                Name = "Sales->Customer",
                ManyTable = "Sales", ManyColumn = "CustomerKey",
                OneTable = "Customer", OneColumn = "CustomerKey",
                Cardinality = card, IsActive = active,
                ManyColumnType = manyType, OneColumnType = oneType,
                Probe = probe,
            };

        private static RelationshipProbeResult Probe(
            long? orphans = null, long? blankFk = null, long? dup = null, long? blankKey = null)
            => new RelationshipProbeResult { OrphanRows = orphans, BlankForeignKeys = blankFk, DuplicateKeys = dup, BlankKeys = blankKey };

        // ---- happy path -------------------------------------------------------------------------------------

        [Fact]
        public void healthy_relationship_is_all_pass_at_full_coverage()
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(probe: Probe(orphans: 0, dup: 0)));
            Assert.Equal(Verdict.Pass, r.DataTypeMatch.Verdict);
            Assert.Equal(Verdict.Pass, r.KeyUniqueness.Verdict);
            Assert.Equal(Verdict.Pass, r.ReferentialIntegrity.Verdict);

            var report = RelationshipIntegrity.Evaluate(new[] { Rel(probe: Probe(orphans: 0, dup: 0)) });
            Assert.Equal(3, report.Summary.Checked);
            Assert.Equal(3, report.Summary.Passed);
            Assert.Equal(0, report.Summary.NotVerifiable);
            Assert.Equal(100.0, report.Summary.CoveragePct);
        }

        // ---- (a) data-type match ---------------------------------------------------------------------------

        [Fact]
        public void type_mismatch_is_fail()
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(manyType: "Int64", oneType: "String", probe: Probe(orphans: 0, dup: 0)));
            Assert.Equal(Verdict.Fail, r.DataTypeMatch.Verdict);
            Assert.Contains("Int64", r.DataTypeMatch.Message);
            Assert.Contains("String", r.DataTypeMatch.Message);
        }

        [Fact]
        public void type_match_is_case_insensitive()   // a casing variance is the SAME type, not a false Fail
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(manyType: "Int64", oneType: "int64", probe: Probe(orphans: 0, dup: 0)));
            Assert.Equal(Verdict.Pass, r.DataTypeMatch.Verdict);
        }

        [Fact]
        public void missing_types_are_not_verifiable_not_pass()   // I1: no metadata ⇒ NotVerifiable, never a guessed Pass
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(manyType: null, oneType: null, probe: Probe(orphans: 0, dup: 0)));
            Assert.Equal(Verdict.NotVerifiable, r.DataTypeMatch.Verdict);
            Assert.NotEqual(Verdict.Pass, r.DataTypeMatch.Verdict);
        }

        // ---- (b) key uniqueness ----------------------------------------------------------------------------

        [Fact]
        public void duplicate_keys_are_a_uniqueness_fail_carrying_the_count()
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(probe: Probe(orphans: 0, dup: 4)));
            Assert.Equal(Verdict.Fail, r.KeyUniqueness.Verdict);
            Assert.Equal(4, r.KeyUniqueness.Count);
        }

        // ---- (c) referential integrity ---------------------------------------------------------------------

        [Fact]
        public void orphans_are_an_RI_fail_when_the_key_is_unique()
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(probe: Probe(orphans: 7, dup: 0)));
            Assert.Equal(Verdict.Pass, r.KeyUniqueness.Verdict);
            Assert.Equal(Verdict.Fail, r.ReferentialIntegrity.Verdict);
            Assert.Equal(7, r.ReferentialIntegrity.Count);
        }

        [Fact]
        public void blank_fks_are_reported_not_counted_as_orphans()   // brief (c): blank FKs are legitimate, not a Fail
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(probe: Probe(orphans: 0, blankFk: 10, dup: 0)));
            Assert.Equal(Verdict.Pass, r.ReferentialIntegrity.Verdict);
            Assert.Equal(10, r.BlankForeignKeys);
        }

        [Fact]
        public void blank_keys_are_reported_not_failed()   // one-side blanks are informational, like blank FKs
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(probe: Probe(orphans: 0, dup: 0, blankKey: 3)));
            Assert.Equal(Verdict.Pass, r.KeyUniqueness.Verdict);
            Assert.Equal(3, r.BlankKeys);
        }

        // ---- I3: root-cause demotion -----------------------------------------------------------------------

        [Fact]
        public void duplicate_keys_demote_RI_to_suspect_with_the_named_root_cause()
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(probe: Probe(orphans: 0, dup: 2)));
            Assert.Equal(Verdict.Fail, r.KeyUniqueness.Verdict);
            Assert.Equal(Verdict.Suspect, r.ReferentialIntegrity.Verdict);
            Assert.NotEqual(Verdict.Fail, r.ReferentialIntegrity.Verdict);   // NOT a second independent Fail
            Assert.Equal("duplicate keys on 'Customer'[CustomerKey]", r.ReferentialIntegrity.RootCause);
        }

        [Fact]
        public void demotion_fires_even_when_orphans_are_present()   // the orphan number is suppressed, not re-reported
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(probe: Probe(orphans: 999, dup: 2)));
            Assert.Equal(Verdict.Suspect, r.ReferentialIntegrity.Verdict);
            Assert.Null(r.ReferentialIntegrity.Count);   // no orphan count leaks out of a suspect result
        }

        [Fact]
        public void a_dup_key_relationship_is_one_fail_not_two()   // I3 at the summary level: one root cause, one Fail
        {
            var report = RelationshipIntegrity.Evaluate(new[] { Rel(probe: Probe(orphans: 5, dup: 2)) });
            Assert.Equal(1, report.Summary.Failed);
            Assert.Equal(1, report.Summary.Suspect);
        }

        [Fact]
        public void demotion_needs_an_actual_failure_not_a_notverifiable_uniqueness()
        {
            // Uniqueness unmeasured (NotVerifiable), orphans measured: RI stands on its own probe, NOT demoted to
            // Suspect — Suspect is "downstream of a real FAILURE", and a NotVerifiable is not a failure.
            var r = RelationshipIntegrity.EvaluateOne(Rel(probe: Probe(orphans: 0, dup: null)));
            Assert.Equal(Verdict.NotVerifiable, r.KeyUniqueness.Verdict);
            Assert.Equal(Verdict.Pass, r.ReferentialIntegrity.Verdict);
        }

        // ---- I1: absent probes -----------------------------------------------------------------------------

        [Fact]
        public void absent_probe_makes_probe_checks_not_verifiable_never_pass()
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(probe: null));
            Assert.Equal(Verdict.NotVerifiable, r.KeyUniqueness.Verdict);
            Assert.Equal(Verdict.NotVerifiable, r.ReferentialIntegrity.Verdict);
            Assert.NotEqual(Verdict.Pass, r.KeyUniqueness.Verdict);
            Assert.NotEqual(Verdict.Pass, r.ReferentialIntegrity.Verdict);
            Assert.Equal(Verdict.Pass, r.DataTypeMatch.Verdict);   // the static check still runs offline
        }

        // ---- (d) inactive ----------------------------------------------------------------------------------

        [Fact]
        public void inactive_is_a_flag_not_a_verdict()
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(active: false, probe: Probe(orphans: 0, dup: 0)));
            Assert.True(r.Inactive);
            Assert.Equal(Verdict.Pass, r.KeyUniqueness.Verdict);      // inactivity suppresses no check
            Assert.Equal(Verdict.Pass, r.ReferentialIntegrity.Verdict);
        }

        // ---- many-to-many ----------------------------------------------------------------------------------

        [Fact]
        public void many_to_many_uniqueness_and_ri_are_not_verifiable_even_with_dupes()
        {
            // Dups are EXPECTED on a many-to-many join, so a supplied DuplicateKeys value must not mint a false Fail.
            var r = RelationshipIntegrity.EvaluateOne(Rel(card: "manyToMany", probe: Probe(orphans: 50, dup: 200)));
            Assert.Equal(Verdict.NotVerifiable, r.KeyUniqueness.Verdict);
            Assert.Equal(Verdict.NotVerifiable, r.ReferentialIntegrity.Verdict);
            Assert.NotEqual(Verdict.Fail, r.KeyUniqueness.Verdict);
            Assert.Equal(Verdict.Pass, r.DataTypeMatch.Verdict);     // matching types still required
        }

        // ---- I2: summary coverage math ---------------------------------------------------------------------

        [Fact]
        public void coverage_is_the_verifiable_fraction_of_all_checks()
        {
            // rel A: fully probed + typed  -> 3 covered (all Pass).
            // rel B: typed but no probe    -> 1 covered (DataType Pass) + 2 NotVerifiable.
            var report = RelationshipIntegrity.Evaluate(new[]
            {
                Rel(probe: Probe(orphans: 0, dup: 0)),
                Rel(probe: null),
            });
            Assert.Equal(2, report.Summary.Relationships);
            Assert.Equal(6, report.Summary.Checked);
            Assert.Equal(4, report.Summary.Passed);
            Assert.Equal(2, report.Summary.NotVerifiable);
            Assert.Equal(0, report.Summary.Failed);
            Assert.Equal(66.7, report.Summary.CoveragePct);
        }

        [Fact]
        public void empty_suite_is_zero_coverage_never_a_vacuous_hundred()
        {
            var report = RelationshipIntegrity.Evaluate(Array.Empty<RelationshipCheckInput>());
            Assert.Equal(0, report.Summary.Relationships);
            Assert.Equal(0, report.Summary.Checked);
            Assert.Equal(0.0, report.Summary.CoveragePct);
        }

        [Fact]
        public void every_check_lands_in_exactly_one_tally()   // the four buckets partition the checks exactly
        {
            var report = RelationshipIntegrity.Evaluate(new[]
            {
                Rel(manyType: "Int64", oneType: "String", probe: Probe(orphans: 3, dup: 0)),  // type Fail + RI Suspect (demoted by the mismatch)
                Rel(card: "manyToMany", probe: Probe(dup: 9)),                                // 2 NotVerifiable
                Rel(probe: Probe(orphans: 0, dup: 1)),                                        // uniq Fail + RI Suspect
            });
            var s = report.Summary;
            Assert.Equal(9, s.Checked);
            Assert.Equal(s.Checked, s.Passed + s.Failed + s.Suspect + s.NotVerifiable);
        }

        // ---- review-fix pins (adversarial hostile-input findings) ------------------------------------------

        // Finding 1: a uniqueness failure with NO orphan measurement must NOT claim Suspect — Suspect is a COVERED,
        // known-tainted determination, and there is nothing measured here to be tainted. It is NotVerifiable (not
        // covered), still naming the duplicate; only a demotion where the orphan probe RAN earns Suspect.
        [Fact]
        public void dup_fail_with_absent_orphan_probe_is_not_verifiable_not_suspect()
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(probe: Probe(orphans: null, dup: 2)));
            Assert.Equal(Verdict.Fail, r.KeyUniqueness.Verdict);
            Assert.Equal(Verdict.NotVerifiable, r.ReferentialIntegrity.Verdict);
            Assert.NotEqual(Verdict.Suspect, r.ReferentialIntegrity.Verdict);   // Suspect would falsely count as covered
            Assert.Contains("duplicate keys on 'Customer'[CustomerKey]", r.ReferentialIntegrity.Message);
            Assert.Contains("the orphan probe did not run", r.ReferentialIntegrity.Message);

            // and it lands in the NOT-covered bucket so coverage can't be inflated by a check that never ran.
            var report = RelationshipIntegrity.Evaluate(new[] { Rel(probe: Probe(orphans: null, dup: 2)) });
            Assert.Equal(0, report.Summary.Suspect);          // NOT the old Suspect-covered outcome
            Assert.Equal(1, report.Summary.NotVerifiable);    // RI is uncovered
            Assert.Equal(66.7, report.Summary.CoveragePct);   // 2 of 3 checks reached a determination, not 3
        }

        // Finding 2: a negative count is a broken measurement, never a Pass. Each dependent check goes NotVerifiable;
        // informational counts are nulled rather than surfaced negative.
        [Fact]
        public void negative_duplicate_count_is_not_verifiable_not_a_pass()
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(probe: Probe(orphans: 0, dup: -1)));
            Assert.Equal(Verdict.NotVerifiable, r.KeyUniqueness.Verdict);
            Assert.NotEqual(Verdict.Pass, r.KeyUniqueness.Verdict);
            Assert.Contains("invalid measurement", r.KeyUniqueness.Message);
        }

        [Fact]
        public void negative_orphan_count_is_not_verifiable_not_a_pass()
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(probe: Probe(orphans: -1, dup: 0)));
            Assert.Equal(Verdict.Pass, r.KeyUniqueness.Verdict);   // uniqueness is clean
            Assert.Equal(Verdict.NotVerifiable, r.ReferentialIntegrity.Verdict);
            Assert.NotEqual(Verdict.Pass, r.ReferentialIntegrity.Verdict);
            Assert.Contains("invalid measurement", r.ReferentialIntegrity.Message);
        }

        [Fact]
        public void negative_informational_counts_are_nulled_not_surfaced()
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(probe: Probe(orphans: 0, dup: 0, blankFk: -1, blankKey: -1)));
            Assert.Null(r.BlankForeignKeys);   // "-1 blank FKs" is a broken measurement, not a fact about the model
            Assert.Null(r.BlankKeys);
            Assert.Equal(Verdict.Pass, r.ReferentialIntegrity.Verdict);   // a negative INFO count doesn't taint the verdict
        }

        // Finding 3: blank endpoint identifiers can't be probed — the probe-backed checks are a config error
        // (NotVerifiable), never an accidental all-pass; the static type check still runs; probe generation THROWS.
        [Fact]
        public void blank_identifiers_make_probe_checks_a_config_error_not_a_pass()
        {
            var rel = new RelationshipCheckInput
            {
                Name = "broken", ManyTable = "", ManyColumn = null, OneTable = "  ", OneColumn = "",
                Cardinality = "manyToOne", ManyColumnType = "Int64", OneColumnType = "Int64",
                Probe = Probe(orphans: 0, dup: 0),   // even WITH probe numbers, blank coordinates can't be trusted
            };
            var r = RelationshipIntegrity.EvaluateOne(rel);
            Assert.Equal(Verdict.NotVerifiable, r.KeyUniqueness.Verdict);
            Assert.Equal(Verdict.NotVerifiable, r.ReferentialIntegrity.Verdict);
            Assert.NotEqual(Verdict.Pass, r.KeyUniqueness.Verdict);
            Assert.NotEqual(Verdict.Pass, r.ReferentialIntegrity.Verdict);
            Assert.Equal(Verdict.Pass, r.DataTypeMatch.Verdict);   // the static type check has no identifier dependency
        }

        [Fact]
        public void probe_generation_throws_on_blank_identifiers()
        {
            Assert.Throws<ArgumentException>(() => RelationshipProbes.For(new RelationshipCheckInput
            {
                ManyTable = "Sales", ManyColumn = "", OneTable = "Customer", OneColumn = "Key",
            }));
            Assert.Throws<ArgumentException>(() => RelationshipProbes.OrphanRowsQuery("Sales", "FK", "  ", "Key"));
            Assert.Throws<ArgumentException>(() => RelationshipProbes.DuplicateKeysQuery(null, "Key"));
            Assert.Throws<ArgumentException>(() => RelationshipProbes.RowCountQuery("", "ManyRowCount"));
        }

        // Finding 4: a type mismatch is an UPSTREAM defect — orphans measured across an incompatible join are a
        // downstream symptom, so RI is demoted to Suspect (mismatch named), not a second independent Fail.
        [Fact]
        public void type_mismatch_demotes_ri_to_suspect_naming_the_mismatch()
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(manyType: "Int64", oneType: "String", probe: Probe(orphans: 7, dup: 0)));
            Assert.Equal(Verdict.Fail, r.DataTypeMatch.Verdict);
            Assert.Equal(Verdict.Suspect, r.ReferentialIntegrity.Verdict);
            Assert.NotEqual(Verdict.Fail, r.ReferentialIntegrity.Verdict);   // NOT a second independent Fail
            Assert.Null(r.ReferentialIntegrity.Count);                       // the orphan number is suppressed under a suspect
            Assert.Contains("type mismatch", r.ReferentialIntegrity.RootCause);

            // one root cause + its symptom = one Fail (the type) + one Suspect (RI), never two Fails.
            var report = RelationshipIntegrity.Evaluate(new[] { Rel(manyType: "Int64", oneType: "String", probe: Probe(orphans: 7, dup: 0)) });
            Assert.Equal(1, report.Summary.Failed);
            Assert.Equal(1, report.Summary.Suspect);
        }

        // Finding 4 precedence: when BOTH uniqueness and type fail, RI names both causes, uniqueness FIRST (the
        // harder invalidator — a non-unique key makes the anti-join itself ambiguous).
        [Fact]
        public void both_upstream_failures_are_named_uniqueness_first()
        {
            var r = RelationshipIntegrity.EvaluateOne(Rel(manyType: "Int64", oneType: "String", probe: Probe(orphans: 3, dup: 2)));
            Assert.Equal(Verdict.Fail, r.DataTypeMatch.Verdict);
            Assert.Equal(Verdict.Fail, r.KeyUniqueness.Verdict);
            Assert.Equal(Verdict.Suspect, r.ReferentialIntegrity.Verdict);
            var cause = r.ReferentialIntegrity.RootCause;
            Assert.Contains("duplicate keys", cause);
            Assert.Contains("type mismatch", cause);
            Assert.True(
                cause.IndexOf("duplicate keys", StringComparison.Ordinal) < cause.IndexOf("type mismatch", StringComparison.Ordinal),
                "uniqueness must be named before the type mismatch (it is the harder invalidator)");
        }

        // Finding 5: the DTO's sides are now STRUCTURAL (ManyTable/OneTable), so probe generation reads the right
        // column off the right side — uniqueness/dup is a ONE-side (key) question; orphans iterate the MANY (FK) side.
        [Fact]
        public void probe_orientation_is_structural_one_is_the_key_side()
        {
            var q = RelationshipProbes.For(new RelationshipCheckInput
            {
                ManyTable = "Sales", ManyColumn = "CustomerKey",
                OneTable = "Customer", OneColumn = "CustomerKey",
            });
            // dup / blank-key are asked on the ONE (Customer) side...
            Assert.Contains("VALUES('Customer'[CustomerKey])", q.DuplicateKeys);
            Assert.Contains("'Customer'", q.BlankKeys);
            // ...orphans iterate the MANY (Sales) side, testing its FK against the ONE-side key set.
            Assert.Contains("'Sales'", q.OrphanRows);
            Assert.Contains("VALUES('Customer'[CustomerKey])", q.OrphanRows);
        }

        // ---- DAX identifier escaping (string-for-string) ---------------------------------------------------

        [Fact]
        public void quote_table_doubles_embedded_apostrophes()
        {
            Assert.Equal("'Sales'", RelationshipProbes.QuoteTable("Sales"));
            Assert.Equal("'Man''s Data'", RelationshipProbes.QuoteTable("Man's Data"));
            Assert.Equal("'a''b''c'", RelationshipProbes.QuoteTable("a'b'c"));
        }

        [Fact]
        public void column_ref_doubles_embedded_brackets_and_quotes_the_table()
        {
            Assert.Equal("'Sales'[Amount]", RelationshipProbes.ColumnRef("Sales", "Amount"));
            Assert.Equal("'Sales Data'[Net ]]USD]", RelationshipProbes.ColumnRef("Sales Data", "Net ]USD"));
            // both hazards at once: apostrophe in the table, bracket in the column.
            Assert.Equal("'Cust''omer'[Ke]]y]", RelationshipProbes.ColumnRef("Cust'omer", "Ke]y"));
        }

        [Fact]
        public void probe_queries_use_the_escaped_identifiers()
        {
            var rel = new RelationshipCheckInput
            {
                ManyTable = "Sales's", ManyColumn = "Cust]Key",
                OneTable = "Customer", OneColumn = "Key",
            };
            var q = RelationshipProbes.For(rel);
            Assert.Contains("'Sales''s'", q.OrphanRows);       // apostrophe doubled in the table literal
            Assert.Contains("'Sales''s'[Cust]]Key]", q.OrphanRows);   // bracket doubled in the column reference
            Assert.Contains("VALUES('Customer'[Key])", q.OrphanRows);
            Assert.Contains("\"OrphanRows\"", q.OrphanRows);   // the named scalar the runner reads back
            Assert.Contains("\"DuplicateKeys\"", q.DuplicateKeys);
            Assert.Contains("\"ManyRowCount\"", q.ManyRowCount);
            Assert.Contains("\"OneRowCount\"", q.OneRowCount);
        }
    }
}

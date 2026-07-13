using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>
    /// Four-state test verdict for the Tests tab (invariant I1 — "Unknown is not a pass"): a check that could not
    /// run is <see cref="NotVerifiable"/>, NEVER a silent Pass and never dropped. <see cref="Suspect"/> is a
    /// KNOWN-tainted state — a check invalidated by an upstream failure (invariant I3) — deliberately DISTINCT from
    /// the colourless NotVerifiable so the UI renders "downstream of a real defect" differently from "we didn't
    /// measure it". Lives in Semanticus.Engine (not a sub-namespace) so the coordinator can fold it into the one
    /// shared Tests-tab enum without touching call sites.
    /// Serialized BY NAME on both doors: the RPC pipe (Newtonsoft, RpcServer.CreateHandler) and the MCP
    /// door (System.Text.Json) would otherwise emit bare ints, which no UI or agent can read as a verdict.
    /// </summary>
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public enum Verdict { Pass, Fail, Suspect, NotVerifiable }

    /// <summary>
    /// The static + probed facts about ONE relationship, provider-agnostic so the whole check set is offline-testable
    /// (no ADOMD, no live connection here). CONTRACT made STRUCTURAL, not just documented: <see cref="ManyTable"/>/
    /// <see cref="ManyColumn"/> is the MANY (foreign-key) side and <see cref="OneTable"/>/<see cref="OneColumn"/> is
    /// the ONE (key) side. The fields are named for the SIDE they hold so a caller can't silently hand us a
    /// conventional oneToMany reversed and have us test the wrong column (the old From/To names invited exactly that).
    /// <see cref="Cardinality"/> is used only to recognise many-to-many (where there is no single-key side).
    /// <see cref="Probe"/> stays null until a live runner fills it in — and a null keeps the probe-backed checks
    /// NotVerifiable (I1), never Pass.
    /// </summary>
    public sealed class RelationshipCheckInput
    {
        public string Name { get; set; }            // relationship display name / id — carried onto the report row
        public string ManyTable { get; set; }       // MANY / foreign-key side
        public string ManyColumn { get; set; }
        public string OneTable { get; set; }        // ONE / key side
        public string OneColumn { get; set; }
        public string Cardinality { get; set; }     // "manyToOne" | "oneToMany" | "manyToMany" | "oneToOne" | null
        public bool IsActive { get; set; } = true;
        public string CrossFilter { get; set; }     // context only — never a verdict
        public string ManyColumnType { get; set; }  // TOM data-type name as a string, e.g. "Int64"
        public string OneColumnType { get; set; }
        public RelationshipProbeResult Probe { get; set; }
    }

    /// <summary>
    /// The counts a live runner obtains by executing <see cref="RelationshipProbes"/> queries. EVERY field is
    /// nullable on purpose: null = that probe did not run (its dependent check is NotVerifiable); a value —
    /// INCLUDING 0 — is a real measurement. Orphan / blank-FK are MANY-side row counts; duplicate / blank-key are
    /// ONE-side counts. Blank-FK and blank-key are carried as information (they are legitimate in many models),
    /// never a Fail.
    /// </summary>
    public sealed class RelationshipProbeResult
    {
        public long? OrphanRows { get; set; }        // many-side rows whose NON-blank FK has no match on the one side
        public long? BlankForeignKeys { get; set; }  // many-side rows whose FK is blank (informational, not a Fail)
        public long? DuplicateKeys { get; set; }     // one-side key VALUES occurring more than once (blanks excluded)
        public long? BlankKeys { get; set; }         // one-side rows whose key is blank (informational)
        public long? ManyRowCount { get; set; }      // population sizes — context (the MANY/FK side)
        public long? OneRowCount { get; set; }       // the ONE/key side
    }

    /// <summary>The names of the three verdict-bearing checks (stable ids for the UI / summary; kept as constants so
    /// a typo can't silently split a tally).</summary>
    public static class RelationshipChecks
    {
        public const string DataTypeMatch = "DataTypeMatch";
        public const string KeyUniqueness = "KeyUniqueness";
        public const string ReferentialIntegrity = "ReferentialIntegrity";
    }

    /// <summary>One check's outcome. <see cref="RootCause"/> is set ONLY when <see cref="Verdict"/> is Suspect
    /// because a sibling check failed (I3) — it names the real defect so the row reads as one root cause, not a
    /// second independent failure. <see cref="Count"/> carries the failing population (orphans / duplicates) when
    /// known, so the report can say root causes with numbers, not just "Fail".</summary>
    public sealed class CheckResult
    {
        public string Check { get; set; }
        public Verdict Verdict { get; set; }
        public string Message { get; set; }
        public string RootCause { get; set; }
        public long? Count { get; set; }
    }

    /// <summary>One relationship's full result: the three check verdicts + the informational context (blank counts,
    /// inactive flag). <see cref="Inactive"/> is check (d) — surfaced as context, NEVER a verdict, because an
    /// inactive relationship is a legitimate modelling choice.</summary>
    public sealed class RelationshipResult
    {
        public string Name { get; set; }
        public string ManyTable { get; set; }
        public string ManyColumn { get; set; }
        public string OneTable { get; set; }
        public string OneColumn { get; set; }
        public string Cardinality { get; set; }
        public bool IsActive { get; set; }
        public bool Inactive => !IsActive;            // check (d): context only, not a verdict
        public long? BlankForeignKeys { get; set; }   // check (c) note: reported, not failed
        public long? BlankKeys { get; set; }
        public long? ManyRowCount { get; set; }       // fact-side population — lets the UI state an orphan RATE, not a bare count
        public CheckResult DataTypeMatch { get; set; }
        public CheckResult KeyUniqueness { get; set; }
        public CheckResult ReferentialIntegrity { get; set; }
        public IEnumerable<CheckResult> Checks => new[] { DataTypeMatch, KeyUniqueness, ReferentialIntegrity };
    }

    /// <summary>Suite roll-up. Grade + Coverage are always paired (invariant I2): E4 owns <see cref="CoveragePct"/>
    /// (the fraction of checks that reached a real determination) and the raw tallies; the E3 analyzer turns them
    /// into a letter. Neither the pass tally nor a downstream grade is ever meaningful without this coverage.</summary>
    public sealed class RelationshipSuiteSummary
    {
        public int Relationships { get; set; }   // how many relationships were examined
        public int Checked { get; set; }         // total verdict-bearing checks (== 3 x Relationships)
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Suspect { get; set; }
        public int NotVerifiable { get; set; }
        public double CoveragePct { get; set; }  // (Checked - NotVerifiable) / Checked * 100; 0 when nothing was checked
    }

    public sealed class RelationshipIntegrityReport
    {
        public IReadOnlyList<RelationshipResult> Relationships { get; set; }
        public IReadOnlyList<TableRowCountResult> TableRowCounts { get; set; } = Array.Empty<TableRowCountResult>();
        public RelationshipSuiteSummary Summary { get; set; }
    }

    /// <summary>
    /// The pure relationship-integrity core for the Tests tab (E4). Consumes relationship metadata + optional probe
    /// results and produces per-relationship verdicts and a suite summary. No I/O: probe RESULTS arrive already
    /// measured (the live adapter, wired later, runs <see cref="RelationshipProbes"/> and fills them in). The three
    /// invariants are enforced HERE:
    ///   I1  a probe-less check is NotVerifiable, never Pass and never skipped;
    ///   I2  the summary always carries CoveragePct beside the tallies;
    ///   I3  a one-side uniqueness failure demotes THIS relationship's referential-integrity check to Suspect
    ///       (naming the duplicate as the root cause) rather than surfacing a second, independent Fail.
    /// </summary>
    public static class RelationshipIntegrity
    {
        public static RelationshipIntegrityReport Evaluate(IEnumerable<RelationshipCheckInput> relationships)
        {
            if (relationships == null) throw new ArgumentNullException(nameof(relationships));
            var results = relationships.Select(EvaluateOne).ToList();
            return new RelationshipIntegrityReport { Relationships = results, Summary = Summarize(results) };
        }

        public static RelationshipResult EvaluateOne(RelationshipCheckInput rel)
        {
            if (rel == null) throw new ArgumentNullException(nameof(rel));
            var probe = rel.Probe;
            var oneSide = RelationshipProbes.ColumnRef(rel.OneTable, rel.OneColumn);
            var manySide = RelationshipProbes.ColumnRef(rel.ManyTable, rel.ManyColumn);

            var dataType = CheckDataType(rel);

            // A blank table/column makes EVERY probe query a forged reference ('' / []) that either errors or — far
            // worse — silently addresses a different object, so a probe-backed check can't be trusted from garbage
            // coordinates: it is a config error (NotVerifiable), never the accidental Pass that blank+matching-probe
            // would otherwise mint (I1). The static data-type check has no such dependency (it compares the supplied
            // type strings), so it still runs — an unnamed endpoint is not a reason to un-know that two types match.
            var endpointsNamed = !IsBlank(rel.ManyTable) && !IsBlank(rel.ManyColumn)
                              && !IsBlank(rel.OneTable) && !IsBlank(rel.OneColumn);
            const string configError = "relationship endpoints are incompletely specified (a table or column name is blank)";

            // Check (b) is evaluated FIRST because it is the ROOT-CAUSE check: its verdict then STEERS check (c).
            // Many-to-many has no single-key side, so both probe-backed checks stand down as NotVerifiable — dups
            // are EXPECTED there, not a defect — and that is decided from cardinality BEFORE any count is trusted,
            // so a supplied DuplicateKeys value can never mint a false Fail on an m2m relationship. Precedence: a
            // structurally-invalid DTO can't be probed AT ALL, so the config error tops the m2m stand-down.
            var m2m = IsManyToMany(rel.Cardinality);
            var uniqueness =
                !endpointsNamed ? NotVerifiable(RelationshipChecks.KeyUniqueness, configError)
                : m2m ? NotVerifiable(RelationshipChecks.KeyUniqueness, "many-to-many cardinality: no single-key side to verify")
                : CheckUniqueness(probe, oneSide);

            var referentialIntegrity =
                !endpointsNamed ? NotVerifiable(RelationshipChecks.ReferentialIntegrity, configError)
                : m2m ? NotVerifiable(RelationshipChecks.ReferentialIntegrity, "many-to-many cardinality: no single-key side to anchor referential integrity")
                : CheckReferentialIntegrity(probe, uniqueness, dataType, manySide, oneSide);

            return new RelationshipResult
            {
                Name = rel.Name,
                ManyTable = rel.ManyTable,
                ManyColumn = rel.ManyColumn,
                OneTable = rel.OneTable,
                OneColumn = rel.OneColumn,
                Cardinality = rel.Cardinality,
                IsActive = rel.IsActive,
                // Informational counts are surfaced as-is EXCEPT a negative, which is a broken measurement, not a
                // fact about the model — null it rather than report "-1 blank FKs" (a negative never leaks out).
                BlankForeignKeys = NonNeg(probe?.BlankForeignKeys),
                BlankKeys = NonNeg(probe?.BlankKeys),
                ManyRowCount = NonNeg(probe?.ManyRowCount),
                DataTypeMatch = dataType,
                KeyUniqueness = uniqueness,
                ReferentialIntegrity = referentialIntegrity,
            };
        }

        // (a) Data-type match — static, metadata-only, verifiable offline. The one NotVerifiable path is missing
        // metadata: in a real TOM model both endpoint columns always carry a type, so a null means the caller
        // simply didn't supply it — say so rather than guess a Pass (I1). Comparison is case-insensitive on
        // purpose: a casing variance across metadata sources ("Int64"/"int64") is the SAME type, and an ordinal
        // compare would mint a false Fail; a real mismatch (Int64 vs String) fails either way.
        private static CheckResult CheckDataType(RelationshipCheckInput rel)
        {
            var many = rel.ManyColumnType == null ? null : rel.ManyColumnType.Trim();
            var one = rel.OneColumnType == null ? null : rel.OneColumnType.Trim();
            if (string.IsNullOrEmpty(many) || string.IsNullOrEmpty(one))
                return NotVerifiable(RelationshipChecks.DataTypeMatch, "column data types were not supplied");
            if (string.Equals(many, one, StringComparison.OrdinalIgnoreCase))
                return Pass(RelationshipChecks.DataTypeMatch, $"key types match ({many})");
            return Fail(RelationshipChecks.DataTypeMatch,
                $"type mismatch: {RelationshipProbes.ColumnRef(rel.ManyTable, rel.ManyColumn)} is {many} but " +
                $"{RelationshipProbes.ColumnRef(rel.OneTable, rel.OneColumn)} is {one}; this breaks join performance and can mis-match keys");
        }

        // (b) Key uniqueness on the ONE side — probe-backed. No probe result ⇒ NotVerifiable (I1). This is the
        // root-cause check; its verdict is consumed by (c).
        private static CheckResult CheckUniqueness(RelationshipProbeResult probe, string oneSide)
        {
            if (probe == null || probe.DuplicateKeys == null)
                return NotVerifiable(RelationshipChecks.KeyUniqueness, "no key-uniqueness probe result supplied");
            var dup = probe.DuplicateKeys.Value;
            // A count can never legitimately be negative; a negative one is a broken measurement (bad adapter /
            // overflow), so the check is NotVerifiable — NOT the accidental Pass a `> 0`-only test mints on -1 (I1).
            if (dup < 0)
                return NotVerifiable(RelationshipChecks.KeyUniqueness, $"invalid measurement: duplicate-key count on {oneSide} is negative ({dup})");
            if (dup > 0)
                return Fail(RelationshipChecks.KeyUniqueness, $"{dup} duplicate key value(s) on {oneSide}", dup);
            return Pass(RelationshipChecks.KeyUniqueness, $"key is unique on {oneSide}");
        }

        // (c) Referential integrity — orphan rows on the MANY side, EXCLUDING genuinely-blank FKs (those are their
        // own count and legitimate). ROOT-CAUSE DEMOTION (I3) is encoded in CONTROL FLOW: an orphan count measured
        // over an invalidated join is a downstream SYMPTOM, not an independent defect, so upstream failures demote
        // this check rather than surface a second Fail. Two things can invalidate the anti-join, in precedence order:
        //   1. a non-unique ONE-side key makes the anti-join itself ambiguous (the harder invalidator);
        //   2. a key TYPE MISMATCH means orphans were counted across an incompatible join.
        // CRUCIAL (finding 1): Suspect is a COVERED, known-tainted determination — it is only honest when the orphan
        // probe actually produced a valid number to BE tainted. With no such measurement there is nothing to taint,
        // so the demotion lands on NotVerifiable (NOT covered), still naming the upstream cause. Conflating the two
        // is exactly what let a probe-less relationship claim coverage it never had.
        private static CheckResult CheckReferentialIntegrity(
            RelationshipProbeResult probe, CheckResult uniqueness, CheckResult dataType, string manySide, string oneSide)
        {
            var causes = new List<string>();
            if (uniqueness.Verdict == Verdict.Fail) causes.Add("duplicate keys on " + oneSide);   // listed first: harder invalidator
            if (dataType.Verdict == Verdict.Fail) causes.Add("key type mismatch");

            // A trustworthy orphan number needs a probe that RAN and returned a valid (non-negative) count.
            var orphansMeasured = probe != null && probe.OrphanRows != null && probe.OrphanRows.Value >= 0;

            if (causes.Count > 0)
            {
                var cause = string.Join("; ", causes);
                if (!orphansMeasured)
                {
                    var why = probe == null || probe.OrphanRows == null ? "the orphan probe did not run" : "the orphan measurement is invalid";
                    return new CheckResult
                    {
                        Check = RelationshipChecks.ReferentialIntegrity,
                        Verdict = Verdict.NotVerifiable,   // finding 1: no measurement to taint ⇒ not covered, never a silent Suspect
                        Message = "cannot verify referential integrity: " + cause + ", and " + why,
                        RootCause = cause,
                    };
                }
                return new CheckResult
                {
                    Check = RelationshipChecks.ReferentialIntegrity,
                    Verdict = Verdict.Suspect,
                    Message = "referential integrity is unreliable until the upstream cause is fixed: " + cause,
                    RootCause = cause,
                };
            }
            if (probe == null || probe.OrphanRows == null)
                return NotVerifiable(RelationshipChecks.ReferentialIntegrity, "no referential-integrity probe result supplied");
            var orphans = probe.OrphanRows.Value;
            // A negative orphan count is a broken measurement — NotVerifiable, not the accidental Pass a `> 0`-only
            // test mints on -1 (I1).
            if (orphans < 0)
                return NotVerifiable(RelationshipChecks.ReferentialIntegrity, $"invalid measurement: orphan-row count in {manySide} is negative ({orphans})");
            if (orphans > 0)
                return Fail(RelationshipChecks.ReferentialIntegrity, $"{orphans} orphan row(s) in {manySide} with no matching key on {oneSide}", orphans);
            return Pass(RelationshipChecks.ReferentialIntegrity, $"no orphan rows in {manySide}");
        }

        // Recognise the many-to-many shape from the combined cardinality string. Format-tolerant (strips '-'/'_'
        // and casing) because callers spell it "manyToMany" / "many-to-many"; everything else — INCLUDING null,
        // which defaults to many-to-one — has a genuine one-side to verify.
        private static bool IsManyToMany(string cardinality)
            => !string.IsNullOrEmpty(cardinality)
               && cardinality.Replace("-", "").Replace("_", "").Trim().Equals("manytomany", StringComparison.OrdinalIgnoreCase);

        private static RelationshipSuiteSummary Summarize(IReadOnlyList<RelationshipResult> results)
        {
            var checks = results.SelectMany(r => r.Checks).ToList();
            int Tally(Verdict v) => checks.Count(c => c.Verdict == v);
            var notVerifiable = Tally(Verdict.NotVerifiable);
            var checkedCount = checks.Count;
            // Coverage (I2) = the fraction of checks that reached a REAL determination. NotVerifiable is the only
            // "not covered" bucket; Pass / Fail / Suspect are all determinations (a Suspect is a definite "tainted
            // by a named upstream failure", not an "unknown"). checkedCount == 0 ⇒ 0% — nothing was verified, so
            // NEVER a vacuous 100 — which also signals the E3 analyzer to treat the category as dormant (Applicable=0).
            var coverage = checkedCount == 0 ? 0.0 : Math.Round((double)(checkedCount - notVerifiable) / checkedCount * 100.0, 1);
            return new RelationshipSuiteSummary
            {
                Relationships = results.Count,
                Checked = checkedCount,
                Passed = Tally(Verdict.Pass),
                Failed = Tally(Verdict.Fail),
                Suspect = Tally(Verdict.Suspect),
                NotVerifiable = notVerifiable,
                CoveragePct = coverage,
            };
        }

        private static CheckResult Pass(string check, string message)
            => new CheckResult { Check = check, Verdict = Verdict.Pass, Message = message };
        private static CheckResult Fail(string check, string message, long? count = null)
            => new CheckResult { Check = check, Verdict = Verdict.Fail, Message = message, Count = count };
        private static CheckResult NotVerifiable(string check, string why)
            => new CheckResult { Check = check, Verdict = Verdict.NotVerifiable, Message = why };

        // A DTO identifier is "blank" when it is null/empty/whitespace — an unnamed endpoint we refuse to probe.
        private static bool IsBlank(string s) => string.IsNullOrWhiteSpace(s);

        // Informational counts can never legitimately be negative; a negative is a broken measurement, so drop it to
        // null rather than surface "-1" to the report (findings: no negative leaks out as if it were a fact).
        private static long? NonNeg(long? v) => v.HasValue && v.Value < 0 ? (long?)null : v;
    }
}

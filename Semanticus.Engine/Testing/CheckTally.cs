using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>
    /// A run counted in the one unit a person sees: the checks on the Validation page. A saved check is one check, Relationships is one
    /// check and Table row counts is one check, however many probes or comparisons each made inside (those stay in that check's detail).
    /// The page, the assistant's note and the kept report all say "2 of 3 checks pass" from this, never the probe count the grade is
    /// worked out from. Mirrors the page's rules (tests-model.ts relationshipChip, countChip) so the three never disagree.
    /// </summary>
    public readonly struct CheckTally
    {
        public int Passed { get; }
        public int Differs { get; }
        public int Cannot { get; }
        public int NotRun { get; }
        /// <summary>Checks that differ and whose exact difference the person accepted. They are in <see cref="Ran"/>, and never in <see cref="Passed"/>.</summary>
        public int Accepted { get; }
        /// <summary>The checks that ran: the number under "of" on the page. A check that has not run never shrinks it.</summary>
        public int Ran => Passed + Differs + Cannot + Accepted;

        private CheckTally(int passed, int differs, int cannot, int notRun, int accepted) { Passed = passed; Differs = differs; Cannot = cannot; NotRun = notRun; Accepted = accepted; }

        public static CheckTally Of(TestSuiteRunResult run)
        {
            int pass = 0, differ = 0, cannot = 0, notRun = 0, accepted = 0;
            foreach (var o in run?.Reconciles ?? System.Array.Empty<ReconcileOutcome>())
            {
                // A measure with no saved check gets a coverage outcome ("unmapped:") so the grade sees the gap. It is not a check the person has, so it never counts here (hero 26 N3: "2 of 11" for 2 of 3).
                if (o == null || (o.DefId != null && o.DefId.StartsWith("unmapped:", System.StringComparison.Ordinal))) continue;
                if (o.Waiver != null && (o.Verdict == Verdict.Fail || o.Verdict == Verdict.Suspect)) { accepted++; continue; }
                switch (o.Verdict)
                {
                    case Verdict.Pass: pass++; break;
                    case Verdict.Fail: case Verdict.Suspect: differ++; break;
                    default: cannot++; break;
                }
            }
            var rels = run?.Relationships?.Relationships?.Where(r => r != null).ToList();
            if (rels != null && rels.Count > 0)
            {
                var perRel = rels.Select(r => r.Checks.Where(c => c != null).Select(c => c.Verdict).ToList()).ToList();
                var attention = perRel.Any(v => v.Any(x => x == Verdict.Fail || x == Verdict.Suspect));
                var passed = perRel.Count(v => v.Count > 0 && v.All(x => x == Verdict.Pass));
                if (attention) differ++;
                else if (passed == perRel.Count) pass++;
                else cannot++;
            }
            var counts = run?.Relationships?.TableRowCounts?.Where(t => t != null).ToList();
            if (counts != null && counts.Count > 0)
            {
                if (counts.Any(t => t.Check?.Verdict == Verdict.Fail)) differ++;
                else if (counts.All(t => t.Check?.Verdict == Verdict.Pass)) pass++;
                else notRun++;
            }
            return new CheckTally(pass, differ, cannot, notRun, accepted);
        }

        /// <summary>"2 of 3 checks pass", or "1 of 1 check passes". An accepted difference is named beside it, never inside it: "4 of 5 checks pass, 1 accepted".</summary>
        public string Words() => Ran == 0 ? "no checks ran" : Passed.ToString(System.Globalization.CultureInfo.InvariantCulture) + " of " + Ran.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + (Ran == 1 ? " check passes" : " checks pass")
            + (Accepted > 0 ? ", " + Accepted.ToString(System.Globalization.CultureInfo.InvariantCulture) + " accepted" : "");

        /// <summary>The words, then what did not pass: "2 of 3 checks pass: 1 differs, 1 could not be checked, 1 not run yet."</summary>
        public string Line()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var rest = new System.Collections.Generic.List<string>();
            if (Differs > 0) rest.Add(Differs.ToString(inv) + (Differs == 1 ? " differs" : " differ"));
            if (Cannot > 0) rest.Add(Cannot.ToString(inv) + " could not be checked");
            if (NotRun > 0) rest.Add(NotRun.ToString(inv) + " not run yet");
            return Words() + (rest.Count > 0 ? ": " + string.Join(", ", rest) + "." : ".");
        }
    }
}
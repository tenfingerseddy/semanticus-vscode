using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Analysis;

// The reading card (Kane's pick A, 12 Oct 2026): on the Plan, the model's own first read. The grade, the grade after the safe fixes,
// the top three issues in plain words with who fixes each, and one Fix them. The Studio's card and the assistant's reading_card and
// fix_them tools all come through LocalEngine.ReadingCardAsync and FixThemAsync (golden rule 2: one path, two doors).
//
// This file is the pure half: it turns a scorecard into the card. It reads nothing from the model and writes nothing.
namespace Semanticus.Engine
{
    /// <summary>Who fixes an issue. The words are the card's own: Semanticus fixes it now, the assistant writes it, or the person decides.</summary>
    public static class ReadingWho
    {
        public const string Semanticus = "semanticus";
        public const string Assistant = "assistant";
        public const string Person = "you";
    }

    /// <summary>One issue on the card: findings of one kind, counted and said in plain words.</summary>
    public sealed class ReadingIssue
    {
        /// <summary>A stable key for this kind of issue (the writing job's send key carries it).</summary>
        public string Key { get; set; }
        public int Count { get; set; }
        /// <summary>The count and the problem, as one sentence: "267 measures have no description".</summary>
        public string Headline { get; set; }
        /// <summary>One short line on why it matters: "Copilot has to guess what they mean."</summary>
        public string Why { get; set; }
        /// <summary>One of <see cref="ReadingWho"/>.</summary>
        public string Who { get; set; }
        /// <summary>True when this kind of issue holds the grade down (a cap, such as "more than half the measures have no description").</summary>
        public bool HoldsGrade { get; set; }
        /// <summary>"open", or "queued" when the assistant already has a writing job for it on the plan. A queued issue is handled.</summary>
        public string State { get; set; } = "open";
        public string[] RuleIds { get; set; } = Array.Empty<string>();
    }

    /// <summary>A writing job Fix them gives the assistant (a plan task for the assistant).</summary>
    public sealed class ReadingJob
    {
        /// <summary>The plan row id of the task, so Undo can take it back while the assistant has not started it.</summary>
        public string RowId { get; set; }
        public string Words { get; set; }
        public int Count { get; set; }
        public string Key { get; set; }
    }

    /// <summary>The card, as both doors read it.</summary>
    public sealed class ReadingCardView
    {
        /// <summary>True while the card should show: at least one of the top three is open and is not only for the person to decide.</summary>
        public bool Show { get; set; }
        public string Grade { get; set; }
        public int Score { get; set; }
        /// <summary>The grade the safe fixes reach, from the same rehearsal the Plan uses. Null while it is not worked out (or not asked for).</summary>
        public string GradeAfter { get; set; }
        public int? ScoreAfter { get; set; }
        /// <summary>The first sentence says what is wrong (the grade is held down by).</summary>
        public string[] HeldBy { get; set; } = Array.Empty<string>();
        public int SafeFixCount { get; set; }
        /// <summary>How many writing jobs Fix them would give the assistant now, with the findings behind them.</summary>
        public int WritingCount { get; set; }
        public int WritingJobs { get; set; }
        public int OpenFindings { get; set; }
        public ReadingIssue[] Top { get; set; } = Array.Empty<ReadingIssue>();
        public long Revision { get; set; }
        /// <summary>The line under Fix them: what the press does.</summary>
        public string Next { get; set; }
    }

    /// <summary>What Fix them did.</summary>
    public sealed class FixThemResult
    {
        public long Revision { get; set; }
        public int Fixed { get; set; }
        public string GradeBefore { get; set; }
        public int ScoreBefore { get; set; }
        public string GradeAfter { get; set; }
        public int ScoreAfter { get; set; }
        public ReadingJob[] Jobs { get; set; } = Array.Empty<ReadingJob>();
        /// <summary>The line the person reads in the note: what changed, in plain words.</summary>
        public string Sentence { get; set; }
    }

    public static class ReadingCardBuilder
    {
        // A kind of issue: which rules it joins, the words, and the grade cap it can hold. The count goes in front of the noun.
        private sealed class Kind
        {
            public string Key; public string[] Rules; public string One; public string Many; public string Why;
        }

        // The wording is the rules' own meaning in plain words (docs/product-copy-style.md: no engine words, no em dashes). A rule with
        // no entry still reads: Fallback names it by its title.
        private static readonly Kind[] Kinds =
        {
            new Kind { Key = "desc-measure", Rules = new[] { "DESC-MEASURE" }, One = "measure has no description", Many = "measures have no description", Why = "Copilot has to guess what they mean." },
            new Kind { Key = "desc-column", Rules = new[] { "DESC-COLUMN" }, One = "column has no description", Many = "columns have no description", Why = "Copilot has to guess what they hold." },
            new Kind { Key = "desc-table", Rules = new[] { "DESC-TABLE" }, One = "table has no description", Many = "tables have no description", Why = "Copilot cannot tell what each table is for." },
            new Kind { Key = "desc-other", Rules = new[] { "DESC-HIERARCHY", "DESC-CALCGROUP-ITEMS", "DESC-ECHO", "DESC-ECHO-OBJECT", "DESC-LONG", "DESC-LONG-OBJECT", "DESC-PLACEHOLDER" },
                One = "description needs a rewrite", Many = "descriptions need a rewrite", Why = "A weak description helps no one." },
            new Kind { Key = "keys", Rules = new[] { "VIS-FK", "VIS-KEY", "FMT-SUMMARIZE", "SUMMARIZE-DIMENSION" },
                One = "key column shows in the field list or adds up as numbers", Many = "key columns show in the field list or add up as numbers", Why = "People see IDs where they expect names." },
            new Kind { Key = "other-names", Rules = new[] { "SYN-FIELD", "SYN-TABLE" }, One = "field has no other names", Many = "fields have no other names", Why = "Copilot only knows the exact names." },
            new Kind { Key = "name-schema", Rules = new[] { "SYN-SCHEMA" }, One = "model has no list of other names", Many = "model has no list of other names", Why = "Copilot cannot learn what people call things." },
            new Kind { Key = "same-name", Rules = new[] { "NAME-DUP" }, One = "field shares its name with another field", Many = "fields share a name with another field", Why = "Copilot cannot tell them apart." },
            new Kind { Key = "names", Rules = new[] { "NAME-MEASURE", "NAME-COLUMN", "NAME-COLUMN-ID", "NAME-TABLE", "NAME-GENERIC", "NAME-TECH-PREFIX", "NAME-HIERARCHY", "NAME-INVALID-CHARS" },
                One = "name is hard to read", Many = "names are hard to read", Why = "People and Copilot both guess wrong." },
            new Kind { Key = "formats", Rules = new[] { "FMT-MEASURE", "FMT-COLUMN", "FMT-DATATYPE", "FMT-SORTBY" },
                One = "number has no format", Many = "numbers have no format", Why = "People see long raw numbers." },
            new Kind { Key = "implicit", Rules = new[] { "DAC-IMPLICIT-MEASURE" }, One = "column can be added up without a measure", Many = "columns can be added up without a measure", Why = "Copilot may add up numbers that should not be added." },
            new Kind { Key = "relationships", Rules = new[] { "REL-BIDI", "REL-DISCONNECTED", "REL-INACTIVE", "REL-M2M", "REL-SNOWFLAKE", "REL-HIERARCHY-MISSING", "REL-HIERARCHY-SINGLE-LEVEL" },
                One = "relationship needs a look", Many = "relationships need a look", Why = "Questions across tables can give wrong answers." },
        };

        // The rule ids a grade cap rests on, by the words the scorecard's GatedBy reasons carry (ReadinessAnalyzer.Analyze).
        private static IEnumerable<string> GateRules(string reason)
        {
            if (reason == null) yield break;
            if (reason.IndexOf("undescribed", StringComparison.OrdinalIgnoreCase) >= 0) yield return "DESC-MEASURE";
            if (reason.IndexOf("scale ceiling", StringComparison.OrdinalIgnoreCase) >= 0) { yield return "LIMIT-SCALE"; yield return "SCALE-QNA-INDEX"; yield return "SCALE-HICARD-COLUMN"; }
            if (reason.IndexOf("refers to an object", StringComparison.OrdinalIgnoreCase) >= 0) yield return "DAX-REFERENCE";
        }

        public static string Count(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The findings that count (an accepted finding is left out; one only covered by an acceptance still counts).</summary>
        private static bool Active(ReadinessFinding f) => !f.Waived || f.WaivedAsTwin;

        /// <summary>Which of the writing jobs a finding belongs to, or null when it is not writing: descriptions or other names.</summary>
        public static string WritingKeyOf(ReadinessFinding f)
        {
            if (f.Fix != nameof(FixKind.AiContent) || f.RuleId == null) return null;
            if (f.RuleId.StartsWith("DESC-", StringComparison.Ordinal)) return "descriptions";
            if (f.RuleId.StartsWith("SYN-", StringComparison.Ordinal)) return "other-names";
            return "other-writing";
        }

        /// <summary>Build the card from a scorecard. <paramref name="queuedKeys"/> are the send keys of writing jobs already open on the plan.</summary>
        public static ReadingCardView Build(Scorecard card, ISet<string> queuedKeys, long revision, IReadOnlyDictionary<string, double> lifts = null)
        {
            var active = card.Findings.Where(Active).ToArray();
            var gateRules = new HashSet<string>((card.GatedBy ?? Array.Empty<string>()).SelectMany(GateRules), StringComparer.Ordinal);
            var kindOf = new Dictionary<string, Kind>(StringComparer.Ordinal);
            foreach (var k in Kinds) foreach (var r in k.Rules) kindOf[r] = k;

            var issues = new List<(ReadingIssue Issue, int Severity, double Lift)>();
            foreach (var g in active.GroupBy(f => kindOf.TryGetValue(f.RuleId ?? "", out var k) ? k.Key : "rule:" + f.RuleId, StringComparer.Ordinal))
            {
                var list = g.ToArray();
                kindOf.TryGetValue(list[0].RuleId ?? "", out var kind);
                var n = list.Length;
                string headline, why;
                if (kind != null)
                {
                    headline = kind.Key == "name-schema" ? "This " + kind.One : Count(n) + " " + (n == 1 ? kind.One : kind.Many);
                    why = kind.Why;
                }
                else
                {
                    // A rule with no plain wording of its own reads by its title, so nothing is hidden.
                    headline = Count(n) + (n == 1 ? " finding: " : " findings: ") + list[0].RuleTitle;
                    why = "Open AI readiness to see each one.";
                }
                var safe = list.Count(f => f.Fix == nameof(FixKind.SafeFix));
                var write = list.Count(f => f.Fix == nameof(FixKind.AiContent));
                var who = safe >= write && safe >= list.Length - safe - write ? ReadingWho.Semanticus
                        : write >= list.Length - safe - write ? ReadingWho.Assistant : ReadingWho.Person;
                var key = kind?.Key ?? g.Key;
                var jobKey = WritingKeyOf(list.FirstOrDefault(f => WritingKeyOf(f) != null) ?? list[0]);
                var queued = who == ReadingWho.Assistant && jobKey != null && queuedKeys != null && queuedKeys.Contains(WritingSendKey(jobKey));
                var sev = list.Max(f => SevRank(f.Severity));
                // The grade points the whole group would lift, when the caller measured them (ReadinessAnalyzer.Lifts); otherwise 0 and severity and count rank it.
                var lift = lifts == null ? 0.0 : list.Sum(f => lifts.TryGetValue(ReadinessAnalyzer.LiftKey(f.RuleId, f.ObjectRef), out var l) ? l : 0.0);
                issues.Add((new ReadingIssue
                {
                    Key = key, Count = n, Headline = headline, Why = why, Who = who,
                    HoldsGrade = list.Any(f => gateRules.Contains(f.RuleId ?? "")),
                    State = queued ? "queued" : "open",
                    RuleIds = list.Select(f => f.RuleId).Distinct(StringComparer.Ordinal).ToArray(),
                }, sev, lift));
            }

            // The grade's holders first, then what Semanticus or the assistant can act on (an issue only the person decides never crowds one the card can fix),
            // then the most grade it would lift, then the worse findings, then the larger groups.
            var ranked = issues.OrderByDescending(x => x.Issue.HoldsGrade).ThenByDescending(x => x.Issue.Who != ReadingWho.Person).ThenByDescending(x => x.Lift)
                .ThenByDescending(x => x.Severity).ThenByDescending(x => x.Issue.Count).ToList();
            var topThree = ranked.Take(3).ToList();
            // Fix them does two things, so the card names both: when Semanticus has a fix to make and none of the three is one, the best of them takes the third place.
            var own = ranked.FirstOrDefault(x => x.Issue.Who == ReadingWho.Semanticus);
            if (own.Issue != null && topThree.Count == 3 && !topThree.Any(x => x.Issue.Who == ReadingWho.Semanticus)) topThree[2] = own;
            var top = topThree.Select(x => x.Issue).ToArray();

            var writing = active.Where(f => WritingKeyOf(f) != null).ToArray();
            var jobs = writing.Select(WritingKeyOf).Distinct(StringComparer.Ordinal).Count(k => queuedKeys == null || !queuedKeys.Contains(WritingSendKey(k)));
            var writingNow = writing.Where(f => queuedKeys == null || !queuedKeys.Contains(WritingSendKey(WritingKeyOf(f)))).Count();
            var view = new ReadingCardView
            {
                Grade = card.Grade,
                Score = (int)Math.Round(card.Overall, MidpointRounding.AwayFromZero),
                HeldBy = card.GatedBy ?? Array.Empty<string>(),
                SafeFixCount = card.SafeFixCount,
                WritingJobs = jobs,
                WritingCount = writingNow,
                OpenFindings = active.Length,
                Top = top,
                Revision = revision,
                // Only an issue Semanticus or the assistant can still act on keeps the card up. An issue only the person can decide never does.
                Show = top.Any(i => i.State == "open" && i.Who != ReadingWho.Person),
            };
            view.Next = NextLine(view);
            return view;
        }

        /// <summary>The send key of a writing job: the plan keeps one open task per key, so pressing Fix them twice never doubles the work.</summary>
        public static string WritingSendKey(string jobKey) => "reading|" + jobKey;

        private static int SevRank(string s) => string.Equals(s, "Critical", StringComparison.OrdinalIgnoreCase) ? 4
            : string.Equals(s, "High", StringComparison.OrdinalIgnoreCase) ? 3 : string.Equals(s, "Medium", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

        private static string Plural(int n, string one, string many) => Count(n) + " " + (n == 1 ? one : many);

        private static string NextLine(ReadingCardView v)
        {
            var parts = new List<string>();
            if (v.SafeFixCount > 0) parts.Add(Plural(v.SafeFixCount, "fix", "fixes") + " now.");
            if (v.WritingJobs > 0) parts.Add((v.WritingJobs == 1 ? "One writing task goes" : v.WritingJobs == 2 ? "Two writing tasks go" : Count(v.WritingJobs) + " writing tasks go") + " to your assistant.");
            return string.Join(" ", parts);
        }
    }
}

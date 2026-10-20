// checks.visual_kind: Jev picks the visual each check on the Validation page opens with (Kane, 15 Oct 2026). OFF by default.
//
// The page's rules always pick first and say which visuals can show a check's shape at all (`fits`). This point may only
// choose among those, or say the rules' pick is fine, so Jev can be wrong but never broken. All the checks of a run are asked in
// ONE call, one question each, named "visual:<check id>". What leaves the machine is check names, measure names, a kind word, a
// status word and counts. Never a value, never a row.
//
// Jev writes no text (golden rule 1). The reason shown beside a pick is one fixed sentence per visual, written here.
//
// THE BAR. JevPicks is false until a replay clears its bar, and only Kane flips it. While it is false nothing is ever asked,
// whatever the point's runtime switch says.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Decisions;

namespace Semanticus.Engine
{
    /// <summary>pick_check_visuals / pickCheckVisuals: what the Validation page (or an assistant) sends.</summary>
    public sealed class CheckVisualAsk
    {
        public CheckVisualItem[] Checks { get; set; }
    }

    public sealed class CheckVisualItem
    {
        /// <summary>The check's id. It names the question, so it is kept short and plain.</summary>
        public string Id { get; set; }
        public string Title { get; set; }
        /// <summary>The measure or table the check is about.</summary>
        public string Measure { get; set; }
        /// <summary>The kind of check in a word: "trusted answer", "against SQL", "row counts", "relationships".</summary>
        public string KindWord { get; set; }
        /// <summary>pass, differs, off or could_not_check.</summary>
        public string Status { get; set; }
        /// <summary>How many comparisons the last run made.</summary>
        public int Rows { get; set; }
        public bool HasPrior { get; set; }
        /// <summary>How many recorded runs there are, at most eight are read.</summary>
        public int Runs { get; set; }
        public bool HasSplit { get; set; }
        /// <summary>The visuals the page's rules leave for this check, in the rules' order.</summary>
        public string[] Fits { get; set; }
        /// <summary>The rules' own pick: the answer whenever Jev does not pick. Null for a check that has no visual (off).</summary>
        public string RulesPick { get; set; }
    }

    public sealed class CheckVisualPick
    {
        public const string ByJev = "jev";
        public const string ByRules = "rules";

        public string Id { get; set; }
        public string Kind { get; set; }
        public string By { get; set; }
        /// <summary>Engine-authored: the fixed sentence for <see cref="Kind"/>. Never Jev's words.</summary>
        public string Reason { get; set; }
        public double? Confidence { get; set; }
        /// <summary>Why Jev did not pick, in plain words, when it did not.</summary>
        public string Note { get; set; }
    }

    public sealed class CheckVisualPicks
    {
        public CheckVisualPick[] Picks { get; set; } = Array.Empty<CheckVisualPick>();
        /// <summary>One plain line about the whole call: why Jev was not asked, or nothing.</summary>
        public string Note { get; set; }
    }

    public static class JevCheckVisuals
    {
        /// <summary>Has the checks.visual_kind replay cleared its bar? No: no run has been made. Only Kane flips this.</summary>
        internal const bool JevPicks = false;

        /// <summary>Every question name starts with this and then names the check's id.</summary>
        public const string Prefix = "visual:";

        public static string Q(string checkId) => Prefix + checkId;

        /// <summary>The most checks one call asks about (the relay's own bound for this point).</summary>
        public const int MaxChecks = 40;

        public const int MaxTextChars = 200;
        public const int MaxIdChars = 120;
        public const int MaxRuns = 8;

        /// <summary>The neutral option: "the rules' pick is fine". It maps to the rules' pick, by the rules.</summary>
        public const string RulesOption = "rules";

        public static readonly IReadOnlyList<string> Kinds = new[]
        {
            "total", "bars", "line", "variance", "dumbbell", "bullet", "slope", "multiples", "waterfall", "sparktable", "heat", "table",
        };

        public static readonly IReadOnlyList<string> Statuses = new[] { "pass", "differs", "off", "could_not_check" };

        internal const string OffNote = "Jev's visual pick is off until it clears its bar.";
        internal const string SwitchedOffNote = "Jev's visual pick is switched off for this model.";
        internal const string NoModelNote = "Jev was not asked: no model is open.";
        internal const string NoChoiceNote = "Each check has only one visual that fits, so there was nothing for Jev to choose.";
        internal const string NeutralNote = "Jev said the rules' pick is fine.";

        /// <summary>The one sentence shown beside each visual. Plain, fixed, and the engine's own. It does not name the visual.</summary>
        public static string ReasonFor(string kind) => kind switch
        {
            "total" => "One number reads best large, with the parts beside it.",
            "bars" => "Side by side, the bars make the numbers easy to compare.",
            "line" => "Numbers over time read best as a line from left to right.",
            "variance" => "The gap from what you expected is the point, so the gap is drawn.",
            "dumbbell" => "Two numbers per row read best as a pair joined by a line.",
            "bullet" => "A number against its target reads best as a bar with a mark.",
            "slope" => "A change between two points reads best as a slanted line.",
            "multiples" => "Several checks on one scale read best as small charts side by side.",
            "waterfall" => "A total that builds from steps reads best as steps.",
            "sparktable" => "A row of small trend lines shows many checks at once.",
            "heat" => "A grid of numbers reads best with the size shown as colour.",
            "table" => "The rows themselves are the clearest way to read this.",
            _ => "The page's rules picked this visual.",
        };

        private static string Criterion(string kind) => kind switch
        {
            "total" => "one number shown large on its own, with its parts beside it",
            "bars" => "bars side by side, one per row",
            "line" => "a line across runs or dates from left to right",
            "variance" => "the gap between two numbers drawn for each row",
            "dumbbell" => "two numbers per row joined by a line, so the distance is the story",
            "bullet" => "a number drawn against its target",
            "slope" => "a slanted line from one point to another for each row",
            "multiples" => "a small chart for each row, all on one scale",
            "waterfall" => "steps that add up to a total",
            "sparktable" => "a table with a small trend line in each row",
            "heat" => "a grid of numbers with colour for size",
            "table" => "the rows as they are, one line each",
            _ => kind,
        };

        /// <summary>The kinds as given, known ones only, each once, in the rules' order.</summary>
        public static List<string> CleanFits(IEnumerable<string> fits) =>
            (fits ?? Array.Empty<string>()).Where(k => k != null && Kinds.Contains(k)).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>Read and tidy one ask. A plain message for anything the page should not have sent.</summary>
        internal static List<CheckVisualItem> Clean(CheckVisualAsk ask)
        {
            var checks = (ask?.Checks ?? Array.Empty<CheckVisualItem>()).Where(c => c != null).ToList();
            if (checks.Count > MaxChecks)
                throw new ArgumentException($"{checks.Count} checks is more than one call can ask about. The most is {MaxChecks}.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in checks)
            {
                if (string.IsNullOrWhiteSpace(c.Id)) throw new ArgumentException("Every check needs an id.");
                if (c.Id.Length > MaxIdChars) throw new ArgumentException($"A check id is longer than {MaxIdChars} characters.");
                if (!seen.Add(c.Id)) throw new ArgumentException($"The check id '{c.Id}' came twice.");
                if ((c.Title ?? "").Length > MaxTextChars) throw new ArgumentException($"A check name is longer than {MaxTextChars} characters.");
                if ((c.Measure ?? "").Length > MaxTextChars) throw new ArgumentException($"A measure name is longer than {MaxTextChars} characters.");
                if ((c.KindWord ?? "").Length > 40) throw new ArgumentException("A check's kind word is longer than 40 characters.");
                if (c.Status != null && !Statuses.Contains(c.Status))
                    throw new ArgumentException($"A check's status must be one of {string.Join(", ", Statuses)}.");
            }
            return checks;
        }

        /// <summary>The declared snapshot for the checks to ask about: their words and their shapes as counts. No values, no rows.</summary>
        public static IReadOnlyDictionary<string, object> Snapshot(IReadOnlyList<CheckVisualItem> asked)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.CheckVisualKind);
            var words = asked.Select(c => (object)new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = c.Id,
                ["name"] = (c.Title ?? "").Trim(),
                ["about"] = (c.Measure ?? "").Trim(),
                ["kind"] = (c.KindWord ?? "").Trim(),
            }).ToList();
            var shapes = asked.Select(c => (object)new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = c.Id,
                ["status"] = c.Status ?? "pass",
                ["comparisons"] = Math.Max(0, c.Rows),
                ["has_earlier_run"] = c.HasPrior,
                ["recorded_runs"] = Math.Min(MaxRuns, Math.Max(0, c.Runs)),
                ["is_split"] = c.HasSplit,
            }).ToList();
            var snapshot = DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("check_words", words),
                new KeyValuePair<string, object>("check_shapes", shapes),
            });
            DecisionSnapshot.RequireWithin(snapshot, 32 * 1024);
            return snapshot;
        }

        /// <summary>One question per check, in the order given. The fitting visuals come in an order seeded from the snapshot and the
        /// check's id (so the rules' order is not a hint, and the same ask is the same digest), then the neutral option last. Every
        /// question names its check and the measure it is about.</summary>
        public static IReadOnlyList<DecisionQuestion> Questions(IReadOnlyDictionary<string, object> snapshot, IReadOnlyList<CheckVisualItem> asked)
        {
            var seed = DecisionDigest.Sha256Hex(DecisionDigest.CanonicalSnapshotJson(snapshot));
            var list = new List<DecisionQuestion>();
            foreach (var c in asked)
            {
                var fits = CleanFits(c.Fits);
                var ordered = fits.OrderBy(k => DecisionDigest.Sha256Hex(seed + ":" + c.Id + ":" + k), StringComparer.Ordinal).ToList();
                var options = ordered.Select(k => new DecisionOption(k, Criterion(k))).ToList();
                options.Add(new DecisionOption(RulesOption, "none of these is clearly better than the visual the page's rules would pick"));
                var about = string.IsNullOrWhiteSpace(c.Measure) ? "" : " about " + c.Measure.Trim();
                list.Add(new DecisionQuestion(Q(c.Id), DecisionQuestionKind.Choice,
                    "On the Validation page, which visual should the check '" + (c.Title ?? "").Trim() + "'" + about
                    + " open with? Its details are under check_words and check_shapes with the id " + c.Id + ".",
                    options));
            }
            return list;
        }

        internal static CheckVisualPick Rules(CheckVisualItem c, string note)
        {
            var kind = !string.IsNullOrEmpty(c.RulesPick) ? c.RulesPick : null;
            return new CheckVisualPick { Id = c.Id, Kind = kind, By = CheckVisualPick.ByRules, Reason = kind == null ? null : ReasonFor(kind), Note = note };
        }

        internal static CheckVisualPicks AllRules(IEnumerable<CheckVisualItem> checks, string note) => new CheckVisualPicks
        {
            Picks = checks.Select(c => Rules(c, null)).ToArray(),
            Note = note,
        };

        /// <summary>Read Jev's answers. A check's pick is Jev's only when it is a visual in that check's fits, above the bar; anything
        /// else is the rules' pick with a note saying why. A check that was not asked is the rules' pick with no note.</summary>
        public static CheckVisualPicks Read(DecisionAnswers answers, double bar, IReadOnlyList<CheckVisualItem> all, IReadOnlySet<string> askedIds)
        {
            var picks = new List<CheckVisualPick>();
            foreach (var c in all)
            {
                if (!askedIds.Contains(c.Id)) { picks.Add(Rules(c, null)); continue; }
                var fits = CleanFits(c.Fits);
                var a = answers?.For(Q(c.Id));
                if (a == null || a.Choice == null) { picks.Add(Rules(c, "Jev did not answer, so the rules' pick stands.")); continue; }
                if (a.Confidence < bar) { picks.Add(WithConfidence(Rules(c, "Jev was not sure enough, so the rules' pick stands."), a.Confidence)); continue; }
                if (string.Equals(a.Choice, RulesOption, StringComparison.Ordinal)) { picks.Add(WithConfidence(Rules(c, NeutralNote), a.Confidence)); continue; }
                if (!fits.Contains(a.Choice, StringComparer.Ordinal))
                { picks.Add(WithConfidence(Rules(c, "Jev named a visual the rules had ruled out, so it was not used and the rules' pick stands."), a.Confidence)); continue; }
                picks.Add(new CheckVisualPick { Id = c.Id, Kind = a.Choice, By = CheckVisualPick.ByJev, Reason = ReasonFor(a.Choice), Confidence = a.Confidence });
            }
            return new CheckVisualPicks { Picks = picks.ToArray() };
        }

        private static CheckVisualPick WithConfidence(CheckVisualPick p, double c) { p.Confidence = c; return p; }

        /// <summary>The seeded order a question offers, for the tests and the replay.</summary>
        internal static IReadOnlyList<string> OfferedOrder(DecisionQuestion question) => question.Options.Select(o => o.Id).ToList();
    }
}

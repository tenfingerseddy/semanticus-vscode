// Three Plan rows for "build a model from your SQL source, press Run the plan, give the words to your assistant"
// (Kane, 28 Sep; the Plan statement check found each gap on the real app).
//
//   bpafix/model   "Fix N best-practice issues". A Semanticus row built from the Model quality scan's own fixes, so
//                  Run the plan takes it and it is one undo step with the rest of the batch. Each line is the rule's
//                  own fix (BpaAnalyzer.PreviewFix, through MapBpaFix, the same mapping propose_plan uses), never a
//                  copy of it. Four kinds of line are left out and named, because the row claims "safe":
//                    - any format the engine cannot prove (THE FORMAT FILL RULE, coordinator 28 Sep; FormatFill below).
//                      It fills an EMPTY format only, only with #,0, and only on a count or a whole-number sum
//                      (COUNTROWS, DISTINCTCOUNT, SUM of an Int64 column) or an Int64 column that is no key, id, code
//                      or flag. A ratio filled with #,0 shows 73% as 1 and money loses its cents, so every other
//                      format is a choice: the words row hands a measure's to the assistant. Never beside a format
//                      string expression (Analysis Services allows one or the other), and never over a format the
//                      object already has.
//                    - a rename, a data type change or an expression, which are not property fills.
//                    - a fix the AI readiness safe fixes already make to the same property of the same object, so
//                      the two rows never race for one write and one press does both.
//                  The count is the lines the row would write, worked out on the model as it is.
//
//   words/model    "Give the words to your assistant". Descriptions, business names, synonyms, the AI instructions and
//                  the formats Semanticus cannot prove are the assistant's to write, and the engine writes no words
//                  (golden rule 1), so the whole queue goes to the person's own assistant as ONE task. get_words_queue
//                  hands it over from one call, a page at a time so a large model fits the assistant's reading limit,
//                  ordered by how much each word lifts the grade (ReadinessAnalyzer.Lifts). It never waits behind the
//                  unused-object cleanup (PlanFootprint.Waits), and it does not open on a model with no tables.
//
//   Starts         A model with no tables has nothing to plan yet, so the view carries the ways to start: from a SQL
//                  source (the Model Spec wizard) and a new blank model. Both doors read it, whatever rows are open.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>One way to start a model, offered by the Plan while the model has no tables.</summary>
    public sealed class ModelPlanStart
    {
        /// <summary>"sql" or "blank".</summary>
        public string Id { get; set; }
        public string Label { get; set; }
        public string Line { get; set; }
        /// <summary>Where the Studio goes: the page and what to open on it.</summary>
        public ModelPlanJump Jump { get; set; }
        /// <summary>The agent door's way to do the same thing.</summary>
        public string Op { get; set; }
    }

    /// <summary>One entry in the words queue: the finding and what it lifts. Its object's grounding is on the page
    /// (<see cref="WordsQueue.Grounding"/>), once for all of that object's items.</summary>
    public sealed class WordsQueueItem
    {
        public string Kind { get; set; }            // "describe" | "name" | "synonym" | "instructions" | "format"
        /// <summary>The MCP tool that writes it: set_description, rename_object, set_synonyms, create_linguistic_schema,
        /// set_ai_instructions, set_measure_format, or set_property (FormatString) for a key or date column's format.</summary>
        public string Tool { get; set; }
        public string RuleId { get; set; }
        public string ObjectRef { get; set; }
        public string ObjectName { get; set; }
        public string Message { get; set; }
        /// <summary>Score points fixing this one alone adds to the model as it is now (ReadinessAnalyzer.Lifts). It is a
        /// marginal figure: a first fix that switches other rules on (the synonym store, a first description) can move
        /// the score by more or less than its Lift once the rules it wakes are counted.</summary>
        public double Lift { get; set; }
        /// <summary>True while this finding's rule holds the grade under a cap.</summary>
        public bool HoldsGate { get; set; }
    }

    /// <summary>What get_words_queue answers.</summary>
    public sealed class WordsQueue
    {
        public long Revision { get; set; }
        public string Grade { get; set; }
        public double Score { get; set; }
        public string[] GatedBy { get; set; } = Array.Empty<string>();
        /// <summary>What the assistant is being asked to do, in one plain line.</summary>
        public string Line { get; set; }
        /// <summary>Every open item, whatever page this is.</summary>
        public int Total { get; set; }
        /// <summary>How many of Total are each kind, so the first page shows the whole job.</summary>
        public Dictionary<string, int> Kinds { get; set; } = new Dictionary<string, int>();
        /// <summary>Where this page starts in the whole ordered list.</summary>
        public int Offset { get; set; }
        /// <summary>The offset of the next page, or null when this page reaches the end.</summary>
        public int? Next { get; set; }
        /// <summary>Each object on this page once, by the ObjectRef of its items: its DAX, table, format and sibling
        /// names. An object with three words to write is one entry here, not three copies inside its items.</summary>
        public Dictionary<string, GroundingBundle> Grounding { get; set; } = new Dictionary<string, GroundingBundle>();
        public WordsQueueItem[] Items { get; set; } = Array.Empty<WordsQueueItem>();
    }

    public sealed partial class LocalEngine
    {
        internal const string BpaFixItemId = "bpafix/model";
        internal const string WordsItemId = "words/model";

        internal static bool IsBpaFixItem(string id) => string.Equals(id, BpaFixItemId, StringComparison.Ordinal);
        internal static bool IsWordsItem(string id) => string.Equals(id, WordsItemId, StringComparison.Ordinal);

        /// <summary>The one plain line the words row and the queue both say.</summary>
        internal const string WordsLine =
            "Your assistant writes the words each one needs (a description, a business name, synonyms or the AI "
            + "instructions) and chooses the formats Semanticus cannot prove, starting with the ones that raise the grade "
            + "most. Semanticus checks every word lands and you can undo any of it.";

        /// <summary>The line the task given from the words row carries first, so the assistant knows the one call that
        /// hands it the whole list (verify, 28 Sep, M6): the task's own brief lists only the first objects.</summary>
        internal const string WordsTaskLine =
            "Call get_words_queue for the whole list, most grade first, a page at a time (pass offset = next until next "
            + "is null), and again after each round you write: creating the synonym store opens the AI instructions. Each "
            + "item names the tool that writes it.";

        /// <summary>The page get_words_queue returns when the caller names none, and the most it returns at once. The
        /// count is a ceiling; the size is the real limit (<see cref="WordsPageChars"/>), because an item with its
        /// object's DAX runs from a few hundred characters to several thousand (verify V8: 40 items on measures of
        /// 2,000 characters of DAX were 161,454 characters).</summary>
        internal const int WordsPageDefault = 40;
        internal const int WordsPageMax = 100;

        /// <summary>The most characters a WHOLE page is, as the JSON goes on the wire (escapes counted: the door writes ' and
        /// + as six characters, so DAX is about 1.4 characters a token). Claude Code lets a result of up to 50,000
        /// characters through uncounted and cuts a longer one, so a default page stays at 40,000 with room for what the
        /// door appends (verify round 3, V8; coordinator ruling 29 Sep). A page always has at least one item, so one object
        /// larger than this still gets a page of its own.</summary>
        internal const int WordsPageChars = 40_000;

        /// <summary>The longest text (a formula, a description, a format string, a name) a page carries whole. A longer one is
        /// cut with a note that says how long it is and where the whole value is (get_grounding), so no single object can
        /// push a page past the reading limit (verify V8, then L10: a 48,000-character description did what a long formula
        /// used to).</summary>
        internal const int WordsExpressionChars = 12_000;

        /// <summary>The most a single object's grounding is on the wire. A formula full of ' and + costs six characters for
        /// each, so every text is cut again, by what it costs and not by what it holds, until the whole grounding fits.</summary>
        internal const int WordsGroundingChars = 24_000;

        /// <summary>The longest message an item carries whole.</summary>
        internal const int WordsMessageChars = 4_000;

        /// <summary>The grounding as a page carries it: every long text in it (the formula, the description, the format, the
        /// names) cut to the same length, and that length shortened until the whole grounding fits. Each cut says how long
        /// the whole value is and that get_grounding returns it.</summary>
        private static GroundingBundle CutForPage(GroundingBundle g)
        {
            if (g == null) return g;
            GroundingBundle cut;
            for (var keep = WordsExpressionChars; ; keep = keep * 2 / 3)
            {
                cut = CutTexts(g, keep);
                if (JsonChars(cut) <= WordsGroundingChars) return cut;
                if (keep <= 100) break;
            }
            // Still too long with every text at 100 characters: the sibling names are many and made of characters the door
            // escapes (' and + are six characters each), so 50 siblings of 500 such characters gave a words page of 45,751
            // (verify R5-4). The list is then shortened, and its last entry says how many names are left out and where they are.
            var all = cut.SiblingNames ?? Array.Empty<string>();
            if (all.Length == 0) return cut;
            for (var n = all.Length / 2; ; n /= 2)
            {
                cut.SiblingNames = all.Take(n)
                    .Append("... and " + (all.Length - n) + " more sibling names; get_grounding has them all").ToArray();
                if (n == 0 || JsonChars(cut) <= WordsGroundingChars) return cut;
            }
        }

        private static GroundingBundle CutTexts(GroundingBundle g, int keep) => new GroundingBundle
        {
            ObjectRef = g.ObjectRef, Kind = g.Kind, DataType = g.DataType,
            Name = CutText(g.Name, keep, "name"), Table = CutText(g.Table, keep, "name"),
            Expression = CutText(g.Expression, keep, "formula"), FormatString = CutText(g.FormatString, keep, "format"),
            ExistingDescription = CutText(g.ExistingDescription, keep, "description"),
            SiblingNames = (g.SiblingNames ?? Array.Empty<string>()).Select(n => CutText(n, Math.Min(keep, 300), "name")).ToArray(),
            Calendars = (g.Calendars ?? Array.Empty<string>()).Select(c => CutText(c, keep, "line")).ToArray(),
        };

        private static string CutText(string whole, int keep, string what) =>
            whole == null || whole.Length <= keep ? whole
            : whole.Substring(0, keep) + " ... [cut here, " + whole.Length + " characters in all"
              + (what == null ? "" : "; get_grounding has the whole " + what) + "]";

        private static WordsQueueItem CutForPage(WordsQueueItem x) =>
            x.Message == null || x.Message.Length <= WordsMessageChars ? x
            : new WordsQueueItem
            {
                Kind = x.Kind, Tool = x.Tool, RuleId = x.RuleId, ObjectRef = x.ObjectRef, ObjectName = x.ObjectName, Lift = x.Lift, HoldsGate = x.HoldsGate,
                Message = CutText(x.Message, WordsMessageChars, null),
            };

        // What the MCP door sends is the tool's return value serialized with the SDK's own options, so that is what is counted.
        private static readonly System.Text.Json.JsonSerializerOptions PageSizeJson = ModelContextProtocol.McpJsonUtilities.DefaultOptions;

        private static int JsonChars(object o) => System.Text.Json.JsonSerializer.Serialize(o, PageSizeJson).Length;

        /// <summary>The page both doors ask for: the default when none is named, never more than the most.</summary>
        internal static int WordsPageSize(int max) => max <= 0 ? WordsPageDefault : Math.Min(max, WordsPageMax);

        // ---- the best-practice fixes --------------------------------------------------------------------------

        /// <summary>A BPA fix the Plan will not make, and why, in the words the row shows.</summary>
        private static string BpaLineRefusal(ChangeItem line, ITabularNamedObject obj)
        {
            var prop = line?.Target ?? "";
            if (string.Equals(prop, "name", StringComparison.OrdinalIgnoreCase))
                return "This fix renames it, so it is yours to choose on Model quality.";
            if (string.Equals(prop, "datatype", StringComparison.OrdinalIgnoreCase))
                return "This fix changes the column's data type, so it is yours to choose on Model quality.";
            if (string.Equals(prop, "expression", StringComparison.OrdinalIgnoreCase))
                return "This fix rewrites a formula, so it is yours to choose on Model quality.";
            // The same bulk rule Fix all keeps (BpaAnalyzer.BulkFixWithheld): a key on a table that already has one.
            if (BpaAnalyzer.BulkFixWithheld(obj, prop, line.After) is { } withheld) return withheld;
            // ANY property that carries a format goes through the fill check, whatever its name: FormatString,
            // FormatStringExpression (a measure's or a calculation item's), TargetFormatString (a KPI's). The check
            // proves a static FormatString only, so every other format write is a choice and is left out (verify V7:
            // a model's own rule with a dynamic-format fix replaced a person's format on the first press).
            if (IsFormatProperty(prop))
            {
                if (string.Equals(prop, "formatstring", StringComparison.OrdinalIgnoreCase))
                    return FormatFill.MayFill(obj, line.After) ? null : FormatLeftOut(obj, line.Before, line.After);
                return "This fix writes a format that changes with the filter or a KPI's own format. Semanticus cannot "
                    + "prove it, so it was left as it is. Model quality lists it if you want to make it yourself.";
            }
            return null;
        }

        /// <summary>True for a property that holds a format: its name has FormatString in it (FormatString,
        /// FormatStringExpression, TargetFormatString). A future format property with that name is caught too.</summary>
        private static bool IsFormatProperty(string prop) =>
            !string.IsNullOrEmpty(prop) && prop.IndexOf("formatstring", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The check a best-practice fix gets AT THE WRITE, on every Model Plan batch (Run the plan, apply_plan_items,
        /// approve), whoever worked the line out: the row itself, or the change plan (propose_plan and Re-analyse seed every
        /// fix the scan can make, and the row runs a proposal it already has as it stands; verify V9). It asks the row's own
        /// question twice: of the line as it was stored, and of what the rule's fix would write to the object NOW (the write
        /// is the rule's expression, not the stored text). Null means write it, or not a best-practice fix. It never makes a
        /// data type change: that changes shown values, so it is left to the person (coordinator ruling, 29 Sep).</summary>
        internal static string BpaFixRefusedAtWrite(Model m, ChangeItem line)
        {
            if (line == null || !string.Equals(OpAliases.CanonicalPlanKind((line.Kind ?? "").Trim()), "bpa_fix", StringComparison.OrdinalIgnoreCase))
                return null;
            ITabularNamedObject obj;
            try { obj = ObjectRefs.Resolve(m, line.ObjectRef) as ITabularNamedObject; } catch { obj = null; }
            if (obj == null) return null;                    // the write itself says the object is gone
            var stored = BpaLineRefusal(line, obj);
            if (stored != null) return stored;
            var rule = GetBpaRules(m).FirstOrDefault(r => string.Equals(r.ID, line.RuleId, StringComparison.OrdinalIgnoreCase));
            if (rule == null) return null;                   // the write itself says which rule is unknown
            var (prop, before, after) = BpaAnalyzer.PreviewFix(rule, obj);
            if (prop == null) return null;
            return BpaLineRefusal(new ChangeItem
            {
                ObjectRef = line.ObjectRef, ObjectName = line.ObjectName, Kind = "bpa_fix", RuleId = line.RuleId,
                Target = prop.ToLowerInvariant(), Before = before, After = after,
            }, obj);
        }

        /// <summary>Test seam: the refusal the best-practice row would give a fix line on this object (null = let through).</summary>
        internal Task<string> BpaLineRefusalForTest(ChangeItem line, string objRef) =>
            _sessions.Require().ReadAsync(m => BpaLineRefusal(line, ObjectRefs.Resolve(m, objRef)));

        /// <summary>Why a format fill was left out, in the words the row shows. The decision is FormatFill.MayFill's
        /// alone; this only says which part of the rule it met, and keeps the rule's advice in words.</summary>
        private static string FormatLeftOut(ITabularNamedObject obj, string before, string after)
        {
            if (obj is Measure dm && !string.IsNullOrWhiteSpace(dm.FormatStringExpression))
                return "Its format changes with the filter (a format string expression). A fixed format beside it is not "
                    + "allowed, so it was left as it is.";
            if (!string.IsNullOrWhiteSpace(before) && before != "(none)")
                return "It already has the format " + before + ". Nothing proves the rule's format is better, so it was left as it is.";
            var suggests = string.IsNullOrEmpty(after) ? "" : " The rule suggests " + after + ".";
            return obj is Measure
                ? "Semanticus cannot prove it holds whole numbers: a ratio or an amount of money written as a whole number "
                  + "would read wrong." + suggests + " Its format is a choice, so the words row hands it to your assistant."
                : "Its format is a choice Semanticus cannot prove." + suggests + " It was left for you to choose.";
        }

        /// <summary>The lines the best-practice row writes, and the fixes it leaves out, on the model as it is. The
        /// readiness safe fixes' own lines are passed in so a write both would make goes on that row only.</summary>
        private static (List<ChangeItem> Lines, List<ModelPlanChangeRefusal> Refused) BpaPlanLines(
            Model m, BpaScorecard card, IEnumerable<ChangeItem> readinessLines, ISet<string> shared = null)
        {
            var lines = new List<ChangeItem>();
            var refused = new List<ModelPlanChangeRefusal>();
            if (card == null) return (lines, refused);
            var taken = new HashSet<string>(
                (readinessLines ?? Enumerable.Empty<ChangeItem>()).Where(l => l != null)
                    .Select(l => (l.ObjectRef ?? "") + "|" + (l.Target ?? "").ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);
            var rules = GetBpaRules(m);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A format finding whose fix the format fill rule withholds (Advice, m11/feedback-rules) is still a fix the row
            // looks at and leaves out in words: it is the row's Refused list and the words queue that carry it to the person.
            foreach (var v in card.Violations.Where(v => (v.CanAutoFix || v.Advice) && !v.Waived))
            {
                var line = MapBpaFix(m, rules, v);
                if (line == null)
                {
                    // Advice with no fix of its own to preview is nothing the row left out: the words queue and Model quality carry it.
                    if (v.CanAutoFix)
                        refused.Add(new ModelPlanChangeRefusal { Target = v.ObjectRef, Reason = "This fix cannot be made to this object, so it was left out." });
                    continue;
                }
                var why = BpaLineRefusal(line, ObjectRefs.Resolve(m, v.ObjectRef));
                if (why != null) { refused.Add(new ModelPlanChangeRefusal { Target = v.ObjectRef, Reason = why }); continue; }
                var key = (line.ObjectRef ?? "") + "|" + (line.Target ?? "").ToLowerInvariant();
                if (taken.Contains(key)) { shared?.Add(key); continue; }          // the AI readiness row makes this same write; Model quality raises it too
                if (!seen.Add(key)) continue;               // two rules asking one property of one object: the first wins
                lines.Add(line);
            }
            return (lines, refused);
        }

        internal static string BpaKey(ChangeItem l) => (l?.ObjectRef ?? "") + "|" + (l?.Target ?? "").ToLowerInvariant();

        /// <summary>The readiness safe-fix lines on the model, the same ones the safefix rows propose.</summary>
        private static IEnumerable<ChangeItem> ReadinessSafeLines(Model m, Scorecard readiness) =>
            (readiness?.Findings ?? Array.Empty<ReadinessFinding>())
                .Where(x => !x.Waived && string.Equals(x.Fix, nameof(FixKind.SafeFix), StringComparison.Ordinal))
                .Select(x => MapSafeFix(m, x))
                .Where(x => x != null)
                .ToList();

        private static ModelPlanItem BpaFixItem(PlanFacts f)
        {
            var lines = f.BpaLines;
            if (lines == null || lines.Count == 0) return null;
            // One touch per object, naming every fix on it, so the table detail counts what Touches counts.
            var touches = lines.GroupBy(l => l.ObjectRef ?? "", StringComparer.Ordinal)
                .Select(g => Touch(g.Key, g.First().ObjectName, "~ ",
                    ": " + string.Join(", ", g.Select(l => Lower(l.Title ?? "a best-practice fix")).Distinct(StringComparer.Ordinal))))
                .ToArray();
            var why = new List<string>
            {
                CountOf(lines.Count, "best-practice issue", "best-practice issues") + " on Model quality "
                    + (lines.Count == 1 ? "has" : "have") + " one exact fix each, so Semanticus can make "
                    + (lines.Count == 1 ? "it" : "them") + " for you.",
                "Each fix is the rule's own. Undo takes them all back in one step.",
            };
            if (f.BpaLeftOut > 0)
                why.Add(CountOf(f.BpaLeftOut, "other fix was", "other fixes were") + " left out, because "
                    + (f.BpaLeftOut == 1 ? "it" : "they") + " would choose a format Semanticus cannot prove, rename or change a type. "
                    + "Model quality lists " + (f.BpaLeftOut == 1 ? "it" : "them") + ".");
            return new ModelPlanItem
            {
                Id = BpaFixItemId,
                Section = ModelPlanSections.Structure,
                Title = "Fix " + CountOf(lines.Count, "best-practice issue", "best-practice issues"),
                Sub = Join(lines.Select(l => l.ObjectName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase), 4),
                Value = "fewer best-practice issues",
                Rules = lines.Select(l => l.RuleId).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Goal = PlanGoals.Quality,
                Who = ModelPlanOwners.Auto,
                Status = ModelPlanStatuses.Ready,
                Why = why.ToArray(),
                Diff = Diff(touches),
                Touches = Objects(lines.Select(l => l.ObjectRef).Distinct(StringComparer.Ordinal).Count()),
                Tables = TableDetail(touches),
                Jump = new ModelPlanJump { Tool = "bpa", Label = "Model quality" },
                DerivedKeys = lines.Select(BpaKey).ToArray(),
            };
        }

        // ---- the words ----------------------------------------------------------------------------------------

        /// <summary>The findings the words row hands over: every word the assistant writes on the way to A
        /// (descriptions, business names, synonyms, the AI instructions and their glossary) and the measure formats
        /// Semanticus cannot prove (THE FORMAT FILL RULE: a choice, never a guess). Open ones only. A DAX rewrite is not
        /// words: it joins the formulas row (<see cref="IsProposalFinding"/>).</summary>
        internal static bool IsWordsFinding(ReadinessFinding x) =>
            x != null && !x.Waived && string.Equals(x.Fix, nameof(FixKind.AiContent), StringComparison.Ordinal)
            && WordsKindOf(x) != null;

        /// <summary>The words the row hands over on this model: none on a model with no tables, where the only open
        /// finding is the model-wide synonym store and the ways to start are the next step (verify, 28 Sep, H3).</summary>
        internal static IEnumerable<ReadinessFinding> WordsFindings(IEnumerable<ReadinessFinding> findings, int tables) =>
            tables > 0 ? (findings ?? Enumerable.Empty<ReadinessFinding>()).Where(IsWordsFinding) : Enumerable.Empty<ReadinessFinding>();

        private static readonly HashSet<string> InstructionRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DAC-AI-INSTRUCTIONS", "DAC-AI-INSTRUCTIONS-LEN", "DAC-GLOSSARY-GAP",
        };

        private static string WordsKindOf(ReadinessFinding x)
        {
            var cat = x.Category ?? "";
            var rule = x.RuleId ?? "";
            if (InstructionRules.Contains(rule)) return "instructions";
            if (string.Equals(rule, "FMT-MEASURE", StringComparison.OrdinalIgnoreCase)) return "format";
            if (cat.IndexOf("Description", StringComparison.OrdinalIgnoreCase) >= 0
                || rule.StartsWith("DESC-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rule, "DAC-CALC-GROUP", StringComparison.OrdinalIgnoreCase)) return "describe";
            if (string.Equals(cat, nameof(ReadinessCategory.Naming), StringComparison.OrdinalIgnoreCase)) return "name";
            if (string.Equals(cat, nameof(ReadinessCategory.Synonyms), StringComparison.OrdinalIgnoreCase)) return "synonym";
            return null;
        }

        private static string WordsToolOf(string kind, string ruleId) =>
            kind == "describe" ? "set_description" : kind == "name" ? "rename_object"
            : kind == "instructions" ? "set_ai_instructions" : kind == "format" ? "set_measure_format"
            : string.Equals(ruleId, "SYN-SCHEMA", StringComparison.OrdinalIgnoreCase) ? "create_linguistic_schema" : "set_synonyms";

        /// <summary>Test seam: the words row for these findings, best-practice formats and table count.</summary>
        internal static ModelPlanItem WordsItemForTest(IReadOnlyList<ReadinessFinding> findings, int tables, IReadOnlyList<WordsQueueItem> bpaFormats) =>
            WordsItem(findings, tables, bpaFormats);

        private static ModelPlanItem WordsItem(IReadOnlyList<ReadinessFinding> findings, int tables, IReadOnlyList<WordsQueueItem> bpaFormats = null)
        {
            var words = WordsFindings(findings, tables).ToArray();
            var formats = tables > 0 ? (bpaFormats ?? Array.Empty<WordsQueueItem>()) : Array.Empty<WordsQueueItem>();
            if (words.Length == 0 && formats.Count == 0) return null;
            int Of(string k) => words.Count(x => WordsKindOf(x) == k);
            // A format on the row is a measure's (readiness FMT-MEASURE, or a best-practice rule the row cannot prove) or a column's
            // (SEM_COLUMN_NEEDS_FORMAT_STRING on a key or a link, which no readiness rule asks about). Each object counts once.
            var measureFormats = words.Where(x => WordsKindOf(x) == "format").Select(x => x.ObjectRef ?? "")
                .Concat(formats.Where(x => x.Tool == "set_measure_format").Select(x => x.ObjectRef ?? ""))
                .Distinct(StringComparer.Ordinal).Count();
            var columnFormats = formats.Where(x => x.Tool == "set_property").Select(x => x.ObjectRef ?? "").Distinct(StringComparer.Ordinal).Count();
            var parts = new List<string>();
            if (Of("describe") > 0) parts.Add(CountOf(Of("describe"), "description", "descriptions"));
            if (Of("name") > 0) parts.Add(CountOf(Of("name"), "name", "names"));
            if (Of("synonym") > 0) parts.Add(CountOf(Of("synonym"), "synonym", "synonyms"));
            if (Of("instructions") > 0) parts.Add("the AI instructions");
            if (measureFormats > 0) parts.Add("a format for " + CountOf(measureFormats, "measure", "measures"));
            if (columnFormats > 0) parts.Add("a format for " + CountOf(columnFormats, "column", "columns"));
            // One touch per object, naming every kind of word it needs, so the table detail counts what Touches counts.
            string Word(ReadinessFinding x) => WordsKindOf(x) switch
            {
                "describe" => "a description", "name" => "a business name", "instructions" => "the AI instructions",
                "format" => "a format to choose", _ => "synonyms",
            };
            var touches = words.GroupBy(x => x.ObjectRef ?? "", StringComparer.Ordinal)
                .Select(g => (Ref: g.Key, Name: g.First().ObjectName, Words: g.Select(Word)))
                .Concat(formats.GroupBy(x => x.ObjectRef ?? "", StringComparer.Ordinal)
                    .Select(g => (Ref: g.Key, Name: g.First().ObjectName, Words: g.Select(_ => "a format to choose"))))
                .GroupBy(t => t.Ref, StringComparer.Ordinal)
                .Select(g => Touch(g.Key, g.First().Name, "+ ", ": " + string.Join(", ", g.SelectMany(t => t.Words).Distinct(StringComparer.Ordinal))))
                .ToArray();
            return new ModelPlanItem
            {
                Id = WordsItemId,
                Section = ModelPlanSections.Meaning,
                // ONE words row (round 1, 8 Oct 2026): the descriptions that used to have a row of their own are in it. The title says
                // what the words are ("Write 3 descriptions and 1 synonym"); the sub line names the objects.
                Title = "Write " + AndList(parts.Take(3).ToList()) + (parts.Count > 3 ? " and more" : ""),
                Sub = Join(words.Select(x => x.ObjectName).Concat(formats.Select(x => x.ObjectName))
                               .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase), 4),
                Value = "assistants stop guessing",
                // The readiness words are AI readiness; a row of only best-practice formats is quality.
                Rules = RuleIdsOf(words).Concat(formats.Select(x => x.RuleId).Where(r => !string.IsNullOrWhiteSpace(r)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Goal = words.Length > 0 ? PlanGoals.Ai : PlanGoals.Quality,
                // The formats are Model quality's own findings, so a row of words and formats is under both filters.
                Goals = words.Length > 0 && formats.Count > 0 ? new[] { PlanGoals.Ai, PlanGoals.Quality } : null,
                Who = ModelPlanOwners.Assistant,
                Status = ModelPlanStatuses.Ready,
                Why = new[]
                {
                    WordsLine,
                    "Words are the one thing Semanticus never writes itself. Your assistant reads the whole list from one "
                    + "call, a page at a time.",
                },
                Diff = Diff(touches),
                Touches = Objects(words.Select(x => x.ObjectRef).Concat(formats.Select(x => x.ObjectRef)).Distinct(StringComparer.Ordinal).Count()),
                Tables = TableDetail(touches),
                Jump = new ModelPlanJump { Tool = "readiness", Label = "AI readiness" },
            };
        }

        /// <summary>The whole words queue from one call, most grade first. Read only; free on every tier, like the
        /// scan it reads. <paramref name="max"/> is the page size (0 = everything from <paramref name="offset"/> on) and
        /// never changes <see cref="WordsQueue.Total"/>; the MCP door always pages it (McpTools.GetWordsQueue).</summary>
        public Task<WordsQueue> GetWordsQueueAsync(int max = 0, int offset = 0) => ReadWordsQueueAsync(_sessions.Require(), max, offset);   // the reading is Plan/WordsQueueCore.cs

        /// <summary>The best-practice rule that asks every visible number or date column for a format (Rules/BPARules-PowerBI.json).
        /// It has no fix of its own: a key's or a date's format is a choice.</summary>
        internal const string ColumnFormatRuleId = "SEM_COLUMN_NEEDS_FORMAT_STRING";

        /// <summary>The columns AI readiness already asks a format of (FMT-COLUMN, open): the formats row or the Jev column-format
        /// row carries those, so the words queue does not ask for them a second time.</summary>
        internal static HashSet<string> ColumnsOnFormatRow(IEnumerable<ReadinessFinding> findings) =>
            new HashSet<string>((findings ?? Enumerable.Empty<ReadinessFinding>())
                .Where(x => x != null && !x.IsAccepted() && string.Equals(x.RuleId, "FMT-COLUMN", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.ObjectRef ?? ""), StringComparer.OrdinalIgnoreCase);

        /// <summary>The best-practice format fixes the row will not make on its own (it cannot prove them): a measure the assistant
        /// sets with set_measure_format, and (plan statement check 2, item 3) a visible number or date column no readiness rule
        /// asks a format of, such as a key or a relationship end, which it sets with set_property FormatString. One item each, no
        /// lift, and never a second one for an object the readiness rules already ask a format for.</summary>
        private static List<WordsQueueItem> BpaFormatItems(Model m, IEnumerable<WordsQueueItem> words, ISet<string> columnsOnFormatRow)
        {
            BpaScorecard card;
            List<BpaRule> rules;
            try { rules = GetBpaRules(m); card = BpaAnalyzer.Analyze(m, rules); }
            catch { return new List<WordsQueueItem>(); }     // a rule set that cannot run leaves the words as they were
            return BpaFormatItems(m, card, rules, words, columnsOnFormatRow);
        }

        private static List<WordsQueueItem> BpaFormatItems(Model m, BpaScorecard card, List<BpaRule> rules,
            IEnumerable<WordsQueueItem> words, ISet<string> columnsOnFormatRow)
        {
            var found = new List<WordsQueueItem>();
            var have = new HashSet<string>(words.Where(w => w.Tool == "set_measure_format").Select(w => w.ObjectRef ?? ""), StringComparer.Ordinal);
            foreach (var v in card.Violations.Where(v => (v.CanAutoFix || v.Advice) && !v.Waived).OrderBy(v => v.ObjectRef, StringComparer.Ordinal).ThenBy(v => v.RuleId, StringComparer.Ordinal))
            {
                try
                {
                    if (have.Contains(v.ObjectRef ?? "")) continue;
                    var line = MapBpaFix(m, rules, v);
                    if (line == null || !string.Equals(line.Target, "formatstring", StringComparison.OrdinalIgnoreCase)) continue;
                    var obj = ObjectRefs.Resolve(m, v.ObjectRef);
                    if (!(obj is Measure) || BpaLineRefusal(line, obj) == null) continue;   // the row makes the ones it proves
                    have.Add(v.ObjectRef ?? "");
                    var now = string.IsNullOrWhiteSpace(line.Before) || line.Before == "(none)" ? "" : ", and it now has " + line.Before;
                    found.Add(new WordsQueueItem
                    {
                        Kind = "format", Tool = "set_measure_format", RuleId = v.RuleId, ObjectRef = v.ObjectRef, ObjectName = v.ObjectName,
                        Message = "The best-practice rule " + line.Title + " (" + v.RuleId + ") asks for the format " + line.After + now
                                  + ". Set it if that suits what the measure holds. Semanticus cannot tell whether it does.",
                    });
                }
                catch { /* one violation that cannot be read is left out, not the queue */ }
            }
            // A column the readiness rules do not ask about (a key, a relationship end): it has no fix of its own, so the
            // format is the assistant's choice, and it must be on the words row or no Plan row ever asks for it.
            // A column whose format the person declined (an accepted FMT-COLUMN, or any acceptance that keeps its format) is not
            // the assistant's to choose: the best-practice twin stays open beside that acceptance, but no one is asked to write.
            var declinedFormats = DeclinedChanges.Read(m, rules);
            foreach (var v in card.Violations.Where(v => string.Equals(v.RuleId, ColumnFormatRuleId, StringComparison.OrdinalIgnoreCase) && !v.IsAccepted())
                         .OrderBy(v => v.ObjectRef, StringComparer.Ordinal))
            {
                try
                {
                    if (columnsOnFormatRow != null && columnsOnFormatRow.Contains(v.ObjectRef ?? "")) continue;
                    if (declinedFormats.Declines(v.ObjectRef, "formatString")) continue;
                    if (!(ObjectRefs.Resolve(m, v.ObjectRef) is Column col) || !string.IsNullOrWhiteSpace(col.FormatString)) continue;
                    var kind = col.DataType == DataType.DateTime ? "date"
                        : col.DataType == DataType.Int64 ? "whole-number" : "decimal";
                    found.Add(new WordsQueueItem
                    {
                        Kind = "format", Tool = "set_property", RuleId = v.RuleId, ObjectRef = v.ObjectRef, ObjectName = v.ObjectName,
                        Message = "The best-practice rule 'Give visible numeric and date columns a format string' (" + v.RuleId + ") asks for a format "
                                  + "on this " + kind + " column" + (col.IsKey || col.UsedInRelationships.Any() ? ", a key or a link between tables" : "")
                                  + ". Semanticus cannot tell what it holds, so the format is yours to choose: set_property with FormatString "
                                  + "(list_format_templates has ready-made date and number formats). A key or a year usually reads best with no thousands separator.",
                    });
                }
                catch { /* one violation that cannot be read is left out, not the queue */ }
            }
            return found;
        }

        // ---- an empty model -------------------------------------------------------------------------------------

        internal static ModelPlanStart[] StartsFor(int tables) => tables > 0 ? Array.Empty<ModelPlanStart>() : new[]
        {
            // The Model Spec card says the same first two sentences (spec.tsx; copy-rules.test.mjs holds them equal).
            new ModelPlanStart
            {
                Id = "sql", Label = "From a SQL source",
                Line = "Read the tables, columns and keys from a Fabric SQL endpoint with a Microsoft sign-in. No data rows are copied and no password is kept. Check the draft, then build it in one step.",
                Jump = new ModelPlanJump { Tool = "spec", Label = "Model Spec" },
                Op = "autogenerate_spec_from_fabric",
            },
            new ModelPlanStart
            {
                Id = "blank", Label = "New blank model",
                Line = "Start with nothing and add tables yourself.",
                Jump = new ModelPlanJump { Tool = "connections", Label = "Connections" },
                Op = "create_model",
            },
        };
    }
}

// The words queue as a function of a MODEL, not of the open session. It used to live inside GetWordsQueueAsync
// (LocalEngine.PlanBestPractice.cs), which can only read the model the person has open. The start plan reads the same
// queue over its hidden preview model (StartPlan/StartPlanWords.cs), so the one reading is lifted out here and both
// callers use it: the open-model queue pages it, the start plan filters it and pages it its own way.
//
// Nothing about the reading changed in the move: the same findings, the same order (holds the grade, then lift, then
// ref), the same best-practice format items last, and the same page limits.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>The whole open words list for a model, in the order the assistant should take it: what holds the grade
        /// under a cap first, then by lift, then by ref; the best-practice formats the row cannot prove come last of all.
        /// Read only. <paramref name="card"/> is the scan it was worked out from.</summary>
        internal static List<WordsQueueItem> ReadWordsQueueItems(Model m, out Scorecard card)
        {
            var analyzer = new ReadinessAnalyzer();
            card = analyzer.Analyze(m);
            var lifts = analyzer.Lifts(m, out var gate);
            var items = WordsFindings(card.Findings, m.Tables.Count)
                .Select(x =>
                {
                    var key = ReadinessAnalyzer.LiftKey(x.RuleId, x.ObjectRef);
                    var kind = WordsKindOf(x);
                    return new WordsQueueItem
                    {
                        Kind = kind, Tool = WordsToolOf(kind, x.RuleId), RuleId = x.RuleId,
                        ObjectRef = x.ObjectRef, ObjectName = x.ObjectName, Message = x.DisplayMessage ?? x.Message,
                        Lift = lifts.TryGetValue(key, out var l) ? l : 0, HoldsGate = gate.Contains(key),
                    };
                })
                .OrderByDescending(x => x.HoldsGate)
                .ThenByDescending(x => x.Lift)
                .ThenBy(x => x.ObjectRef, StringComparer.Ordinal)
                .ToList();
            // The best-practice format findings that remain come last, after every word: a rule such as INTEGER_FORMATTING
            // wants an exact pattern, and the row never writes a format it cannot prove, so without them here the assistant
            // is never told they are left (verify round 3, L-g).
            items.AddRange(BpaFormatItems(m, items, ColumnsOnFormatRow(card.Findings)));
            return items;
        }

        /// <summary>The open model's queue, one page: the revision is taken before the read, as the queue always did.</summary>
        internal static async Task<WordsQueue> ReadWordsQueueAsync(Session s, int max, int offset, bool isPro = false)
        {
            var revision = s.Revision;
            return await s.ReadAsync(m => PageWordsQueue(m, revision, max, offset, isPro)).ConfigureAwait(false);
        }

        /// <summary>One page of the words queue of a model: <paramref name="max"/> is the page size (0 = everything from
        /// <paramref name="offset"/> on) and never changes <see cref="WordsQueue.Total"/>.</summary>
        internal static WordsQueue PageWordsQueue(Model m, long revision, int max, int offset, bool isPro = false)
        {
            var items = ReadWordsQueueItems(m, out var card);
            // Project the tool at the open-model door, before measuring the page. The start plan still classifies single tools.
            if (isPro) foreach (var item in items) item.Tool = item.Tool switch
            {
                "set_description" or "set_synonyms" => "write_words",
                "rename_object" or "set_measure_format" or "set_property" => "edit_objects",
                _ => item.Tool,
            };
            var total = items.Count;
            var from = Math.Max(0, Math.Min(offset, total));
            var kinds = items.GroupBy(x => x.Kind, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            // One grounding per object on the page: an object that needs a description, a name and synonyms is
            // read once, and its three items share it. A page ends at max items or when the WHOLE page (its own fields,
            // its items and their grounding, as the JSON goes on the wire) would pass WordsPageChars, whichever comes
            // first (never before its first item); max 0 is everything from offset on.
            var grounded = new Dictionary<string, GroundingBundle>(StringComparer.Ordinal);
            var shown = new List<WordsQueueItem>();
            WordsQueue Page()
            {
                var next = from + shown.Count;
                return new WordsQueue
                {
                    Revision = revision, Grade = card.Grade, Score = card.Overall, GatedBy = card.GatedBy,
                    Line = WordsLine, Total = total, Offset = from, Next = next < total ? next : (int?)null,
                    Kinds = kinds, Grounding = grounded, Items = shown.ToArray(),
                };
            }
            var used = max > 0 ? JsonChars(new WordsQueue
                {
                    Revision = revision, Grade = card.Grade, Score = card.Overall, GatedBy = card.GatedBy, Line = WordsLine,
                    Total = total, Offset = from, Next = total, Kinds = kinds,
                }) : 0;
            for (var at = from; at < total && (max <= 0 || shown.Count < max); at++)
            {
                var x = CutForPage(items[at]);
                var key = x.ObjectRef ?? "";
                var fresh = !grounded.TryGetValue(key, out var g);
                if (fresh) g = CutForPage(BuildGrounding(m, x.ObjectRef));
                if (max > 0)
                {
                    var cost = JsonChars(x) + 1 + (fresh ? JsonChars(key) + 2 + JsonChars(g) : 0);
                    if (shown.Count > 0 && used + cost > WordsPageChars) break;
                    used += cost;
                }
                if (fresh) grounded[key] = g;
                shown.Add(x);
            }
            var page = Page();
            // The sum above is an estimate of the whole; the page itself has the last word. Drop the last item until it fits.
            while (max > 0 && shown.Count > 1 && JsonChars(page) > WordsPageChars)
            {
                var last = shown[shown.Count - 1];
                shown.RemoveAt(shown.Count - 1);
                if (!shown.Any(i => string.Equals(i.ObjectRef ?? "", last.ObjectRef ?? "", StringComparison.Ordinal)))
                    grounded.Remove(last.ObjectRef ?? "");
                page = Page();
            }
            return page;
        }
    }
}

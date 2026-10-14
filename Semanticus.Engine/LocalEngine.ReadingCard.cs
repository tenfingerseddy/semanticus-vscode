using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;

// The reading card's two engine calls (Kane's pick A, 12 Oct 2026). Both doors use them: the Plan's card (readingCard, fixThem on
// EngineRpcTarget) and the assistant's reading_card and fix_them tools. The pure half, ReadingCardBuilder, is in ReadingCard.cs.
namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>The open model's grade and its top three issues. The first read is the grade: it goes ahead of the open-time warm-up (Session.WarmObservers),
        /// so it lands in about 0.2 s on a 100-table model. <paramref name="withAfter"/> adds the grade the safe fixes reach, from the Plan's own rehearsal:
        /// it costs more, so the Studio asks for the card first and the grade after second.</summary>
        public async Task<ReadingCardView> ReadingCardAsync(bool withAfter = true)
        {
            var s = _sessions.Require();
            var scan = await AiReadinessScanAsync().ConfigureAwait(false);
            // How much each finding lifts the grade ranks the top three (the words queue orders by the same numbers). A second, cheap pass over the rules.
            IReadOnlyDictionary<string, double> lifts = null;
            try { lifts = await s.ReadAsync(m => new ReadinessAnalyzer().Lifts(m, out _)).ConfigureAwait(false); }
            catch { /* the card ranks by severity and count without it */ }
            var view = ReadingCardBuilder.Build(scan, QueuedWritingKeys(), s.Revision, lifts);
            if (withAfter)
            {
                var after = await ProjectedScorecardAsync(s, scan).ConfigureAwait(false);
                if (after?.Grade != null)
                {
                    view.GradeAfter = after.Grade;
                    view.ScoreAfter = (int)Math.Round(after.Overall, MidpointRounding.AwayFromZero);
                }
            }
            return view;
        }

        /// <summary>The send keys of the writing jobs that are open on the plan now.</summary>
        private HashSet<string> QueuedWritingKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var t in ReadPlanState(BindPlanMemory()).Tasks)
                {
                    var key = t.OriginLink?.SendKey;
                    if (PlanTasks.IsOpenTask(t) && key != null && key.StartsWith("reading|", StringComparison.Ordinal)) keys.Add(key);
                }
            }
            catch { /* a plan that cannot be read shows no queued job: the card then offers the jobs again, and the plan keeps one task per key */ }
            return keys;
        }

        /// <summary>Fix them: apply every safe fix as ONE undo step, then give the writing the assistant must do to the plan as jobs. One call, both halves.
        /// The grade comes from the real model after the fixes, never an estimate.</summary>
        public async Task<FixThemResult> FixThemAsync(string origin)
        {
            var s = _sessions.Require();
            var before = await AiReadinessScanAsync().ConfigureAwait(false);
            var fixes = await ApplySafeFixesAsync(origin).ConfigureAwait(false);
            var after = fixes.Scorecard;

            // The writing left, in the jobs a person would name: descriptions, other names, anything else that needs words.
            var queued = QueuedWritingKeys();
            var jobs = new List<ReadingJob>();
            foreach (var g in after.Findings.Where(f => (!f.Waived || f.WaivedAsTwin) && ReadingCardBuilder.WritingKeyOf(f) != null)
                         .GroupBy(ReadingCardBuilder.WritingKeyOf, StringComparer.Ordinal).OrderBy(g => g.Key == "descriptions" ? 0 : g.Key == "other-names" ? 1 : 2))
            {
                var sendKey = ReadingCardBuilder.WritingSendKey(g.Key);
                if (queued.Contains(sendKey)) continue;
                var list = g.ToArray();
                var words = g.Key == "descriptions" ? "Write the missing descriptions (" + ReadingCardBuilder.Count(list.Length) + ")"
                    : g.Key == "other-names" ? "Add other names to " + ReadingCardBuilder.Count(list.Length) + (list.Length == 1 ? " field" : " fields")
                    : "Fix the other " + ReadingCardBuilder.Count(list.Length) + (list.Length == 1 ? " finding that needs words" : " findings that need words");
                var result = await AddPlanTaskAsync(new PlanTaskRequest
                {
                    Words = words,
                    Brief = WritingBrief(list),
                    SourcePage = "readiness",
                    DoneWhen = PlanDoneWhen.AssistantSays,
                    OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Page, Ref = "readiness" },
                    Objects = Array.Empty<string>(),
                    SendKey = sendKey,
                }, compose: false).ConfigureAwait(false);
                jobs.Add(new ReadingJob { RowId = result.ItemId, Words = words, Count = list.Length, Key = g.Key });
            }

            // One push to the page for all the tasks, and no plan worked out for each: that took seconds apiece on a large model.
            if (jobs.Count > 0) TellThePlanMoved(BindPlanMemory());

            var fixedCount = fixes.Applied.Length;
            var gradeBefore = before.Grade; var gradeAfter = after.Grade;
            var scoreBefore = (int)Math.Round(before.Overall, MidpointRounding.AwayFromZero);
            var scoreAfter = (int)Math.Round(after.Overall, MidpointRounding.AwayFromZero);
            var sentence = (fixedCount == 0 ? "Semanticus had nothing safe to fix." : "Semanticus fixed " + ReadingCardBuilder.Count(fixedCount) + (fixedCount == 1 ? " thing." : " things."))
                + (gradeBefore != gradeAfter ? " Grade " + gradeBefore + " to " + gradeAfter + "." : "")
                + (jobs.Count == 0 ? "" : jobs.Count == 1 ? " Your assistant has a writing task." : " Your assistant has " + (jobs.Count == 2 ? "two" : jobs.Count.ToString()) + " writing tasks.");
            return new FixThemResult
            {
                Revision = fixes.Revision, Fixed = fixedCount, GradeBefore = gradeBefore, ScoreBefore = scoreBefore, GradeAfter = gradeAfter, ScoreAfter = scoreAfter,
                Jobs = jobs.ToArray(), Sentence = sentence,
            };
        }

        // The brief the assistant reads: each finding as rule, object and a stable key, so it asks the engine for that finding's own fix prompt instead of this
        // call copying hundreds of them (the same shape the Review page's "Give the rest to your assistant" sends).
        private static string WritingBrief(IReadOnlyList<ReadinessFinding> list)
        {
            var brief = $"Fix these {list.Count} AI readiness findings that Semanticus cannot fix by itself. For each line, ask Semanticus for the fix prompt for that rule and object, then make the change. Skip a finding you disagree with and say why. Do not ask the person first: anything Undo can take back is done, then reported.";
            var listed = 0;
            foreach (var f in list)
            {
                var line = $"\n- {f.RuleTitle}: {f.ObjectName} ({f.RuleId}|{f.ObjectRef})";
                if ((brief + line).Length > 3800) break;
                brief += line; listed++;
            }
            if (listed < list.Count) brief += $"\n- and {list.Count - listed} more: check AI readiness again for the rest.";
            return brief;
        }
    }
}

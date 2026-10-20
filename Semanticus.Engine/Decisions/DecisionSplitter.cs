// A request the relay refuses as too big, asked again in smaller ones (m16/jev-batch, 1.5.1).
//
// The relay used to take 40 questions of cleanup.suggest, 24 of query.summarize and 64 of compose.draft; the client now sends up to 120, 64 and 100. A relay
// rolled back to the old release refuses the bigger request as too big (413, free, nothing forwarded). The same questions then go again over the SAME
// state, in requests of at most what that relay took, and again in halves while one is still refused, down to one question. The answers are merged
// back into one reply, so the caller reads it as if it had come whole. A question that could not be asked is listed with the refusal's words, never dropped.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;

namespace Semanticus.Engine.Decisions
{
    internal sealed class DecisionSplitter
    {
        // Points whose relay was seen to refuse the whole request: the next ask starts at the old bound and spends no refused call.
        private readonly ConcurrentDictionary<string, int> _bound = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        internal int BoundForTest(string pointName) => _bound.TryGetValue(pointName, out var b) ? b : int.MaxValue;

        private static bool TooBig(DecisionOutcome o) =>
            o != null && o.Asked && !o.Answers.Decided && JevDecisionOracle.IsTooBigRefusal(o.Answers.NotDecidedReason);

        public async Task<DecisionOutcome> AskAsync(DecisionService decisions, string pointName, IReadOnlyDictionary<string, object> snapshot,
            IReadOnlyList<DecisionQuestion> questions, bool isPro, bool enabled, DecisionSessionFence fence, CancellationToken ct, bool perQuestion = false)
        {
            Task<DecisionOutcome> One(IReadOnlyList<DecisionQuestion> qs) =>
                decisions.AskAsync(pointName, snapshot, qs, isPro, enabled, fence, ct, perQuestion: perQuestion);

            async Task<DecisionOutcome> Split(IReadOnlyList<DecisionQuestion> qs, int bound)
            {
                var chunks = new List<IReadOnlyList<DecisionQuestion>>();
                for (var at = 0; at < qs.Count; at += bound) chunks.Add(qs.Skip(at).Take(bound).ToList());
                var outcomes = await Task.WhenAll(chunks.Select(async c =>
                {
                    var o = await One(c).ConfigureAwait(false);
                    return TooBig(o) && c.Count > 1 ? await Split(c, Math.Max(1, c.Count / 2)).ConfigureAwait(false) : o;
                })).ConfigureAwait(false);
                // Not asked (switched off, no service, no permission): the same for every piece, say it once.
                var skipped = outcomes.FirstOrDefault(o => o != null && !o.Asked);
                if (skipped != null || outcomes.Any(o => o == null)) return skipped ?? DecisionOutcome.Skipped("not asked");
                var whole = new DecisionRequest(pointName, DecisionRegistry.Require(pointName).SchemaVersion, snapshot, qs, perQuestion);
                return DecisionOutcome.Answered(DecisionAnswers.Merge(whole, qs,
                    outcomes.Select((o, i) => (chunks[i], o.Answers)).ToList(), perQuestion));
            }

            var known = _bound.TryGetValue(pointName, out var b) ? b : int.MaxValue;
            if (questions.Count > known) return await Split(questions, known).ConfigureAwait(false);
            var first = await One(questions).ConfigureAwait(false);
            if (!TooBig(first) || questions.Count <= 1) return first;
            var legacy = Math.Max(1, Math.Min(JevTableBatch.LegacyQuestionsFor(pointName), questions.Count - 1));
            _bound[pointName] = legacy;
            return await Split(questions, legacy).ConfigureAwait(false);
        }
    }
}

// Group the misses of a finished Copilot test round (Kane, 15 Oct 2026). The rules always have an answer; on Pro, with the point switched on and live, Jev's
// answer to the same question replaces the rule's for the misses it is sure of. One call per round, never one per miss (memory jev-costs-nothing).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>1.5.1: Jev's grouping of a round's misses. OFF until its replay clears the bar (Kane, 15 Oct: ship it off, like the other new points).</summary>
        internal const bool CopilotJevDecides = false;
        internal bool? CopilotJevDecidesForTest;
        private bool CopilotJevLive => CopilotJevDecidesForTest ?? CopilotJevDecides;

        /// <summary>A test's own wait for Jev, in place of <see cref="CopilotMissPoint.Wait"/>. Null on every real path.</summary>
        internal TimeSpan? CopilotJevWaitForTest;

        /// <summary>What the last grouping said about Jev, for the round's note and for tests: "asked", "not asked: <why>".</summary>
        internal string CopilotJevNote;

        private async Task GroupCopilotMissesAsync(Session s, CopilotRoundRecord round, List<InterviewQuestion> bank, string file)
        {
            var facts = round.Answers.Where(a => !a.Held && CopilotMiss.Misses.Contains(a.Miss) && a.N >= 1 && a.N <= bank.Count)
                .Select(a => CopilotCauseRules.Facts(a.N, bank[a.N - 1], a.StudentDax, a.Miss)).OrderBy(f => f.N).ToList();
            var picks = new Dictionary<int, (string Cause, string Fix, bool Jev)>();
            foreach (var f in facts) { var (c, x) = CopilotCauseRules.Rule(f); picks[f.N] = (c, x, false); }

            var asked = 0;
            if (facts.Count > 0) asked = await AskCopilotJevAsync(s, facts, picks).ConfigureAwait(false);

            var groups = CopilotCauseRules.Group(facts.Select(f => (f, picks[f.N].Cause, picks[f.N].Fix, picks[f.N].Jev)));
            var by = groups.Any(g => g.By == "jev") ? "jev" : "rules";
            CopilotRoundStore.SetGroups(file, round.Id, by, groups.Select(g => new CopilotGroupRecord
            {
                Cause = g.Cause, Fix = g.Fix, Label = g.Label, By = g.By, Numbers = g.Numbers.OrderBy(n => n).ToArray(), Objects = g.Objects.ToArray(),
            }), DateTime.UtcNow.ToString("o"), out _);
        }

        /// <summary>Pro only. Returns how many of the rule's answers Jev replaced. Anything that goes wrong leaves the rules' answers.</summary>
        private async Task<int> AskCopilotJevAsync(Session s, List<CopilotMissFacts> facts, Dictionary<int, (string Cause, string Fix, bool Jev)> picks)
        {
            try
            {
                if (!Entitlement.FeatureGrants.IsProTier(_entitlement)) { CopilotJevNote = "not asked: Free"; return 0; }
                if (!CopilotJevLive) { CopilotJevNote = "not asked: this build keeps the point off"; return 0; }
                var point = DecisionRegistry.Require(DecisionRegistry.CopilotMisses);
                if (!DecisionPointEnabled(point.Name)) { CopilotJevNote = "not asked: switched off"; return 0; }
                var decisions = Decisions;
                if (!decisions.Oracle.Available) { CopilotJevNote = "not asked: no service"; return 0; }
                var fence = new DecisionSessionFence(s.Id, s.Revision, () =>
                {
                    var current = _sessions.Current;
                    return (current == null ? null : current.Id, current == null ? -1L : current.Revision);
                });
                var wait = CopilotJevWaitForTest ?? CopilotMissPoint.Wait;
                using var cts = new CancellationTokenSource(wait);
                var asks = CopilotMissPoint.Requests(facts)
                    .Select(async r =>
                    {
                        try { return await Splitter.AskAsync(decisions, point.Name, r.Snapshot, r.Questions, true, true, fence, cts.Token).ConfigureAwait(false); }
                        catch { return null; }
                    }).ToArray();
                var all = Task.WhenAll(asks);
                await Task.WhenAny(all, Task.Delay(wait + TimeSpan.FromMilliseconds(250))).ConfigureAwait(false);
                var used = 0;
                foreach (var ask in asks.Where(t => t.IsCompletedSuccessfully))
                {
                    var outcome = ask.Result;
                    if (outcome == null || !outcome.Asked) continue;
                    used += CopilotMissPoint.Apply(picks, outcome.Answers, point.ConfidenceBar);
                }
                CopilotJevNote = "asked: Jev settled " + used + " of " + facts.Count;
                return used;
            }
            catch (Exception ex) { CopilotJevNote = "failed: " + ex.Message; return 0; }
        }
    }
}

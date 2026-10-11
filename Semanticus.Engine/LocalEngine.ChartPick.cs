// pick_chart_kind on the engine: Jev's chart pick for a Queries result, the queries.chart_kind decision point.
//
// Both doors reach this one method: the MCP op pick_chart_kind and the Studio's pickChartKind RPC.
// Pro only (Ask your data, FeatureMap), read-only, and it never throws for an ordinary reason Jev did not pick: the
// point is off (the build's bar, JevChartPick.JevPicks, or the model's switch), no model, no key, no permission, a
// low confidence, a refused or unreadable answer, or nothing to choose between. Each of those returns the rules' pick,
// by "rules", with a plain note. Jev writes no text: the reason is the engine's fixed sentence for the kind.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Query;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>A test's own answer to "has the queries.chart_kind replay cleared its bar". Null on every real path.</summary>
        internal bool? JevPicksChartForTest;

        private bool JevPicksChart => JevPicksChartForTest ?? JevChartPick.JevPicks;

        public async Task<ChartPick> PickChartKindAsync(ChartPickAsk ask)
        {
            RequireProFeature();
            ask ??= new ChartPickAsk();
            var rulesPick = ask.RulesPick;
            if (string.IsNullOrEmpty(rulesPick) || !JevChartPick.Kinds.Contains(rulesPick))
                throw new ArgumentException($"rulesPick must be one of {string.Join(", ", JevChartPick.Kinds)}: the chart the rules picked.");
            var fits = JevChartPick.CleanFits(ask.Fits);

            if (fits.Count == 0) return JevChartPick.Rules(rulesPick, JevChartPick.NoFitsNote);
            if (!fits.Contains(rulesPick, StringComparer.Ordinal))
                throw new ArgumentException("rulesPick must be one of fits: the rules always pick a chart they left.");
            if (!JevPicksChart) return JevChartPick.Rules(rulesPick, JevChartPick.OffNote);
            if (!DecisionPointEnabled(DecisionRegistry.QueriesChartKind)) return JevChartPick.Rules(rulesPick, JevChartPick.SwitchedOffNote);
            if (fits.Count == 1) return JevChartPick.Rules(rulesPick, JevChartPick.OneFitNote);

            var s = _sessions.Current;
            if (s == null) return JevChartPick.Rules(rulesPick, JevChartPick.NoModelNote);

            var point = DecisionRegistry.Require(DecisionRegistry.QueriesChartKind);
            System.Collections.Generic.IReadOnlyDictionary<string, object> snapshot;
            System.Collections.Generic.IReadOnlyList<DecisionQuestion> questions;
            try
            {
                snapshot = JevChartPick.Snapshot(ask.Words, ask.Shape);
                questions = JevChartPick.Questions(snapshot, fits);
            }
            catch (Exception ex) { return JevChartPick.Rules(rulesPick, "Jev was not asked: " + ex.Message); }

            var fence = new DecisionSessionFence(s.Id, s.Revision, () =>
            {
                var current = _sessions.Current;
                return (current == null ? null : current.Id, current == null ? -1L : current.Revision);
            });
            DecisionOutcome asked;
            try
            {
                asked = await Decisions.AskAsync(point.Name, snapshot, questions,
                    isPro: Entitlement.FeatureGrants.IsProTier(_entitlement),
                    enabled: true,
                    fence: fence,
                    ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { return JevChartPick.Rules(rulesPick, "Jev was not asked: " + ex.Message + "."); }
            if (!asked.Asked) return JevChartPick.Rules(rulesPick, "Jev was not asked: " + asked.SkippedReason + ".");
            if (!asked.Answers.Decided) return JevChartPick.Rules(rulesPick, NotAnsweredNote(asked.Answers.NotDecidedReason));
            return JevChartPick.Read(asked.Answers, point.ConfidenceBar, fits, rulesPick);
        }
    }
}

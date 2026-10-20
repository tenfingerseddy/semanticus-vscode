// pick_check_visuals on the engine: Jev's visual pick for every check on the Validation page, the checks.visual_kind decision point.
//
// Both doors reach this one method: the MCP op pick_check_visuals and the Studio's pickCheckVisuals RPC. Pro only (Ask your data,
// FeatureMap), read-only, and it never throws for an ordinary reason Jev did not pick: the point is off (the build's bar,
// JevCheckVisuals.JevPicks, or the model's switch), no model, no permission, a low confidence, a refused or unreadable answer, or
// nothing to choose between. Each of those returns the rules' pick for every check, by "rules", with a plain note. All the checks
// are asked in ONE call. Jev writes no text: the reason is the engine's fixed sentence for the visual.

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
        /// <summary>A test's own answer to "has the checks.visual_kind replay cleared its bar". Null on every real path.</summary>
        internal bool? JevPicksCheckVisualsForTest;

        private bool JevPicksCheckVisuals => JevPicksCheckVisualsForTest ?? JevCheckVisuals.JevPicks;

        public async Task<CheckVisualPicks> PickCheckVisualsAsync(CheckVisualAsk ask)
        {
            RequireProFeature();
            var checks = JevCheckVisuals.Clean(ask);
            if (checks.Count == 0) return new CheckVisualPicks();
            foreach (var c in checks)
                if (!string.IsNullOrEmpty(c.RulesPick) && !JevCheckVisuals.Kinds.Contains(c.RulesPick))
                    throw new ArgumentException($"rulesPick of '{c.Id}' must be one of {string.Join(", ", JevCheckVisuals.Kinds)}: the visual the rules picked.");

            if (!JevPicksCheckVisuals) return JevCheckVisuals.AllRules(checks, JevCheckVisuals.OffNote);
            if (!DecisionPointEnabled(DecisionRegistry.CheckVisualKind)) return JevCheckVisuals.AllRules(checks, JevCheckVisuals.SwitchedOffNote);

            // A check with a visual to choose between has two fits at least; one fit, or none (an off check), is the rules' pick.
            var asked = checks.Where(c => JevCheckVisuals.CleanFits(c.Fits).Count > 1).ToList();
            if (asked.Count == 0) return JevCheckVisuals.AllRules(checks, JevCheckVisuals.NoChoiceNote);

            var s = _sessions.Current;
            if (s == null) return JevCheckVisuals.AllRules(checks, JevCheckVisuals.NoModelNote);

            var point = DecisionRegistry.Require(DecisionRegistry.CheckVisualKind);
            IReadOnlyDictionary<string, object> snapshot;
            IReadOnlyList<DecisionQuestion> questions;
            try
            {
                snapshot = JevCheckVisuals.Snapshot(asked);
                questions = JevCheckVisuals.Questions(snapshot, asked);
            }
            catch (Exception ex) { return JevCheckVisuals.AllRules(checks, "Jev was not asked: " + ex.Message); }

            var fence = new DecisionSessionFence(s.Id, s.Revision, () =>
            {
                var current = _sessions.Current;
                return (current == null ? null : current.Id, current == null ? -1L : current.Revision);
            });
            DecisionOutcome outcome;
            try
            {
                outcome = await Decisions.AskAsync(point.Name, snapshot, questions,
                    isPro: Entitlement.FeatureGrants.IsProTier(_entitlement),
                    enabled: true,
                    fence: fence,
                    ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { return JevCheckVisuals.AllRules(checks, "Jev was not asked: " + ex.Message + "."); }
            if (!outcome.Asked) return JevCheckVisuals.AllRules(checks, "Jev was not asked: " + outcome.SkippedReason + ".");
            if (!outcome.Answers.Decided) return JevCheckVisuals.AllRules(checks, NotAnsweredNote(outcome.Answers.NotDecidedReason));
            return JevCheckVisuals.Read(outcome.Answers, point.ConfidenceBar, checks, new HashSet<string>(asked.Select(c => c.Id), StringComparer.Ordinal));
        }
    }
}

// What both doors share for the spec-first start (lane sf-doors): the page size for the words, the pointer a Plan read
// carries while a start plan exists, and the one line the fold shows an assistant when somebody else moved the plan.
// Nothing here decides anything. The engine verbs (LocalEngine.StartPlan.cs) own behaviour; this file only keeps the
// two doors from drifting, so the Plan page and an assistant are told the same thing in the same words.

using System;
using System.Linq;

namespace Semanticus.Engine.StartPlan
{
    /// <summary>What a Plan read says about a start plan, so the Plan page and get_model_plan both point at it. Null
    /// (and left out of the JSON) when no start plan exists, so a model with none reads exactly as it did before.</summary>
    public sealed class StartPlanPointer
    {
        public string Id { get; set; }
        /// <summary>"Source database" the plan reads, for the row's first line.</summary>
        public string Source { get; set; }
        /// <summary>One of <see cref="StartDecideState"/>.</summary>
        public string State { get; set; }
        public int NeedsYou { get; set; }
        public bool Approved { get; set; }
        public bool CanBuild { get; set; }
        /// <summary>The digest to quote, the same one get_start_plan shows.</summary>
        public string Digest { get; set; }
        /// <summary>One plain sentence: what to do next, and the tool that reads the whole plan.</summary>
        public string Next { get; set; }
    }

    internal static class StartPlanDoors
    {
        /// <summary>The tool every pointer and every fold line names.</summary>
        internal const string ReadTool = "get_start_plan";

        /// <summary>A page of words: the default, and the most one call returns. Both doors clamp the same way, so an
        /// assistant and the Studio page through the same list in the same steps.</summary>
        internal const int WordsPageDefault = 40;
        internal const int WordsPageMax = 100;

        internal static int WordsPageSize(int max) => max <= 0 ? WordsPageDefault : Math.Min(max, WordsPageMax);
        internal static int WordsOffset(int offset) => Math.Max(0, offset);

        /// <summary>The pointer for a view, or null when there is no start plan (so the Plan carries nothing).</summary>
        internal static StartPlanPointer PointerOf(StartPlanView v)
        {
            if (v == null || !v.Exists) return null;
            var approved = v.Approval != null && !string.IsNullOrEmpty(v.Approval.Digest)
                && string.Equals(v.Approval.Digest, v.Digest, StringComparison.Ordinal);
            var needs = v.NeedsYou?.Count ?? 0;
            string next;
            if (v.CanBuild && approved) next = "The person approved this plan. Build it with build_start_plan, quoting the digest.";
            else if (v.CanBuild) next = "The plan is ready for the person to approve in Studio. Read it with " + ReadTool + ".";
            else if (!string.IsNullOrWhiteSpace(v.BlockedBecause)) next = v.BlockedBecause.Trim() + " Read the plan with " + ReadTool + ".";
            else next = "A new model is being drafted from a SQL source. Read the plan with " + ReadTool + ".";
            return new StartPlanPointer
            {
                Id = v.Id,
                Source = v.Source == null ? null : string.Join(" ", new[] { v.Source.Server, v.Source.Database }.Where(x => !string.IsNullOrEmpty(x))),
                State = v.Decide?.State ?? StartDecideState.Off,
                NeedsYou = needs,
                Approved = approved,
                CanBuild = v.CanBuild,
                Digest = v.Digest,
                Next = next,
            };
        }

        /// <summary>What a start tells the person and the assistant about the model it closed to make room.</summary>
        internal static string ClosedModelNotice(string closedModel)
            => "The model '" + closedModel + "' that was open has been closed to start this one. It had no unsaved changes and its files were not touched; open it again to carry on with it.";

        /// <summary>The short digest a fold line quotes (the whole one is long and an assistant only needs to tell two apart).</summary>
        internal static string ShortDigest(string digest)
            => string.IsNullOrEmpty(digest) ? "none" : digest.Length <= 12 ? digest : digest.Substring(0, 12);

        /// <summary>What the fold says about the plan, in plain words, from the newest view somebody else published.
        /// Terse and stable, like the model fold: <c>start plan changed since your last call (in Studio): drafting,
        /// needs you 2, not approved; digest 3fa91c0d2b7e</c>. A discarded plan reads "discarded".</summary>
        internal static string FoldClause(StartPlanView v)
        {
            if (v == null || !v.Exists) return "discarded";
            var p = PointerOf(v);
            var state = p.State switch
            {
                StartDecideState.WaitingGrant => "waiting for permission",
                StartDecideState.Deciding => "deciding",
                StartDecideState.Stable => "settled",
                StartDecideState.Incomplete => "stopped short",
                _ => "drafted",
            };
            var needs = p.NeedsYou == 0 ? "nothing needs the person" : "needs the person " + p.NeedsYou;
            var approval = p.Approved ? "approved" : "not approved";
            return state + ", " + needs + ", " + approval + "; digest " + ShortDigest(p.Digest);
        }
    }
}

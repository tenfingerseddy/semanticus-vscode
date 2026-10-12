using System;

namespace Semanticus.Engine.Entitlement
{
    /// <summary>What Pro is. Kane reset this line on 2026-09-22: "free is the tool, Pro is the tool that works for
    /// you". Everything a person can do by hand, or with their own assistant driving the agent door, is FREE. What
    /// is paid is the product deciding and doing for them.
    ///
    /// Eight whole tabs left the paid line that day: Advanced modelling, Validation, Reports, Model Spec, Docs,
    /// Model notes, Power Query and the advanced half of Published. The features that used to hold them
    /// (modelCreate, tests, publishedAdvanced) are gone with them rather than left behind empty, because an empty
    /// feature is a wire id a page can still ask about and get a meaningless answer for.
    ///
    /// Three things are left, and two of them are TABS (Ask your data joined on 26 September):
    ///
    ///   Workflows, the whole tab. Running, authoring, bindings, activation, profiles, and the library and
    ///   document reads. It is the one feature <c>get_entitlement.features</c> reports, because it is the one a
    ///   page locks a tab on.
    ///
    ///   Ask, the box. One operation (<c>route_ask</c>). It is NOT a tab: the box sits on the Overview, which is
    ///   free to open, and only the box inside it is paid. It carries no wire id for the same reason the plan
    ///   ranking carries none (features.ts:50-56): a page that asked "do I have the ask feature" by wire id would
    ///   be asking about a tab that does not exist. The page keys the box on the TIER.
    ///
    /// The plan ranking and running, and the other registered decision points, are not in this enum at all. They
    /// are gated on the tier through <see cref="FeatureGrants.IsProTier"/>, which is the same single computation
    /// reached by a different question.</summary>
    public enum ProFeature
    {
        /// <summary>Running, authoring, bindings, activation, profiles, and the library and document reads.</summary>
        Workflows,

        /// <summary>The ask box on the Overview: one operation, no tab of its own, no wire id.</summary>
        Ask,

        /// <summary>Ask your data (Kane, 26 Sep): a question about the model's numbers read by Jev, the parts builder
        /// and the question history. It is a PAGE under Explore since Kane chose to build the page on 26 September,
        /// so it is a tab feature with a wire id the page locks its tab on.</summary>
        AskData,

        /// <summary>Many assistant-authored edits or plan controls in one call. Model edits share one undo step. No tab of its own.</summary>
        Batch,
    }

    /// <summary>The names and copy that go with each feature. The refusal sentence is assembled from ONE template in
    /// <see cref="EntitlementGuard"/>, so the phrase "Semanticus Pro feature" the UI matches on never moves.</summary>
    public static class ProFeatures
    {
        /// <summary>Every feature a Pro licence grants.</summary>
        public static readonly ProFeature[] All = { ProFeature.Workflows, ProFeature.Ask, ProFeature.AskData, ProFeature.Batch };

        /// <summary>The features that are a TAB, so a page can lock the tab and show a preview. These and only
        /// these are reported in <c>get_entitlement.features</c>.</summary>
        public static readonly ProFeature[] TabFeatures = { ProFeature.Workflows, ProFeature.AskData };

        /// <summary>The wire id carried in <c>get_entitlement.features</c> and asked for by the webview, or NULL
        /// for a feature that is not a tab and therefore has nothing for a page to lock.</summary>
        public static string WireId(ProFeature f) => f switch
        {
            ProFeature.Workflows => "workflows",
            ProFeature.Ask => null,
            ProFeature.AskData => "askData",
            ProFeature.Batch => null,
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };

        /// <summary>Parse a wire id back to the feature. Returns false for anything unknown, so an older or newer
        /// page asking for a feature this engine does not have is a plain "no", never a crash. Only tab features
        /// have a wire id, so only they can be parsed back.</summary>
        public static bool TryParse(string wireId, out ProFeature feature)
        {
            if (!string.IsNullOrEmpty(wireId))
                foreach (var f in TabFeatures)
                    if (string.Equals(WireId(f), wireId, StringComparison.Ordinal)) { feature = f; return true; }
            feature = default;
            return false;
        }

        /// <summary>The tab a person would open when the whole feature is named rather than one of its tabs. The
        /// per-operation tab in <see cref="FeatureMap"/> is preferred; this is the fallback.</summary>
        public static string DefaultTab(ProFeature f) => f switch
        {
            ProFeature.Workflows => "Workflows",
            ProFeature.Ask => "Ask about this model",
            ProFeature.AskData => "Ask your data",
            ProFeature.Batch => "Writing many objects in one call",
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };

        /// <summary>The free alternative sentence, one per feature. It tells a person and an agent what they can
        /// still do for free, so a refusal teaches rather than only blocks.</summary>
        public static string FreeAlternative(ProFeature f) => f switch
        {
            ProFeature.Workflows =>
                "The app still shows you any run already in flight and anything waiting for your approval.",
            ProFeature.Ask =>
                "Every page is still one click away on the area strip, and your own assistant can still open any of them.",
            ProFeature.AskData =>
                "Queries still runs any DAX you write, and your own assistant can still write and run DAX for you.",
            ProFeature.Batch =>
                "Use the matching single-item tool on Free.",
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };
    }
}

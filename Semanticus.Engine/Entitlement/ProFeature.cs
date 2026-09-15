using System;

namespace Semanticus.Engine.Entitlement
{
    /// <summary>The four Pro features. Kane set this line on 2026-09-15: Pro is four WHOLE features, not a list of
    /// big buttons, so a Pro feature's reads are Pro too and everything else is free. The enum is the gate key; the
    /// wire ids below are what <c>get_entitlement</c> reports and what the webview's <c>useFeature</c> asks for.</summary>
    public enum ProFeature
    {
        /// <summary>Model Spec, Advanced Modelling, Power Query, Docs and Model notes.</summary>
        ModelCreate,
        /// <summary>Tests and Saved reports, reads included, relationship checks and table row counts included.</summary>
        Tests,
        /// <summary>Source control writes and history, Fabric Git, CI/CD publish and the Data agent.</summary>
        PublishedAdvanced,
        /// <summary>Running, authoring, bindings, activation, profiles, and the library and document reads.</summary>
        Workflows,
    }

    /// <summary>The names and copy that go with each feature. The refusal sentence is assembled from ONE template in
    /// <see cref="EntitlementGuard"/>, so the phrase "Semanticus Pro feature" the UI matches on never moves.</summary>
    public static class ProFeatures
    {
        /// <summary>Every feature, in the order the matrix lists them.</summary>
        public static readonly ProFeature[] All =
        {
            ProFeature.ModelCreate, ProFeature.Tests, ProFeature.PublishedAdvanced, ProFeature.Workflows,
        };

        /// <summary>The wire id carried in <c>get_entitlement.features</c> and asked for by the webview.</summary>
        public static string WireId(ProFeature f) => f switch
        {
            ProFeature.ModelCreate => "modelCreate",
            ProFeature.Tests => "tests",
            ProFeature.PublishedAdvanced => "publishedAdvanced",
            ProFeature.Workflows => "workflows",
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };

        /// <summary>Parse a wire id back to the feature. Returns false for anything unknown, so an older or newer
        /// page asking for a feature this engine does not have is a plain "no", never a crash.</summary>
        public static bool TryParse(string wireId, out ProFeature feature)
        {
            foreach (var f in All)
                if (string.Equals(WireId(f), wireId, StringComparison.Ordinal)) { feature = f; return true; }
            feature = default;
            return false;
        }

        /// <summary>The tab a person would open when the whole feature is named rather than one of its tabs. The
        /// per-operation tab in <see cref="FeatureMap"/> is preferred; this is the fallback.</summary>
        public static string DefaultTab(ProFeature f) => f switch
        {
            ProFeature.ModelCreate => "Create in Model",
            ProFeature.Tests => "Tests",
            ProFeature.PublishedAdvanced => "Advanced publishing",
            ProFeature.Workflows => "Workflows",
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };

        /// <summary>The free alternative sentence, one per feature, verbatim from the matrix (section 2). It tells a
        /// person and an agent what they can still do for free, so a refusal teaches rather than only blocks.</summary>
        public static string FreeAlternative(ProFeature f) => f switch
        {
            ProFeature.ModelCreate =>
                "You can still edit measures, columns and relationships anywhere else in the model.",
            ProFeature.Tests =>
                "Model quality and AI understanding still check your model for free, and probe_measure still checks one number.",
            ProFeature.PublishedAdvanced =>
                "Publish, Promote, compare and restore points stay free.",
            ProFeature.Workflows =>
                "The App still shows you any run already in flight and anything waiting for your approval.",
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };
    }
}

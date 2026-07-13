using System;

namespace Semanticus.Engine.Evidence
{
    /// <summary>The transport form of one sealed evidence document. JSON is the record of truth; HTML is the
    /// deterministic human view over those exact bytes. Both doors return this shape instead of re-rendering.</summary>
    public sealed class EvidenceArtifact
    {
        public string Json { get; set; }
        public string Html { get; set; }
        public string ContentHash { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }

        public static EvidenceArtifact Seal(EvidenceDoc doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            doc.Validate();
            doc.ContentHash = EvidenceHash.Compute(doc);
            return new EvidenceArtifact
            {
                Json = EvidenceHash.CanonicalJson(doc),
                Html = EvidenceRenderer.Render(doc),
                ContentHash = doc.ContentHash,
            };
        }
    }
}

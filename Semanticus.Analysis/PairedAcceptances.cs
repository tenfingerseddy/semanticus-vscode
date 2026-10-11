// One acceptance for one problem seen by two pages (plan statement check 2, item 3b).
//
// AI readiness and Model quality each raise the SAME relationship problem under their own rule: an inactive relationship
// (REL-INACTIVE and INACTIVE_RELATIONSHIPS_THAT_ARE_NEVER_ACTIVATED) and a table that sits between two others
// (REL-SNOWFLAKE and SNOWFLAKE_SCHEMA_ARCHITECTURE). The Plan's "Sort out N relationships" row asks the person about the
// problem once, so accepting it, on either page, accepts it for both. There is no second record: the twin reads the first
// one, so reopening the acceptance reopens both, and an undo of the acceptance undoes both.
//
// "Both" means the two warnings the person could see when they decided. The record stores the twin that was firing then
// (WaiverRecord.PairedWith); a twin warning that appears later, such as a dead relationship after the measure that used it is
// deleted, was never shown and is open.
//
// This is NOT the related-change rule of DeclinedChanges (a twin that stays open and says "accept this one too"). That rule is
// about a CHANGE two rules both want to make; these two rules make no change at all, they only ask for a decision, and the
// decision is the same decision.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Analysis
{
    public static class PairedAcceptances
    {
        /// <summary>(AI readiness rule, Model quality rule) pairs about one and the same object.</summary>
        public static readonly IReadOnlyList<(string Air, string Bpa)> Pairs = new[]
        {
            ("REL-INACTIVE", "INACTIVE_RELATIONSHIPS_THAT_ARE_NEVER_ACTIVATED"),
            ("REL-SNOWFLAKE", "SNOWFLAKE_SCHEMA_ARCHITECTURE"),
        };

        /// <summary>The Model quality rule that asks the same question as this AI readiness rule, or null.</summary>
        public static string BpaTwinOf(string airRuleId) =>
            Pairs.Where(p => string.Equals(p.Air, airRuleId, StringComparison.OrdinalIgnoreCase)).Select(p => p.Bpa).FirstOrDefault();

        /// <summary>The AI readiness rule that asks the same question as this Model quality rule, or null.</summary>
        public static string AirTwinOf(string bpaRuleId) =>
            Pairs.Where(p => string.Equals(p.Bpa, bpaRuleId, StringComparison.OrdinalIgnoreCase)).Select(p => p.Air).FirstOrDefault();

        /// <summary>The stored acceptance that closes this finding because its twin on the other page was accepted, or null.
        /// <paramref name="system"/> is the page the finding is on ("air" or "bpa"). Only a record that was PAIRED with this rule when
        /// it was written counts: the twin was firing on the object then, so the person saw both and decided both. A twin that
        /// fires later (a measure that used the relationship was deleted) was never seen and is open.</summary>
        public static WaiverRecord TwinAcceptance(IEnumerable<WaiverRecord> waivers, string system, string ruleId, string objectRef)
        {
            var isAir = string.Equals(system, "air", StringComparison.OrdinalIgnoreCase);
            var twin = isAir ? BpaTwinOf(ruleId) : AirTwinOf(ruleId);
            if (twin == null || waivers == null) return null;
            var paired = waivers.Where(w => !WaiverStore.IsRuleLevel(w.ObjectRef) && string.Equals(w.PairedWith, ruleId, StringComparison.OrdinalIgnoreCase));
            return WaiverStore.Match(paired, isAir ? "bpa" : "air", twin, objectRef);
        }
    }
}

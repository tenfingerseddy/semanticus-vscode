// Consent: the record of what a person agreed may leave their machine.
//
// The rule the plan commits to, and the one this type exists to make unavoidable: no request leaves without
// CURRENT consent for the schema version in force. Current means three things at once, and all three are
// checked on every ask and again before any cached answer is served:
//   the person granted consent and has not withdrawn it
//   the granted schema version equals the version the shipped registry is on
//   every field the point is about to send is in the granted list
//
// The third check is what makes the second safe to reason about. A version match alone would trust that the
// version was bumped when a field was added; comparing the fields themselves does not need that trust.
//
// A consent record belongs to ONE model. The store, not this type, knows which: this type is what was agreed,
// and the store keeps one of these per model identity. Agreeing on a sample model must never speak for a
// private project opened later on the same machine.
//
// Generation is how a request proves it is still running under the permission it was admitted under. The
// store bumps it on every grant and every withdrawal for that model, so a request that was admitted, then had
// permission taken back and given again while it was in flight, comes back to a number that no longer matches
// and is dropped. A boolean cannot see that, and neither can a timestamp a second engine wrote.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Decisions
{
    public sealed class DecisionConsent
    {
        public static readonly DecisionConsent None = new DecisionConsent(false, 0, new string[0], null, null);

        public DecisionConsent(bool granted, int schemaVersion, IEnumerable<string> approvedFields,
                               DateTimeOffset? grantedUtc, DateTimeOffset? withdrawnUtc, long generation = 0, string grantedBy = null)
        {
            Generation = generation;
            GrantedBy = grantedBy;
            Granted = granted;
            SchemaVersion = schemaVersion;
            ApprovedFields = (approvedFields ?? Enumerable.Empty<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList()
                .AsReadOnly();
            GrantedUtc = grantedUtc;
            WithdrawnUtc = withdrawnUtc;
        }

        public bool Granted { get; }
        public int SchemaVersion { get; }

        /// <summary>The approved fields as "point:field", exactly the list shown when consent was asked for.</summary>
        public IReadOnlyList<string> ApprovedFields { get; }

        public DateTimeOffset? GrantedUtc { get; }

        /// <summary>Who gave it: "human" (the person, in Studio) or "agent" (their assistant, on their word in chat). Null on a record an older build wrote, which reads as the person's.</summary>
        public string GrantedBy { get; }
        public DateTimeOffset? WithdrawnUtc { get; }

        /// <summary>Which version of this model's permission this is. It goes up on every grant and every
        /// withdrawal, so a request admitted under one can tell it is no longer running under it.</summary>
        public long Generation { get; }

        public static DecisionConsent Grant(IEnumerable<string> fields, int schemaVersion, DateTimeOffset nowUtc,
                                            long generation = 0, string grantedBy = null) =>
            new DecisionConsent(true, schemaVersion, fields, nowUtc, null, generation, grantedBy);

        public DecisionConsent Withdraw(DateTimeOffset nowUtc, long generation = 0) =>
            new DecisionConsent(false, SchemaVersion, ApprovedFields, GrantedUtc, nowUtc, generation, GrantedBy);

        /// <summary>Whether this consent covers <paramref name="point"/> right now, and if not, why not in plain
        /// words. The reason is shown to the person and recorded in the result, so it never says "denied".</summary>
        public bool Covers(DecisionPoint point, int schemaVersionInForce, out string reason)
        {
            reason = null;

            if (!Granted)
            {
                reason = WithdrawnUtc.HasValue
                    ? "you withdrew permission to send this model's details for advice"
                    : "you have not given permission to send this model's details for advice";
                return false;
            }

            if (SchemaVersion != schemaVersionInForce)
            {
                reason = "the list of details that would be sent has changed since you agreed, so permission is needed again";
                return false;
            }

            if (point != null)
            {
                var missing = point.SnapshotFields
                    .Select(f => point.Name + ":" + f)
                    .Where(f => !ApprovedFields.Contains(f, StringComparer.Ordinal))
                    .ToList();
                if (missing.Count > 0)
                {
                    reason = $"permission does not cover every detail this would send ({string.Join(", ", missing)})";
                    return false;
                }
            }

            return true;
        }
    }
}

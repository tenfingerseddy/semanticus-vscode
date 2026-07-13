using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Semanticus.Engine
{
    /// <summary>One entry in the "Waiting for you" queue, or a granted approval.</summary>
    public sealed class ApprovalRecord
    {
        public string Id { get; set; }              // capability|label|intentHash, hashed — stable per intent
        public string Capability { get; set; }
        public string Label { get; set; }
        public string IntentHash { get; set; }
        public string Summary { get; set; }         // human-readable: what the agent wants to do
        public string Target { get; set; }          // endpoint/database, for the UI
        public string RequestedUtc { get; set; }
        public string GrantedUtc { get; set; }      // null while pending
        public string ExpiresUtc { get; set; }      // set on grant
        // How long a grant lives once approved — so the approval UI can state the true scope of a click BEFORE it is
        // granted (ExpiresUtc exists only after). Data, not copy: the card derives its wording from capability + this.
        public int TtlMinutes { get; set; }
    }

    /// <summary>
    /// The mechanism that turns an <see cref="GateOutcome.Ask"/> into a real approval flow across the TWO engine
    /// processes: the agent (MCP) registers a request, the human (RPC/UI) approves it, the agent's retry consumes it.
    /// So it is FILE-based and cross-process, exactly like the connection registry.
    ///
    /// Three properties make it safe:
    ///   • bound to (capability, label, intentHash) — approving one action does not approve a different one;
    ///   • consumption depends on the grant MODE: a one-shot action consumes its grant on use
    ///     (<see cref="TryConsume"/>), while a read/session grant is REUSABLE within its TTL and only checked, never
    ///     spent (<see cref="HasLiveGrant"/>) — the QueryData "read rows from this target until it expires" mode;
    ///   • TTL'd (<see cref="Ttl"/>, 15 min) — a grant expires, so a stale approval cannot be replayed later.
    /// Only a human may grant; the request comes from the agent. The generalisation of DeployGuard.MintToken the spec
    /// called for: the "token" here is a granted, unexpired, matching ApprovalRecord that only the human door can mint.
    /// </summary>
    public static class ApprovalLedger
    {
        public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
        // Tolerance for clock drift between the two engine processes sharing this file — NOT a way to stretch a grant.
        private static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);
        private const string FileName = "agent-approvals.json";

        /// <summary>Test seam.</summary>
        public static string RootOverride { get; set; }

        private sealed class Ledger { public List<ApprovalRecord> Items { get; set; } = new(); }

        private static string Path_() => System.IO.Path.Combine(HomeFile.Root(RootOverride), FileName);

        /// <summary>Bind an approval to the exact intent. The caller decides the granularity of <paramref name="intent"/>
        /// (e.g. endpoint|database for a deploy); a coarser intent means one approval covers a class of action, a finer
        /// one means per-object. Kept stable so the agent's retry hashes to the same id the human approved.</summary>
        public static string IntentHash(params string[] intent)
        {
            var basis = string.Join("|", (intent ?? Array.Empty<string>()).Select(s => (s ?? "").Length + ":" + (s ?? "")));
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(basis))).Substring(0, 32).ToLowerInvariant();   // 128-bit — an authorisation key, not a display id
        }

        private static string KeyOf(AgentCapability cap, string label, string intentHash) =>
            IntentHash(cap.ToString(), AgentPolicy.NormalizeLabel(label), intentHash);

        /// <summary>Register (or refresh) a pending request — what the agent does when it hits an Ask. Idempotent on the
        /// intent: re-asking updates the summary and time, it does not stack duplicates in the queue.</summary>
        public static ApprovalRecord Request(AgentCapability cap, string label, string intentHash, string summary, string target)
        {
            var id = KeyOf(cap, label, intentHash);
            return HomeFile.Mutate<Ledger, ApprovalRecord>(Path_(), () => new Ledger(), led =>
            {
                Prune(led);
                var rec = led.Items.FirstOrDefault(r => r.Id == id);
                if (rec == null) { rec = new ApprovalRecord { Id = id }; led.Items.Add(rec); }
                rec.Capability = cap.ToString();
                rec.Label = AgentPolicy.NormalizeLabel(label);
                rec.IntentHash = intentHash;
                rec.Summary = summary;
                rec.Target = target;
                rec.RequestedUtc = DateTimeOffset.UtcNow.ToString("O");
                rec.GrantedUtc = null;   // a fresh request revokes any prior grant for this intent — re-ask ⇒ re-approve
                rec.ExpiresUtc = null;
                rec.TtlMinutes = (int)Ttl.TotalMinutes;
                return rec;
            });
        }

        /// <summary>The queue the UI shows. Pending (ungranted, unexpired) first; a granted-but-unused entry is included
        /// so the UI can show "approved, waiting for the agent to act".</summary>
        public static IReadOnlyList<ApprovalRecord> List()
        {
            var led = HomeFile.Read(Path_(), () => new Ledger());
            var now = DateTimeOffset.UtcNow;
            return led.Items.Where(r => !IsExpired(r, now)).OrderBy(r => r.RequestedUtc, StringComparer.Ordinal).ToList();
        }

        /// <summary>Grant an approval. HUMAN ONLY — this is the whole mechanism: only the human door can mint the token
        /// the agent is missing. Grants a TTL from now.</summary>
        public static ApprovalRecord Approve(string id, string origin)
        {
            RequireHuman(origin);
            return HomeFile.Mutate<Ledger, ApprovalRecord>(Path_(), () => new Ledger(), led =>
            {
                Prune(led);
                var rec = led.Items.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("No pending approval with that id (it may have expired or been withdrawn).");
                var now = DateTimeOffset.UtcNow;
                rec.GrantedUtc = now.ToString("O");
                rec.ExpiresUtc = now.Add(Ttl).ToString("O");
                return rec;
            });
        }

        /// <summary>Deny/withdraw a request. Human only. Removes it from the queue.</summary>
        public static bool Deny(string id, string origin)
        {
            RequireHuman(origin);
            return HomeFile.Mutate<Ledger, bool>(Path_(), () => new Ledger(), led =>
            {
                var n = led.Items.RemoveAll(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                return n > 0;
            });
        }

        /// <summary>Read-only: is there a live grant for this exact intent? Does NOT consume. For capabilities whose
        /// grant is a time-boxed session rather than a single shot — QueryData means "the agent may read rows from THIS
        /// target until the grant expires", because one-shot consumption would demand an approval per query and teach
        /// the user to rubber-stamp. No lock: nothing is mutated, so the worst race is missing a just-granted approval
        /// and refusing once more (fail closed, retryable).</summary>
        public static bool HasLiveGrant(AgentCapability cap, string label, string intentHash)
        {
            var id = KeyOf(cap, label, intentHash);
            var led = HomeFile.Read(Path_(), () => new Ledger());
            var now = DateTimeOffset.UtcNow;
            return led.Items.Any(r => r.Id == id && IsLiveGrant(r, now));
        }

        /// <summary>The agent's retry path: if a granted, unexpired approval matches this exact intent, CONSUME it (one
        /// shot) and return true. Otherwise false — the guard then re-registers the request and refuses.</summary>
        public static bool TryConsume(AgentCapability cap, string label, string intentHash)
        {
            var id = KeyOf(cap, label, intentHash);
            var now = DateTimeOffset.UtcNow;
            return HomeFile.Mutate<Ledger, bool>(Path_(), () => new Ledger(), led =>
            {
                Prune(led);
                // A consumable grant must be genuinely granted AND carry a valid, unexpired expiry. A record with a
                // null/unparsable ExpiresUtc is malformed and must NOT be consumed forever — it fails closed.
                var rec = led.Items.FirstOrDefault(r => r.Id == id && IsLiveGrant(r, now));
                if (rec == null) return false;
                led.Items.Remove(rec);   // one-shot: consumed
                return true;
            });
        }

        // Exact "human" only — an unknown/whitespace/agent origin cannot grant (fail closed), matching the guard.
        private static void RequireHuman(string origin)
        {
            if (!string.Equals(origin, "human", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only a human can approve or deny an agent action — that asymmetry is the entire point of an approval.");
        }

        // A grant we may act on: BOTH timestamps parse, the grant isn't from the future, now is inside the window, and
        // the window is no longer than the TTL we ever mint — so a hand-edited far-future expiry (or a garbage
        // GrantedUtc paired with a plausible expiry) is dead, not immortal. Anything malformed fails closed.
        private static bool IsLiveGrant(ApprovalRecord r, DateTimeOffset now) =>
            DateTimeOffset.TryParse(r.GrantedUtc, out var g)
            && DateTimeOffset.TryParse(r.ExpiresUtc, out var t)
            && g <= now + Skew && now < t && t <= g + Ttl + Skew;

        private static bool IsExpired(ApprovalRecord r, DateTimeOffset now) =>
            r.GrantedUtc != null && !(DateTimeOffset.TryParse(r.ExpiresUtc, out var t) && now < t);

        // Drop expired grants AND stale pending requests (a request nobody approved within a generous window is noise).
        private static void Prune(Ledger led)
        {
            var now = DateTimeOffset.UtcNow;
            led.Items.RemoveAll(r => IsExpired(r, now));
            led.Items.RemoveAll(r => r.GrantedUtc == null
                && DateTimeOffset.TryParse(r.RequestedUtc, out var t) && t < now.AddHours(-24));
        }
    }
}

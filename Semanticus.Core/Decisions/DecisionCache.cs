// One answer, cached on the complete evidence.
//
// The key is the request digest (point, schema version, canonical snapshot, questions, option order) plus
// the served model version, because the same question answered by a different model version is a different
// answer and must not be replayed as if it were the same one. The client asks for a moving alias, so it
// learns the served version only from a reply; an entry therefore CARRIES the version that produced it and
// a lookup under a different current version misses. That is the honest behaviour for an alias, and the cost
// is one extra call the first time the vendor rolls a version forward.
//
// THE ALIAS MOVES, SO A HIT HAS A SHELF LIFE. The served version is only ever learned from a reply, so once
// an answer is cached nothing in this process can discover that jev-latest has rolled: the last observed
// version would be treated as current for the whole session and the old answer served forever. Of the two
// ways to bound that, the choice here is AGE, not a time bucket in the key: an entry is served only while it
// is younger than RevalidateAfter (ten minutes by default), and the ask that follows an expiry is what
// re-resolves the alias, because the reply names the version that served it. Age was chosen over a bucket in
// the key because it bounds staleness to the window from the moment the answer was bought, where a floor
// bucket can expire an entry a second after it lands or hold one for nearly two windows. The cost is one
// extra call per ten minutes per repeated piece of evidence. (Codex on PR 384.)
//
// Bounded and session scoped. The bound is a plain count with least-recently-used eviction: a cache that can
// grow with the model is a memory leak wearing a helpful name.
//
// What this type deliberately does NOT do: it does not check consent or entitlement. The caller rechecks
// both before serving a hit, because a person who withdraws consent must not keep receiving advice that was
// bought before they withdrew it.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Decisions
{
    public sealed class DecisionCache
    {
        private sealed class Slot
        {
            public DecisionAnswers Answers;
            public long Stamp;
            public DateTimeOffset PutAt;
        }

        private readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private long _clock;

        private readonly Func<DateTimeOffset> _now;

        public DecisionCache(int capacity = 256, TimeSpan? revalidateAfter = null, Func<DateTimeOffset> clock = null)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
            RevalidateAfter = revalidateAfter ?? DefaultRevalidateAfter;
            if (RevalidateAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(revalidateAfter));
            _now = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public int Capacity { get; }

        /// <summary>How long an entry may be served before the alias behind it has to be re-resolved.</summary>
        public TimeSpan RevalidateAfter { get; }

        public static readonly TimeSpan DefaultRevalidateAfter = TimeSpan.FromMinutes(10);

        public int Count { get { lock (_gate) return _slots.Count; } }

        public int Hits { get; private set; }
        public int Misses { get; private set; }

        private static string Key(string requestDigest, string servedModelVersion) =>
            requestDigest + "|" + (servedModelVersion ?? "unknown");

        /// <summary>Look up an answer for this evidence under this served version. A miss is the normal case and
        /// is never an error.</summary>
        public bool TryGet(string requestDigest, string servedModelVersion, out DecisionAnswers answers)
        {
            lock (_gate)
            {
                var key = Key(requestDigest, servedModelVersion);
                Slot slot;
                if (_slots.TryGetValue(key, out slot) && _now() - slot.PutAt >= RevalidateAfter)
                {
                    // The alias may have rolled. Drop the entry so the next ask goes to the service, which is
                    // what re-resolves it: the served version is only ever learned from a reply.
                    _slots.Remove(key);
                    slot = null;
                }
                if (slot != null)
                {
                    slot.Stamp = ++_clock;
                    Hits++;
                    // Cached answers cost nothing, so they report no spend. Otherwise a replayed hit would
                    // charge the session budget a second time for one purchase.
                    answers = slot.Answers.WithoutSpend();
                    return true;
                }

                Misses++;
                answers = null;
                return false;
            }
        }

        /// <summary>Keep a decided answer. A "not decided" is never cached: a timeout or a shrug is about the
        /// moment, not about the evidence, and caching it would make one bad minute permanent for the session.</summary>
        public void Put(DecisionAnswers answers)
        {
            if (answers == null || !answers.Decided || answers.RequestDigest == null) return;

            lock (_gate)
            {
                var key = Key(answers.RequestDigest, answers.ServedModelVersion);
                _slots[key] = new Slot { Answers = answers, Stamp = ++_clock, PutAt = _now() };

                while (_slots.Count > Capacity)
                {
                    var oldest = _slots.OrderBy(kv => kv.Value.Stamp).First().Key;
                    _slots.Remove(oldest);
                }
            }
        }

        /// <summary>Drop everything. Called when the session changes, when consent is withdrawn, and when a point
        /// is turned off, so no advice outlives the permission it was bought under.</summary>
        public void Clear()
        {
            lock (_gate) _slots.Clear();
        }
    }
}

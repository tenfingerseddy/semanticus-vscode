// Every gate a decision has to clear, in one place.
//
// The client (JevDecisionOracle) knows how to talk to a vendor and nothing else. THIS file knows when it is
// allowed to. Keeping them apart is what makes the frozen case matrix provable: a refused path never reaches
// the oracle at all, so "zero requests" is read off a counter rather than argued from the code.
//
// The gates, in order, and each one produces a plain sentence rather than a code:
//   1. the point is switched off for this model
//   2. the point is Pro and this session is not
//   3. no decision service is configured (the null path, and the default)
//   4. the open model has no stable identity, so no permission can belong to it
//   5. permission for THIS model is not current for the schema version in force
// Only then is a request built, and only then is the cache consulted. That order is deliberate: the plan
// requires consent and entitlement to be rechecked BEFORE a cached answer is served, and looking in the
// cache after them is how that is made structural instead of remembered.
//   6. the session has used its advice budget
// The budget comes AFTER the cache, because an answer already known costs nothing to read: a spent budget
// stops what would be sent, not what was already bought (a family working in batches reads every known
// answer first, and a spent budget used to hide them all).
//
// Withdrawal is the hard one, and the order below is the whole answer to it:
//   the cancellation source is captured BEFORE permission is read, never after. Reading first left a gap
//   where a withdrawal could swap in a fresh, uncancelled source for a request it had already refused.
//   admission is bound to the permission GENERATION it was granted under, and that number is checked again
//   immediately before the request goes out and again before any answer is returned. A withdrawal made in
//   another engine on this machine cannot cancel our token, but it does move the number on disk, and the
//   check before returning sees it.
// Honest limit: a second engine's withdrawal cannot interrupt a call that is already on the wire. It stops
// the answer being used, and it stops anything else going out; it does not reach into the socket.
//
// Two things are therefore dropped after the fact, not prevented:
//   a reply that arrives after permission was withdrawn
//   a reply that arrives after the model moved underneath it
// Both are real: the ask is off the dispatcher and takes up to four seconds, so the world can change while
// it is in flight. A dropped reply is reported as a skip with its reason, never applied quietly. The second is
// still KEPT when the model is the same one it was asked about (an edit moved the revision): it answers the
// evidence it was asked on, and that has not changed. A reply for a model that is no longer the one the session
// cache belongs to never goes into that cache. The first is not kept at all.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;

namespace Semanticus.Engine.Decisions
{
    /// <summary>What the model looked like when the ask started, and how to read it again when the reply lands.</summary>
    public sealed class DecisionSessionFence
    {
        public DecisionSessionFence(string sessionId, long revision, Func<(string SessionId, long Revision)> readNow,
                                    Func<DecisionRequest, Task<string>> refuse = null)
        {
            SessionId = sessionId;
            Revision = revision;
            ReadNow = readNow;
            Refuse = refuse;
        }

        /// <summary>A rule about what the request ITSELF may carry, for a caller whose permission is narrower than the model's
        /// (the start plan: only the tables the person let Jev see, StartPlanConsent). Read with the finished request in hand,
        /// again just before it is sent, and again when the reply lands. Returns a clause saying why it must not go ("...") or
        /// null; the service finishes the sentence. Null means no extra rule, which is every other caller.</summary>
        public Func<DecisionRequest, Task<string>> Refuse { get; }

        public string SessionId { get; }
        public long Revision { get; }
        public Func<(string SessionId, long Revision)> ReadNow { get; }

        public bool StillCurrent()
        {
            if (ReadNow == null) return true;
            var now = ReadNow();
            return string.Equals(now.SessionId, SessionId, StringComparison.Ordinal) && now.Revision == Revision;
        }

        /// <summary>A fence that is already stale. Used by the boundary matrix to prove a stale generation is
        /// refused before anything is sent.</summary>
        public static DecisionSessionFence Stale(string sessionId, long revision) =>
            new DecisionSessionFence(sessionId, revision, () => (sessionId + "-moved", revision + 1));
    }

    /// <summary>What one ask produced: either answers, or a plain reason it was skipped. Never both.</summary>
    public sealed class DecisionOutcome
    {
        private DecisionOutcome(DecisionAnswers answers, string skippedReason)
        {
            Answers = answers;
            SkippedReason = skippedReason;
        }

        public DecisionAnswers Answers { get; }
        public string SkippedReason { get; }
        public bool Asked { get { return Answers != null; } }

        public static DecisionOutcome Skipped(string reason) => new DecisionOutcome(null, reason);
        public static DecisionOutcome Answered(DecisionAnswers answers) => new DecisionOutcome(answers, null);
    }

    public sealed class DecisionService : IDisposable
    {
        private readonly object _gate = new object();
        private CancellationTokenSource _consentEpoch = new CancellationTokenSource();
        private long _requestsStarted;
        private string _cacheScope;
        private bool _retired;

        /// <summary>Test seams. They run at the two moments a withdrawal is hardest to get right: immediately
        /// before a request is handed to the service, and immediately after its reply lands. A test uses them
        /// to withdraw permission in exactly that window. Null on every production path.</summary>
        internal Action BeforeSendForTest;
        internal Action BeforeReturnForTest;

        /// <summary>A third seam, at the one moment the cache can belong to the wrong model: after this ask has
        /// claimed the cache for ITS model and before it reads either the cache or the store. A test opens another
        /// model in that window and proves two things about it: nothing this ask finds lands in the other model's
        /// cache, and nothing the other model put there is served to this one. Null on every production path.</summary>
        internal Action BeforeLookupForTest;

        public DecisionService(IDecisionOracle oracle, DecisionBudget budget = null, DecisionCache cache = null,
                               Func<string, DecisionConsent> readConsent = null, DecisionAnswerStore answers = null)
        {
            Oracle = oracle ?? NullDecisionOracle.Instance;
            Budget = budget ?? new DecisionBudget();
            Cache = cache ?? new DecisionCache();
            ReadConsent = readConsent ?? DecisionConsentStore.Read;
            Answers = answers;
        }

        public IDecisionOracle Oracle { get; }
        public DecisionBudget Budget { get; }
        public DecisionCache Cache { get; }

        /// <summary>The answers kept on this computer across restarts (DecisionAnswerStore.cs), or null for a service
        /// that keeps nothing past its own life. Looked up at the same point as the session cache, after every gate.</summary>
        public DecisionAnswerStore Answers { get; }

        /// <summary>Whether the session is still entitled to this point's tier, read at the moment of sending rather
        /// than trusted from the start of the ask (F-243). The ENGINE sets this from its live entitlement; null reads
        /// as "the answer the caller passed still holds", which is what a service with no engine behind it has.</summary>
        public Func<bool> StillEntitled { get; set; }

        /// <summary>Whether this service was retired because the licence it was built for changed.</summary>
        public bool Retired { get { lock (_gate) return _retired; } }

        /// <summary>Retire this service for good (F-243). The engine calls it when the licence changes: the client
        /// here was built with the old licence's credential, so a request that started under the old licence must
        /// not send, and one already on its way is cancelled and its reply dropped. A retired service never sends
        /// again; the engine builds a fresh one for the next ask.</summary>
        public void Retire()
        {
            CancellationTokenSource epoch;
            lock (_gate)
            {
                _retired = true;
                epoch = _consentEpoch;
            }
            try { epoch.Cancel(); } catch { }
            Cache.Clear();
        }

        /// <summary>Which model's permission applies, or null when the open model has no stable identity to
        /// keep one under. The ENGINE sets this; a service nobody bound one to reads as no permission and
        /// therefore sends nothing, which is the right default for a thing that can spend money.
        ///
        /// It is ASYNC because the scope stopped being a property of the address alone (F-177): it now carries a
        /// witness taken from the model itself, and reading the model means a hop to the single-writer
        /// dispatcher. A synchronous seam here would force either a blocking wait inside the ask or a cached
        /// value that can be a revision out of date, and a stale witness is the very reuse this fixes.
        /// <see cref="AskAsync"/> is the only caller and it is always off the dispatcher by then, so the hop is
        /// a normal await.</summary>
        public Func<Task<string>> ReadScope { get; set; }

        /// <summary>Read the stored permission for one model. Takes the scope so the read cannot accidentally
        /// be the whole machine's.</summary>
        public Func<string, DecisionConsent> ReadConsent { get; set; }

        /// <summary>How many asks actually reached the oracle this session. The frozen case matrix asserts this
        /// stays at zero on every refused path.</summary>
        public long RequestsStarted { get { return Interlocked.Read(ref _requestsStarted); } }

        /// <summary>Cancel everything in flight or queued, and drop every cached answer. Called when consent is
        /// withdrawn for <paramref name="scope"/>, so nothing bought under the old permission survives it.
        ///
        /// The scope is the WITHDRAWN model's, passed in by the caller that just wrote the withdrawal under it. It is
        /// never the scope this service last asked under: right after a restart nothing has been asked yet, and after
        /// another model was asked about that would be the other model's, so reading it here forgot the wrong model's
        /// answers and left this one's on disk (verify round 2, R2-1).</summary>
        public void OnConsentWithdrawn(string scope)
        {
            CancellationTokenSource old;
            lock (_gate)
            {
                old = _consentEpoch;
                _consentEpoch = new CancellationTokenSource();
            }
            try { old.Cancel(); } catch { }
            old.Dispose();
            Cache.Clear();
            // What was bought under that permission is forgotten on disk too. The generation check in the store is
            // the second guard, for a withdrawal made by another engine.
            Answers?.Forget(scope);
        }

        /// <summary>The reason an ask gives when the session's budget is spent and the answer is not already known.</summary>
        public const string BudgetSpentReason = "this session has used its suggestion budget";

        /// <summary>The reason an ask made with cachedOnly gives when nothing is kept for it: it was not sent.</summary>
        public const string NotCachedReason = "not asked yet";

        /// <summary>Drop cached answers without cancelling anything. Called when a point is switched off and when
        /// the session changes, because a cache is scoped to one session by design.</summary>
        public void ClearCache() => Cache.Clear();

        /// <summary>The model version serving answers now, read off the two oracles that can honestly say (see AskAsync), or null
        /// when none is known yet. The cache key: an answer is served again only under the version that gave it.</summary>
        public string ServedVersionNow =>
            (Oracle as JevDecisionOracle)?.LastServedModelVersion ?? (Oracle as RecordedDecisionOracle)?.ServedModelVersion;

        public async Task<DecisionOutcome> AskAsync(string pointName,
                                                    IReadOnlyDictionary<string, object> snapshot,
                                                    IReadOnlyList<DecisionQuestion> questions,
                                                    bool isPro, bool enabled, DecisionSessionFence fence,
                                                    CancellationToken ct, bool cachedOnly = false, bool perQuestion = false)
        {
            var point = DecisionRegistry.Require(pointName);

            if (!enabled)
                return DecisionOutcome.Skipped(SwitchedOff);

            if (point.ProOnly && !isPro)
                return DecisionOutcome.Skipped(ProOnly);

            if (!Oracle.Available)
                return DecisionOutcome.Skipped(NoService);

            var scope = ReadScope == null ? null : await ReadScope().ConfigureAwait(false);
            if (string.IsNullOrEmpty(scope))
                return DecisionOutcome.Skipped(NoScope);

            // The cancellation source is captured BEFORE permission is read. A withdrawal that lands after this
            // line cancels THIS source, and one that lands before it is seen by the read below. Doing it the
            // other way round leaves a window where a withdrawal swaps in a fresh source and the request it
            // meant to stop carries on under it.
            CancellationTokenSource epoch;
            lock (_gate) epoch = _consentEpoch;

            string why;
            var admitted = ReadConsent(scope);
            if (!admitted.Covers(point, DecisionRegistry.ConsentSchemaVersion, out why))
                return DecisionOutcome.Skipped(why);
            var admittedGeneration = admitted.Generation;

            if (fence != null && !fence.StillCurrent())
                return DecisionOutcome.Skipped("the model changed, so nothing was sent");

            // An answer bought for one model is not an answer for the next one. The cache is one per session
            // and the open model can change under it, so a change of model empties it.
            lock (_gate)
            {
                if (!string.Equals(_cacheScope, scope, StringComparison.Ordinal))
                {
                    _cacheScope = scope;
                    Cache.Clear();
                }
            }

            var beforeLookup = BeforeLookupForTest;
            if (beforeLookup != null) beforeLookup();

            var request = new DecisionRequest(point.Name, point.SchemaVersion, snapshot, questions, perQuestion);

            // A fence that narrows what may be sent (the start plan's disclosure) is asked BEFORE the cache is read, like consent
            // and entitlement: a cached answer bought on a table the person has since taken back is not served either.
            if (fence?.Refuse != null)
            {
                var refused = await fence.Refuse(request).ConfigureAwait(false);
                if (refused != null) return DecisionOutcome.Skipped(refused + ", so nothing was sent");
            }

            // The served version is not on IDecisionOracle, and deliberately: that interface is AskAsync and
            // Available and nothing else, so an implementation cannot be asked to promise a version before it
            // has answered once. The cache needs one anyway, so it is read off the two implementations that
            // can honestly supply it. A null here simply means "no version known yet", and the lookup is
            // skipped rather than done under a made-up key.
            var servedVersion = ServedVersionNow;
            // READ UNDER THE SAME GUARD AS THE WRITE. The re-scope above makes the cache this model's, so on this
            // ask's own path the two always agree; another ask that re-scopes between them is the only thing that can
            // tell, which is exactly the case this guards. Without it, a cache that now belongs to another model is
            // read for this one, and two models whose evidence hashes the same (the digest is the point, the schema
            // version, the snapshot and the questions, so a same-named table with the same facts does it) serve each
            // other's answers. An answer bought for one model is not an answer for the next one.
            DecisionAnswers cached = null;
            if (servedVersion != null)
            {
                lock (_gate)
                {
                    if (string.Equals(_cacheScope, scope, StringComparison.Ordinal)
                        && Cache.TryGet(request.Digest, servedVersion, out cached))
                        return DecisionOutcome.Answered(cached);
                }
            }
            // Kept on this computer from an earlier session, under this model and this permission. Right after a
            // restart no served version is known yet, and the stored one is trusted (DecisionAnswerStore.cs).
            if (Answers != null && Answers.TryGet(scope, admittedGeneration, request, servedVersion, out cached))
            {
                lock (_gate)
                {
                    if (string.Equals(_cacheScope, scope, StringComparison.Ordinal)) Cache.Put(cached);
                }
                return DecisionOutcome.Answered(cached.WithoutSpend());
            }
            // A caller that only wants what is already known (a family working in batches) is told nothing was sent.
            if (cachedOnly) return DecisionOutcome.Skipped(NotCachedReason);

            // Only now, with nothing known for it: the budget stops what would be SENT (the header, gate 6).
            if (Budget.Exhausted)
                return DecisionOutcome.Skipped(BudgetSpentReason);

            var beforeSend = BeforeSendForTest;
            if (beforeSend != null) beforeSend();

            // Everything above took time: gates, a digest, a cache lookup. The licence and permission are read once
            // more here, so the last thing checked before anything leaves the machine is whether it still may.
            // The licence goes first: a request admitted under Pro holds this service and its client, and a licence
            // removed while it waited must stop it here (F-243).
            if (!StillLicensed(point, out why))
                return DecisionOutcome.Skipped(why);
            if (!StillAdmitted(scope, point, admittedGeneration, out why))
                return DecisionOutcome.Skipped(why);
            // The last look before the wire, for what the request may carry and for the fence: a table added to a start plan, or a
            // new round, in the time the lookups above took.
            // Only a fence that narrows what may be sent is looked at again here. Every other fence keeps its one look before the
            // lookups: a reply for a model that moved is still kept (above), so moving it in this window is not a reason to hold back.
            if (fence?.Refuse != null)
            {
                var refused = await fence.Refuse(request).ConfigureAwait(false);
                if (refused != null) return DecisionOutcome.Skipped(refused + ", so nothing was sent");
                if (!fence.StillCurrent()) return DecisionOutcome.Skipped("the model changed, so nothing was sent");
            }

            DecisionAnswers answers;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, epoch.Token))
            {
                Interlocked.Increment(ref _requestsStarted);
                answers = await Oracle.AskAsync(request, linked.Token).ConfigureAwait(false);
            }

            var beforeReturn = BeforeReturnForTest;
            if (beforeReturn != null) beforeReturn();

            // Late replies. Consent first, because a withdrawal is a person changing their mind and outranks
            // anything already paid for. The stored permission is reread rather than only the local token
            // checked, because a withdrawal in another engine on this machine never touched our token.
            if (!StillLicensed(point, out why))
                return DecisionOutcome.Skipped("the license changed while the suggestion was on its way, so it was dropped");

            if (epoch.IsCancellationRequested || !StillAdmitted(scope, point, admittedGeneration, out why))
                return DecisionOutcome.Skipped("permission was withdrawn while the suggestion was on its way, so it was dropped");

            // Kept BEFORE the model check below, and only after the licence and permission checks above. An answer is
            // about the evidence it was asked on (the digest), not about the revision, so a reply that lands after an
            // edit is still the answer for that evidence: dropping it here only meant buying it again on the next run
            // (UAT lane p, 26 Sep). It is not used for the model that moved; it waits for the same evidence.
            //
            // But only for the MODEL it was asked about (verify round 2, R2-3). The session cache is one per session
            // and belongs to one scope at a time; when another model was asked about while this reply was on its way,
            // the cache now belongs to that model, and putting this reply in it would answer that model's question
            // about the same evidence with an answer bought for this one. So the check and the put are one step under
            // the gate that re-scopes the cache. The store on disk is keyed by the scope asked under, so it keeps this
            // model's answer in this model's file whatever is open now.
            // Only a WHOLE reply: a batched request whose reply had an answer refused (DecisionAnswers.Rejected) is used for the
            // answers it has, and asked again next time, so the gap is never served back from a cache.
            if (answers.Complete)
            {
                lock (_gate)
                {
                    if (string.Equals(_cacheScope, scope, StringComparison.Ordinal)) Cache.Put(answers);
                }
                Answers?.Put(scope, admittedGeneration, request, answers);
            }

            // The client holds each ask's estimate before it sends, so the session budget usually stops there, while
            // what is left is still more than nothing and Budget.Exhausted above reads false. That is the same stop as
            // gate 6 and is reported as the same skip, so every caller has one signal for "the budget stopped this"
            // however it stopped (verify round 3, R3-1: a family working in batches read only gate 6's skip, and
            // its rows read as the whole answer once the client, not the gate, stopped the budget).
            if (answers.OverBudget)
                return DecisionOutcome.Skipped(BudgetSpentReason);

            if (fence?.Refuse != null)
            {
                var refused = await fence.Refuse(request).ConfigureAwait(false);
                if (refused != null) return DecisionOutcome.Skipped(refused + ", so the suggestion was dropped");
            }

            if (fence != null && !fence.StillCurrent())
                return DecisionOutcome.Skipped("the model changed while the suggestion was on its way, so it was dropped");

            return DecisionOutcome.Answered(answers);
        }

        private const string SwitchedOff = "this suggestion is switched off for this model";
        private const string ProOnly = "suggestions like this one are part of Semanticus Pro";
        private const string NoService = "no suggestion service is set up, so nothing was sent and nothing changed";
        private const string NoScope = "this model has not been saved or opened live, so there is no permission that belongs to it";
        private const string BudgetSpent = "this session has used its suggestion budget";

        /// <summary>Why an ask at this point would stop before it could leave the machine, read now and asking nothing: the
        /// first gates of <see cref="AskAsync"/>, in its order and its words. Null when none of them would stop it (a change
        /// of model or licence while it waits still can). For a sentence kept from an earlier ask, so it is said only while
        /// it is still true (verifier on m11/sol-fixes-1 round 2, VR4).</summary>
        public async Task<string> WouldNotAskAsync(string pointName, bool isPro, bool enabled)
        {
            var point = DecisionRegistry.Require(pointName);
            if (!enabled) return SwitchedOff;
            if (point.ProOnly && !isPro) return ProOnly;
            if (!Oracle.Available) return NoService;
            var scope = ReadScope == null ? null : await ReadScope().ConfigureAwait(false);
            if (string.IsNullOrEmpty(scope)) return NoScope;
            if (!ReadConsent(scope).Covers(point, DecisionRegistry.ConsentSchemaVersion, out var why)) return why;
            return Budget.Exhausted ? BudgetSpent : null;
        }

        /// <summary>Whether the licence this request was admitted under still stands: the service has not been
        /// retired by a licence change, and a Pro point is still backed by a Pro session.</summary>
        private bool StillLicensed(DecisionPoint point, out string reason)
        {
            reason = null;
            if (Retired)
            {
                reason = "the license changed before the suggestion was sent, so nothing was sent";
                return false;
            }
            var entitled = StillEntitled;
            if (point.ProOnly && entitled != null && !entitled())
            {
                reason = "suggestions like this one are part of Semanticus Pro, and this session is no longer Pro, so nothing was sent";
                return false;
            }
            return true;
        }

        /// <summary>Whether this request is still running under the exact permission it was admitted under.
        /// Not "is there permission": a withdrawal followed by a fresh grant is a different permission, and a
        /// request admitted under the old one has no claim on the new one.</summary>
        private bool StillAdmitted(string scope, DecisionPoint point, long admittedGeneration, out string reason)
        {
            var now = ReadConsent(scope);
            if (!now.Covers(point, DecisionRegistry.ConsentSchemaVersion, out reason)) return false;
            if (now.Generation != admittedGeneration)
            {
                reason = "permission was taken back and given again while this suggestion was being prepared, so it was dropped";
                return false;
            }
            return true;
        }

        public void Dispose()
        {
            CancellationTokenSource epoch;
            lock (_gate) epoch = _consentEpoch;
            try { epoch.Cancel(); } catch { }
            epoch.Dispose();
            (Oracle as IDisposable)?.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>The free Pro month (Kane, 11 Oct 2026, option B), on the engine side: a licence that ends while the engine
    /// runs, and the trial's tally.
    ///
    /// THE END, WHILE A WINDOW IS OPEN. Before this the entitlement was read once at start, so a trial left open would stay
    /// Pro until a restart. Two things now move it on time, and both are needed:
    ///   1. Every read of <see cref="_entitlement"/> compares the clock with the licence's next change instant
    ///      (<see cref="Entitlement.LicenseEntitlement.ChangesAt"/>). Every gate reads it, so no Pro door is open one second
    ///      past the end, whatever a timer did. It is one clock read, and only for a licence that has an end.
    ///   2. A one-shot timer, re-armed after every entitlement change, fires at that instant, so the change is PUSHED to the
    ///      Studio (<c>entitlement/didChange</c>) even when nobody calls the engine. The timer never waits more than an hour
    ///      at a time, so a computer that slept through the end still tells the Studio within the hour; the gate is exact anyway.
    /// At the end the hosted decision service is dropped for NEW asks (a Free session has no credential to give it), but it
    /// is not retired: a request already sent finishes. An ask that has not been sent yet sees the session is no longer Pro
    /// and sends nothing (DecisionService.StillLicensed), which is the same skip a free session gets, never an error. A
    /// licence swap by a person (ApplyLicense) still retires the old service, because there the credential was taken away.
    ///
    /// THE TALLY. Counted from the events every commit, decision and workflow run already raise, only while the licence is a
    /// running trial, so the counts start at the trial and stop at its end. Kept in one small file
    /// (<see cref="Entitlement.TrialTallyStore"/>) because nothing that exists counts across models and restarts.</summary>
    public sealed partial class LocalEngine
    {
        // Never wait longer than this for the end: a timer sleeping through a suspended computer would otherwise tell the
        // Studio days late, and Timer cannot wait more than about 49 days at all.
        private static readonly TimeSpan MaxEntitlementWait = TimeSpan.FromHours(1);
        // Evaluation is now > end, so fire a moment after the instant rather than on it.
        private static readonly TimeSpan PastTheInstant = TimeSpan.FromMilliseconds(5);

        private Func<DateTimeOffset> _clock = () => DateTimeOffset.UtcNow;
        private readonly object _expiryTimerGate = new object();
        private Timer _expiryTimer;
        private TimeSpan? _expiryDue;
        private bool _expiryTimerStopped;

        private Entitlement.TrialTallyStore _trialTally;
        private readonly HashSet<string> _trialRunsCounted = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The running entitlement. Reads move a licence past its end at the instant it ends; a write (start, or a
        /// person's licence swap) re-arms the timer for the new licence's end.</summary>
        private Entitlement.IEntitlement _entitlement
        {
            get => CurrentEntitlement();
            set
            {
                _heldEntitlement = value;
                ArmEntitlementTimer();
            }
        }

        /// <summary>The entitlement now, moving a licence past its end first when it has ended.</summary>
        private Entitlement.IEntitlement CurrentEntitlement()
        {
            var held = _heldEntitlement;
            if (held is Entitlement.LicenseEntitlement le && le.ChangesAt is DateTimeOffset at && _clock() > at)
                return MovePast(le);
            return held;
        }

        /// <summary>Test seam: a clock the test moves, so nothing sleeps. No production path reaches it.</summary>
        internal void UseClockForTest(Func<DateTimeOffset> clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            ArmEntitlementTimer();
        }

        /// <summary>Test seam: what the timer does when it fires.</summary>
        internal void FireEntitlementTimerForTest() => OnEntitlementTimer();

        /// <summary>Test seam: how long the timer was last armed to wait, or null when it is not armed.</summary>
        internal TimeSpan? EntitlementTimerDueForTest { get { lock (_expiryTimerGate) return _expiryDue; } }

        /// <summary>The get_entitlement answer for <paramref name="e"/>: the effective grants, and the trial's days left at
        /// this engine's now (the info was made at one instant and a window stays open for days).</summary>
        private Entitlement.EntitlementInfo StampedInfo(Entitlement.IEntitlement e)
        {
            var info = Entitlement.FeatureGrants.Stamp(e?.Info, e);
            if (info != null && info.IsTrial) info.TrialDaysLeft = Entitlement.LicenseEntitlement.DaysLeft(info.TrialEndsAt, _clock());
            return info;
        }

        private Entitlement.IEntitlement MovePast(Entitlement.LicenseEntitlement stale)
        {
            var next = stale.At(_clock());
            if (ReferenceEquals(next, stale)) return stale;
            // Compare-and-swap rather than a lock: this runs inside whatever read the gate, which may already hold the
            // decision gate, and a person's licence swap takes the licence gate before the decision gate. If anything else
            // replaced the licence in between (a swap, or another reader moving the same licence), that one stands.
#pragma warning disable 420 // a volatile field passed by ref: Interlocked is a full fence
            var won = ReferenceEquals(Interlocked.CompareExchange(ref _heldEntitlement, next, stale), stale);
#pragma warning restore 420
            if (!won) return _heldEntitlement;

            if (!string.Equals(Entitlement.LicenseEntitlement.CloudCredentialOf(stale), Entitlement.LicenseEntitlement.CloudCredentialOf(next), StringComparison.Ordinal))
                lock (_decisionGate) _decisions = null;   // new asks get a service built for this licence; nothing in flight is cancelled
            ArmEntitlementTimer();
            PublishEntitlementChanged();
            return next;
        }

        /// <summary>Tell every Studio connection what the entitlement is now. On a pool thread: this can run inside a gate
        /// read on the dispatcher, and a broadcast has no business holding that up.</summary>
        private void PublishEntitlementChanged()
        {
            Task.Run(() =>
            {
                try { _sessions.Bus.PublishEntitlement(StampedInfo(_entitlement)); }
                catch { /* a notification never fails the change it reports */ }
            });
        }

        private void ArmEntitlementTimer()
        {
            var at = (_heldEntitlement as Entitlement.LicenseEntitlement)?.ChangesAt;
            lock (_expiryTimerGate)
            {
                if (_expiryTimerStopped) return;
                if (at == null)
                {
                    _expiryTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _expiryDue = null;
                    return;
                }
                var due = at.Value - _clock() + PastTheInstant;
                if (due < TimeSpan.Zero) due = TimeSpan.Zero;
                if (due > MaxEntitlementWait) due = MaxEntitlementWait;
                _expiryDue = due;
                // The timer holds the engine weakly: a scheduled timer is rooted by the runtime, and an engine nobody
                // disposed must still be collectable rather than kept alive by its own hourly re-arm.
                _expiryTimer ??= new Timer(static state =>
                {
                    if (((WeakReference<LocalEngine>)state).TryGetTarget(out var engine)) engine.OnEntitlementTimer();
                }, new WeakReference<LocalEngine>(this), Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _expiryTimer.Change(due, Timeout.InfiniteTimeSpan);
            }
        }

        private void OnEntitlementTimer()
        {
            try
            {
                CurrentEntitlement();    // moves the licence past its end, if it has ended, and tells the Studio
                ArmEntitlementTimer();   // early or not, wait for whatever is next
            }
            catch { /* a timer callback must never take the process down */ }
        }

        // ---- the tally ---------------------------------------------------------------------------------------------------

        public Task<Entitlement.TrialTally> GetTrialTallyAsync()
        {
            var claims = Entitlement.LicenseEntitlement.TrialOf(_entitlement, runningOnly: false);
            return Task.FromResult(claims == null ? new Entitlement.TrialTally() : TrialTallyFile.Read(TrialKey(claims), claims.Iat));
        }

        private Entitlement.TrialTallyStore TrialTallyFile =>
            LazyInitializer.EnsureInitialized(ref _trialTally,
                () => new Entitlement.TrialTallyStore(System.IO.Path.Combine(ConnectionRegistry.Root(), Entitlement.TrialTallyStore.FileName)));

        /// <summary>Test seam: write what this engine has counted, as it does when it stops.</summary>
        internal void FlushTrialTallyForTest() => _trialTally?.Flush();

        private static string TrialKey(Entitlement.LicenseClaims claims) =>
            !string.IsNullOrEmpty(claims.Sid) ? claims.Sid : "iat:" + claims.Iat;

        /// <summary>The claims of the trial that is running right now, or null (free, paid, or a trial that has ended).</summary>
        private Entitlement.LicenseClaims RunningTrial() => Entitlement.LicenseEntitlement.TrialOf(_entitlement, runningOnly: true);

        private void AttachTrialTally() => _sessions.Bus.Changed += OnTrialCommit;

        /// <summary>A commit: one step on the undo timeline, and each object it changed. An undo or a redo only walks the
        /// timeline, and a save or a scope change moves no object, so none of those count.</summary>
        private void OnTrialCommit(ChangeNotification n)
        {
            try
            {
                if (n == null || !n.IsCommit || n.Replay != null || n.Label == "undo" || n.Label == "redo") return;
                var trial = RunningTrial();
                if (trial == null) return;
                var objects = (n.Deltas ?? Array.Empty<ChangeDelta>())
                    .Select(d => d?.Ref).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.Ordinal).Count();
                TrialTallyFile.Add(TrialKey(trial), trial.Iat, changesApplied: objects, steps: 1);
            }
            catch { /* the tally rides along: it never fails a commit */ }
        }

        /// <summary>The hosted decision service answered: each answered question is one choice it made.</summary>
        private void NoteTrialJevChoices(Semanticus.Decisions.DecisionAnswers answers)
        {
            try
            {
                var trial = RunningTrial();
                if (trial == null || answers == null || !answers.Decided || answers.Answers.Count == 0) return;
                TrialTallyFile.Add(TrialKey(trial), trial.Iat, jevChoices: answers.Answers.Count);
            }
            catch { /* ride-along */ }
        }

        /// <summary>A workflow run finished. Counted once per run, however many times its end is published.</summary>
        private void NoteTrialWorkflowRun(string runId)
        {
            try
            {
                var trial = RunningTrial();
                if (trial == null || string.IsNullOrEmpty(runId)) return;
                lock (_trialRunsCounted) if (!_trialRunsCounted.Add(runId)) return;
                TrialTallyFile.Add(TrialKey(trial), trial.Iat, workflowsRun: 1);
            }
            catch { /* ride-along */ }
        }

        private void StopTrialWork()
        {
            lock (_expiryTimerGate)
            {
                _expiryTimerStopped = true;
                _expiryTimer?.Dispose();
                _expiryTimer = null;
                _expiryDue = null;
            }
            try { _sessions.Bus.Changed -= OnTrialCommit; } catch { }
            try { _trialTally?.Flush(); } catch { }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Serialization;

namespace Semanticus.Engine
{
    /// <summary>A cascade advisory bound to the object it concerns. Carrying the renamed object (not just its old
    /// name) lets a multi-rename batch attribute the warning to the EXACT object — matching by old name is ambiguous
    /// precisely in the duplicate-name collision this whole cascade exists for. <see cref="Text"/> is self-contained
    /// (it already names the qualified object) so a caller can surface it verbatim.</summary>
    public sealed class CascadeWarning
    {
        public TabularNamedObject Obj;   // durable identity of the renamed object (same instance across the rename)
        public string Text;              // the advisory sentence
    }

    /// <summary>
    /// THE canonical state for one open model. All access goes through the single-writer
    /// <see cref="ModelDispatcher"/>. Mutations are tracked: the real ObjectChanged firehose is
    /// collected into deltas and published on the <see cref="ChangeBus"/> as one coalesced
    /// notification per applied operation, tagged with the originating driver (human|agent).
    /// One undo/checkpoint timeline (the handler's UndoManager) is shared by both doors.
    /// </summary>
    public sealed class Session : IDisposable
    {
        private readonly ChangeBus _bus;
        private List<ChangeDelta> _pending;   // non-null only while a tracked op runs (dispatcher thread)

        // Every object a tracked commit of THIS session added, by reference, so a rename does not lose it. The Plan's
        // cleanup row never offers to remove one (UAT plan statement check, 28 Sep): "nothing uses it" says nothing
        // about a thing somebody built minutes ago. Dispatcher-thread only; a rehearsal never adds to it.
        private readonly HashSet<ITabularObject> _builtHere = new HashSet<ITabularObject>(ReferenceEqualityComparer.Instance);

        // A caller can capture this session immediately before another door replaces it. Retiring the session
        // closes admission atomically, then waits for work that already started to drain before the dispatcher is
        // torn down. A late caller holding the stale reference is refused instead of enqueueing onto a dead worker.
        private readonly object _lifetimeGate = new object();
        private int _activeOperations;
        private int _retired;
        private bool _disposeWhenDrained;
        private TaskCompletionSource<bool> _drained;
        internal bool RetiredForTest => System.Threading.Volatile.Read(ref _retired) != 0;

        public string Id { get; }
        /// <summary>When this session opened. The History's seam for Plan decisions: one older than this is from
        /// before this session (contract 8.9).</summary>
        public DateTimeOffset OpenedUtc { get; } = DateTimeOffset.UtcNow;
        internal ModelDispatcher Dispatcher { get; }
        // The TE2 handler now lives behind the IModelSession seam (ModelSession.cs); the engine talks to the
        // interface, never the concrete handler — see Te2ModelSession for why (the Singleton dies here later).
        private readonly IModelSession _model;
        // Settable: a model created from scratch starts with no path; the first save_model(path) anchors it here so
        // a later save_model() (no path) overwrites the same target (matching file-opened session semantics).
        public string SourcePath { get; internal set; }
        /// <summary>Set once by open_live right after the session is created, to bind it to its live source so
        /// deploy_live can push back without re-supplying the endpoint. Null for file-opened sessions. Holds no
        /// secret (see <see cref="LiveOrigin"/>). Reference assignment is atomic; written on the open path,
        /// read later on the deploy path.</summary>
        public LiveOrigin LiveOrigin { get; internal set; }

        /// <summary>The published model this session's file is a cached snapshot of, read from the snapshot folder's origin marker when a
        /// file under the snapshot root is opened as a plain model (a cold start reopens the cached snapshot). IDENTITY ONLY: it names the
        /// model for a held draft (<c>SessionInfo.ModelIdentity</c>) and nothing else. It is NOT a <see cref="LiveOrigin"/>, so deploy never
        /// reads it and it never makes the session live-bound.</summary>
        public LiveModelExport.SnapshotOrigin SnapshotOf { get; internal set; }

        /// <summary>A random key minted with the session, for a model that has no file, no live origin and no snapshot marker: two such
        /// models must never share a draft key, and the session id cannot say so (it restarts at s1 in every engine process).</summary>
        internal string InstanceKey { get; } = Guid.NewGuid().ToString("N");

        // Identities this model had before a Save As moved it (file:<old path>). A page that was showing the model before the Save still names it, and it is the same model
        // (drafts-sol round 3). Only ever added to, and only ever read by IdentityNames; the lock is for the fence, which reads it from the RPC thread.
        private readonly object _formerLock = new object();
        private readonly List<string> _formerIdentities = new List<string>();
        internal void AddFormerIdentity(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return;
            lock (_formerLock) if (!_formerIdentities.Contains(identity)) _formerIdentities.Add(identity);
        }
        internal string[] FormerIdentities { get { lock (_formerLock) return _formerIdentities.ToArray(); } }

        // The canonical form of SourcePath, read from disk once per path (Save As changes the path, and then it is read again).
        internal (string Path, string Canon) CanonCache;

        /// <summary>The session's PRIMER identity, computed once on first use and pinned for the session's lifetime
        /// (LocalEngine.Primer.cs derives it from LiveOrigin/SourcePath — fields stamped once on the open path). A
        /// cached value means the primer's filename can NEVER move mid-session, whatever future identity inputs are
        /// added. Only a non-null identity is cached (null = "no durable anchor yet", e.g. the window between the
        /// session swap and the LiveOrigin stamp — recomputed until one exists).</summary>
        internal string PrimerIdentityCache;

        /// <summary>The decision points switched on or off for this model while it has never been saved and is not live,
        /// so it has nowhere on disk to keep them. They are this session's own, so no other model reads them, and the first
        /// save writes them beside the model (LocalEngine.Decisions.cs). Guarded by the engine's decision gate.</summary>
        internal readonly Dictionary<string, bool> UnsavedSwitches = new Dictionary<string, bool>(StringComparer.Ordinal);

        // ---- Live-auth reuse (in-memory only; never serialized) ------------------------------------------
        // The bug this fixes: each live op (open/deploy/refresh) built a NEW Azure.Identity credential whose
        // token cache is per-instance, so interactive auth re-popped the browser EVERY time. We keep ONE
        // credential per identity for the session's lifetime: Azure.Identity serves its in-memory cached access
        // token and silently renews via the refresh token — the browser prompts once, then never again (like
        // Tabular Editor). We also cache the last token VALUE so a still-valid op skips the credential entirely
        // (and so the caller-supplied "token" mode can be reused to-source). This holds a live secret, but only
        // transiently in memory — the engine already mints+injects these tokens; nothing is persisted to disk.
        private readonly object _authLock = new object();
        private Azure.Core.TokenCredential _liveCredential;
        private string _liveCredentialKey;          // the LiveAuthKey (family|tenant|homeAccountId, or mode|tenant when no stable identity is known) this credential was built for
        // TWO slots, not one (T163). The human slot above may be PROMPT-CAPABLE; this one never is. With a single
        // slot an agent op inherited whatever the human's open had left there, so a stale refresh token could pop a
        // browser on the user's machine with no human present. Separate slots also mean selecting either never
        // EVICTS the other, so neither driver forces the other to re-authenticate. The cached ACCESS TOKEN stays
        // shared (a token is not prompt-capable), so an agent still rides a human's warm token in the common case;
        // only credential-level renewal is split.
        private Azure.Core.TokenCredential _agentCredential;
        private string _agentCredentialKey;          // same LiveAuthKey shape as the human slot above
        private Azure.Core.AccessToken _liveToken;  // last acquired token (value type; default = none, .Token == null)
        private string _liveTokenKey;

        /// <summary>Return the cached token if it matches <paramref name="key"/> (mode|tenant) and is not within
        /// <paramref name="skew"/> of expiry; otherwise default (its <c>.Token</c> is null) so the caller acquires.</summary>
        public Azure.Core.AccessToken TryReuseLiveToken(string key, TimeSpan skew)
        {
            lock (_authLock)
                return (System.Threading.Volatile.Read(ref _retired) == 0
                    && _liveTokenKey == key && _liveToken.Token != null && _liveToken.ExpiresOn - skew > DateTimeOffset.UtcNow) ? _liveToken : default;
        }

        // The auth setters below are DISPOSAL-AWARE (checked under _authLock): open_live seeds the token +
        // credential AFTER the swap core returns, so a newer open can commit and dispose this session inside
        // that window — a non-aware setter would then RE-populate the dead session with a live secret that the
        // idempotent Dispose never cleans again (held until process exit). Ordering makes the check sound:
        // retirement sets _retired BEFORE the auth-clear stage takes _authLock, so a setter that wins the lock
        // first is cleaned by the clear right behind it, and one that loses sees _retired and drops the offer.

        /// <summary>Cache the last acquired token under its identity key (for the fast-path reuse above).
        /// Dropped without caching on a disposed session.</summary>
        public void CacheLiveToken(string key, Azure.Core.AccessToken token)
        {
            lock (_authLock)
            {
                if (System.Threading.Volatile.Read(ref _retired) != 0) return;   // dead/retiring session: drop the offer
                _liveTokenKey = key; _liveToken = token;
            }
        }

        /// <summary>Get the cached credential for <paramref name="key"/> (a LocalEngine.LiveAuthKey: family + tenant +
        /// home-account id, or mode + tenant where no stable identity is known), building (and caching) it once if absent
        /// or if the identity changed. Reusing the instance is what makes interactive renew silently. THROWS
        /// ObjectDisposedException on a disposed session — a dead session must not manufacture live credentials
        /// at all (an uncached build would have ambiguous ownership: no caller could know to dispose it). The
        /// throw only surfaces in the narrow stale-session race, where refusing is exactly right.</summary>
        public Azure.Core.TokenCredential GetOrBuildLiveCredential(string key, bool nonInteractive, Func<Azure.Core.TokenCredential> build)
        {
            lock (_authLock)
            {
                if (System.Threading.Volatile.Read(ref _retired) != 0)
                    throw new ObjectDisposedException(nameof(Session), "This model session was replaced; re-run the operation.");
                if (nonInteractive)
                {
                    if (_agentCredential == null || _agentCredentialKey != key) { _agentCredential = build(); _agentCredentialKey = key; }
                    return _agentCredential;
                }
                if (_liveCredential == null || _liveCredentialKey != key) { _liveCredential = build(); _liveCredentialKey = key; }
                return _liveCredential;
            }
        }

        // Test-observable only: whether ANY live credential is currently retained (the exception-safe-clear pin) —
        // both slots, so the pin can't pass while the other one is still rooted.
        internal bool HasLiveCredentialForTest { get { lock (_authLock) return _liveCredential != null || _agentCredential != null; } }

        // Test-observable only: the credential currently held in ONE slot, so the open-time seeding of BOTH slots can
        // be asserted directly (same pinned identity, opposite prompt-capability).
        internal Azure.Core.TokenCredential PeekLiveCredentialForTest(bool nonInteractive)
        { lock (_authLock) return nonInteractive ? _agentCredential : _liveCredential; }

        /// <summary>Seed the credential acquired during open so the FIRST deploy/refresh reuses that exact
        /// instance (the one that already prompted) instead of building a fresh, blank-cache one. On a disposed
        /// session the offered credential is disposed immediately, never retained.</summary>
        public void SeedLiveCredential(string key, bool nonInteractive, Azure.Core.TokenCredential cred)
        {
            lock (_authLock)
            {
                if (System.Threading.Volatile.Read(ref _retired) != 0)
                {
                    try { (cred as IDisposable)?.Dispose(); } catch { }   // dead session: dispose the offer, retain nothing
                    return;
                }
                if (nonInteractive) { _agentCredential = cred; _agentCredentialKey = key; }
                else { _liveCredential = cred; _liveCredentialKey = key; }
            }
        }

        private long _revision;
        /// <summary>Monotonic op counter. Bumped on the dispatcher thread but read from other threads
        /// (e.g. the plan ops stamp it onto a broadcast view), so go through Interlocked for a coherent read.</summary>
        public long Revision => System.Threading.Interlocked.Read(ref _revision);

        public Model Model => _model.Model;

        /// <summary>The raw TOM database (read-only interop, e.g. .vpax export). Touch on the dispatcher thread.</summary>
        public Microsoft.AnalysisServices.Tabular.Database TomDatabase => _model.TomDatabase;

        /// <summary>Has the model unsaved edits? (Read passthrough for the engine's save/git/cicd paths.)
        /// OR-ed with the audit-dirty bit: a Verified Edits record is written NON-undoably (no undo action →
        /// invisible to the checkpoint arithmetic TE2's dirty flag is built on), so without this bit an
        /// edit-then-undo session reads CLEAN and save/git/close silently drop the audit record — breaking
        /// append-only across the session boundary.</summary>
        public bool HasUnsavedChanges => _model.HasUnsavedChanges || _auditDirty || _consentMarkerDirty;

        /// <summary>Unsaved work of the model's writers: everything <see cref="HasUnsavedChanges"/> counts except the
        /// consent marker a save writes back for itself (<see cref="MarkConsentMarkerDirty"/>). A save that writes only
        /// that marker has saved nobody's changes, so it does not count toward <see cref="SaveCount"/>.</summary>
        public bool HasUnsavedEdits => (_model.HasUnsavedChanges && !ConsentIsTheOnlyEdit) || _auditDirty;

        // THE MARKER FOR JEV'S PERMISSION IS NOT THE PERSON'S WORK (Kane, 8 Oct 2026, K2). Giving permission writes an annotation into the model,
        // and an undoable one (undo takes the permission back), so the model reads unsaved and the window said "unsaved edits are not included"
        // under a model nobody had edited. The revision right after that write is remembered; while the model is still at it, the marker is the
        // only change and nothing of the person's is unsaved. Any later change moves the revision and the marker counts with the rest.
        private long _consentOnlyRevision = -1;
        private bool ConsentIsTheOnlyEdit => System.Threading.Volatile.Read(ref _consentOnlyRevision) is long at && at >= 0 && Revision == at;

        /// <summary>Called right after the consent marker is written, when nothing of the person's was unsaved before it.</summary>
        public void NoteConsentMarkerIsTheOnlyEdit() => System.Threading.Volatile.Write(ref _consentOnlyRevision, Revision);

        // A checkout/pull changes the files backing this live session. Admission is assigned under one lock so an
        // edit/save either owns the state before Git intent (and may finish) or is refused; there is no semaphore
        // handoff gap where both sides can believe they won. Git takes priority over queued, non-owning mutations.
        private readonly object _modelStateGate = new object();
        private readonly Queue<ModelStateWaiter> _modelStateWaiters = new Queue<ModelStateWaiter>();
        private TaskCompletionSource<bool> _sourceControlStateWaiter;
        private bool _modelStateOwned;
        private int _sourceControlStateChange;
        private long _sourceControlStateEpoch;

        // Test-only one-shot pauses pin both sides of admission: a stale pre-admission waiter must fail, while a
        // caller that already owns the state must finish before Git can continue. Exceptions are swallowed below.
        internal Func<Task> ModelStateLeaseReadyForTest;
        internal Func<Task> ModelStateLeaseAcquiredForTest;

        internal Task<IDisposable> AcquireSourceControlStateLeaseAsync()
            => WithTransferredLifetimeLeaseAsync(AcquireSourceControlStateLeaseCoreAsync);

        private async Task<IDisposable> AcquireSourceControlStateLeaseCoreAsync()
        {
            Task wait = null;
            ModelStateWaiter[] rejected;
            lock (_modelStateGate)
            {
                if (_sourceControlStateChange != 0)
                    throw new InvalidOperationException("Another source-control state change is already running. Try again when it finishes.");
                _sourceControlStateChange = 1;
                _sourceControlStateEpoch++;
                rejected = _modelStateWaiters.ToArray();
                _modelStateWaiters.Clear();
                if (_modelStateOwned)
                {
                    _sourceControlStateWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _sourceControlStateWaiter.Task;
                }
                else _modelStateOwned = true;
            }
            foreach (var waiter in rejected)
                waiter.Completion.TrySetException(new InvalidOperationException(waiter.Error));
            if (wait != null) await wait.ConfigureAwait(false);
            return new SourceControlStateLease(this);
        }

        internal Task<IDisposable> AcquireModelFileWriteLeaseAsync()
            => WithTransferredLifetimeLeaseAsync(() => AcquireModelStateLeaseAsync("saved"));

        private async Task<IDisposable> AcquireModelStateLeaseAsync(string action)
        {
            var message = $"The model cannot be {action} while source control is changing its files. Try again when it finishes.";
            long epoch;
            lock (_modelStateGate)
            {
                if (_sourceControlStateChange != 0) throw new InvalidOperationException(message);
                epoch = _sourceControlStateEpoch;
            }
            var hook = Interlocked.Exchange(ref ModelStateLeaseReadyForTest, null);
            if (hook != null) { try { await hook().ConfigureAwait(false); } catch { /* a test seam must never break production */ } }

            Task wait = null;
            lock (_modelStateGate)
            {
                if (_sourceControlStateChange != 0 || _sourceControlStateEpoch != epoch)
                    throw new InvalidOperationException(message);
                if (_modelStateOwned)
                {
                    var waiter = new ModelStateWaiter(message);
                    _modelStateWaiters.Enqueue(waiter);
                    wait = waiter.Completion.Task;
                }
                else _modelStateOwned = true;
            }
            if (wait != null) await wait.ConfigureAwait(false);
            var acquiredHook = Interlocked.Exchange(ref ModelStateLeaseAcquiredForTest, null);
            if (acquiredHook != null) { try { await acquiredHook().ConfigureAwait(false); } catch { /* test-only */ } }
            return new ModelStateLease(this);
        }

        private sealed class ModelStateLease : IDisposable
        {
            private Session _owner;
            internal ModelStateLease(Session owner) => _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseModelStateLease();
        }

        private sealed class ModelStateWaiter
        {
            internal readonly string Error;
            internal readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal ModelStateWaiter(string error) => Error = error;
        }

        private sealed class SourceControlStateLease : IDisposable
        {
            private Session _owner;
            internal SourceControlStateLease(Session owner) => _owner = owner;
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner == null) return;
                owner.ReleaseSourceControlStateLease();
            }
        }

        private void ReleaseModelStateLease()
        {
            TaskCompletionSource<bool> next = null;
            lock (_modelStateGate)
            {
                if (_sourceControlStateChange != 0 && _sourceControlStateWaiter != null)
                {
                    next = _sourceControlStateWaiter;
                    _sourceControlStateWaiter = null;
                }
                else if (_sourceControlStateChange == 0 && _modelStateWaiters.Count > 0)
                    next = _modelStateWaiters.Dequeue().Completion;
                else _modelStateOwned = false;
            }
            next?.TrySetResult(true);
        }

        private void ReleaseSourceControlStateLease()
        {
            TaskCompletionSource<bool> next = null;
            lock (_modelStateGate)
            {
                _sourceControlStateChange = 0;
                if (_modelStateWaiters.Count > 0) next = _modelStateWaiters.Dequeue().Completion;
                else _modelStateOwned = false;
            }
            next?.TrySetResult(true);
        }

        // Set on the dispatcher thread by the audit-record writer; read from any thread (hence volatile).
        private volatile bool _auditDirty;
        public void MarkAuditDirty() => _auditDirty = true;

        // Set on the dispatcher thread when a save writes the consent marker back without an undo step
        // (LocalEngine.Decisions.cs, StampPendingConsentMarkerAsync): the model reads unsaved until that save lands.
        private volatile bool _consentMarkerDirty;
        public void MarkConsentMarkerDirty() => _consentMarkerDirty = true;

        /// <summary>Set on the dispatcher thread when the Plan reads this model unsaved, so "Save the changes you have
        /// made" is on the plan from then on. A save that writes changes after that records itself in the plan memory
        /// (LocalEngine.SaveAsync), because the row it answers must still read as done after a restart.</summary>
        public bool PlanSawUnsaved { get; set; }

        // ---- ambient commit observers ---------------------------------------------------------------------
        // The generalized probe seam (#82 follow-up): what used to be one settable health-probe slot is a small
        // REGISTRATION-ORDERED list, so a second ambient consumer can't evict the first. Copy-on-write array:
        // TrackAsync pays exactly one volatile read (zero observers = zero further cost on the hot path);
        // registration is rare (session open / tests). Session stays analyzer-ignorant — it only knows the
        // ISessionObserver seam; the health analysis lives in HealthDeltaProbe. Observers must never fail or
        // delay-fail an edit, so every invocation below is exception-isolated PER OBSERVER (one faulty observer
        // is skipped for that commit; its peers and the commit itself proceed).
        private volatile ISessionObserver[] _observers = Array.Empty<ISessionObserver>();
        private readonly object _observersLock = new object();

        /// <summary>Registration-ordered snapshot (diagnostics/tests — e.g. replacing the installed probe).
        /// Wrapped read-only: handing out the backing array would let a caller cast it back and mutate it in
        /// place, breaking the copy-on-write invariant. Our own code never mutates an array after publishing
        /// it, so the wrapper is a true snapshot.</summary>
        public IReadOnlyList<ISessionObserver> Observers => Array.AsReadOnly(_observers);

        public void RegisterObserver(ISessionObserver observer)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));
            lock (_observersLock)
            {
                if (Array.IndexOf(_observers, observer) >= 0) return;   // idempotent — a double-register can't double-fire
                var next = new ISessionObserver[_observers.Length + 1];
                Array.Copy(_observers, next, _observers.Length);
                next[_observers.Length] = observer;
                _observers = next;
            }
        }

        public void UnregisterObserver(ISessionObserver observer)
        {
            lock (_observersLock)
            {
                var i = Array.IndexOf(_observers, observer);
                if (i < 0) return;
                var next = new ISessionObserver[_observers.Length - 1];
                Array.Copy(_observers, next, i);
                Array.Copy(_observers, i + 1, next, i, _observers.Length - i - 1);
                _observers = next;
            }
        }

        public Session(string id, ModelDispatcher dispatcher, IModelSession model, ChangeBus bus, string sourcePath)
        {
            Id = id;
            Dispatcher = dispatcher;
            _model = model;
            _bus = bus;
            SourcePath = sourcePath;
            _model.ObjectChanged += OnObjectChanged;
            _model.ObjectDeleting += OnObjectDeleting;
            _model.ObjectDeleted += OnObjectDeleted;
            _model.Undo.SetCheckpoint(); // freshly opened == clean
        }

        /// <summary>Hear every property change made to this session's model until the returned scope is disposed. Dispatcher thread only (it is for work inside a
        /// mutation: the start plan's build asks afterwards which properties it set). It does not touch the Session's own change collector.</summary>
        internal IDisposable Listen(ObjectChangedEventHandler handler)
        {
            _model.ObjectChanged += handler;
            return new Subscription(() => _model.ObjectChanged -= handler);
        }

        private sealed class Subscription : IDisposable
        {
            private Action _off;
            public Subscription(Action off) { _off = off; }
            public void Dispose() { var o = _off; _off = null; try { o?.Invoke(); } catch { } }
        }

        private void OnObjectChanged(object sender, ObjectChangedEventArgs e)
        {
            // Fires synchronously on the dispatcher thread during a tracked op. The values are rendered, capped and
            // redacted here, before they can reach the broadcast, the MCP fold or the ledger (contract 8.3).
            if (_pending == null) return;
            var (before, after) = ChangeValues.For(e.TabularObject, e.PropertyName, e.OldValue, e.NewValue);
            _pending.Add(new ChangeDelta
            {
                Kind = "update",
                Ref = ObjectRefs.For(e.TabularObject),
                Props = e.PropertyName == null ? null : new[] { e.PropertyName },
                Before = before,
                After = after,
            });
        }

        // A deleted object may no longer be able to name itself (its parent link is gone), so its ref is taken while
        // it is being deleted and handed to the deleted event. Dispatcher-thread-only, like _pending.
        private readonly Dictionary<ITabularObject, string> _deleting = new Dictionary<ITabularObject, string>(ReferenceEqualityComparer.Instance);

        private void OnObjectDeleting(object sender, ObjectDeletingEventArgs e)
        {
            if (_pending == null || e?.TabularObject == null) return;
            try { _deleting[e.TabularObject] = ObjectRefs.For(e.TabularObject); } catch { /* named below, if it can be */ }
        }

        private void OnObjectDeleted(object sender, ObjectDeletedEventArgs e)
        {
            if (_pending == null || e?.TabularObject == null) return;
            if (!_deleting.Remove(e.TabularObject, out var r))
            {
                try { r = ObjectRefs.For(e.TabularObject); } catch { r = null; }
            }
            if (string.IsNullOrEmpty(r)) return;
            _pending.Add(new ChangeDelta { Kind = "remove", Ref = r });
        }

        // TOM fires no event for an add, so the undo actions the commit pushed are read for the objects it added,
        // each written as an "add" delta ahead of that commit's other deltas (contract 8.3).
        private List<ChangeDelta> WithAdds(List<ChangeDelta> deltas, int undoSizeBefore, bool builtHere = false)
        {
            var added = _model.Undo.ObjectsAddedSince(undoSizeBefore);
            if (added.Count == 0) return deltas;
            if (builtHere) foreach (var obj in added) if (obj != null) _builtHere.Add(obj);
            var adds = new List<ChangeDelta>();
            foreach (var obj in added)
            {
                string r;
                try { r = ObjectRefs.For(obj); } catch { continue; }
                if (string.IsNullOrEmpty(r) || adds.Exists(d => d.Ref == r)) continue;
                adds.Add(new ChangeDelta { Kind = "add", Ref = r });
            }
            adds.AddRange(deltas);
            return adds;
        }

        /// <summary>Dispatcher-thread only (inside <see cref="ReadAsync"/>): true when a tracked commit of this session
        /// added <paramref name="obj"/>, or the table it sits on.</summary>
        internal bool BuiltHere(ITabularObject obj)
        {
            if (obj == null) return false;
            if (_builtHere.Contains(obj)) return true;
            return obj is ITabularTableObject t && t.Table != null && _builtHere.Contains(t.Table);
        }

        public Task<T> ReadAsync<T>(Func<Model, T> work) => RunAsync(() => work(Model));

        /// <summary>Run arbitrary model-session work on the dispatcher under this session's lifetime lease.</summary>
        internal Task<T> RunAsync<T>(Func<T> work) => WithLifetimeLeaseAsync(() => OnDispatcher(work));

        /// <summary>The ONE way a session's work reaches its dispatcher thread: "gate, pin this model's handler, work"
        /// (<see cref="TabularEditor.TOMWrapper.Utils.WrapperGate"/>). The vendored wrapper binds every object it creates to a process-wide static
        /// handler, so without the pin a second model on another dispatcher (a hidden scratch build, a replacement
        /// built before the old session retires) could hand this model's new objects to itself. Reads, tracked commits,
        /// rehearsals, undo, redo, walks and retracts all come through here, so no entry point can forget it.</summary>
        private Task<T> OnDispatcher<T>(Func<T> work) => Dispatcher.RunAsync(() =>
        {
            using (_model.Pin()) return work();
        });

        /// <summary>Apply a mutation as one undo step, collect deltas, and broadcast a change notification. Pass
        /// <paramref name="cascadeSink"/> to receive the LSDL-cascade advisories THIS batch produced: the retry
        /// deposits survivors into it INSIDE the lease, before the returned task completes, so the warnings are owned
        /// by this exact call — never drained by a concurrent request (a session-global list let whoever drained first
        /// steal another caller's advisory). Only the rename surfaces need it; every other caller passes null.</summary>
        public Task<long> MutateAsync(string origin, string label, Action<Model> work, List<CascadeWarning> cascadeSink = null)
            => MutateAsync(origin, label, work, cascadeSink, keepsModelSaved: false);

        /// <summary>A tracked change to something kept BESIDE the model (the Primer, F-353): one undo step and one
        /// History entry like any edit, but the model itself is not edited, so a model that was saved stays saved.</summary>
        public Task<long> MutateBesideModelAsync(string origin, string label, Action<Model> work)
            => MutateAsync(origin, label, work, null, keepsModelSaved: true);

        /// <summary>Dispatcher-thread only, inside a tracked commit's work: add a change the TOM events cannot see
        /// (a sidecar's before and after) to the deltas this commit broadcasts and the History records.</summary>
        internal void RecordDelta(ChangeDelta delta)
        {
            if (delta != null) _pending?.Add(delta);
        }

        private async Task<long> MutateAsync(string origin, string label, Action<Model> work, List<CascadeWarning> cascadeSink, bool keepsModelSaved)
        {
            // Read the ambient dry-run scope on the CALLER's thread, BEFORE the dispatcher hop — AsyncLocal need
            // not flow to the dispatcher's dedicated thread, so we capture the collector here and never rely on it
            // being visible inside RunAsync. Non-null ⇒ rehearse (RehearseAsync); null ⇒ the real mutation.
            var dry = DryRunScope.Current;
            return await WithLifetimeLeaseAsync(() => dry != null
                ? RehearseAsync(dry, label, work, cascadeSink)
                : TrackAsync(origin, label, () =>
                {
                    var redo = (_model.Undo as IRedoPreserve)?.CaptureRedo();
                    var depth = _model.BatchDepth;
                    var wasSaved = keepsModelSaved && depth == 0 && _model.Undo.AtCheckpoint;
                    _model.BeginUpdate(label);
                    // RetryPendingCascades runs INSIDE the batch (before EndUpdate) so retried LSDL writes join
                    // the same undo step as the renames that queued them. On a work throw the rollback reverts
                    // the renames themselves, so the pendings are moot — just cleared. RestoreRedo puts back the
                    // redo stack TE2 cleared when the first action of this now-refused batch was recorded.
                    var structure = (_model as IStructureGuard)?.Capture();
                    var sealedYet = false;
                    try
                    {
                        work(Model); RetryPendingCascades(cascadeSink);
                        sealedYet = true;
                        (_model as IStructureGuard)?.Seal(structure);
                        _model.EndUpdate();
                        // TE2 counts "saved" by stack position, so the new step would read as an unsaved model edit.
                        if (wasSaved) _model.Undo.SetCheckpoint();
                    }
                    catch
                    {
                        _pendingCascades.Clear();
                        // A work that threw part way may have written values outside TE2's undo: seal what it did, so the
                        // rollback below takes those back too.
                        if (!sealedYet) { try { (_model as IStructureGuard)?.Seal(structure); } catch { } }
                        try { RollBackTo(depth); }
                        finally { (_model.Undo as IRedoPreserve)?.RestoreRedo(redo); }
                        throw;
                    }
                })).ConfigureAwait(false);
        }

        /// <summary>Dry-run variant: run the REAL work on the dispatcher, then ALWAYS roll the batch back — the
        /// vendored EndBatch(rollback) failure path used as the success path (it undoes the whole batch and leaves
        /// NO undo entry, verified). Collect the would-be deltas + label into the scope, DON'T bump <c>_revision</c>,
        /// DON'T <c>_bus.Publish</c>, and return the CURRENT revision unchanged — a rehearsal is invisible to both
        /// doors. On a work exception the batch still rolls back and the exception rethrows, so the caller reports
        /// the failed rehearsal (a dry-run that finds the failure is still a successful rehearsal).</summary>
        private Task<long> RehearseAsync(DryRunCollector dry, string label, Action<Model> work, List<CascadeWarning> cascadeSink) =>
            OnDispatcher(() =>
            {
                _pending = new List<ChangeDelta>();   // the same ObjectChanged collector the real path uses
                var redo = (_model.Undo as IRedoPreserve)?.CaptureRedo();
                var sizeBefore = _model.Undo.UndoSize;
                var depth = _model.BatchDepth;
                try
                {
                    _model.BeginUpdate(label);
                    ChangeDelta[] mutationDeltas;
                    // The retry runs in rehearsals too (same behavior as the real path, all rolled back below);
                    // the finally clears any pendings a work throw left so they can't leak into the next batch.
                    try
                    {
                        var structure = (_model as IStructureGuard)?.Capture();
                        // Sealed even when the work threw (the rollback below must take back what it wrote outside TE2's undo).
                        try { work(Model); }
                        catch { try { (_model as IStructureGuard)?.Seal(structure); } catch { } throw; }
                        (_model as IStructureGuard)?.Seal(structure);
                        RetryPendingCascades(cascadeSink); mutationDeltas = WithAdds(_pending, sizeBefore).ToArray();
                    }
                    finally { _pendingCascades.Clear(); _deleting.Clear(); RollBackTo(depth); }   // ALWAYS undo, success OR exception
                    // The snapshot is taken BEFORE the rollback: reverting re-fires ObjectChanged for every undone
                    // property (that's how a real undo broadcasts its deltas), so snapshotting after would report
                    // each edit twice — forward and reversed. The report must carry what the op WOULD have broadcast.
                    dry.Deltas.AddRange(mutationDeltas);
                    dry.MutationLabels.Add(label);
                }
                finally
                {
                    (_model.Undo as IRedoPreserve)?.RestoreRedo(redo);
                    _pending = null;
                }
                return Revision;
            });

        /// <summary>Roll back and close every edit batch opened since the batch depth was <paramref name="depth"/>,
        /// innermost first. TE2 opens a batch of its own inside some edits and closes it only when the edit finishes:
        /// a table rename opens one for FormulaFixup (Table.OnPropertyChanging) and closes it in OnPropertyChanged, and
        /// TOM refuses a reserved name such as "Measures" between the two. One EndUpdate then closed only that inner
        /// batch and left this call's batch open, so every later edit nested inside it, the next Undo drained the
        /// whole stack and threw halfway, and TE2 was left recording nothing and rolling nothing back. A rehearsal
        /// then applied for real, which hid key columns with no revision and no History row (UAT-S07b-1-007, 008),
        /// and every later edit made no undo step and no History row (UAT-S07b-1-001, 004, 005, 006).</summary>
        private void RollBackTo(int depth)
        {
            // The progress check stops the loop if TE2 ever declines to close a batch (it does while an undo is
            // running), so a refusal can never spin.
            int before;
            do { before = _model.BatchDepth; _model.EndUpdate(undoable: true, rollback: true); }
            while (_model.BatchDepth > depth && _model.BatchDepth < before);
        }

        /// <summary>Take back the newest commit and leave nothing to redo: for a change its caller refused after it
        /// landed (a direct workflow step whose gate refused the submission, Sol 6 slice 8 finding 4). Refused, with
        /// nothing changed, unless <paramref name="revision"/> is still the newest commit, so it never takes back an
        /// edit someone else made in between. Published as an undo, so the ledger and both doors see the step go.</summary>
        internal Task<long> RetractAsync(string origin, string label, long revision) =>
            WithLifetimeLeaseAsync(() => TrackAsync(origin, label, () =>
            {
                if (Revision != revision || !_model.Undo.CanUndo)
                    throw new InvalidOperationException("Another edit landed after it, so it was not taken back.");
                RefuseIfSidecarMoved(redo: false, 1);
                var redo = (_model.Undo as IRedoPreserve)?.CaptureRedo();
                _model.Undo.Undo();
                (_model.Undo as IRedoPreserve)?.RestoreRedo(redo);
            }, "undo"));

        public Task<long> UndoAsync(string origin) => WithLifetimeLeaseAsync(() => TrackAsync(origin, "undo", () => { RefuseIfSidecarMoved(redo: false, 1); _model.Undo.Undo(); }, "undo"));
        public Task<long> RedoAsync(string origin) => WithLifetimeLeaseAsync(() => TrackAsync(origin, "redo", () => { RefuseIfSidecarMoved(redo: true, 1); _model.Undo.Redo(); }, "redo"));

        /// <summary>Refuse, before anything is walked, an undo or redo that would put back a file beside the model (the
        /// Primer, its suggestion list) that was changed on disk since: undoing would overwrite that newer edit (Sol on
        /// m11/integration 568435d3, finding 2). Throwing here, inside the commit and before the walk, changes nothing
        /// and publishes nothing, as every refusal in TrackAsync does.</summary>
        private void RefuseIfSidecarMoved(bool redo, int steps)
        {
            var why = (_model.Undo as ISidecarGuard)?.SidecarRefusal(redo, steps);
            if (why != null) throw new InvalidOperationException(why);
        }

        /// <summary>The model was saved somewhere else (Save As carries the Primer to the new model's name): each sidecar
        /// file an undo step owns follows to where <paramref name="follow"/> says it now is for this model, so the move is
        /// never read as an edit made in another editor (verifier on m11/sol-fixes-1, D4), or is let go on null, so Undo
        /// never writes another model's file (VR1). On the dispatcher, like every other touch of the timeline; it changes
        /// no file and no revision.</summary>
        internal Task RebaseSidecarsAsync(Func<string, string> follow) =>
            follow == null ? Task.CompletedTask
            : RunAsync(() => { (_model.Undo as ISidecarGuard)?.SidecarRebase(follow); return true; });

        /// <summary>Undo (or redo) several steps as ONE commit: one revision bump, one broadcast, labelled
        /// <paramref name="label"/> (contract 8.6, "Undo to here"). <paramref name="steps"/> runs ON the dispatcher,
        /// inside the commit, so the count is worked out against the stack exactly as it stands when the walk
        /// starts: no edit can land between reading the ledger and walking the stack. It throws to refuse, and a
        /// refusal walks nothing and publishes nothing.</summary>
        public Task<long> WalkAsync(string origin, bool redo, string label, Func<int> steps) =>
            WithLifetimeLeaseAsync(() => TrackAsync(origin, label, () =>
            {
                var n = steps();
                RefuseIfSidecarMoved(redo, n);
                for (var i = 0; i < n; i++)
                {
                    if (redo) _model.Undo.Redo();
                    else _model.Undo.Undo();
                }
            }, redo ? "redo" : "undo"));

        /// <summary>Serialize the model to <paramref name="target"/>. Engine-only (must run on the dispatcher
        /// thread); deliberately NOT a tracked mutation — saving is not an undoable edit.</summary>
        public void Save(string target, SaveFormat format, SerializeOptions options, bool resetCheckpoint)
        {
            var edits = HasUnsavedEdits;
            _model.Save(target, format, options, resetCheckpoint);
            // A checkpoint-resetting save persisted everything, audit records included. A non-resetting save
            // (deploy_live's temp serialization) keeps the bit — the LOCAL file still hasn't been written.
            if (resetCheckpoint) MarkSavedToDisk(edits);
        }

        /// <summary>How many saves this session has made that WROTE unsaved edits to its files. The Plan reads it to
        /// tell saving the changes from throwing them away: both leave the model clean, and only one is "done". A save
        /// of a model that was already clean (an Undo back to what the files hold, then Save) wrote nobody's changes
        /// and is not counted (verifier round 5, D15).</summary>
        public int SaveCount { get; private set; }

        /// <summary>Mark the in-memory model clean after a temp-tree save has been swapped onto the real path.
        /// Must not run until that swap succeeds, or a failed save would hide unsaved edits.
        /// <paramref name="wroteEdits"/> is <see cref="HasUnsavedEdits"/> read in the dispatcher turn that serialized
        /// what was written, so the count says what that save wrote and not what the model holds now.</summary>
        public void MarkSavedToDisk(bool wroteEdits)
        {
            _model.Undo.SetCheckpoint();
            _auditDirty = false;
            _consentMarkerDirty = false;
            System.Threading.Volatile.Write(ref _consentOnlyRevision, -1);
            if (wroteEdits) SaveCount++;
        }

        /// <summary>Rename via the FormulaFixup contract seam (<see cref="IRenameService"/>): rewrites every DAX /
        /// RLS reference, THEN cascades the rename into every JSON linguistic culture so the LSDL bindings follow the
        /// object to its new name (an un-cascaded rename orphans them, and the next set_synonyms/create_linguistic_schema prunes
        /// the orphan — silently deleting authored synonyms one write later). The single rename seam: rename_object,
        /// Find&amp;Replace's rename branch, apply_plan's rename item, and the property grid's Name writes
        /// (set_property / set_properties) all route through here. Must be called inside a <see cref="MutateAsync"/>
        /// so both the fixup and the LSDL cascade are tracked, broadcast and undoable. The cascade never throws
        /// (see <see cref="LsdlSynonyms.CascadeRename(TabularNamedObject, string, out string)"/>); a culture whose
        /// recommit the live validator refuses is NOT warned about here — it is queued and retried once at batch
        /// end (<see cref="RetryPendingCascades"/>), because a LATER rename in the same batch can remove the very
        /// collision that blocked it. Callers surface survivors via the per-batch sink they pass to MutateAsync.</summary>
        public bool Rename(TabularNamedObject obj, string newName)
        {
            var oldName = obj.Name;
            var changed = _model.Renamer.Rename(obj, newName);
            if (changed)
            {
                if (obj is Table table) RenameMatchingDefaultPartition(table, oldName, newName);
                LsdlSynonyms.CascadeRename(obj, oldName, out var warning);
                if (warning != null) _pendingCascades.Add((obj, oldName));
            }
            return changed;
        }

        // A calculated table's default partition is named after the table. Rename keeps that match so a
        // later save does not leave the old name on disk (D-063). Custom-named extra partitions stay put.
        private static void RenameMatchingDefaultPartition(Table table, string oldName, string newName)
        {
            var match = table.Partitions.FirstOrDefault(p => string.Equals(p.Name, oldName, StringComparison.Ordinal));
            if (match == null) return;
            if (table.Partitions.Any(p => !ReferenceEquals(p, match) && string.Equals(p.Name, newName, StringComparison.Ordinal)))
                return;
            match.Name = newName;
        }

        // Renames whose LSDL cascade a culture refused MID-batch, keyed by the identity the cascade rewrites.
        // Dispatcher-thread-only (populated by Rename, drained by RetryPendingCascades inside the same batch).
        private readonly List<(TabularNamedObject Obj, string OldName)> _pendingCascades = new List<(TabularNamedObject, string)>();

        /// <summary>Batch-end second chance for refused cascades (transient-collision sequencing): in a multi-rename
        /// batch, rename #1's cascade can be refused by a collision that rename #2 later REMOVES — retried after the
        /// whole batch's work, the recommit succeeds and no warning is owed. CascadeRename makes the retry safe:
        /// cultures that already cascaded have no bindings naming the old identity left (nothing rewritten, nothing
        /// recommitted), so only the refused cultures are re-attempted. Runs INSIDE the undo batch (see MutateAsync)
        /// so a retried write stays in the rename's undo step. Survivors go into <paramref name="sink"/> (the caller's
        /// per-batch channel) so each advisory belongs to the exact call that produced it; deposited here, inside the
        /// lease, so it is bound before the awaiting caller resumes. A rename with no sink can't surface warnings.</summary>
        private void RetryPendingCascades(List<CascadeWarning> sink)
        {
            if (_pendingCascades.Count == 0) return;
            var pending = _pendingCascades.ToArray();
            _pendingCascades.Clear();
            foreach (var (obj, oldName) in pending)
            {
                string warning;
                // CascadeRename is non-throwing by contract, but the guard reads obj.Name — an object DELETED later
                // in the same batch throws there. Its entity is then an ordinary deletion orphan (the pruner's job),
                // so the honest report is the neutral consequence, not a crash of the whole batch.
                try { LsdlSynonyms.CascadeRename(obj, oldName, out warning); }
                catch (Exception ex) { warning = $"The linguistic schema could not be re-checked after renaming '{oldName}' ({ex.Message})"; }
                if (warning != null) sink?.Add(new CascadeWarning { Obj = obj, Text = warning });
            }
        }

        /// <summary>The agent-permissions gate for a commit, set by the owning engine. Asked before anything is touched.</summary>
        internal AgentWriteGate AgentWriteGate { get; set; }

        // LEFT OVER (see OnObjectDeleting): no caller passes `fingerprint` any more, so neither flag is ever set. Dispatcher-thread only.

        /// <summary>The sink's own question for an assistant's commit: the ticket in scope for THIS session (the commits made under it
        /// move the revision it may start from), after refusing, with nothing touched, an assistant's commit that no ticket covers
        /// while the policy asks or denies. The sink cannot name the change (it holds only the label), so the request it registers
        /// is never one an approval can be spent on: the way through is the assistant's door, which asks in words made from the
        /// request and mints the ticket.</summary>
        private AssistantTicket ClearAssistantCommit(string origin, string label)
        {
            var scoped = GateContext.Ticket;
            var ticket = scoped != null && (string.IsNullOrEmpty(scoped.SessionId) || string.Equals(scoped.SessionId, Id, StringComparison.Ordinal)) ? scoped : null;
            var gate = AgentWriteGate;
            if (gate == null || !string.Equals(origin, "agent", StringComparison.Ordinal) || GateSeams.Off("commit") || !gate.Asks(origin)) return ticket;
            if (ticket != null && ticket.Covers(AgentCapability.EditLocal, Id)) return ticket;
            throw new InvalidOperationException(gate.Refuse(origin, label));
        }

        private async Task<long> TrackAsync(string origin, string label, Action action, string replay = null)
        {
            // THE ONE PLACE A MODEL EDIT COMMITS, and the sink every assistant edit ends at. An assistant's edit is asked about at
            // the assistant's door (AssistantEngine), before the inner engine sees it, and arrives here carrying the person's
            // approval as a ticket. With no ticket, and a policy that asks (Locked) or denies, it is refused here and nothing is
            // touched: a call that reached this sink by another road fails closed. A person's own edit is never asked (the
            // guard's first rule), and neither is the engine's own (origin "system").
            var ticket = ClearAssistantCommit(origin, label);
            // Tool-call identity (health delta): read the ambient correlation id on the CALLER's thread, BEFORE
            // the dispatcher hop — AsyncLocal need not flow to the dispatcher's dedicated thread (the same
            // capture pattern MutateAsync uses for DryRunScope). Null outside an MCP tool call.
            var correlationId = HealthCorrelation.CurrentId;
            // F-246: who is committing, captured the same way. A tool call of this process's MCP door carries a
            // correlation id; a pipe request carries its connection id (RpcServer sets it around each dispatch).
            var writer = correlationId != null ? ChangeJournal.InProcessReader : ChangeWriter.Current;
            // Contract 8.4: the cause (plan item, run, draft, task) is captured the same way and stamped on THIS commit.
            var cause = ChangeCause.Current;
            using var modelStateLease = await AcquireModelStateLeaseAsync("edited").ConfigureAwait(false);
            return await OnDispatcher(() =>
                {
                    // The approval is for the model as the person saw it: re-checked here, in the turn that commits, so an edit that
                    // landed between the question and this commit is refused, not ridden over.
                    if (ticket != null && ticket.ExpectedRevision >= 0 && Revision != ticket.ExpectedRevision)
                        throw new InvalidOperationException("The open model changed after you approved this, so it was not applied. Ask again.");
                    // Observers see the commit twice: BEFORE the mutation (e.g. the health probe memoizes its
                    // pre-edit baseline on the first tracked commit) and post-commit/pre-broadcast below. One
                    // volatile read; the whole commit sees a stable registration snapshot.
                    var observers = _observers;
                    foreach (var o in observers) { try { o.BeforeCommit(Model); } catch { /* isolated: never fail an edit */ } }
                    _pending = new List<ChangeDelta>();
                    var stepsBefore = _model.Undo.UndoSteps;
                    var sizeBefore = _model.Undo.UndoSize;
                    try
                    {
                        action();
                    }
                    catch
                    {
                        _pending = null;
                        _deleting.Clear();
                        throw;
                    }
                    var deltas = replay == null ? WithAdds(_pending, sizeBefore, builtHere: true) : _pending;
                    _pending = null;
                    _deleting.Clear();
                    var rev = System.Threading.Interlocked.Increment(ref _revision);
                    // A call may commit more than once: its own commits move the revision it may start from, nobody else's.
                    if (ticket != null && ticket.ExpectedRevision >= 0) ticket.ExpectedRevision = rev;
                    // Post-commit, pre-broadcast: an observer's contribution (the health delta) must ride THIS
                    // commit's model/didChange (the human chip) and be stashed for the agent door's tool-result
                    // block. Rehearsals never get here (MutateAsync short-circuits dry-runs to RehearseAsync), so a
                    // dry-run can never reach an observer.
                    HealthDelta health = null;
                    if (observers.Length != 0)
                    {
                        var commit = new SessionCommit
                        {
                            Model = Model, Revision = rev, Origin = origin, Label = label,
                            Deltas = deltas, CorrelationId = correlationId,
                        };
                        foreach (var o in observers) { try { o.AfterCommit(commit); } catch { /* isolated: peers + commit proceed */ } }
                        health = commit.Health;
                    }
                    _bus.Publish(new ChangeNotification
                    {
                        SessionId = Id,
                        Revision = rev,
                        Origin = origin,
                        Label = label,
                        By = ChangeBy.For(cause, origin),
                        Cause = cause,
                        Deltas = deltas.ToArray(),
                        Undo = UndoStateNow(),
                        Health = health,
                        Writer = writer,
                        IsCommit = true,
                        StepsBefore = stepsBefore,
                        Replay = replay,
                    });
                    return Revision;
                }).ConfigureAwait(false);
        }

        public UndoState UndoStateNow() => new UndoState
        {
            CanUndo = _model.Undo.CanUndo,
            CanRedo = _model.Undo.CanRedo,
            AtCheckpoint = _model.Undo.AtCheckpoint,
            Steps = _model.Undo.UndoSteps,
            RedoSteps = _model.Undo.RedoSteps,
        };

        // Dispose runs from three owners — SessionManager.Commit (the replaced session), a failed swap (the
        // never-published replacement), and SessionManager.Dispose — so it must be IDEMPOTENT (a repeat of a
        // completed teardown is a clean no-op) and internally NO-THROW: Commit catches its dispose wholesale, so
        // a throw mid-teardown would silently strand every stage after it — a live ModelDispatcher worker rooting
        // the whole session, and a still-subscribed observer with it. Each stage is guarded independently. The
        // DISPATCHER stage is tracked separately (_dispatcherDown) and RETRIED by any later Dispose call while it
        // has not yet succeeded — a swallowed dispatcher-teardown failure behind a plain one-shot flag would be
        // permanently non-retryable (every later call no-ops), rooting the dead session for the process lifetime.
        private int _disposed;          // one-shot stages have run (event, observers, auth)
        private bool _dispatcherDown;   // guarded by _dispatcherDisposeGate; set ONLY after a SUCCESSFUL teardown
        private readonly object _dispatcherDisposeGate = new object();
        // Test seam: the dispatcher-stage teardown (production: Dispatcher.Dispose). Lets the retry pin inject a
        // first-throw-then-succeed teardown without faking a whole IModelSession. Null in production.
        internal Action DisposeDispatcherForTest;

        public void Dispose()
        {
            var drain = RetireAsync(disposeWhenDrained: true);
            if (drain.IsCompleted) DisposeCore();
        }

        /// <summary>Close operation admission and complete when every already-admitted operation has left.</summary>
        internal Task RetireAsync(bool disposeWhenDrained = false)
        {
            lock (_lifetimeGate)
            {
                System.Threading.Volatile.Write(ref _retired, 1);
                _disposeWhenDrained |= disposeWhenDrained;
                if (_activeOperations == 0) return Task.CompletedTask;
                _drained ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _drained.Task;
            }
        }

        /// <summary>Open admission again after <see cref="RetireAsync"/> when the swap that retired this session was refused before it exchanged
        /// anything (an approval for throwing away unsaved work that no longer matched): the session was never replaced, so it goes on being
        /// the open model. Does nothing for a session that is already disposed or is waiting to be.</summary>
        internal void ReopenAdmission()
        {
            lock (_lifetimeGate)
            {
                if (_disposeWhenDrained || System.Threading.Volatile.Read(ref _disposed) != 0 || _activeOperations != 0) return;
                System.Threading.Volatile.Write(ref _retired, 0);
            }
        }

        internal void DisposeRetired() => DisposeCore();

        private void DisposeCore()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                DisposeDispatcherStage();   // repeat call: retry ONLY a previously-failed dispatcher teardown
                return;
            }
            try
            {
                try { _model.ObjectChanged -= OnObjectChanged; } catch { }
                try { _model.ObjectDeleting -= OnObjectDeleting; _model.ObjectDeleted -= OnObjectDeleted; } catch { }
                // Detach the ambient observers registered on this session (AttachObserver registers on the
                // still-unpublished replacement, so a failed swap's dispose must tear that registration down
                // too) — the probe and whatever it captured must not stay reachable through a dead session.
                try { lock (_observersLock) _observers = Array.Empty<ISessionObserver>(); } catch { }
                // Drop the cached live token + credential (a live secret + its in-memory refresh-token cache) at
                // end-of-life so they become unreachable immediately rather than lingering on the heap until GC —
                // honouring the "only transiently in memory" intent. SessionManager disposes the prior session on
                // every new open, so this runs whenever a model is replaced. _disposed is already set, so the
                // disposal-aware auth setters above cannot repopulate what this clears.
                // Exception-safe ordering: null ALL auth fields under the lock FIRST (capturing the credential
                // into a local), then dispose the capture OUTSIDE the lock in its own swallow — disposing before
                // nulling would exit on a throwing credential Dispose with _liveCredential still set, and since
                // later Dispose calls retry only the dispatcher stage, that credential would be retained forever.
                IDisposable liveCred, agentCred;
                lock (_authLock)
                {
                    _liveToken = default; _liveTokenKey = null;
                    liveCred = _liveCredential as IDisposable;   // most Azure.Identity credentials aren't IDisposable — null here
                    _liveCredential = null; _liveCredentialKey = null;
                    agentCred = _agentCredential as IDisposable;
                    _agentCredential = null; _agentCredentialKey = null;
                }
                // Each slot disposes in its OWN swallow: a throwing human credential must not leave the agent one
                // rooted for the process lifetime (the same exception-safety the single-slot version already had).
                try { liveCred?.Dispose(); }
                catch (Exception ex) { try { Console.Error.WriteLine("[session] disposing the live credential during dispose failed (the fields are already cleared): " + ex.Message); } catch { } }
                try { agentCred?.Dispose(); }
                catch (Exception ex) { try { Console.Error.WriteLine("[session] disposing the agent live credential during dispose failed (the fields are already cleared): " + ex.Message); } catch { } }
            }
            finally { DisposeDispatcherStage(); }   // ALWAYS attempted, whatever an earlier stage did
        }

        private IDisposable AcquireLifetimeLease()
        {
            lock (_lifetimeGate)
            {
                if (_retired != 0)
                    throw new InvalidOperationException(
                        "This model session was replaced or closed before the operation started. Retry against the current model.");
                _activeOperations++;
                return new LifetimeLease(this);
            }
        }

        private async Task<T> WithLifetimeLeaseAsync<T>(Func<Task<T>> operation)
        {
            using var lease = AcquireLifetimeLease();
            return await operation().ConfigureAwait(false);
        }

        private async Task<IDisposable> WithTransferredLifetimeLeaseAsync(Func<Task<IDisposable>> operation)
        {
            var lifetime = AcquireLifetimeLease();
            try
            {
                var inner = await operation().ConfigureAwait(false);
                return new CompositeLease(inner, lifetime);
            }
            catch
            {
                lifetime.Dispose();
                throw;
            }
        }

        private void ReleaseLifetimeLease()
        {
            TaskCompletionSource<bool> drained = null;
            var dispose = false;
            lock (_lifetimeGate)
            {
                if (--_activeOperations != 0) return;
                drained = _drained;
                _drained = null;
                dispose = _disposeWhenDrained;
            }
            drained?.TrySetResult(true);
            if (dispose) DisposeCore();
        }

        private sealed class LifetimeLease : IDisposable
        {
            private Session _owner;
            internal LifetimeLease(Session owner) => _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseLifetimeLease();
        }

        private sealed class CompositeLease : IDisposable
        {
            private IDisposable _inner;
            private IDisposable _lifetime;
            internal CompositeLease(IDisposable inner, IDisposable lifetime) { _inner = inner; _lifetime = lifetime; }
            public void Dispose()
            {
                Interlocked.Exchange(ref _inner, null)?.Dispose();
                Interlocked.Exchange(ref _lifetime, null)?.Dispose();
            }
        }

        private void DisposeDispatcherStage()
        {
            // Locking discipline: the ENTIRE check/invoke/mark-success transition is serialized under
            // _dispatcherDisposeGate. A volatile flag gave visibility, not mutual exclusion — two concurrent
            // Dispose calls could both read it unset and run Dispatcher.Dispose() concurrently (its concurrency
            // safety is unproven). A second entrant waits its turn and re-checks under the lock: no-op when the
            // first succeeded, retry when it failed (the flag stays unset on failure — that IS the retry).
            lock (_dispatcherDisposeGate)
            {
                if (_dispatcherDown) return;
                try
                {
                    (DisposeDispatcherForTest ?? Dispatcher.Dispose)();
                    _dispatcherDown = true;
                }
                catch (Exception ex) { try { Console.Error.WriteLine("[session] dispatcher teardown during dispose failed (a later dispose will retry it): " + ex.Message); } catch { } }
            }
        }
    }
}

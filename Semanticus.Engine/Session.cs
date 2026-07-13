using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Serialization;

namespace Semanticus.Engine
{
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

        public string Id { get; }
        public ModelDispatcher Dispatcher { get; }
        // The TE2 handler now lives behind the IModelSession seam (ModelSession.cs); the engine talks to the
        // interface, never the concrete handler — see Te2ModelSession for why (the Singleton dies here later).
        private readonly IModelSession _model;
        // Settable: a model created from scratch starts with no path; the first save_model(path) anchors it here so
        // a later save_model() (no path) overwrites the same target (matching file-opened session semantics).
        public string SourcePath { get; set; }
        /// <summary>Set once by open_live right after the session is created, to bind it to its live source so
        /// deploy_live can push back without re-supplying the endpoint. Null for file-opened sessions. Holds no
        /// secret (see <see cref="LiveOrigin"/>). Reference assignment is atomic; written on the open path,
        /// read later on the deploy path.</summary>
        public LiveOrigin LiveOrigin { get; set; }

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
        private string _liveCredentialKey;          // "mode|tenant" the credential was built for
        private Azure.Core.AccessToken _liveToken;  // last acquired token (value type; default = none, .Token == null)
        private string _liveTokenKey;

        /// <summary>Return the cached token if it matches <paramref name="key"/> (mode|tenant) and is not within
        /// <paramref name="skew"/> of expiry; otherwise default (its <c>.Token</c> is null) so the caller acquires.</summary>
        public Azure.Core.AccessToken TryReuseLiveToken(string key, TimeSpan skew)
        {
            lock (_authLock)
                return (_liveTokenKey == key && _liveToken.Token != null && _liveToken.ExpiresOn - skew > DateTimeOffset.UtcNow) ? _liveToken : default;
        }

        // The auth setters below are DISPOSAL-AWARE (checked under _authLock): open_live seeds the token +
        // credential AFTER the swap core returns, so a newer open can commit and dispose this session inside
        // that window — a non-aware setter would then RE-populate the dead session with a live secret that the
        // idempotent Dispose never cleans again (held until process exit). Ordering makes the check sound:
        // Dispose sets _disposed BEFORE its auth-clear stage takes _authLock, so a setter that wins the lock
        // first is cleaned by the clear right behind it, and one that loses sees _disposed and drops the offer.

        /// <summary>Cache the last acquired token under its identity key (for the fast-path reuse above).
        /// Dropped without caching on a disposed session.</summary>
        public void CacheLiveToken(string key, Azure.Core.AccessToken token)
        {
            lock (_authLock)
            {
                if (System.Threading.Volatile.Read(ref _disposed) != 0) return;   // dead session: drop the offer
                _liveTokenKey = key; _liveToken = token;
            }
        }

        /// <summary>Get the cached credential for <paramref name="key"/>, building (and caching) it once if absent
        /// or if the identity changed. Reusing the instance is what makes interactive renew silently. THROWS
        /// ObjectDisposedException on a disposed session — a dead session must not manufacture live credentials
        /// at all (an uncached build would have ambiguous ownership: no caller could know to dispose it). The
        /// throw only surfaces in the narrow stale-session race, where refusing is exactly right.</summary>
        public Azure.Core.TokenCredential GetOrBuildLiveCredential(string key, Func<Azure.Core.TokenCredential> build)
        {
            lock (_authLock)
            {
                if (System.Threading.Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(Session), "This model session was replaced; re-run the operation.");
                if (_liveCredential == null || _liveCredentialKey != key) { _liveCredential = build(); _liveCredentialKey = key; }
                return _liveCredential;
            }
        }

        // Test-observable only: whether a live credential is currently retained (the exception-safe-clear pin).
        internal bool HasLiveCredentialForTest { get { lock (_authLock) return _liveCredential != null; } }

        /// <summary>Seed the credential acquired during open so the FIRST deploy/refresh reuses that exact
        /// instance (the one that already prompted) instead of building a fresh, blank-cache one. On a disposed
        /// session the offered credential is disposed immediately, never retained.</summary>
        public void SeedLiveCredential(string key, Azure.Core.TokenCredential cred)
        {
            lock (_authLock)
            {
                if (System.Threading.Volatile.Read(ref _disposed) != 0)
                {
                    try { (cred as IDisposable)?.Dispose(); } catch { }   // dead session: dispose the offer, retain nothing
                    return;
                }
                _liveCredential = cred; _liveCredentialKey = key;
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
        public bool HasUnsavedChanges => _model.HasUnsavedChanges || _auditDirty;

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

        internal async Task<IDisposable> AcquireSourceControlStateLeaseAsync()
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
            => AcquireModelStateLeaseAsync("saved");

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
            _model.Undo.SetCheckpoint(); // freshly opened == clean
        }

        private void OnObjectChanged(object sender, ObjectChangedEventArgs e)
        {
            // Fires synchronously on the dispatcher thread during a tracked op.
            if (_pending == null) return;
            _pending.Add(new ChangeDelta
            {
                Kind = "update",
                Ref = ObjectRefs.For(e.TabularObject),
                Props = e.PropertyName == null ? null : new[] { e.PropertyName }
            });
        }

        public Task<T> ReadAsync<T>(Func<Model, T> work) => Dispatcher.RunAsync(() => work(Model));

        /// <summary>Apply a mutation as one undo step, collect deltas, and broadcast a change notification.</summary>
        public Task<long> MutateAsync(string origin, string label, Action<Model> work)
        {
            // Read the ambient dry-run scope on the CALLER's thread, BEFORE the dispatcher hop — AsyncLocal need
            // not flow to the dispatcher's dedicated thread, so we capture the collector here and never rely on it
            // being visible inside RunAsync. Non-null ⇒ rehearse (RehearseAsync); null ⇒ the real mutation.
            var dry = DryRunScope.Current;
            if (dry != null) return RehearseAsync(dry, label, work);
            return TrackAsync(origin, label, () =>
            {
                _model.BeginUpdate(label);
                try { work(Model); _model.EndUpdate(); }
                catch { _model.EndUpdate(undoable: true, rollback: true); throw; }
            });
        }

        /// <summary>Dry-run variant: run the REAL work on the dispatcher, then ALWAYS roll the batch back — the
        /// vendored EndBatch(rollback) failure path used as the success path (it undoes the whole batch and leaves
        /// NO undo entry, verified). Collect the would-be deltas + label into the scope, DON'T bump <c>_revision</c>,
        /// DON'T <c>_bus.Publish</c>, and return the CURRENT revision unchanged — a rehearsal is invisible to both
        /// doors. On a work exception the batch still rolls back and the exception rethrows, so the caller reports
        /// the failed rehearsal (a dry-run that finds the failure is still a successful rehearsal).</summary>
        private Task<long> RehearseAsync(DryRunCollector dry, string label, Action<Model> work) =>
            Dispatcher.RunAsync(() =>
            {
                _pending = new List<ChangeDelta>();   // the same ObjectChanged collector the real path uses
                try
                {
                    _model.BeginUpdate(label);
                    ChangeDelta[] mutationDeltas;
                    try { work(Model); mutationDeltas = _pending.ToArray(); }
                    finally { _model.EndUpdate(undoable: true, rollback: true); }   // ALWAYS undo — success OR exception
                    // The snapshot is taken BEFORE the rollback: reverting re-fires ObjectChanged for every undone
                    // property (that's how a real undo broadcasts its deltas), so snapshotting after would report
                    // each edit twice — forward and reversed. The report must carry what the op WOULD have broadcast.
                    dry.Deltas.AddRange(mutationDeltas);
                    dry.MutationLabels.Add(label);
                }
                finally { _pending = null; }
                return Revision;
            });

        public Task<long> UndoAsync(string origin) => TrackAsync(origin, "undo", () => _model.Undo.Undo());
        public Task<long> RedoAsync(string origin) => TrackAsync(origin, "redo", () => _model.Undo.Redo());

        /// <summary>Serialize the model to <paramref name="target"/>. Engine-only (must run on the dispatcher
        /// thread); deliberately NOT a tracked mutation — saving is not an undoable edit.</summary>
        public void Save(string target, SaveFormat format, SerializeOptions options, bool resetCheckpoint)
        {
            _model.Save(target, format, options, resetCheckpoint);
            // A checkpoint-resetting save persisted everything, audit records included. A non-resetting save
            // (deploy_live's temp serialization) keeps the bit — the LOCAL file still hasn't been written.
            if (resetCheckpoint) _auditDirty = false;
        }

        /// <summary>Rename via the FormulaFixup contract seam (<see cref="IRenameService"/>): rewrites every DAX /
        /// RLS reference. Must be called inside a <see cref="MutateAsync"/> so it is tracked and broadcast.</summary>
        public bool Rename(TabularNamedObject obj, string newName) => _model.Renamer.Rename(obj, newName);

        private async Task<long> TrackAsync(string origin, string label, Action action)
        {
            // Tool-call identity (health delta): read the ambient correlation id on the CALLER's thread, BEFORE
            // the dispatcher hop — AsyncLocal need not flow to the dispatcher's dedicated thread (the same
            // capture pattern MutateAsync uses for DryRunScope). Null outside an MCP tool call.
            var correlationId = HealthCorrelation.CurrentId;
            using var modelStateLease = await AcquireModelStateLeaseAsync("edited").ConfigureAwait(false);
            return await Dispatcher.RunAsync(() =>
                {
                    // Observers see the commit twice: BEFORE the mutation (e.g. the health probe memoizes its
                    // pre-edit baseline on the first tracked commit) and post-commit/pre-broadcast below. One
                    // volatile read; the whole commit sees a stable registration snapshot.
                    var observers = _observers;
                    foreach (var o in observers) { try { o.BeforeCommit(Model); } catch { /* isolated: never fail an edit */ } }
                    _pending = new List<ChangeDelta>();
                    try
                    {
                        action();
                    }
                    catch
                    {
                        _pending = null;
                        throw;
                    }
                    var deltas = _pending;
                    _pending = null;
                    var rev = System.Threading.Interlocked.Increment(ref _revision);
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
                        Deltas = deltas.ToArray(),
                        Undo = UndoStateNow(),
                        Health = health
                    });
                    return Revision;
                }).ConfigureAwait(false);
        }

        public UndoState UndoStateNow() => new UndoState
        {
            CanUndo = _model.Undo.CanUndo,
            CanRedo = _model.Undo.CanRedo,
            AtCheckpoint = _model.Undo.AtCheckpoint
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
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                DisposeDispatcherStage();   // repeat call: retry ONLY a previously-failed dispatcher teardown
                return;
            }
            try
            {
                try { _model.ObjectChanged -= OnObjectChanged; } catch { }
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
                IDisposable liveCred;
                lock (_authLock)
                {
                    _liveToken = default; _liveTokenKey = null;
                    liveCred = _liveCredential as IDisposable;   // most Azure.Identity credentials aren't IDisposable — null here
                    _liveCredential = null; _liveCredentialKey = null;
                }
                try { liveCred?.Dispose(); }
                catch (Exception ex) { try { Console.Error.WriteLine("[session] disposing the live credential during dispose failed (the fields are already cleared): " + ex.Message); } catch { } }
            }
            finally { DisposeDispatcherStage(); }   // ALWAYS attempted, whatever an earlier stage did
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

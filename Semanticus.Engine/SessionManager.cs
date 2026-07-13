using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// Owns the (single, for M1) live <see cref="Session"/> and the shared <see cref="ChangeBus"/>.
    /// De-static-ing to multiple concurrent sessions is an M5 concern; for now one model per process.
    /// </summary>
    public sealed class SessionManager : IDisposable
    {
        private int _counter;
        // volatile: Current is published from the lifecycle path and read from every request thread — without
        // the fence a request thread could keep seeing the stale session after a swap. Post-swap code must use
        // the session RETURNED by Publish (its local), never a re-read of Current.
        private volatile Session _current;

        public ChangeBus Bus { get; } = new ChangeBus();
        public Session Current => _current;

        public SessionManager()
        {
            // The bus stamps every activity publish with the session current AT EMIT (finding: direct
            // Bus.PublishActivity emitters bypassed LocalEngine.PublishActivityAsync's stamp, so a model swap
            // between emit and the tee's handling could attribute the record to the wrong model).
            Bus.ActivitySessionId = () => Current?.Id;
        }

        /// <summary>Set by the owning <see cref="LocalEngine"/>: builds the ambient commit observer registered on
        /// each NEW session — today the health-delta probe (feature #4), which is installed on EVERY session and
        /// reads the entitlement LAZILY per commit (the SOFT Pro gate — free costs one bool check, never a
        /// throw), so a license activated mid-session starts reporting on the next edit without a reopen.
        /// Further ambient probes register straight on <see cref="Session.RegisterObserver"/>.</summary>
        public Func<Session, ISessionObserver> ObserverFactory { get; set; }

        public async Task<Session> OpenAsync(string path) => Publish(await BuildOpenAsync(path));

        /// <summary>Create a brand-new, EMPTY model from scratch (no file on disk) and make it the current session.
        /// Mirrors <see cref="OpenAsync"/> but builds the handler with the empty/compat-level constructor on the
        /// single-writer thread. The session has no <see cref="Session.SourcePath"/> until the first save.</summary>
        public async Task<Session> CreateAsync(string name, int compatibilityLevel) => Publish(await BuildCreateAsync(name, compatibilityLevel));

        /// <summary>Build a fully-loaded replacement session WITHOUT touching <see cref="Current"/> — a bad path or
        /// parse failure must leave the live session (and its unsaved work) fully intact. The split from
        /// <see cref="Publish"/> lets the engine invalidate its model-scoped state BETWEEN build-success and
        /// publication, so no reader can pair the NEW session with the OLD model's state.</summary>
        internal async Task<Session> BuildOpenAsync(string path)
        {
            // Normalise a modern TMDL PBIP entry point (.pbip / project folder / .SemanticModel folder) to its inner
            // 'definition' TMDL root the vendored TE2 loader can actually open; other formats pass through unchanged.
            var full = ModelPathResolver.Resolve(Path.GetFullPath(path));
            var dispatcher = new ModelDispatcher();
            try
            {
                // The handler MUST be built on the single-writer thread (its ctor sets the process-wide Singleton and
                // builds the wrapper graph there). The TE2 handler now lives behind the IModelSession seam, so the
                // concrete type + its build settings are owned by Te2ModelSession.Open (ModelSession.cs), not here.
                IModelSession model = await dispatcher.RunAsync(() => (IModelSession)Te2ModelSession.Open(full));
                var id = "s" + Interlocked.Increment(ref _counter);
                return new Session(id, dispatcher, model, Bus, full);
            }
            catch
            {
                // The replacement never came to life — dispose the freshly-created dispatcher so its worker thread
                // doesn't leak, and leave Current (the still-usable previous session) untouched.
                dispatcher.Dispose();
                throw;
            }
        }

        /// <summary>Build (don't publish) a brand-new empty model — see <see cref="BuildOpenAsync"/>.</summary>
        internal async Task<Session> BuildCreateAsync(string name, int compatibilityLevel)
        {
            var dispatcher = new ModelDispatcher();
            try
            {
                // Built on the single-writer thread; the concrete handler + its empty-model build settings are owned
                // by Te2ModelSession.Create (ModelSession.cs) behind the seam, mirroring BuildOpenAsync.
                IModelSession model = await dispatcher.RunAsync(() => (IModelSession)Te2ModelSession.Create(name, compatibilityLevel));
                var id = "s" + Interlocked.Increment(ref _counter);
                return new Session(id, dispatcher, model, Bus, null);    // unsaved: no source path yet
            }
            catch
            {
                dispatcher.Dispose();       // don't leak the worker thread of a model that never came to life
                throw;
            }
        }

        /// <summary>The FALLIBLE half of publication: create + register the ambient observer on the still-UNPUBLISHED
        /// session. Runs BEFORE any model-scoped invalidation (LocalEngine step c) so a factory/registration failure
        /// leaves the old session — and its stores AND its live connection — fully intact. Deliberately does NOT
        /// dispose <paramref name="next"/>: the caller owns exactly-once disposal of a session that won't publish.</summary>
        internal void AttachObserver(Session next)
        {
            var observer = ObserverFactory?.Invoke(next);
            if (observer != null) next.RegisterObserver(observer);
        }

        /// <summary>The NO-THROW half of publication: ONLY the volatile flip + the caught old-session disposal.
        /// Everything fallible (build, result read, observer setup) has already run; the engine's model-scoped
        /// invalidation sits immediately before this call with no fallible step in between (invalidate-before-
        /// publish), so no reader can ever pair the NEW session with the OLD model's state. Disposing the replaced
        /// session must never abort the already-committed swap — caught and logged.</summary>
        internal Session Commit(Session next)
        {
            var previous = _current;
            _current = next;                // atomic + volatile: the LAST step of the swap
            try { previous?.Dispose(); }    // only NOW is the old session (and its dispatcher) torn down
            catch (Exception ex)
            {
                try { Console.Error.WriteLine("[session] disposing the replaced session failed (the swap itself is committed): " + ex.Message); } catch { }
            }
            return next;
        }

        // Compose for direct SessionManager callers (tests/smokes) — LocalEngine calls the two halves separately,
        // with its model-scoped invalidation between them (SwapSessionCoreAsync).
        private Session Publish(Session next)
        {
            try { AttachObserver(next); }
            catch { next.Dispose(); throw; }
            return Commit(next);
        }

        public Session Require() =>
            Current ?? throw new InvalidOperationException("No model is open. Call 'open' first.");

        public void Dispose() => Current?.Dispose();
    }
}

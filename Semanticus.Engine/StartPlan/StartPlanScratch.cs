// The hidden scratch model a start plan decides on: a TMDL clone of the person's actual (empty) model, opened as a
// SEPARATE session with its OWN ChangeBus, and never published. Because the bus is private, a commit on the scratch
// raises nothing on SessionManager.Bus: no health probe, no ledger row, no change journal entry, no live event for the
// window. The person's model is not touched by anything that happens here.
//
// SEEDED FROM THE PERSON'S MODEL, NOT BUILT FRESH, so the scratch carries whatever the model carries (the consent
// marker above all), and a projection compared later sees the same starting bytes.
//
// THE WRAPPER HAZARD (design S6). TE2's handler sets a process-wide Singleton when it is constructed, so building a
// scratch while another model is being constructed can bind new objects to the wrong handler. WrapperGate closes
// that, and CloneAsync needs nothing of its own for it: Te2ModelSession.Open builds the handler inside
// WrapperGate.Construct, and every later work item on the scratch's Session runs pinned to its own handler
// (Session.OnDispatcher). The engine also builds a scratch while holding the start-plan gate, after the swap hook
// has parked the plan, but that is about plan state, not the static.

using System;
using System.IO;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Serialization;

namespace Semanticus.Engine.StartPlan
{
    internal sealed class StartPlanScratch : IDisposable
    {
        private int _disposed;

        /// <summary>This scratch's own bus. Nothing else subscribes to it, and nothing here ever publishes to the manager's.</summary>
        public ChangeBus Bus { get; }
        public Session Session { get; }
        public string Dir { get; }

        private StartPlanScratch(ChangeBus bus, Session session, string dir)
        {
            Bus = bus;
            Session = session;
            Dir = dir;
        }

        /// <summary>Serialize <paramref name="baseline"/> to TMDL under <paramref name="dir"/> and open that copy as a
        /// private session. <paramref name="dir"/> is the engine's own scratch folder (StartPlanStore.ScratchDirFor), so
        /// a stale one from a crashed run is replaced.</summary>
        public static async Task<StartPlanScratch> CloneAsync(Session baseline, string dir)
        {
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("A scratch folder is required.", nameof(dir));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var definition = Path.Combine(dir, "definition");
            Directory.CreateDirectory(definition);
            ModelDispatcher dispatcher = null;
            try
            {
                // Not a tracked mutation and not a checkpoint reset: the person's model stays exactly as dirty as it was.
                await baseline.RunAsync(() =>
                {
                    baseline.Save(definition, SaveFormat.TMDL, SerializeOptions.Default, resetCheckpoint: false);
                    return true;
                }).ConfigureAwait(false);
                if (!ModelPathResolver.IsTmdlRoot(definition))
                    throw new InvalidOperationException("The model could not be copied for planning.");
                dispatcher = new ModelDispatcher();
                var model = await dispatcher.RunAsync(() => (IModelSession)Te2ModelSession.Open(definition)).ConfigureAwait(false);
                var bus = new ChangeBus();
                return new StartPlanScratch(bus, new Session("x" + Guid.NewGuid().ToString("N"), dispatcher, model, bus, definition), dir);
            }
            catch
            {
                try { dispatcher?.Dispose(); } catch { }
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                throw;
            }
        }

        /// <summary>Close admission, wait for work already running to leave, then dispose and delete the clone.</summary>
        public async Task DisposeAsync()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await Session.RetireAsync().ConfigureAwait(false); } catch { }
            try { Session.Dispose(); } catch { }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
        }

        public void Dispose() => DisposeAsync().GetAwaiter().GetResult();
    }
}

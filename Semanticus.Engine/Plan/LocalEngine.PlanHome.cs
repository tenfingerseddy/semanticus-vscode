using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Semanticus.Engine
{
    // What the person decided on the Plan for a published model that has no folder (UAT F17: Who does it > Me came back as the assistant's row after File > Open Folder).
    // Such a model keeps its tasks and decisions in the engine's memory, which a new engine does not have. They are also kept in the per-user home, keyed by the model
    // (the same plan-memory shape and file code as the folder's), read back when the model is opened again, and moved into the folder's plan memory when the model gets one.
    public sealed partial class LocalEngine
    {
        private readonly ConditionalWeakTable<SessionContext, object> _homeHeldRead = new ConditionalWeakTable<SessionContext, object>();
        private readonly ConditionalWeakTable<SessionContext, object> _homeHeldMoved = new ConditionalWeakTable<SessionContext, object>();

        private static string HomePlanFile()
        {
            try { return PlanMemory.FileIn(Path.Combine(SemanticusHome.Root(), "plan")); }
            catch { return null; }   // no per-user folder: nothing to keep it in, which is where it was
        }

        /// <summary>The model's identity for the home copy, or null when the model is one only this launch can name (an unsaved one).</summary>
        private static string HomePlanIdentity(PlanMemoryBinding bound)
        {
            var id = ModelIdentityOf(bound.Session);
            return string.IsNullOrEmpty(id) || id.StartsWith("unsaved:", StringComparison.Ordinal) ? null : id;
        }

        /// <summary>The session-held plan memory of a model with nowhere to keep it, filled once from the home copy.</summary>
        private PlanMemoryState HeldPlanState(PlanMemoryBinding bound)
        {
            var held = _sessionPlanTasks.GetOrCreateValue(bound.Context);
            if (_homeHeldRead.TryGetValue(bound.Context, out _)) return held;
            lock (held)
            {
                if (_homeHeldRead.TryGetValue(bound.Context, out _)) return held;
                _homeHeldRead.Add(bound.Context, new object());
                var file = HomePlanFile(); var id = HomePlanIdentity(bound);
                if (file == null || id == null) return held;
                var home = PlanMemory.Read(file, id);
                if (held.Tasks.Count == 0) held.Tasks = Copy(home.Tasks);
                foreach (var pair in home.Residue) if (!held.Residue.ContainsKey(pair.Key)) held.Residue[pair.Key] = Copy(pair.Value);
                if (held.ClearedDone.Count == 0) held.ClearedDone = new List<string>(home.ClearedDone);
                if (held.AssistantQueue == PlanAssistantQueue.On) held.AssistantQueue = home.AssistantQueue;
            }
            return held;
        }

        /// <summary>Keep the person's change in the home copy too. Best effort: the session still holds it.</summary>
        private void KeepHeldInHome(PlanMemoryBinding bound, PlanMemoryState held)
        {
            var file = HomePlanFile(); var id = HomePlanIdentity(bound);
            if (file == null || id == null) return;
            List<PlanTask> tasks; Dictionary<string, PlanRowResidue> residue; string queue; List<string> cleared;
            lock (held)
            {
                tasks = Copy(held.Tasks); queue = held.AssistantQueue; cleared = new List<string>(held.ClearedDone);
                residue = held.Residue.ToDictionary(p => p.Key, p => Copy(p.Value), StringComparer.Ordinal);
            }
            try { PlanMemory.Mutate(file, id, state => { state.Tasks = tasks; state.Residue = residue; state.AssistantQueue = queue; state.ClearedDone = cleared; }); }
            catch { /* the session copy is still right */ }
        }

        /// <summary>The model now has a folder: what was kept for it in the home moves into the folder's plan memory, once, and leaves the home.</summary>
        private bool MoveHomeIntoFolder(PlanMemoryBinding bound)
        {
            if (_homeHeldMoved.TryGetValue(bound.Context, out _)) return false;
            _homeHeldMoved.Add(bound.Context, new object());
            var homeFile = HomePlanFile(); var id = HomePlanIdentity(bound);
            if (homeFile == null || id == null || !File.Exists(homeFile)) return false;
            var home = PlanMemory.Read(homeFile, id);
            if (home.Tasks.Count == 0 && home.Residue.Count == 0 && home.ClearedDone.Count == 0 && home.AssistantQueue == PlanAssistantQueue.On) return false;
            try
            {
                if (!PlanMemory.Mutate(bound.File, bound.Identity, file =>
                {
                    foreach (var t in home.Tasks) if (file.FindTask(t.Id) == null) file.Tasks.Add(Copy(t));
                    foreach (var pair in home.Residue) if (!file.Residue.ContainsKey(pair.Key)) file.Residue[pair.Key] = Copy(pair.Value);
                    foreach (var done in home.ClearedDone) if (!file.ClearedDone.Contains(done)) file.ClearedDone.Add(done);
                    if (file.AssistantQueue == PlanAssistantQueue.On) file.AssistantQueue = home.AssistantQueue;
                })) return false;
                PlanMemory.Mutate(homeFile, id, state => { state.Tasks = new List<PlanTask>(); state.Residue = new Dictionary<string, PlanRowResidue>(StringComparer.Ordinal); state.ClearedDone = new List<string>(); state.AssistantQueue = PlanAssistantQueue.On; });
                return true;
            }
            catch { return false; /* the home copy stays; a later open moves it */ }
        }

        private static List<PlanTask> Copy(List<PlanTask> tasks) =>
            JsonSerializer.Deserialize<List<PlanTask>>(JsonSerializer.Serialize(tasks, TaskCopy), TaskCopy);
        private static PlanTask Copy(PlanTask task) => JsonSerializer.Deserialize<PlanTask>(JsonSerializer.Serialize(task, TaskCopy), TaskCopy);
        private static PlanRowResidue Copy(PlanRowResidue r) =>
            new PlanRowResidue { Held = r.Held, Doer = r.Doer, Note = r.Note, AssistantNote = r.AssistantNote, Absent = r.Absent };
    }
}

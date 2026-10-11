// Edits made during a guided workflow run are filed under the run (Plan-heart contract 8.4, lane m11/plan-gaps).
//
// A guided run moves step by step, and the work of a step is done BETWEEN submissions: the person edits in Studio,
// or the assistant calls ordinary tools, then submits the step. Only the submission itself opened the run's cause
// scope, so everything the step asked for landed in History as a loose edit with no run. The door now stamps the run
// the same way it stamps a claimed task: per reader, from explicit state that reader set by starting or submitting
// the run, never inferred afterwards.
//
// WHO IS "THAT READER". The reader that started the run, or last submitted a step of it: the writer id a commit is
// stamped with ("mcp" for this process's MCP door, a pipe connection's id otherwise). Another reader's edits are not
// the run's, even while it is active: the person editing beside an assistant's run did not start it.
//
// A run beats a task. An assistant working a claimed task that starts a guided run is doing the task through the
// run; the run is the narrower cause, and its report already sits in History under the run's own row.
//
// HONEST COST. The pointer names one run per reader; a reader that starts a second run files its edits under the
// newer one. An explicit scope opened deeper (a plan approve, a step's own direct writes) still wins, because it is
// the inner scope.

using System;
using System.Collections.Concurrent;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Reader to the guided run it has on a step, and the context that run lives in.</summary>
        private readonly ConcurrentDictionary<string, (SessionContext Context, string RunId)> _runOnStep =
            new ConcurrentDictionary<string, (SessionContext, string)>(StringComparer.Ordinal);

        /// <summary>The reader of the call running now, spelled as Session.TrackAsync spells a commit's writer.</summary>
        private static string CurrentReader() =>
            HealthCorrelation.CurrentId != null ? ChangeJournal.InProcessReader : ChangeWriter.Current;

        /// <summary>Called when a reader starts a run or submits one of its steps.</summary>
        private void NoteRunOnStep(SessionContext context, string runId)
        {
            var reader = CurrentReader();
            if (reader != null && context != null && !string.IsNullOrEmpty(runId)) _runOnStep[reader] = (context, runId);
        }

        /// <summary>The run cause for this reader's next commit, or null when it has no active run on a step in the
        /// model open now. A finished or aborted run clears the pointer.</summary>
        internal ChangeCauseInfo RunCauseFor(string reader)
        {
            if (reader == null || !_runOnStep.TryGetValue(reader, out var on)) return null;
            try
            {
                if (!ReferenceEquals(on.Context, _sessions.CurrentContext)) return null;
                var run = on.Context.WorkflowRuns.Get(on.RunId);
                if (run == null || run.Status != "active")
                {
                    _runOnStep.TryRemove(new System.Collections.Generic.KeyValuePair<string, (SessionContext, string)>(reader, on));
                    return null;
                }
                if (run.CurrentStep == null) return null;
                return new ChangeCauseInfo { Kind = ChangeCauseKinds.WorkflowRun, Ids = new[] { run.RunId }, Title = run.Def?.Title ?? run.Def?.Name };
            }
            catch { return null; }   // the run store moved under us: this commit simply files under no run
        }
    }
}

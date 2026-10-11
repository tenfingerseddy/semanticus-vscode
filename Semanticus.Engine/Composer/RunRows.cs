// A guided workflow run as a Plan row (Plan-heart contract 1.3 and 14; composer contract 4.3).
//
// "Guided run rows get the Plan's state words." A started workflow is a row "run/<wfr id>" while it runs, carrying
// the Plan's word for who it waits on: Needs you while the current step waits for a person's answer, On it (step N
// of M) while the assistant is doing the step. It leaves the Plan when it ends and lives on as a History entry (the
// run report snapshot, lane E4).
//
// READ WITHOUT THE WORKFLOW GATE. The run store is serialized by the workflow gate, and the Plan's compose runs
// under the Plan's own gate; taking one inside the other is the lock order this code base has avoided everywhere.
// So every published run view (PublishWorkflowRun: start, submit, skip, abort) is also noted here, and the compose
// reads the last one noted. A row read a moment early is corrected on the next call, like every other Plan row.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        internal const string RunRowPrefix = "run/";

        private readonly ConditionalWeakTable<SessionContext, ConcurrentDictionary<string, WorkflowRunView>> _runsForPlan =
            new ConditionalWeakTable<SessionContext, ConcurrentDictionary<string, WorkflowRunView>>();

        private void NoteRunForPlan(SessionContext context, WorkflowRunView view)
        {
            if (context == null || view?.RunId == null) return;
            var runs = _runsForPlan.GetOrCreateValue(context);
            if (view.Status == "active") runs[view.RunId] = view;
            else runs.TryRemove(view.RunId, out _);
        }

        internal static bool IsRunRow(string id) => id != null && id.StartsWith(RunRowPrefix, StringComparison.Ordinal);

        /// <summary>One row per active guided run of this model, oldest first.</summary>
        internal List<ModelPlanItem> RunPlanRows(PlanMemoryBinding bound)
        {
            if (bound?.Context == null || !_runsForPlan.TryGetValue(bound.Context, out var runs)) return new List<ModelPlanItem>();
            // A draft's guided sub-run is part of the draft, whose own row carries it (lane l9-finish).
            return runs.Values.Where(v => DraftOfGuidedRun(v.RunId) == null).OrderBy(v => v.StartedUtc, StringComparer.Ordinal).Select(v =>
            {
                var (word, note, doer) = WorkflowRunner.PlanWordOf(v);
                return new ModelPlanItem
                {
                    Id = RunRowPrefix + v.RunId,
                    Section = ModelPlanSections.Structure,
                    Title = string.IsNullOrWhiteSpace(v.Title) ? v.Workflow : v.Title,
                    Sub = v.CurrentStep?.Title ?? string.Empty,
                    Value = string.Empty,
                    Who = PlanTasks.WhoOf(doer),
                    Status = ModelPlanStatuses.Running,
                    State = ModelPlanItemStates.Gap,
                    Why = new[] { "The workflow " + v.Workflow + " is running. It ends as an entry in History." },
                    Diff = Array.Empty<string>(),
                    Touches = v.TotalSteps == 1 ? "1 step" : v.TotalSteps.ToString(CultureInfo.InvariantCulture) + " steps",
                    Tables = Array.Empty<ModelPlanItemTable>(),
                    Source = PlanSources.Workflow,
                    Doer = doer,
                    DoneWhen = PlanDoneWhen.ModelCheck,
                    DoneText = "Done when the workflow's last step passes.",
                    Objects = Array.Empty<string>(),
                    OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Run, Ref = v.RunId },
                    StateWord = word,
                    StateNote = note,
                };
            }).ToList();
        }
    }
}

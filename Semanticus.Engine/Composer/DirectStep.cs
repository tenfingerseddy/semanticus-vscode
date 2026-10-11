// A direct assistant step is one undo entry (composer contract 3.4, Sol 24 Sep).
//
// In a format-3 workflow a `performer: assistant` step may change the model. Made by ordinary op calls, three
// writes would be three undo entries. Instead the assistant submits the step's writes with submit_workflow_step
// (writes = an ordered list of {op, args} from the composer's list), and the engine applies them through the shared
// appliers in ONE MutateAsync labelled with the step's title: one step, one undo entry, filed under the run.
//
// THE ORDER (Sol 6, slice 8, finding 4). A refused submission must leave the model as it was, so the writes land
// last among everything that does not need them:
//   1. screened, outside any gate: each op is on the composer's list, and none sets who can see which data;
//   2. under the run's gate, which is then held to the end of the submission, so the step cannot move between its
//      checks and its commit: the run and step are the ones named, the writes rehearse cleanly (every applier runs,
//      then it all rolls back), and the step's input gate passes against the model as the writes WOULD leave it;
//   3. committed as one change, still under the gate;
//   4. the ordinary submission then runs the step's verify against the model as the writes left it. A refusal from
//      it takes the writes back (Session.RetractAsync), so the model is as it was and nothing is left to redo.
//      What the writes record goes with them (Codex on pull request 395): the verified-edit trail is append-only,
//      so its records wait for the step to be accepted; the workflow-authored hashes are written at the commit,
//      because this step's own verify reads them, and a take back puts back what each replaced (DirectStepAudit).
// The binding hook and the drift hook read the runs under the same gate; the commit tells them the gate is held,
// because the gate is not re-entrant.
//
// ROW FILTERS NEVER RIDE A DIRECT STEP (Sol 6, slice 8, finding 1). Composer contract 5.4 holds every
// set_table_permission for the person's own Approve and run. A direct step has no such approval, so it is refused
// at step 1, and ApplyComposedChangeAsync refuses any row filter no person approved in case another caller forgets.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine.Composer;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Step 1: the writes as composed actions, screened. Null when there are none.</summary>
        private static List<ComposedAction> DirectStepWrites(string writesJson)
        {
            List<ComposeActionInput> writes;
            try { writes = JsonSerializer.Deserialize<List<ComposeActionInput>>(writesJson, ComposeJson); }
            catch (JsonException ex) { throw new ArgumentException("writes must be a JSON list of {op, args}: " + ex.Message, nameof(writesJson)); }
            if (writes == null || writes.Count == 0) return null;
            foreach (var w in writes)
            {
                if (string.IsNullOrWhiteSpace(w.Op) || !ComposedOps.Kinds.ContainsKey(w.Op) || w.Op == "starter_measure")
                    throw new ArgumentException("'" + w.Op + "' is not one of the writes a step can make as one change. Use the change ops "
                        + "(create_measure, set_description, set_column_data_type and the rest of the composer's list), or make other edits with their own ops.", nameof(writesJson));
                if (ComposedOps.HeldForPerson(w.Op))
                    throw new InvalidOperationException(DraftCopy.DirectStepRowSecurity);
            }
            return writes.Select(w =>
            {
                var a = new ComposedAction { Op = w.Op.Trim() };
                foreach (var kv in w.Args ?? new Dictionary<string, JsonElement>())
                    a.Args[kv.Key] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : kv.Value.GetRawText();
                return a;
            }).ToList();
        }

        /// <summary>Steps 2 and 3, called with the run's workflow gate held: check, rehearse, then commit the writes
        /// as one change. Returns the revision of the commit, or 0 when it changed nothing. Throws with nothing
        /// written on any refusal.</summary>
        private async Task<long> CommitDirectStepWritesAsync(SessionContext context, WorkflowRunState run, string stepId,
            Dictionary<string, AnswerValue> answers, List<ComposedAction> writes, string origin, DirectStepAudit direct)
        {
            var step = run.CurrentStep ?? throw new InvalidOperationException("This run has no current step to submit writes for. Nothing was written.");
            if (!string.IsNullOrWhiteSpace(stepId) && !string.Equals(step.Id, stepId.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException($"The run is on step '{step.Id}', not '{stepId}'. Nothing was written.");
            if (run.Def.SchemaVersion < 3)
                throw new InvalidOperationException("Writes as one change are for format-3 workflows. In this workflow, make the edits with their own ops, then submit the step.");
            if (step.Performer != WorkflowPerformers.Assistant)
                throw new InvalidOperationException("Only a step your assistant does can submit writes. Nothing was written.");
            var s = context.Session ?? throw new InvalidOperationException("No model is open. Nothing was written.");

            var title = string.IsNullOrWhiteSpace(step.Title) ? step.Id : step.Title;
            var change = new ComposedChange { Title = title, Workflow = run.Def.Name };
            var composed = new ComposedStep { Id = step.Id, Title = title };
            composed.Actions.AddRange(writes);
            change.Steps.Add(composed);

            // The rehearsal: every write applies, then all of it rolls back. The model inventory the input policies
            // read is taken from the rehearsed model, so a formula naming a measure these writes create is judged
            // against the model it will actually meet.
            var compiled = ComposedChangeCompiler.Compile(change, BlankFill.Require);
            var rehearsed = await RehearseComposedAsync(s, compiled.Steps, m => (
                m.Tables.SelectMany(t => t.Measures).Select(mm => mm.Name).ToArray(),
                m.Tables.SelectMany(t => t.Columns.Select(c =>
                    (t.Name, c.Name, c is CalculatedColumn || c is CalculatedTableColumn || t is CalculatedTable))).ToArray(),
                m.Functions.Select(f => f.Name).ToArray(),
                m.Tables.Select(t => (t.Name, t is CalculatedTable)).ToArray())).ConfigureAwait(false);
            if (!rehearsed.Ok) throw new InvalidOperationException("Nothing was written. " + rehearsed.Problem);
            var (measures, columns, functions, tables) = rehearsed.Value;
            if (!WorkflowRunner.PreflightSubmission(run, stepId, answers, measures, columns, functions, tables))
                throw new InvalidOperationException("This step only sets up the list it repeats over, so it takes no writes. Submit it without writes first. Nothing was written.");

            // The commit, with the gate still held: the step checked above is the step the writes land on.
            var result = await ApplyComposedChangeAsync(change, origin, workflowGateHeld: true, direct).ConfigureAwait(false);
            if (result.Outcome != ComposedOutcome.Applied)
                throw new InvalidOperationException(result.Reason ?? "Nothing changed.");
            return result.Changed ? result.Revision : 0;
        }

        /// <summary>Step 4's take back: the submission was refused after its writes landed. Null when they were taken
        /// back; otherwise the sentence saying they stayed, because another edit landed after them.</summary>
        private async Task<string> TakeBackDirectStepAsync(long revision, string origin)
        {
            var s = _sessions.Current;
            if (s == null) return DraftCopy.DirectStepKept;
            try { await s.RetractAsync(origin, "Took back a refused step", revision).ConfigureAwait(false); return null; }
            catch (Exception) { return DraftCopy.DirectStepKept; }
        }

        /// <summary>The refusal, plus the sentence that its writes stayed. Keeps the refusal's own data (the gate's
        /// problems and display sentence) so each door still reads it as it would have.</summary>
        private static Exception DirectStepKept(Exception refused, string kept)
        {
            var ex = new InvalidOperationException(refused.Message + " " + kept, refused);
            foreach (System.Collections.DictionaryEntry e in refused.Data) ex.Data[e.Key] = e.Value;
            return ex;
        }
    }
}

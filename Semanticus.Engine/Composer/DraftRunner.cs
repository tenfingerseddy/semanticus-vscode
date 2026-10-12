// The draft's life: compose, ask, check, run (composer contract sections 3 to 6 and 9; Plan-heart contract 14).
//
// THE ORDER OF EVENTS IS THE WHOLE DESIGN.
//   1. compose_task makes a draft: steps, the questions the person answers, and a blank for every piece of text
//      neither Semanticus nor Jev can write. Nothing touches the model.
//   2. The blanks go on the assistant's queue as Plan tasks (lane E3's PlanTaskQueue.PostAsync), so a blank has an
//      owner record whether or not an assistant is attached, and any MCP assistant finds it on its next call.
//   3. complete_request on a blank runs the blank's written checks against a rehearsal of the draft (lane L3). A
//      failure keeps the claim and counts a try; the third closes the request and the blank waits for the person.
//      A decline hands the blank to the person at once, with the choices that resolve to real objects.
//   4. When the draft is approved and every blank has passed, the change runs by itself: ONE MutateAsync, one undo
//      entry, filed under the draft as its cause. Then the steps after the change, and the checks after it.
//
// HELD DRAFTS. A kept row filter, or a blank in a draft that publishes after the change, still passes its checks by
// itself but never runs by itself: the draft waits for the person's Approve and run, which only the human-only
// target can give. run_draft refuses a held draft on every door. That approval counts only once every part has
// passed, so the person has read the words they approve, and it stands only for those words: it is kept with a
// digest of the content it covers, and any change to that content clears it (Sol 6, slice 8, finding 2).
//
// WHERE THE STATE LIVES. The draft owns its blanks; the Plan task owns the request. A blank that is queued or being
// written is read off its task when the view is built, so a claim, a release after a restart, or a set aside shows
// here without a second write that could disagree.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Jev;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>One draft change at a time. Taken BEFORE the queue gate wherever both are needed, never after.</summary>
        private readonly SemaphoreSlim _draftGate = new SemaphoreSlim(1, 1);

        /// <summary>Drafts of a model with nowhere to keep them, for as long as that model is open.</summary>
        private readonly ConditionalWeakTable<SessionContext, Dictionary<string, Draft>> _sessionDrafts =
            new ConditionalWeakTable<SessionContext, Dictionary<string, Draft>>();

        /// <summary>A test seam for a live result: the value of a measure ref, or null for "not connected". Null on
        /// every real path, which reads the live connection. Only Semanticus.Tests assigns it.</summary>
        internal Func<string, Task<double?>> DraftProbeForTest;

        /// <summary>A test seam for an after step: stands in for the op, given its name and the arguments it would be
        /// bound with, and returns what the op would return. Null on every real path. Only Semanticus.Tests assigns it.</summary>
        internal Func<string, string, Task<object>> DraftAfterOpForTest;

        internal const int DraftTries = 3;

        /// <summary>The ops that leave the model and reach something outside it (composer contract 1.6).</summary>
        private static readonly HashSet<string> OutwardOps = new HashSet<string>(StringComparer.Ordinal)
        {
            "deploy_stage", "deploy_live", "publish_data_agent", "fabric_git_commit", "git_push",
        };


        // ---- where drafts live -----------------------------------------------------------------------------

        private sealed class DraftPlace
        {
            public PlanMemoryBinding Bound;
            /// <summary>The drafts folder, or null for a model with nowhere to keep things.</summary>
            public string Dir;
        }

        private DraftPlace BindDrafts()
        {
            var bound = BindPlanMemory();
            return new DraftPlace { Bound = bound, Dir = bound.Nowhere ? null : DraftStore.DirIn(SidecarDir(bound.Context)) };
        }

        private List<Draft> ReadDrafts(DraftPlace p)
        {
            if (p.Dir != null) return DraftStore.List(p.Dir, p.Bound.Identity);
            var held = _sessionDrafts.GetOrCreateValue(p.Bound.Context);
            lock (held) return held.Values.Select(DraftStore.Copy).OrderBy(d => d.CreatedUtc).ToList();
        }

        private Draft ReadDraft(DraftPlace p, string id)
        {
            var key = id?.Trim();
            if (key != null && key.StartsWith(DraftRows.Prefix, StringComparison.Ordinal)) key = key.Substring(DraftRows.Prefix.Length);
            if (p.Dir != null) return DraftStore.Read(p.Dir, p.Bound.Identity, key);
            var held = _sessionDrafts.GetOrCreateValue(p.Bound.Context);
            lock (held) return key != null && held.TryGetValue(key, out var d) ? DraftStore.Copy(d) : null;
        }

        private Draft RequireDraft(DraftPlace p, string id) =>
            ReadDraft(p, id) ?? throw new ArgumentException(
                "No draft of this model is named '" + id + "'. get_model_plan lists drafts as rows whose id starts with 'draft/'.", nameof(id));

        private void WriteDraft(DraftPlace p, Draft d)
        {
            if (!PlanMemoryStillOpen(p.Bound)) throw new InvalidOperationException(PlanMemoryModelSwitchedSentence);
            d.UpdatedUtc = DateTimeOffset.UtcNow;
            // The one place a draft is written, so an approval whose content changed never outlives the change.
            if (d.PersonApproved && !PersonApprovalStands(d)) { d.PersonApproved = false; d.PersonApprovedDigest = null; }
            d.State = StateOfDraft(d, TaskReader(p)).State;
            if (p.Dir != null) { DraftStore.Write(p.Dir, d); return; }
            var held = _sessionDrafts.GetOrCreateValue(p.Bound.Context);
            lock (held) held[d.Id] = DraftStore.Copy(d);
        }

        private Func<string, PlanTask> TaskReader(DraftPlace p)
        {
            var memory = ReadPlanState(p.Bound);
            return id => id == null ? null : memory.FindTask(id);
        }

        private static string NewDraftId() => "dr-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

        // ---- compose_task ------------------------------------------------------------------------------------

        /// <summary>compose_task: a draft from explicit steps or a saved v3 workflow. Nothing changes in the model.
        /// A draft composed from the agent door puts its blanks on the queue at once, because the composing
        /// assistant is already there to claim them (Kane, 24 Sep).</summary>
        public async Task<DraftView> ComposeTaskAsync(string words, string workflow, string inputsJson, string stepsJson, string title, string origin, string document = null)
        {
            RequireProFeature();
            origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin;
            var hasSteps = !string.IsNullOrWhiteSpace(stepsJson);
            var hasDocument = !string.IsNullOrWhiteSpace(document);
            var hasWorkflow = !string.IsNullOrWhiteSpace(workflow) || hasDocument;
            if (hasSteps && hasWorkflow)
                throw new ArgumentException("Pass steps or a workflow, not both.", nameof(stepsJson));
            if (!hasSteps && !hasWorkflow && string.IsNullOrWhiteSpace(words))
                throw new ArgumentException("Say what to do: the words, the steps (a list of {op, args}) or the name of a saved workflow.", nameof(words));

            // The person's words are kept whole, as a task's are (UAT S12-1-005); only past the one limit is it refused.
            PlanTasks.RequireWithinFullText(words?.Trim(), "a draft", nameof(words));
            var d = new Draft
            {
                Id = NewDraftId(),
                Words = string.IsNullOrWhiteSpace(words) ? null : words.Trim(),
                Origin = origin,
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            if (hasSteps) ComposeFromSteps(d, stepsJson);
            else if (hasDocument) ComposeFromWorkflow(d, UnsavedWorkflow(workflow, document), inputsJson, document);
            else if (hasWorkflow)
            {
                var def = await GetWorkflowAsync(workflow.Trim()).ConfigureAwait(false);
                ComposeFromWorkflow(d, def, inputsJson, def.FilePath != null && System.IO.File.Exists(def.FilePath) ? System.IO.File.ReadAllText(def.FilePath) : null);
            }
            // Words alone (lane L5). On Pro with permission, Jev drafts per clause: a clause Jev is sure of takes Jev's
            // steps, a clause it is not takes the word match, and a clause the words name is never dropped; its sure
            // picks answer their questions and the rest stay the person's. With no service or no permission, or
            // nothing answered, the draft opens as the menu (L5a): no step chosen, the matches first.
            else d.Source = DraftSources.Words;
            JevDraftBuilder.Result drafted = null;
            ClauseAsk asked = null;
            if (d.Source == DraftSources.Words && JevDraftsOnPlan)
            {
                asked = await AskJevDraftByClauseAsync(d.Words).ConfigureAwait(false);
                drafted = asked.Drafted;
                // Every word no answered ask read is kept on the draft, named in its notes, until one does (R3-D6b), with
                // why no ask could leave when none could (D5).
                if (asked.Unread.Count > 0) { d.NotRead = asked.Unread; d.NotReadWhy = asked.NotAsked; }
                // A clause the adding rule left with no step is kept with the rule's reason (never as a step).
                if (asked.NoStep.Count > 0) d.NoStep = asked.NoStep;
                if (asked.Unticked.Count > 0) d.Unticked = asked.Unticked;
            }
            // Words drafted while the Plan's Jev switch is off were read by no one: kept for when it is on (R3-D6b).
            else if (d.Source == DraftSources.Words) d.NotRead = DescribedSteps.ClausesOf(d.Words, out _);
            var unsure = 0;
            var notTicked = 0;
            // Asked and answered, with no step it was sure of: the menu says so, and only then.
            // Not when Jev answered every clause surely (a sure no, or a sure add the adding rule set aside): the notes say why no
            // step stands, and "Jev was not sure" would be false (Sol on m11/completeness round 4).
            if (drafted != null && drafted.BelowBar && !drafted.SureOfEach) d.JevNotSure = true;
            if (drafted != null && !drafted.BelowBar)
            {
                foreach (var x in drafted.Steps)
                {
                    var step = AddTemplateStep(d, x.Template, x.Picked);
                    if (x.Untick) { step.Kept = false; notTicked++; if (x.Note != null) x.Note.StepId = step.Id; continue; }
                    if (!x.Sure) { step.Kept = false; unsure++; }
                    else if (AddingRule.Adds(x.Template.Op) && x.Clause >= 0 && x.Clause < asked.ClauseWords.Count) step.TickedFromWords = asked.ClauseWords[x.Clause];
                    if (x.FromWords) d.WordSteps++;
                }
                CheckStepOrder(d);
                // Jev drafted only when Jev was sure of a step; a draft the words named alone says so, and credits no one.
                d.JevDrafted = drafted.Steps.Any(x => x.Sure && !x.FromWords);
            }
            d.Title = !string.IsNullOrWhiteSpace(title) ? title.Trim() : d.Title ?? d.Words ?? DefaultTitle(d);
            // A title longer than a title holds is its start, cut at a word and marked "...": the words stay whole on the
            // draft, so the cut loses nothing.
            if (d.Title.Length > PlanTasks.MaxWordsChars) d.Title = PlanTasks.TitleOf(d.Title);
            // A workflow input that answers an object question names an object of this model (as edit_draft's answer does).
            foreach (var q in d.Questions.Where(q => q.Type == "objectRef" && q.Answer != null && q.AnsweredBy == "you"))
                await RequireObjectAnswerAsync(q, q.Answer).ConfigureAwait(false);

            var p = BindDrafts();
            d.ModelIdentity = p.Bound.Identity;
            ResolveSemanticusAnswers(d);
            await _draftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await GenerateBlanksAsync(d).ConfigureAwait(false);
                await SnapshotBaselinesAsync(d).ConfigureAwait(false);
                WriteDraft(p, d);
                if (origin == "agent") await PostOpenBlanksAsync(p, d).ConfigureAwait(false);
                WriteDraft(p, d);
            }
            finally { _draftGate.Release(); }
            // A step Jev only leaned to is said on every branch that can hold one (R3-D7s), not only when Jev drafted all.
            var sentence = d.JevDrafted && d.WordSteps > 0
                ? DraftCopy.JevDraftedWithWords(d.WordSteps) + (unsure == 0 ? "" : " " + DraftCopy.UnsureDropped(unsure))
                : d.JevDrafted
                ? (unsure == 0 ? DraftCopy.JevDrafted : DraftCopy.JevDraftedWithUnsure(unsure))
                : d.WordSteps > 0 ? DraftCopy.WordsDrafted + (unsure == 0 ? "" : " " + DraftCopy.UnsureDropped(unsure))
                : d.Source == DraftSources.Words
                ? "Drafted from your words. " + (d.JevNotSure ? DraftCopy.JevNotSure : DraftCopy.PickTheSteps)
                : origin == "agent"
                ? "Drafted. Nothing has changed yet. The parts your assistant writes are on its queue."
                : "Drafted. Nothing has changed yet.";
            // Steps the words name past the most one draft asks about are said, never dropped silently (D6a); the draft
            // names them in its notes (Sol, finding 4), and Add more words reads them, as the sentence says (R3-D6a). Not
            // when no ask could leave: then none was read, Add more words asks no one either, and the note says why (VR3).
            if (asked != null && asked.LeftOut > 0 && asked.NotAsked == null) sentence += " " + DraftCopy.PlanStepsLeftOut(asked.LeftOut, DescribedSteps.MaxSteps);
            // A step the adding rule offers unticked is said, so a step you did not see ticked is never a surprise.
            if (notTicked > 0) sentence += " " + DraftCopy.NotTicked(notTicked);
            return await DraftChangedAsync(p, d.Id, sentence).ConfigureAwait(false);
        }

        /// <summary>Words whose word match is a step the Plan's list of steps does not hold (Save the model, Refresh a
        /// table, Publish, Run the saved checks): only an answered ask can add it, so the note says so (R3-D6b).</summary>
        private static bool NamesAStepOffTheList(string words) =>
            DescribedSteps.AfterTheChange.Contains(DraftMenu.Best(words, DescribedSteps.WordTemplates));

        private static string DefaultTitle(Draft d) =>
            d.Steps.Count == 1 ? d.Steps[0].Title : "Change in " + d.Steps.Count.ToString(CultureInfo.InvariantCulture) + " steps";

        private static readonly JsonSerializerOptions ComposeJson = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        };

        private void ComposeFromSteps(Draft d, string stepsJson)
        {
            List<ComposeStepInput> steps;
            try { steps = JsonSerializer.Deserialize<List<ComposeStepInput>>(stepsJson, ComposeJson); }
            catch (JsonException ex)
            {
                throw new ArgumentException("steps must be a JSON list of {op, args}, or of {title, actions: [{op, args}], blanks, checks, brief}: " + ex.Message, nameof(stepsJson));
            }
            if (steps == null || steps.Count == 0) throw new ArgumentException("steps is empty. Name at least one step.", nameof(stepsJson));
            d.Source = DraftSources.Steps;
            var n = 0;
            foreach (var input in steps)
            {
                n++;
                var performer = PerformerOf(input.Performer, n);
                var actions = new List<ComposedAction>();
                if (!string.IsNullOrWhiteSpace(input.Op)) actions.Add(ActionOf(n, input.Op, input.Args));
                foreach (var a in input.Actions ?? new List<ComposeActionInput>()) actions.Add(ActionOf(n, a.Op, a.Args));
                if (performer == null && actions.Count == 0) throw new ArgumentException($"Step {n} names no op.", nameof(stepsJson));
                if (performer != null && actions.Count > 0)
                    throw new ArgumentException($"Step {n} is done by {PerformerWords(performer)}, so it names no op. Leave out 'op' and 'actions', or leave out 'performer' for a step Semanticus runs.", nameof(stepsJson));
                var step = new DraftStep
                {
                    Id = string.IsNullOrWhiteSpace(input.Id) ? "step-" + n.ToString(CultureInfo.InvariantCulture) : input.Id.Trim(),
                    Number = n,
                    Title = !string.IsNullOrWhiteSpace(input.Title) ? input.Title.Trim()
                          : performer == DraftPerformers.Person ? "Stop and ask me here"
                          : performer == DraftPerformers.Assistant ? "Your assistant's step" : TitleOf(actions[0]),
                    Actions = actions,
                    Blanks = input.Blanks ?? new List<DraftBlank>(),
                    Checks = input.Checks ?? new List<ComposedCheck>(),
                    Brief = string.IsNullOrWhiteSpace(input.Brief) ? null : input.Brief.Trim(),
                    Performer = performer,
                };
                step.Phase = performer == null ? PhaseOf(step) : null;
                d.Steps.Add(step);
            }
            FinishSteps(d);
        }

        /// <summary>A step's performer as the draft keeps it: null for a step Semanticus runs, else assistant or person.</summary>
        private static string PerformerOf(string performer, int n)
        {
            var p = performer?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(p) || p == DraftPerformers.Semanticus) return null;
            if (p == DraftPerformers.Assistant || p == DraftPerformers.Person) return p;
            throw new ArgumentException($"Step {n}: performer is one of semanticus, assistant or person.");
        }

        private static string PerformerWords(string performer) => performer == DraftPerformers.Person ? "you" : "your assistant";

        private static ComposedAction ActionOf(int step, string op, Dictionary<string, JsonElement> args)
        {
            var a = new ComposedAction { Op = op?.Trim() };
            foreach (var kv in args ?? new Dictionary<string, JsonElement>())
                a.Args[kv.Key] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : kv.Value.GetRawText();
            return a;
        }

        /// <param name="text">The file's text: a gated step's gate runs from these bytes when the draft runs.</param>
        private void ComposeFromWorkflow(Draft d, WorkflowDef def, string inputsJson, string text)
        {
            if (!WorkflowParser.IsComposed(def))
                throw new InvalidOperationException($"Workflow '{def.Name}' has no step Semanticus runs itself and none of the fields only a draft runs, so it runs as a guided workflow. Start it with start_workflow.");
            if (def.Error != null) throw new InvalidOperationException($"Workflow '{def.Name}' does not parse: {def.Error}");
            // A question that says 'jev:' (composer lane L5): compose.draft drafts steps from words and answers no workflow
            // question, so drafting as if the line were not there would quietly ask the person something the file says
            // Jev answers. Refused plainly, the same way check_workflow warns.
            var jevAsked = def.Steps.SelectMany(st => st.Questions.Select(q => (st, q))).FirstOrDefault(x => x.q.Jev != null);
            var jevInput = def.Inputs.FirstOrDefault(q => q.Jev != null);
            if (jevInput != null)
                throw new InvalidOperationException($"Workflow '{def.Name}': input '{jevInput.Name}' says 'jev: {jevInput.Jev}', but Jev does not answer workflow questions, so this workflow cannot be drafted as written. Remove the 'jev:' line; the input is then asked each run.");
            if (jevAsked.q != null)
                throw new InvalidOperationException($"Workflow '{def.Name}' step {jevAsked.st.Number}: question '{jevAsked.q.Name}' says 'jev: {jevAsked.q.Jev}', but Jev does not answer workflow questions, so this workflow cannot be drafted as written. Remove the 'jev:' line; the question is then asked as its 'by:' says.");
            // A gate on an assistant's step "runs exactly as a guided step does today" (composer contract 1.3; lane
            // l9-finish): the draft runs it in a guided sub-run (DraftStops.cs). A person's stop changes nothing, so it has
            // nothing for a gate to check, and one that carries a gate is still a field no path runs.
            var unrun = WorkflowParser.UnexecutableControlFields(def);
            if (unrun.Count > 0)
                throw new InvalidOperationException($"Workflow '{def.Name}' cannot be drafted by this version: step {unrun[0].StepNumber} uses '{unrun[0].Field}:' in a way this engine reads but does not run yet.");

            d.Source = DraftSources.Workflow;
            d.Workflow = def.Name;
            d.Title = string.IsNullOrWhiteSpace(def.Title) ? def.Name : def.Title;
            d.AssistantNotes = def.AssistantNotes;
            var given = ParseInputs(inputsJson);
            foreach (var q in def.Inputs) d.Questions.Add(QuestionOf(q, null, given));
            foreach (var st in def.Steps)
            {
                var actions = new List<ComposedAction>();
                if (st.Action != null)
                {
                    var action = new ComposedAction { Op = st.Action.Op };
                    foreach (var arg in st.Action.Arguments)
                        action.Args[arg.Name] = arg.Values != null ? JsonSerializer.Serialize(arg.Values) : arg.Value;
                    actions.Add(action);
                }
                var step = new DraftStep
                {
                    Id = st.Id,
                    Number = st.Number,
                    Title = st.Title,
                    Performer = st.Performer == WorkflowPerformers.Semanticus ? null : st.Performer,
                    Actions = actions,
                    Brief = st.Brief,
                    Blanks = st.Blanks.Select(b => new DraftBlank
                    {
                        Name = b.Name, Kind = b.Kind, Fills = b.Fills, By = b.By ?? "assistant",
                        DependsOn = b.DependsOn ?? Array.Empty<string>(), MaxChars = b.MaxChars, Generator = b.Generator,
                    }).ToList(),
                    Checks = st.Checks.Select(c => new ComposedCheck
                    {
                        Kind = c.Kind, Phase = c.Phase, Target = c.Target, Of = c.Of, When = c.When,
                        Min = double.TryParse(c.Min, NumberStyles.Float, CultureInfo.InvariantCulture, out var mn) ? mn : (double?)null,
                        Max = double.TryParse(c.Max, NumberStyles.Float, CultureInfo.InvariantCulture, out var mx) ? mx : (double?)null,
                    }).ToList(),
                };
                foreach (var q in st.Questions) d.Questions.Add(QuestionOf(q, st.Id, given));
                step.Phase = !string.IsNullOrWhiteSpace(st.Phase) ? st.Phase : step.BySemanticus ? PhaseOf(step) : null;
                // A `yaml gate` fence with only `ops:` parses to no gate, but its ops still matter to a guided run: they are
                // the step a team rule's exemption and the authored-measure record read. So it runs in the sub-run too.
                if (step.ByAssistant && (st.Gate != null || (st.Ops?.Length ?? 0) > 0))
                {
                    step.Guided = true;
                    step.GuidedWrites = (st.Ops ?? Array.Empty<string>()).Any(op => ComposedOps.Kinds.ContainsKey(op));
                    step.GuidedChecks = (st.Gate?.Verify?.Length ?? 0) > 0;
                }
                d.Steps.Add(step);
            }
            if (d.Steps.Any(s => s.Guided))
            {
                if (string.IsNullOrEmpty(text))
                    throw new InvalidOperationException($"Workflow '{def.Name}' has a step with a gate, and its file could not be read to run the gate from. Nothing was drafted.");
                d.GuidedDocument = text;
            }
            FinishSteps(d);
        }

        private static Dictionary<string, string> ParseInputs(string inputsJson)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(inputsJson)) return map;
            try
            {
                using var doc = JsonDocument.Parse(inputsJson);
                foreach (var p in doc.RootElement.EnumerateObject())
                    map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
            }
            catch (JsonException ex) { throw new ArgumentException("inputs must be a JSON object of {name: value}: " + ex.Message, "inputsJson"); }
            return map;
        }

        private static DraftQuestion QuestionOf(WorkflowQuestion q, string step, Dictionary<string, string> given)
        {
            var dq = new DraftQuestion
            {
                Name = q.Name, Question = q.Question ?? q.Name, Type = q.Type, By = q.By ?? "you", Values = q.Values ?? Array.Empty<string>(),
                From = q.From, Default = q.Default, Required = q.Required != "optional", Step = step,
            };
            if (given.TryGetValue(q.Name, out var v) && !string.IsNullOrWhiteSpace(v)) { dq.Answer = v; dq.AnsweredBy = "you"; }
            // A Semanticus question's default may name an input or another answer; ResolveSemanticusAnswers fills it in
            // once every answer it names is there, and never copies the token itself (Codex, F-278).
            return dq;
        }

        /// <summary>Checks every step against what a draft can run, and adds the blanks the engine requires: an
        /// argument of kind authored with no value becomes a blank of that kind (composer contract 1.5). Nothing
        /// here reads the model.</summary>
        private static void FinishSteps(Draft d)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in d.Steps)
                if (!ids.Add(step.Id)) throw new ArgumentException($"Two steps have the id '{step.Id}'. Give each step its own id.");
            // A step someone else does sits where the author put it: in the change, or after it once a step that leaves
            // the model came before it.
            var sawAfter = false;
            foreach (var step in d.Steps)
            {
                if (step.BySemanticus) { if (step.Phase == DraftPhases.After) sawAfter = true; continue; }
                if (string.IsNullOrWhiteSpace(step.Phase)) step.Phase = sawAfter ? DraftPhases.After : DraftPhases.Change;
            }
            foreach (var step in d.Steps) FinishStep(step);
            CheckStepOrder(d);
        }

        /// <summary>Reads come first, then the change, then the steps after it: the order a draft runs them in
        /// (ExecuteDraftAsync runs every read before the change), so the order it shows is the order it runs.</summary>
        private static void CheckStepOrder(Draft d)
        {
            var sawAfter = false;
            var sawChange = false;
            foreach (var step in d.Steps)
            {
                // A step someone else does runs where it stands, so a read after it would still run before it.
                if (!step.BySemanticus) { sawChange = true; continue; }
                if (step.Phase == DraftPhases.Change) sawChange = true;
                if (step.Phase == DraftPhases.Prepare && sawChange && !sawAfter)
                    throw new ArgumentException($"Step {step.Number} reads after a step that changes the model. Every read runs before the change, so put it before the change steps.");
                if (step.Phase == DraftPhases.After) sawAfter = true;
                else if (sawAfter)
                    throw new ArgumentException(step.Phase == DraftPhases.Prepare
                        ? $"Step {step.Number} reads after a step that leaves the model. Every read comes before the steps after the change."
                        : $"Step {step.Number} changes the model after a step that leaves it. Every change step comes before the steps after the change.");
            }
        }

        /// <summary>One step: its ops are ones a draft runs, all in one phase, and it gets the blanks the engine requires.
        /// Only this step's blanks are reset, so a step added to a draft later leaves the others as they are.</summary>
        private static void FinishStep(DraftStep step)
        {
            if (!step.BySemanticus)
            {
                if (step.Blanks.Count > 0)
                    throw new ArgumentException($"Step {step.Number} is done by {PerformerWords(step.Performer)}, and a blank fills an argument of an action Semanticus runs. Take its blanks off.");
                if (step.ByPerson && step.Checks.Count > 0)
                    throw new ArgumentException($"Step {step.Number} stops for you and changes nothing, so it has nothing to check. Take its checks off, or put them on the step before it.");
                if (step.Phase != DraftPhases.Change && step.Phase != DraftPhases.After)
                    throw new ArgumentException($"Step {step.Number}: a step {PerformerWords(step.Performer)} do{(step.ByPerson ? "" : "es")} sits in the change or after it, not in the reads.");
                foreach (var c in step.Checks)
                    if (!DraftCheckKinds.Runs(c.Phase, c.Kind) || c.Phase == "written" && !WrittenCheckOnWrites.Contains(c.Kind))
                        throw new ArgumentException($"Step {step.Number}: a '{c.Phase}' check of kind '{c.Kind}' is not one a step your assistant does can run. "
                            + "Before: " + string.Join(", ", DraftCheckKinds.Before) + ". Once written: " + string.Join(", ", WrittenCheckOnWrites)
                            + ". After it runs: " + string.Join(", ", DraftCheckKinds.After) + ".");
                return;
            }
            foreach (var a in step.Actions)
            {
                if (string.IsNullOrWhiteSpace(a.Op)) throw new ArgumentException($"Step {step.Number} has an action with no op.");
                var change = ComposedOps.Kinds.ContainsKey(a.Op);
                var group = OpParameterKinds.GroupOf(a.Op);
                if (!change && group != OpParameterKinds.After && group != OpParameterKinds.Prepare)
                    throw new ArgumentException($"Step {step.Number}: '{a.Op}' is not a step Semanticus can run in a draft. "
                        + "A draft runs the change ops (create_measure, set_description and the rest of the list), the reads before the change (list_columns, get_source_schema and the like) and the steps after the change (refresh_partition, save_model, run_tests, deploy_stage and the like). Do other work yourself, outside the draft.");
                var phase = change ? DraftPhases.Change : group == OpParameterKinds.After ? DraftPhases.After : DraftPhases.Prepare;
                if (step.Phase != phase)
                    throw new ArgumentException(phase == DraftPhases.Prepare || step.Phase == DraftPhases.Prepare
                        ? $"Step {step.Number} mixes a read with a step that changes or leaves the model. Put '{a.Op}' in a step of its own."
                        : $"Step {step.Number} mixes a change to the model with a step after the change. Put '{a.Op}' in a step of its own.");
            }
            if (step.Phase == DraftPhases.Prepare && step.Blanks.Count > 0)
                throw new ArgumentException($"Step {step.Number} only reads, so it has nothing for anyone to write. Take its blanks off.");

            // The blanks the engine requires.
            foreach (var a in step.Actions.Where(x => ComposedOps.Kinds.ContainsKey(x.Op)))
            {
                var op = a.Op == "starter_measure" ? null : a.Op;
                foreach (var p in (op == null ? null : OpParameterKinds.Parameters(op)) ?? Array.Empty<OpParameterKinds.OpParameter>())
                {
                    var kind = OpParameterKinds.AuthoredKind(op, p.Name);
                    if (kind == null || !p.Required || a.Args.ContainsKey(p.Name)) continue;
                    if (!OpParameterKinds.BlankKinds.Contains(kind)) continue;
                    var name = UniqueBlankName(step, p.Name);
                    step.Blanks.Add(new DraftBlank { Name = name, Kind = kind, Fills = p.Name });
                    a.Args[p.Name] = "{{blanks." + name + "}}";
                }
            }
            foreach (var b in step.Blanks)
            {
                if (string.IsNullOrWhiteSpace(b.Name) || string.IsNullOrWhiteSpace(b.Fills))
                    throw new ArgumentException($"Step {step.Number} has a blank with no name or no argument to fill.");
                if (!OpParameterKinds.BlankKinds.Contains(b.Kind ?? ""))
                    throw new ArgumentException($"Step {step.Number}: a blank's kind is one of {string.Join(", ", OpParameterKinds.BlankKinds)}.");
                if (b.Generator != null)
                {
                    var ops = step.Actions.Select(x => x.Op).ToArray();
                    if (!OpParameterKinds.Generators.TryGetValue(b.Generator, out var gen))
                        throw new ArgumentException($"Step {step.Number}: blank '{b.Name}' names the generator '{b.Generator}', which Semanticus does not have. It has: {string.Join(", ", OpParameterKinds.Generators.Keys)}.");
                    if (!ops.Contains(gen.Op) || !gen.Writes.Contains(b.Fills))
                        throw new ArgumentException($"Step {step.Number}: blank '{b.Name}' names the generator '{b.Generator}', which does not write '{b.Fills}' of this step.");
                }
                b.By = b.By == "you" ? "you" : "assistant";
                b.State = DraftBlankStates.Empty;
                b.Text = null;
                // Point the argument at the blank when the step left it unset.
                foreach (var a in step.Actions.Where(x => !x.Args.ContainsKey(b.Fills) && ComposedOps.Kinds.ContainsKey(x.Op)))
                {
                    a.Args[b.Fills] = "{{blanks." + b.Name + "}}";
                    break;
                }
            }
            foreach (var c in step.Checks)
            {
                if (!DraftCheckKinds.Runs(c.Phase, c.Kind))
                    throw new ArgumentException($"Step {step.Number}: a '{c.Phase}' check of kind '{c.Kind}' is not one a draft runs. "
                        + "Before: " + string.Join(", ", DraftCheckKinds.Before) + ". Once written: " + string.Join(", ", DraftCheckKinds.Written)
                        + ". After it runs: " + string.Join(", ", DraftCheckKinds.After) + ".");
            }
        }

        /// <summary>The written checks a step your assistant does runs on the formulas its changes carry.</summary>
        private static readonly HashSet<string> WrittenCheckOnWrites = new HashSet<string>(StringComparer.Ordinal)
            { "valid_dax", "dax_lint", "refs_within" };

        private static string UniqueBlankName(DraftStep step, string wanted)
        {
            var name = wanted;
            var i = 2;
            while (step.Blanks.Any(b => string.Equals(b.Name, name, StringComparison.Ordinal))) name = wanted + i++;
            return name;
        }

        private static string PhaseOf(DraftStep step) =>
            step.Actions.Count > 0 && step.Actions.All(a => OpParameterKinds.GroupOf(a.Op) == OpParameterKinds.After) ? DraftPhases.After
            : step.Actions.Count > 0 && step.Actions.All(a => OpParameterKinds.GroupOf(a.Op) == OpParameterKinds.Prepare) ? DraftPhases.Prepare
            : DraftPhases.Change;

        private static string TitleOf(ComposedAction a)
        {
            var name = ObjectNameOf(a);
            var words = a.Op.Replace('_', ' ');
            return char.ToUpperInvariant(words[0]) + words.Substring(1) + (name == null ? "" : " " + name);
        }

        /// <summary>The object an action is about, as a name: its "name" argument, else the last part of its ref.</summary>
        private static string ObjectNameOf(ComposedAction a)
        {
            if (a.Args.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n) && !n.StartsWith("{{", StringComparison.Ordinal)) return n;
            foreach (var key in new[] { "objRef", "measureRef", "columnRef", "tableRef", "partitionRef" })
                // A reference still waiting for an answer or a part names nothing yet.
                if (a.Args.TryGetValue(key, out var r) && !string.IsNullOrWhiteSpace(r) && !r.StartsWith("{{", StringComparison.Ordinal))
                {
                    var rest = r.Contains(':') ? r.Substring(r.IndexOf(':') + 1) : r;
                    return rest.Contains('/') ? rest.Substring(rest.LastIndexOf('/') + 1) : rest;
                }
            return null;
        }

        // ---- the change the draft makes ------------------------------------------------------------------------

        /// <summary>The kept change steps as lane L3's ComposedChange, the questions and inputs filled in. A blank
        /// that has not passed stays open, which the compiler turns into a placeholder in a rehearsal and a refusal
        /// in a real run.</summary>
        internal static ComposedChange ChangeOf(Draft d) =>
            ChangeOf(d, d.Steps.Where(s => s.Kept && s.Phase == DraftPhases.Change && s.BySemanticus));

        /// <summary>One change group: the given steps Semanticus runs, in order.</summary>
        internal static ComposedChange ChangeOf(Draft d, IEnumerable<DraftStep> steps)
        {
            var answers = AnswersOf(d);
            var change = new ComposedChange { DraftId = d.Id, Title = d.Title, Workflow = d.Workflow, PersonApproved = PersonApprovalStands(d), Answers = answers, Baselines = d.Baselines };
            foreach (var st in steps)
            {
                var cs = new ComposedStep { Id = st.Id, Title = st.Title, Checks = st.Checks.Where(c => c.Phase != "after").Select(c => FilledCheck(c, answers)).ToList() };
                foreach (var a in st.Actions) cs.Actions.Add(Filled(a, answers));
                foreach (var b in st.Blanks)
                    cs.Blanks.Add(new ComposedBlank
                    {
                        Name = b.Name, Kind = b.Kind, Fills = b.Fills, MaxChars = b.MaxChars, Choices = b.Choices,
                        Text = DraftBlankStates.Passed(b.State) ? b.Text : null,
                    });
                change.Steps.Add(cs);
            }
            return change;
        }

        /// <summary>The draft's answers by name: its inputs and its questions, as the person or Semanticus gave them.</summary>
        internal static Dictionary<string, string> AnswersOf(Draft d) =>
            d.Questions.Where(q => q.Answer != null).GroupBy(q => q.Name).ToDictionary(g => g.Key, g => g.First().Answer, StringComparer.Ordinal);

        /// <summary>An action with its input and question references filled from the answers. The same fill for a
        /// change step and an after step (Codex on pull request 395: after steps were passed the literal token).</summary>
        private static ComposedAction Filled(ComposedAction a, Dictionary<string, string> answers)
        {
            var ca = new ComposedAction { Op = a.Op };
            foreach (var kv in a.Args) ca.Args[kv.Key] = FillAnswers(kv.Value, answers);
            return ca;
        }

        /// <summary>A check with the answers filled into its target and its list, so a check can name the object a
        /// question chose (a relationship's key column).</summary>
        private static ComposedCheck FilledCheck(ComposedCheck c, Dictionary<string, string> answers) => new ComposedCheck
        {
            Kind = c.Kind, Phase = c.Phase, Min = c.Min, Max = c.Max, When = c.When,
            Target = FillAnswers(c.Target, answers),
            Of = c.Of?.Select(o => FillAnswers(o, answers)).ToArray(),
        };

        private static string FillAnswers(string value, Dictionary<string, string> answers)
        {
            if (value == null || value.IndexOf("{{", StringComparison.Ordinal) < 0) return value;
            foreach (var kv in answers)
                value = value.Replace("{{questions." + kv.Key + "}}", kv.Value).Replace("{{inputs." + kv.Key + "}}", kv.Value);
            return value;
        }

        // ---- the state (composer contract 4.1) -----------------------------------------------------------------

        internal readonly struct DraftStateInfo
        {
            public string State { get; init; }
            public string Reason { get; init; }
            public string Hold { get; init; }
        }

        /// <summary>A blank's state now: a queued or written one is read off its request's task.</summary>
        internal static string BlankStateNow(DraftBlank b, Func<string, PlanTask> task)
        {
            if (DraftBlankStates.Passed(b.State) || b.State == DraftBlankStates.Needs) return b.State;
            if (string.IsNullOrEmpty(b.RequestId)) return DraftBlankStates.Empty;
            var t = task(b.RequestId);
            if (t == null || !PlanTasks.IsOpenTask(t)) return DraftBlankStates.Empty;   // the request went without words
            return t.Claim != null && t.Claim.ClaimedBy != PlanDoers.You ? DraftBlankStates.Writing : DraftBlankStates.Queued;
        }

        internal static string HoldOf(Draft d)
        {
            var kept = d.Steps.Where(s => s.Kept).ToList();
            if (kept.Any(s => s.Actions.Any(a => ComposedOps.HeldForPerson(a.Op)))) return DraftHolds.RowSecurity;
            if (kept.Any(s => s.Phase == DraftPhases.After && s.Actions.Any(a => OutwardOps.Contains(a.Op)))
                && kept.Any(s => s.Blanks.Count > 0)) return DraftHolds.Publish;
            return null;
        }

        /// <summary>A digest of what a person's Approve and run covers: every kept step, in order, with its actions as
        /// they will run (answers filled in) and the words of every blank. The state of a blank is not in it, so a part
        /// that the model moved under and that passes again with the same words keeps the approval; new words do not.</summary>
        internal static string ApprovedContentOf(Draft d)
        {
            var answers = d.Questions.Where(q => q.Answer != null).GroupBy(q => q.Name).ToDictionary(g => g.Key, g => g.First().Answer, StringComparer.Ordinal);
            var content = d.Steps.Where(s => s.Kept).Select(s => new
            {
                s.Id,
                s.Phase,
                Actions = s.Actions.Select(a => new
                {
                    a.Op,
                    Args = a.Args.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new[] { kv.Key, FillAnswers(kv.Value, answers) }).ToArray(),
                }).ToArray(),
                Blanks = s.Blanks.Select(b => new[] { b.Name, b.Text }).ToArray(),
            }).ToArray();
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content));
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        }

        /// <summary>The person's approval stands only for the content it was given on.</summary>
        internal static bool PersonApprovalStands(Draft d) =>
            d.PersonApproved && d.PersonApprovedDigest != null && string.Equals(d.PersonApprovedDigest, ApprovedContentOf(d), StringComparison.Ordinal);

        /// <summary>Every question the kept steps need is answered and every kept part has passed its checks: the
        /// only moment a person's Approve and run on a held draft counts.</summary>
        internal static bool EveryPartPassed(Draft d, Func<string, PlanTask> task) =>
            d.Questions.All(q => !q.Required || q.Answer != null || !AskedBeforeTheRun(d, q))
            && d.Steps.Where(s => s.Kept && s.Phase == DraftPhases.Change).SelectMany(s => s.Blanks)
                .All(b => DraftBlankStates.Passed(BlankStateNow(b, task)));

        /// <summary>A question the run needs before it starts: one on a kept step, or an input. A question on a person's
        /// stop is asked at the stop instead (composer contract 1.4), so it does not hold the start.</summary>
        internal static bool AskedBeforeTheRun(Draft d, DraftQuestion q) =>
            q.Step == null || d.Steps.Any(s => s.Id == q.Step && s.Kept && !s.ByPerson);

        /// <summary>True when a kept step is done by someone other than Semanticus: the run then pauses there.</summary>
        internal static bool HasStops(Draft d) => d.Steps.Any(s => s.Kept && !s.BySemanticus);

        /// <summary>The one state, by the precedence of contract 4.1: needs you over waiting for your assistant over
        /// ready. A draft that ran is running, done or stopped; one paused at a step someone else does waits on them.</summary>
        internal DraftStateInfo StateOfDraft(Draft d, Func<string, PlanTask> task)
        {
            var hold = HoldOf(d);
            // A run whose change was taken back reads as though it never ran: nothing it did is in the model (UAT
            // S12-3-002). Redo puts the change back, and the draft reads done again, so this is read, never written.
            if (d.Run != null && !RunTakenBack(d))
            {
                if (d.Run.Outcome == "paused")
                {
                    var at = d.Steps.FirstOrDefault(s => s.Id == d.Run.PausedAt);
                    // A restart ended the guided sub-run a gated step waits in (lane l9-finish).
                    if (at != null && at.Guided && GuidedRunLost(d))
                        return new DraftStateInfo { State = DraftStates.Stopped, Reason = DraftCopy.Restarted, Hold = hold };
                    if (at == null || at.ByPerson)
                        return new DraftStateInfo { State = DraftStates.NeedsYou, Reason = at == null ? "Continue the run." : DraftCopy.PersonStop(at.Number, at.Title), Hold = hold };
                    return StepRequestNeedsYou(at, task)
                        ? new DraftStateInfo { State = DraftStates.NeedsYou, Reason = at.Reason ?? DraftCopy.BlankNeeds, Hold = hold }
                        : new DraftStateInfo { State = DraftStates.WaitingForAssistant, Hold = hold };
                }
                if (d.Run.Outcome == "running")
                    return RunCutOff(d) ? new DraftStateInfo { State = DraftStates.Stopped, Reason = DraftCopy.Restarted, Hold = hold }
                                        : new DraftStateInfo { State = DraftStates.Running, Hold = hold };
                if (d.Run.Outcome == "applied") return new DraftStateInfo { State = DraftStates.Done, Hold = hold };
                if (d.Run.Outcome != "not_ready")
                    return new DraftStateInfo { State = DraftStates.Stopped, Reason = d.Run.Reason, Hold = hold };
            }

            // A run stopped at a value changed after the draft was made: the person chooses (UAT S12-5-004).
            if (d.Overwrite != null)
                return new DraftStateInfo { State = DraftStates.NeedsYou, Reason = DraftCopy.Overwrite(d.Overwrite.ObjectRef, d.Overwrite.Kind), Hold = hold };
            // A draft from words opens as the menu with no step chosen (lane L5a): the person picks first.
            if (!d.Steps.Any(s => s.Kept))
                return new DraftStateInfo { State = DraftStates.NeedsYou, Reason = d.Source == DraftSources.Words && !d.JevDrafted && d.WordSteps == 0 ? (d.JevNotSure ? DraftCopy.JevNotSure : DraftCopy.PickTheSteps) : "Keep a step first.", Hold = hold };
            var needs = new List<string>();
            foreach (var q in d.Questions.Where(q => q.Required && q.Answer == null && AskedBeforeTheRun(d, q)))
                needs.Add("Answer: " + q.Question);
            var blanks = d.Steps.Where(s => s.Kept && s.Phase == DraftPhases.Change).SelectMany(s => s.Blanks).ToList();
            foreach (var b in blanks.Where(b => BlankStateNow(b, task) == DraftBlankStates.Needs))
                needs.Add(NeedsLine(b));
            var waiting = blanks.Any(b => { var s = BlankStateNow(b, task); return s == DraftBlankStates.Queued || s == DraftBlankStates.Writing; });
            var empty = blanks.Where(b => BlankStateNow(b, task) == DraftBlankStates.Empty).ToList();
            if (empty.Count > 0 && !waiting)
                needs.Add(empty.All(b => b.By == "you") ? "Write " + (empty.Count == 1 ? "the part" : "the parts") + " yourself." : DraftCopy.NotSentYet);
            if (needs.Count > 0) return new DraftStateInfo { State = DraftStates.NeedsYou, Reason = needs[0], Hold = hold };
            if (waiting) return new DraftStateInfo { State = DraftStates.WaitingForAssistant, Hold = hold };
            if (hold != null && !PersonApprovalStands(d))
                return new DraftStateInfo { State = DraftStates.NeedsYou, Reason = DraftCopy.Held(hold), Hold = hold };
            return new DraftStateInfo { State = DraftStates.Ready, Hold = hold };
        }

        /// <summary>What a blank in needs says: the assistant's question for a decline, else the reason itself.</summary>
        private static string NeedsLine(DraftBlank b) =>
            b.NeedsWhy == "declined" ? DraftCopy.AssistantAsks(b.Reason ?? "") : b.Reason ?? DraftCopy.BlankNeeds;

        /// <summary>True when this draft's run changed the model and History has taken that change back: the one edit
        /// the run made is undone, or gone with a redo branch (EditLedger.NewestByCause, the same answer a Plan row's
        /// applied set and a task said done read). A run from an earlier session cannot be undone, so it stands.</summary>
        internal bool RunTakenBack(Draft d)
        {
            if (d?.Run == null || d.Run.Outcome != "applied" || !d.Run.Changed) return false;
            var s = _sessions.Current;
            return s != null && _sessions.Ledger.NewestByCause(s.Id, ChangeCauseKinds.Draft).TryGetValue(d.Id, out var newest)
                && newest.TakenBack;
        }

        /// <summary>A run a restart cut off: it names an engine run that has ended.</summary>
        private bool RunCutOff(Draft d) =>
            d.Run?.Outcome == "running" && !string.Equals(d.Run.EngineRunId, EngineRunId, StringComparison.Ordinal)
            && !EngineRunLiveness.IsAlive(d.Run.EngineRunId);

        // ---- posting the blanks ------------------------------------------------------------------------------

        /// <summary>Put every open blank the assistant writes on the queue, as a Plan task with the packet.</summary>
        private async Task PostOpenBlanksAsync(DraftPlace p, Draft d)
        {
            await GenerateBlanksAsync(d).ConfigureAwait(false);
            var task = TaskReader(p);
            foreach (var st in d.Steps.Where(s => s.Kept && s.Phase == DraftPhases.Change))
                foreach (var b in st.Blanks.Where(b => b.By == "assistant" && BlankStateNow(b, task) == DraftBlankStates.Empty && !WaitsForItsAnswers(d, b)))
                    await PostBlankAsync(p, d, st, b).ConfigureAwait(false);
        }

        private async Task PostBlankAsync(DraftPlace p, Draft d, DraftStep st, DraftBlank b)
        {
            var action = st.Actions.FirstOrDefault(a => a.Args.TryGetValue(b.Fills, out var v) && v == "{{blanks." + b.Name + "}}") ?? st.Actions[0];
            var post = new AssistantRequestPost
            {
                JobId = d.Id,
                Need = DraftCopy.PartTitle(ObjectNameOf(action), st.Title, b.Kind),
                Brief = await PacketOfAsync(d, st, b, action).ConfigureAwait(false),
                Objects = await ExistingRefsAsync(action).ConfigureAwait(false),
            };
            var r = await AssistantRequests.PostAsync(post).ConfigureAwait(false);
            if (!r.Ok) { b.Reason = r.Message; return; }
            b.RequestId = r.Request.RequestId;
            b.State = DraftBlankStates.Queued;
            b.Attempts = 0;
            b.Reason = null;
        }

        /// <summary>The packet a claim returns (composer contract 5.3): the notes, the step's brief, what to write
        /// and where, what the draft itself creates, the checks it must pass, and the person's answers and pick.
        /// Grounding for the objects that exist is added by the claim itself.</summary>
        private Task<string> PacketOfAsync(Draft d, DraftStep st, DraftBlank b, ComposedAction action) => Task.FromResult(PacketOf(d, st, b, action));

        /// <summary>The packet, as <see cref="PacketOfAsync"/> builds it, for the blank's own action.</summary>
        private static string PacketOf(Draft d, DraftStep st, DraftBlank b) =>
            PacketOf(d, st, b, st.Actions.FirstOrDefault(a => a.Args.TryGetValue(b.Fills, out var v) && v == "{{blanks." + b.Name + "}}") ?? st.Actions[0]);

        private static string PacketOf(Draft d, DraftStep st, DraftBlank b, ComposedAction action)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(d.AssistantNotes)) lines.Add("Notes for you: " + d.AssistantNotes.Trim());
            if (!string.IsNullOrWhiteSpace(st.Brief)) lines.Add("Brief: " + st.Brief.Trim());
            lines.Add("Write the " + DraftCopy.KindWord(b.Kind) + " for step " + st.Number.ToString(CultureInfo.InvariantCulture)
                      + ", " + st.Title + " (" + action.Op + ", argument " + b.Fills + ").");
            var creates = CreatedTablesOf(d, st.Number);
            if (creates.Count > 0)
                lines.Add("The draft also creates, before this step: " + string.Join("; ", creates.Select(t => t.Key + " (" + string.Join(", ", t.Value) + ")")) + ".");
            var checks = st.Checks.Where(c => c.Phase == "written").Select(DraftCopy.CheckWords).ToList();
            if (b.Kind == "dax" && !st.Checks.Any(c => c.Kind == "valid_dax")) checks.Insert(0, DraftCopy.CheckWords(new ComposedCheck { Kind = "valid_dax" }));
            if (checks.Count > 0) lines.Add("Checks it must pass: " + string.Join(" ", checks));
            foreach (var q in d.Questions.Where(q => q.Answer != null && (b.DependsOn ?? Array.Empty<string>()).Contains(q.Name)))
                lines.Add(q.Question + ": " + q.Answer + ".");
            if (!string.IsNullOrWhiteSpace(b.Picked)) lines.Add("The person chose " + PlanTasks.NameOfRef(b.Picked) + ".");
            lines.Add("Submit the text with complete_request. Semanticus checks it and tells you what to fix.");
            var text = string.Join("\n", lines);
            return text.Length <= PlanTasks.MaxBriefChars ? text : text.Substring(0, PlanTasks.MaxBriefChars);
        }

        /// <summary>The tables the draft creates in the steps before <paramref name="beforeStep"/>, with their columns.</summary>
        private static Dictionary<string, List<string>> CreatedTablesOf(Draft d, int beforeStep)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var st in d.Steps.Where(s => s.Kept && s.Number < beforeStep))
                foreach (var a in st.Actions)
                {
                    if (a.Op == "create_import_table" && a.Args.TryGetValue("name", out var t)) map[t] = new List<string>();
                    if (a.Op == "create_column" && a.Args.TryGetValue("tableRef", out var tr) && a.Args.TryGetValue("name", out var c))
                    {
                        var table = tr.StartsWith("table:", StringComparison.Ordinal) ? tr.Substring(6) : tr;
                        if (map.TryGetValue(table, out var cols)) cols.Add(c);
                    }
                }
            return map;
        }

        /// <summary>The refs an action names that exist in the model now, for the claim's grounding.</summary>
        private async Task<string[]> ExistingRefsAsync(ComposedAction action)
        {
            var refs = action.Args.Values.Where(v => v != null && v.IndexOf(':') > 0 && !v.StartsWith("{{", StringComparison.Ordinal)
                                                     && v.IndexOf(' ') != v.IndexOf(':') + 1).Distinct(StringComparer.Ordinal).ToArray();
            if (refs.Length == 0) return Array.Empty<string>();
            try
            {
                return await _sessions.Require().ReadAsync(m => refs.Where(r =>
                {
                    try { return ObjectRefs.Resolve(m, r) != null; } catch { return false; }
                }).ToArray()).ConfigureAwait(false);
            }
            catch { return Array.Empty<string>(); }
        }

        // ---- the queue's side for a blank: complete_request and decline_request (composer contract 5.4) --------

        /// <summary>The draft and blank a request carries, or null when the request is an ordinary task.</summary>
        private (Draft Draft, DraftStep Step, DraftBlank Blank) DraftPartOf(DraftPlace p, PlanTask task)
        {
            if (task?.OriginLink?.Kind != PlanOriginKinds.Draft || !DraftStore.IsDraftId(task.OriginLink.Ref)) return (null, null, null);
            var d = ReadDraft(p, task.OriginLink.Ref);
            if (d == null) return (null, null, null);
            foreach (var st in d.Steps)
                foreach (var b in st.Blanks)
                    if (string.Equals(b.RequestId, task.Id, StringComparison.Ordinal)) return (d, st, b);
            return (null, null, null);
        }

        /// <summary>complete_request on a blank: the words are checked against the rehearsed draft in the same call.
        /// Null when the request is not a draft's, so the ordinary task path answers it.</summary>
        internal async Task<HandoffResult> TryCompleteDraftPartAsync(string requestId, string claimToken, string text, string reader)
        {
            var stepPart = await TryCompleteDraftStepAsync(requestId, claimToken, text).ConfigureAwait(false);
            if (stepPart != null) return stepPart;
            var p = BindDrafts();
            var probe = ReadPlanState(p.Bound).FindTask(requestId?.Trim());
            if (DraftPartOf(p, probe).Draft == null) return null;

            await _draftGate.WaitAsync().ConfigureAwait(false);
            HandoffResult result;
            string draftId;
            try
            {
                // The queue gate, as the ordinary answer path takes it, so a claim cannot move while this answer lands.
                await _queueGate.WaitAsync().ConfigureAwait(false);
                Draft ready = null;
                try
                {
                var task = ReadPlanState(p.Bound).FindTask(requestId.Trim());
                var (d, st, b) = DraftPartOf(p, task);
                if (d == null) return null;
                draftId = d.Id;
                var refused = ClaimProblem(task, claimToken);
                if (refused != null) return refused;
                var words = text?.Trim();
                if (string.IsNullOrEmpty(words))
                    return PartRefused(HandoffRefusal.ChecksFailed, "Write the " + DraftCopy.KindWord(b.Kind) + " before you submit it. Nothing was counted as a try.", task, b);
                if (b.MaxChars is int cap && words.Length > cap * 4)
                    words = words.Substring(0, cap * 4);

                var check = await CheckDraftBlankAsync(ChangeOf(d), st.Id, b.Name, words).ConfigureAwait(false);
                if (check.Passed)
                {
                    b.State = DraftBlankStates.Filled;
                    b.Text = words;
                    b.FilledBy = "assistant";
                    b.Reason = null;
                    b.NeedsWhy = null;
                    CloseTask(p, task.Id, t => { t.Reply = words; t.State = PlanTaskStates.Done; t.DoneBy = PlanDoers.Assistant; });
                    await SnapshotBaselinesAsync(d).ConfigureAwait(false);
                    WriteDraft(p, d);
                    ready = d;
                    result = new HandoffResult
                    {
                        Request = PartRequest(ReadPlanState(p.Bound).FindTask(task.Id), b, AssistantRequestState.Completed),
                        Message = "Written and checked. The change runs when the rest of the draft is ready.",
                    };
                }
                else
                {
                    b.Attempts++;
                    var failures = FailureText(check);
                    if (b.Attempts >= DraftTries)
                    {
                        b.State = DraftBlankStates.Needs;
                        b.NeedsWhy = "failed";
                        b.Reason = failures;
                        b.RequestId = null;
                        CloseTask(p, task.Id, t => { t.State = PlanTaskStates.SetAside; t.DeclineReason = DraftCopy.FailedThreeTimes; });
                        WriteDraft(p, d);
                        result = PartRefused(HandoffRefusal.ChecksFailed, failures + " That was the third try, so this part now waits for the person.", task, b);
                    }
                    else
                    {
                        WriteDraft(p, d);
                        result = PartRefused(HandoffRefusal.ChecksFailed, failures + " " + DraftCopy.TriesLeft(DraftTries - b.Attempts)
                            + " Fix it and call complete_request again with the same claimToken.", task, b);
                    }
                }
                }
                finally { _queueGate.Release(); }

                // Outside the queue gate: the change itself, when this was the last part of an approved draft.
                if (ready != null)
                {
                    var ran = await RunIfReadyAsync(p, ready).ConfigureAwait(false);
                    if (ran.Run != null && ran.Run.Outcome != "not_ready")
                        result.Message = "Written and checked. Every part was ready, so the change ran. Read get_draft for how it went.";
                }
            }
            finally { _draftGate.Release(); }
            await DraftChangedAsync(p, draftId, null).ConfigureAwait(false);
            return result;
        }

        /// <summary>decline_request on a blank: the blank needs the person at once, with the reason word for word and
        /// the offered choices that resolve to real objects (or to objects the draft itself creates).</summary>
        internal async Task<HandoffResult> TryDeclineDraftPartAsync(string requestId, string claimToken, string reason, string[] choices, string reader)
        {
            var stepPart = await TryDeclineDraftStepAsync(requestId, claimToken, reason).ConfigureAwait(false);
            if (stepPart != null) return stepPart;
            var p = BindDrafts();
            var probe = ReadPlanState(p.Bound).FindTask(requestId?.Trim());
            if (DraftPartOf(p, probe).Draft == null) return null;
            var why = RequireReason(reason);

            HandoffResult result;
            string draftId;
            await _draftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _queueGate.WaitAsync().ConfigureAwait(false);
                try
                {
                var task = ReadPlanState(p.Bound).FindTask(requestId.Trim());
                var (d, st, b) = DraftPartOf(p, task);
                if (d == null) return null;
                draftId = d.Id;
                var refused = ClaimProblem(task, claimToken);
                if (refused != null) return refused;

                var offered = (choices ?? Array.Empty<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())
                    .Distinct(StringComparer.Ordinal).Take(6).ToArray();
                var created = CreatedRefsOf(d);
                var real = await _sessions.Require().ReadAsync(m => offered.Where(c =>
                {
                    if (created.Contains(c)) return true;
                    try { return ObjectRefs.Resolve(m, c) != null; } catch { return false; }
                }).ToArray()).ConfigureAwait(false);

                b.State = DraftBlankStates.Needs;
                b.NeedsWhy = "declined";
                b.Reason = why;
                b.AskChoices = real.Length == 0 ? null : real;
                b.RequestId = null;
                b.Attempts = 0;
                CloseTask(p, task.Id, t => { t.State = PlanTaskStates.SetAside; t.DeclineReason = why; });
                WriteDraft(p, d);
                result = new HandoffResult
                {
                    Request = PartRequest(ReadPlanState(p.Bound).FindTask(task.Id), b, AssistantRequestState.Declined),
                    Message = "Handed back to the person with your reason"
                        + (real.Length == 0 ? "." : " and " + (real.Length == 1 ? "1 choice" : real.Length.ToString(CultureInfo.InvariantCulture) + " choices") + " to pick from.")
                        + (offered.Length > real.Length ? " Choices that name nothing in the model or the draft were left out." : ""),
                };
                }
                finally { _queueGate.Release(); }
            }
            finally { _draftGate.Release(); }
            await DraftChangedAsync(p, draftId, null).ConfigureAwait(false);
            return result;
        }

        /// <summary>The claim checks complete_request and decline_request share with the ordinary task path.</summary>
        private HandoffResult ClaimProblem(PlanTask task, string claimToken)
        {
            if (!PlanTasks.IsOpenTask(task))
                return Refused(HandoffRefusal.AlreadyTerminal, "That part is already written or closed. Nothing was changed.", task);
            if (task.Claim == null || task.Claim.ClaimedBy == PlanDoers.You)
                return Refused(HandoffRefusal.NotClaimed, "Claim " + task.Id + " with claim_request first, then answer it.", task);
            if (!string.Equals(task.Claim.ClaimTokenHash, HashToken(claimToken), StringComparison.Ordinal))
                return Refused(HandoffRefusal.WrongClaimToken, "That claim on " + task.Id + " is not the current one. Claim it again before answering.", task);
            return null;
        }

        private static HandoffResult PartRefused(HandoffRefusal why, string message, PlanTask task, DraftBlank b)
        {
            var r = PartRequest(task, b, task?.Claim != null ? AssistantRequestState.Claimed : AssistantRequestState.Queued);
            return new HandoffResult { Refusal = why, Message = message, Request = r };
        }

        private static AssistantRequest PartRequest(PlanTask task, DraftBlank b, AssistantRequestState state)
        {
            var r = RequestOf(task, null, state);
            r.AttemptsLeft = Math.Max(0, DraftTries - b.Attempts);
            return r;
        }

        private static string FailureText(BlankCheckResult check)
        {
            var parts = check.Checks.Where(c => c.Status != ComposedCheckStatus.Passed).Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (!string.IsNullOrWhiteSpace(check.RehearsalProblem)) parts.Insert(0, check.RehearsalProblem);
            var text = parts.Count == 0 ? "It did not pass its checks." : string.Join(" ", parts.Select(t => t.TrimEnd().EndsWith(".") ? t.Trim() : t.Trim() + "."));
            return text;
        }

        /// <summary>Close a blank's request task: done with the words, or set aside with a reason.</summary>
        private void CloseTask(DraftPlace p, string taskId, Action<PlanTask> close)
        {
            WritePlanTasks(p.Bound, state =>
            {
                var t = state.FindTask(taskId);
                if (t == null || !PlanTasks.IsOpenTask(t)) return;
                close(t);
                t.Claim = null;
                t.DoneUtc = DateTimeOffset.UtcNow;
            });
            ClearPointersTo(taskId);
        }

        private static HashSet<string> CreatedRefsOf(Draft d)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var st in d.Steps.Where(s => s.Kept))
                foreach (var r in CreatedBy(st)) set.Add(r);
            return set;
        }

        /// <summary>The refs a step creates, so a later step that names one needs it.</summary>
        private static IEnumerable<string> CreatedBy(DraftStep st)
        {
            foreach (var a in st.Actions)
            {
                a.Args.TryGetValue("name", out var name);
                a.Args.TryGetValue("tableRef", out var tableRef);
                var table = tableRef != null && tableRef.StartsWith("table:", StringComparison.Ordinal) ? tableRef.Substring(6) : tableRef;
                switch (a.Op)
                {
                    case "create_import_table":
                    case "create_calculation_group":
                        if (name != null) yield return "table:" + name;
                        break;
                    case "create_column":
                        if (name != null && table != null) yield return "column:" + table + "/" + name;
                        break;
                    case "create_measure":
                    case "starter_measure":
                        if (name != null && table != null) yield return "measure:" + table + "/" + name;
                        break;
                    case "create_role":
                        if (name != null) yield return "role:" + name;
                        break;
                }
            }
        }

        /// <summary>The step numbers a step needs: the earlier kept steps that create something it names.</summary>
        private static int[] NeedsOf(Draft d, DraftStep step)
        {
            var needs = new SortedSet<int>();
            foreach (var earlier in d.Steps.Where(s => s.Number < step.Number))
            {
                var made = CreatedBy(earlier).ToList();
                if (made.Count == 0) continue;
                foreach (var v in step.Actions.SelectMany(a => a.Args.Values).Where(v => v != null))
                    if (made.Any(r => string.Equals(v, r, StringComparison.OrdinalIgnoreCase)
                                   || (r.StartsWith("table:", StringComparison.Ordinal) && (v.StartsWith("column:" + r.Substring(6) + "/", StringComparison.OrdinalIgnoreCase)
                                                                                         || v.StartsWith("measure:" + r.Substring(6) + "/", StringComparison.OrdinalIgnoreCase)))))
                        needs.Add(earlier.Number);
            }
            return needs.ToArray();
        }

        // ---- running ----------------------------------------------------------------------------------------

        /// <summary>Run the change when the draft is approved and ready. Returns the draft as it now is.</summary>
        private async Task<Draft> RunIfReadyAsync(DraftPlace p, Draft d)
        {
            if (!d.Approved) return d;
            // Its change was undone: running it again is the person's call (Approve and run), never automatic.
            if (RunTakenBack(d)) return d;
            if (StateOfDraft(d, TaskReader(p)).State != DraftStates.Ready) return d;
            return await ExecuteDraftAsync(p, d).ConfigureAwait(false);
        }

        private async Task<Draft> ExecuteDraftAsync(DraftPlace p, Draft d)
        {
            // Evidence first: a part that passed while it waited is checked again against the model as it is now. A
            // part that no longer passes goes back to the person, and nothing runs.
            var moved = false;
            foreach (var st in d.Steps.Where(s => s.Kept && s.Phase == DraftPhases.Change))
                foreach (var b in st.Blanks.Where(b => (b.State == DraftBlankStates.Filled || b.State == DraftBlankStates.Self) && b.Text != null))
                {
                    var again = await CheckDraftBlankAsync(ChangeOf(d), st.Id, b.Name, b.Text).ConfigureAwait(false);
                    if (again.Passed) continue;
                    b.State = DraftBlankStates.Needs;
                    b.NeedsWhy = "evidence";
                    b.Reason = DraftCopy.EvidenceMoved(EvidenceName(d, st, b)) + " " + FailureText(again);
                    moved = true;
                }
            if (moved) { WriteDraft(p, d); return d; }

            d.Run = new DraftRun { StartedUtc = DateTimeOffset.UtcNow, EngineRunId = EngineRunId, Outcome = "running" };
            EngineRunLiveness.Hold(EngineRunId);
            WriteDraft(p, d);
            PublishDraftQuietly(p, d);

            // The reads before the change (composer contract 2.4; Codex, F-276). They change nothing, so a read that cannot
            // run stops the draft here, before the change, and says which step and why.
            var readAnswers = AnswersOf(d);
            foreach (var st in d.Steps.Where(s => s.Kept && s.Phase == DraftPhases.Prepare))
            {
                var read = await ReadStepAsync(st, readAnswers).ConfigureAwait(false);
                d.Run.After.AddRange(read);
                var failedRead = read.FirstOrDefault(r => r.Status != DraftAfterStatus.Ran);
                if (failedRead == null) continue;
                d.Run.Outcome = "stopped";
                d.Run.StopStep = st.Number;
                d.Run.Reason = "Step " + st.Number.ToString(CultureInfo.InvariantCulture) + " could not run: " + failedRead.Text;
                d.Run.Revision = _sessions.Current?.Revision ?? 0;
                d.Run.FinishedUtc = DateTimeOffset.UtcNow;
                WriteDraft(p, d);
                return d;
            }

            var change = ChangeOf(d);
            try
            {
                var rehearsal = await RehearseComposedChangeAsync(change).ConfigureAwait(false);
                d.Run.Kinds = rehearsal.Kinds;
            }
            catch { }

            // A gated step's gate runs in a guided sub-run, started here: after the reads, before any change, so its
            // start-of-run snapshots are of the model as it was (lane l9-finish, DraftGuided.cs).
            var cannot = await StartGuidedRunAsync(d).ConfigureAwait(false);
            if (cannot != null)
            {
                d.Run.Outcome = "stopped";
                d.Run.StopStep = d.Steps.Where(s => s.Kept && s.Guided).Select(s => s.Number).DefaultIfEmpty(0).Min();
                d.Run.Reason = cannot;
                d.Run.Revision = _sessions.Current?.Revision ?? 0;
                d.Run.FinishedUtc = DateTimeOffset.UtcNow;
                WriteDraft(p, d);
                return d;
            }

            // A draft with a step someone else does runs step by step and pauses at each such step (lane author-gaps, B9).
            if (HasStops(d)) return await RunOnAsync(p, d, null).ConfigureAwait(false);

            ComposedChangeResult r;
            try { r = await ApplyComposedChangeAsync(change, d.ApprovedBy ?? "human").ConfigureAwait(false); }
            catch (Exception ex)
            {
                r = new ComposedChangeResult { Outcome = ComposedOutcome.Failed, Revision = _sessions.Current?.Revision ?? 0, Reason = "Nothing changed. " + ex.Message };
            }
            if (r.Moved != null)
            {
                // Nothing was written: a value this draft writes changed after it was made. It waits for the person's
                // choice, keep theirs or apply the draft, as though it had not run (UAT S12-5-004).
                d.Run = null;
                d.Overwrite = new DraftOverwrite
                {
                    StepId = r.Moved.StepId, StepNumber = r.Moved.StepNumber, ObjectRef = r.Moved.ObjectRef,
                    Kind = r.Moved.Kind, Was = r.Moved.Was, Now = r.Moved.Now,
                };
                WriteDraft(p, d);
                return d;
            }
            d.Run.Revision = r.Revision;
            d.Run.Changed = r.Changed;
            d.Run.AppliedStepIds = r.AppliedStepIds ?? Array.Empty<string>();
            d.Run.StopStep = r.StopStep;
            d.Run.Reason = r.Reason;
            d.Run.BeforeChecks = r.BeforeChecks ?? Array.Empty<ComposedCheckResult>();
            d.Run.Outcome = r.Outcome switch
            {
                ComposedOutcome.Applied => "applied",
                ComposedOutcome.Stopped => "stopped",
                ComposedOutcome.NotReady => "not_ready",
                _ => "failed",
            };
            if (r.Changed) { d.Run.UndoEntries = 1; d.Run.LastUndoSteps = d.Run.AppliedStepIds.Length; }
            if (d.Run.Outcome == "applied") d.Run.After.AddRange(await RunAfterAsync(d, null).ConfigureAwait(false));
            d.Run.FinishedUtc = DateTimeOffset.UtcNow;
            WriteDraft(p, d);
            return d;
        }

        /// <summary>The table a failed formula reads, for "Sales changed while this waited."</summary>
        private static string EvidenceName(Draft d, DraftStep st, DraftBlank b)
        {
            var action = st.Actions.FirstOrDefault(a => a.Args.TryGetValue(b.Fills, out var v) && v == "{{blanks." + b.Name + "}}");
            if (action != null && action.Args.TryGetValue("tableRef", out var tr) && tr != null)
                return tr.StartsWith("table:", StringComparison.Ordinal) ? tr.Substring(6) : tr;
            return null;
        }

        /// <summary>After the change: the after checks of the change steps first, then each after step in order and
        /// its own checks. An outward step runs only when every check before it PASSED, the before-checks included
        /// (composer contract 1.6 and 1.7): a failed check stops it, and so does one that could not run, because "Could
        /// not check" is never a pass. The stop says which, in the check's own words (Sol 6, slice 8, finding 3).
        /// With <paramref name="previous"/> (Try again, the Sol F-269 follow-up), a step that already ran and a check that
        /// already came out keep their result and are not run a second time; only what did not run, or could not check,
        /// runs again, so a refresh or a publish that went through is never repeated.</summary>
        private Task<List<DraftAfterResult>> RunAfterAsync(Draft d, List<DraftAfterResult> previous) =>
            RunAfterAsync(d, previous,
                d.Steps.Where(s => s.Kept && s.Phase == DraftPhases.Change && !s.ByPerson),
                d.Steps.Where(s => s.Kept && s.Phase == DraftPhases.After && !s.ByPerson), null);

        /// <param name="changeSteps">The change steps whose after checks run.</param>
        /// <param name="afterSteps">The steps after the change to run, with their checks.</param>
        /// <param name="already">Results this run already has (a step-by-step run), read for the outward-step rule.</param>
        private async Task<List<DraftAfterResult>> RunAfterAsync(Draft d, List<DraftAfterResult> previous,
            IEnumerable<DraftStep> changeSteps, IEnumerable<DraftStep> afterSteps, IEnumerable<DraftAfterResult> already)
        {
            var results = new List<DraftAfterResult>();
            var failed = false;
            string couldNotCheck = null;
            void Saw(string status, string text)
            {
                if (status == ComposedCheckStatus.Passed) return;
                if (status == ComposedCheckStatus.Failed) failed = true;
                else couldNotCheck ??= text;
            }
            // The earlier results by step, kind and place, so the second of two like checks on one step is its own.
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            DraftAfterResult Earlier(string stepId, string what, string name)
            {
                if (previous == null) return null;
                var key = stepId + "|" + what + "|" + name;
                seen.TryGetValue(key, out var n);
                seen[key] = n + 1;
                var r = previous.Where(x => x.StepId == stepId && x.What == what && (x.Op ?? x.Kind) == name).Skip(n).FirstOrDefault();
                return r == null ? null
                     : what == "step" ? (r.Status == DraftAfterStatus.Ran ? r : null)
                     : r.Status == DraftAfterStatus.Passed || r.Status == DraftAfterStatus.Failed ? r : null;
            }
            async Task Check(DraftStep st, ComposedCheck c, Dictionary<string, string> ans)
            {
                if (!await ComposedCheckAppliesAsync(c, ans).ConfigureAwait(false)) return;
                var r = Earlier(st.Id, "check", c.Kind) ?? await AfterCheckAsync(d, st, c).ConfigureAwait(false);
                Saw(r.Status, r.Text);
                results.Add(r);
            }
            var answers = AnswersOf(d);
            foreach (var c in d.Run?.BeforeChecks ?? Array.Empty<ComposedCheckResult>()) Saw(c.Status, c.Text);
            foreach (var r in already ?? Array.Empty<DraftAfterResult>())
                if (r.What == "check") Saw(r.Status == DraftAfterStatus.Passed ? ComposedCheckStatus.Passed : r.Status == DraftAfterStatus.Failed ? ComposedCheckStatus.Failed : ComposedCheckStatus.CouldNotCheck, r.Text);
            foreach (var st in changeSteps)
                foreach (var c in st.Checks.Where(c => c.Phase == "after"))
                    await Check(st, c, answers).ConfigureAwait(false);
            foreach (var st in afterSteps)
            {
                foreach (var a in st.Actions)
                {
                    var done = Earlier(st.Id, "step", a.Op);
                    if (done != null) { results.Add(done); continue; }
                    if ((failed || couldNotCheck != null) && OutwardOps.Contains(a.Op))
                    {
                        results.Add(new DraftAfterResult { StepId = st.Id, StepNumber = st.Number, What = "step", Op = a.Op, Status = DraftAfterStatus.NotRun,
                            Text = failed ? "Did not run: a check before it failed." : DraftCopy.NotRunUnchecked(couldNotCheck) });
                        continue;
                    }
                    var filled = Filled(a, answers);
                    // A draft's record is its guided sub-run's: Semanticus names the run itself (lane l9-finish).
                    if (filled.Op == "save_evidence" && (!filled.Args.TryGetValue("source", out var source) || source == "workflow"))
                    {
                        if (d.Run?.GuidedRunId == null)
                        {
                            results.Add(new DraftAfterResult { StepId = st.Id, StepNumber = st.Number, What = "step", Op = a.Op, Status = DraftAfterStatus.CouldNotRun,
                                Text = DraftCopy.NoCheckedRecord });
                            continue;
                        }
                        filled.Args["source"] = "workflow";
                        filled.Args["sourceId"] = d.Run.GuidedRunId;
                    }
                    results.Add(await AfterStepAsync(st, filled).ConfigureAwait(false));
                }
                foreach (var c in st.Checks.Where(c => c.Phase == "after"))
                    await Check(st, c, answers).ConfigureAwait(false);
            }
            return results;
        }

        /// <summary>One after step, through the same op the agent door calls, so it does exactly what that op does.
        /// The ops that write only when told to commit (a refresh, a deploy) are told to: the draft's approval is the
        /// go-ahead, and a workflow cannot set commit itself. deploy_live previews first for the review token its
        /// commit must echo, as the agent door does. The step reads Ran only when the op's report says the write
        /// happened (Codex on pull request 395: it ran as a preview and read Ran).</summary>
        private async Task<DraftAfterResult> AfterStepAsync(DraftStep st, ComposedAction a)
        {
            var r = new DraftAfterResult { StepId = st.Id, StepNumber = st.Number, What = "step", Op = a.Op };
            try
            {
                if (a.Args.Values.Any(v => v != null && (v.Contains("{{inputs.", StringComparison.Ordinal) || v.Contains("{{questions.", StringComparison.Ordinal))))
                    throw new InvalidOperationException("it needs an answer this draft does not have yet.");
                if (CommitOps.Contains(a.Op))
                {
                    if (a.Op == "deploy_live")
                    {
                        a.Args["commit"] = "false";
                        var preview = await InvokeAfterOpAsync(a).ConfigureAwait(false) as DeployReport;
                        if (!string.IsNullOrWhiteSpace(preview?.Error)) throw new InvalidOperationException(preview.Error);
                        a.Args["confirmToken"] = preview?.ConfirmToken;
                    }
                    a.Args["commit"] = "true";
                }
                var problem = WriteProblem(await InvokeAfterOpAsync(a).ConfigureAwait(false));
                if (problem != null) throw new InvalidOperationException(problem);
                r.Status = DraftAfterStatus.Ran;
                r.Text = "Done.";
            }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                r.Status = DraftAfterStatus.CouldNotRun;
                r.Text = LooksOffline(inner.Message) ? DraftCopy.NotConnectedRun : "Could not run: " + inner.Message;
            }
            return r;
        }

        /// <summary>One read before the change, through the same op the agent door calls. A read writes nothing, so it
        /// is never asked to commit. Its text is the reason when it cannot run, without the "Could not run" prefix the
        /// stop line adds itself.</summary>
        private async Task<List<DraftAfterResult>> ReadStepAsync(DraftStep st, Dictionary<string, string> answers)
        {
            var results = new List<DraftAfterResult>();
            foreach (var raw in st.Actions)
            {
                var a = Filled(raw, answers);
                var r = new DraftAfterResult { StepId = st.Id, StepNumber = st.Number, What = "read", Op = a.Op };
                try
                {
                    if (a.Args.Values.Any(v => v != null && v.Contains("{{", StringComparison.Ordinal)))
                        throw new InvalidOperationException("it needs an answer this draft does not have yet.");
                    await InvokeAfterOpAsync(a).ConfigureAwait(false);
                    r.Status = DraftAfterStatus.Ran;
                    r.Text = "Done.";
                }
                catch (Exception ex)
                {
                    var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    r.Status = DraftAfterStatus.CouldNotRun;
                    r.Text = LooksOffline(inner.Message) ? "not connected." : inner.Message;
                }
                results.Add(r);
                if (r.Status != DraftAfterStatus.Ran) break;
            }
            return results;
        }

        /// <summary>The after ops that write only with commit=true (OpParameterKinds classes commit as internal for
        /// each, so no workflow can set it).</summary>
        private static readonly HashSet<string> CommitOps = new HashSet<string>(StringComparer.Ordinal)
        {
            "refresh_partition", "deploy_stage", "deploy_live",
        };

        private async Task<object> InvokeAfterOpAsync(ComposedAction a)
        {
            var seam = DraftAfterOpForTest;
            if (seam != null) return await seam(a.Op, ArgsJson(a)).ConfigureAwait(false);
            if (!OpSurface.Methods.TryGetValue(a.Op, out var method)) throw new InvalidOperationException($"'{a.Op}' is not a known op.");
            var args = BindArgs(a.Op, method.GetParameters(), ArgsJson(a));
            var t = (Task)method.Invoke(null, args);
            await t.ConfigureAwait(false);
            return UnwrapTaskResult(t);
        }

        /// <summary>Why a write op's report says it did not write, or null when it wrote (or the op has no such
        /// report). A deploy with nothing to deploy wrote nothing and did nothing wrong.</summary>
        private static string WriteProblem(object report) => report switch
        {
            RefreshReport x when !string.IsNullOrWhiteSpace(x.Error) => x.Error,
            RefreshReport x when !x.Committed => "the refresh did not run.",
            DeployReport x when !string.IsNullOrWhiteSpace(x.Error) => x.Error,
            DeployReport x when !x.Committed && x.TotalChanges > 0 => "nothing was deployed.",
            DeployStageReport x when !string.IsNullOrWhiteSpace(x.Error) => x.Error,
            DeployStageReport x when !x.Committed => "nothing was deployed.",
            EvidenceSaveResult x when !x.Saved => x.Error ?? x.Note ?? "the record was not saved.",
            _ => null,
        };

        private static bool LooksOffline(string message) =>
            message != null && (message.IndexOf("not connected", StringComparison.OrdinalIgnoreCase) >= 0
                             || message.IndexOf("no live", StringComparison.OrdinalIgnoreCase) >= 0
                             || message.IndexOf("live connection", StringComparison.OrdinalIgnoreCase) >= 0
                             || message.IndexOf("connect_", StringComparison.OrdinalIgnoreCase) >= 0);

        private static string ArgsJson(ComposedAction a)
        {
            var o = new System.Text.Json.Nodes.JsonObject();
            foreach (var kv in a.Args)
            {
                var v = kv.Value;
                System.Text.Json.Nodes.JsonNode node;
                if (v == null) node = null;
                else if (v == "true" || v == "false") node = System.Text.Json.Nodes.JsonValue.Create(v == "true");
                else if (v.StartsWith("[", StringComparison.Ordinal) || v.StartsWith("{", StringComparison.Ordinal))
                {
                    try { node = System.Text.Json.Nodes.JsonNode.Parse(v); } catch { node = System.Text.Json.Nodes.JsonValue.Create(v); }
                }
                else if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var num) && v.Trim() == v)
                    node = System.Text.Json.Nodes.JsonValue.Create(num);
                else node = System.Text.Json.Nodes.JsonValue.Create(v);
                o[kv.Key] = node;
            }
            return o.ToJsonString();
        }

        /// <summary>One after check. A check that needs a live connection and has none reads "Could not check: not
        /// connected", never a pass (composer contract 1.7).</summary>
        private async Task<DraftAfterResult> AfterCheckAsync(Draft d, DraftStep st, ComposedCheck c)
        {
            var r = new DraftAfterResult { StepId = st.Id, StepNumber = st.Number, What = "check", Kind = c.Kind };
            var answers = AnswersOf(d);
            c = FilledCheck(c, answers);
            // What the step made, named as it ran: its answers and its written parts filled in, so a measure whose table
            // was a question and whose name was a part is found by its real ref.
            var made = CreatedBy(new DraftStep
            {
                Actions = st.Actions.Select(a =>
                {
                    var f = Filled(a, answers);
                    foreach (var b in st.Blanks.Where(b => b.Text != null))
                        foreach (var k in f.Args.Keys.ToList())
                            if (f.Args[k] == "{{blanks." + b.Name + "}}") f.Args[k] = b.Text;
                    return f;
                }).ToList(),
            }).ToList();
            switch (c.Kind)
            {
                // The measure this step made gives a number. With a number the person knew (new-measure's verification
                // value), it must match it; without one, the line says it was exercised and not reconciled, and a pass means
                // only that (lane author-gaps, C7).
                case "dax_probe":
                {
                    var target = c.Target != null && c.Target.StartsWith("measure:", StringComparison.Ordinal) ? c.Target
                                 : made.FirstOrDefault(x => x.StartsWith("measure:", StringComparison.Ordinal));
                    if (target == null) { r.Status = DraftAfterStatus.CouldNotCheck; r.Text = "Could not check: this step makes no measure."; break; }
                    var shown = target.Substring(target.LastIndexOf('/') + 1);
                    var value = await ProbeValueAsync(target).ConfigureAwait(false);
                    if (value == null) { r.Status = DraftAfterStatus.CouldNotCheck; r.Text = DraftCopy.NotConnected; break; }
                    var q = c.Of?.FirstOrDefault();
                    var known = q != null && answers.TryGetValue(q, out var a) && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : (double?)null;
                    var got = value.Value.ToString("#,##0.####", CultureInfo.InvariantCulture);
                    if (known == null) { r.Status = DraftAfterStatus.Passed; r.Text = shown + " gives " + got + ". It was not checked against a number you know."; break; }
                    var ok = Math.Abs(value.Value - known.Value) <= 1e-6 * Math.Max(1.0, Math.Abs(known.Value));
                    r.Status = ok ? DraftAfterStatus.Passed : DraftAfterStatus.Failed;
                    r.Text = ok ? shown + " gives " + got + ", the number you gave."
                                : shown + " gives " + got + ". The number you gave is " + known.Value.ToString("#,##0.####", CultureInfo.InvariantCulture) + ".";
                    break;
                }
                // The best practice rules on the object this step made, offline (new-measure's bpa_clean, scope object).
                case "bpa_clean":
                {
                    var target = c.Target ?? made.FirstOrDefault();
                    if (target == null) { r.Status = DraftAfterStatus.CouldNotCheck; r.Text = "Could not check: this step makes nothing to check."; break; }
                    var shown = target.Substring(Math.Max(target.LastIndexOf('/'), target.IndexOf(':')) + 1);
                    try
                    {
                        var card = await BpaScanAsync().ConfigureAwait(false);
                        var broken = card.Violations.Where(v => !v.Waived && string.Equals(v.ObjectRef, target, StringComparison.OrdinalIgnoreCase)).ToList();
                        r.Status = broken.Count == 0 ? DraftAfterStatus.Passed : DraftAfterStatus.Failed;
                        r.Text = broken.Count == 0 ? shown + " passes the best practice rules."
                               : shown + " breaks " + (broken.Count == 1 ? "a best practice rule: " : broken.Count.ToString(CultureInfo.InvariantCulture) + " best practice rules: ")
                                 + string.Join("; ", broken.Select(v => v.RuleName).Distinct()) + ".";
                    }
                    catch (Exception ex) { r.Status = DraftAfterStatus.CouldNotCheck; r.Text = "Could not check: " + ex.Message; }
                    break;
                }
                case "result_range":
                {
                    var target = c.Target ?? made.FirstOrDefault(x => x.StartsWith("measure:", StringComparison.Ordinal))
                                 ?? st.Actions.Select(a => a.Args.TryGetValue("objRef", out var o) ? o : null).FirstOrDefault(o => o != null && o.StartsWith("measure:", StringComparison.Ordinal));
                    var name = target == null ? "The result" : PlanTasks.NameOfRef(target);
                    var shown = target == null ? name : target.Substring(target.LastIndexOf('/') + 1);
                    var value = target == null ? null : await ProbeValueAsync(target).ConfigureAwait(false);
                    if (value == null) { r.Status = DraftAfterStatus.CouldNotCheck; r.Text = DraftCopy.NotConnected; break; }
                    var ok = (c.Min == null || value >= c.Min) && (c.Max == null || value <= c.Max);
                    r.Status = ok ? DraftAfterStatus.Passed : DraftAfterStatus.Failed;
                    r.Text = ok ? shown + " is " + DraftCopy.Percent(value) + ", inside " + DraftCopy.Percent(c.Min) + " to " + DraftCopy.Percent(c.Max) + "."
                                : DraftCopy.RangeFailed(shown, value.Value, c.Min, c.Max);
                    break;
                }
                case "row_count":
                {
                    var table = c.Target ?? made.FirstOrDefault(x => x.StartsWith("table:", StringComparison.Ordinal))
                                ?? st.Actions.Select(a => a.Args.TryGetValue("tableRef", out var t) ? t : null).FirstOrDefault(t => t != null);
                    var value = table == null ? null : await ProbeValueAsync("rows:" + table).ConfigureAwait(false);
                    if (value == null) { r.Status = DraftAfterStatus.CouldNotCheck; r.Text = DraftCopy.NotConnected; break; }
                    var shownTable = PlanTasks.NameOfRef(table);
                    var rowsText = shownTable + " has " + value.Value.ToString("0", CultureInfo.InvariantCulture) + " rows";
                    // A row count the person knew from the source (the parked import-table's control total; lane l9-finish).
                    var of = c.Of?.FirstOrDefault();
                    if (of != null && answers.TryGetValue(of, out var knownText)
                        && double.TryParse(knownText, NumberStyles.Float, CultureInfo.InvariantCulture, out var knownRows))
                    {
                        var same = Math.Abs(value.Value - knownRows) < 0.5;
                        r.Status = same ? DraftAfterStatus.Passed : DraftAfterStatus.Failed;
                        r.Text = rowsText + (same ? ", the number you gave." : ". The number you gave is " + knownRows.ToString("#,##0", CultureInfo.InvariantCulture) + ".");
                        break;
                    }
                    var ok = (c.Min == null || value >= c.Min) && (c.Max == null || value <= c.Max);
                    r.Status = ok ? DraftAfterStatus.Passed : DraftAfterStatus.Failed;
                    r.Text = rowsText
                           + (ok ? "." : ". The check needs " + (c.Min?.ToString("0", CultureInfo.InvariantCulture) ?? "any") + " to " + (c.Max?.ToString("0", CultureInfo.InvariantCulture) ?? "any") + ".");
                    break;
                }
                default:
                    r.Status = DraftAfterStatus.CouldNotCheck;
                    r.Text = "Could not check: this kind of check runs in guided workflows.";
                    break;
            }
            return r;
        }

        /// <summary>A live number: a measure's value, or "rows:table:X" for a row count. Null when it cannot be read.</summary>
        private async Task<double?> ProbeValueAsync(string what)
        {
            var seam = DraftProbeForTest;
            if (seam != null) return await seam(what).ConfigureAwait(false);
            try
            {
                string query;
                if (what.StartsWith("rows:", StringComparison.Ordinal))
                {
                    var table = what.Substring(5);
                    table = table.StartsWith("table:", StringComparison.Ordinal) ? table.Substring(6) : table;
                    query = "EVALUATE ROW(\"v\", COUNTROWS('" + table.Replace("'", "''") + "'))";
                }
                else
                {
                    var rest = what.Substring(what.IndexOf(':') + 1);
                    var slash = rest.IndexOf('/');
                    var measure = slash < 0 ? rest : rest.Substring(slash + 1);
                    query = "EVALUATE ROW(\"v\", [" + measure.Replace("]", "]]") + "])";
                }
                var rs = await RunDaxAsync(query, 1, "human").ConfigureAwait(false);
                if (rs == null || !string.IsNullOrEmpty(rs.Error) || rs.Rows.Length == 0 || rs.Rows[0].Length == 0 || rs.Rows[0][0] == null) return null;
                return Convert.ToDouble(rs.Rows[0][0], CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        // ---- the ops both doors call -----------------------------------------------------------------------

        public async Task<DraftView> GetDraftAsync(string draftId)
        {
            RequireProFeature();
            var p = BindDrafts();
            var d = RequireDraft(p, draftId);
            await FreshenNotReadWhyAsync(d).ConfigureAwait(false);
            var view = ViewOf(d, TaskReader(p), null);
            await FillOptionsAsync(d, view).ConfigureAwait(false);
            return view;
        }

        /// <summary>The reason kept on a draft for words no ask could read (Draft.NotReadWhy, D5) was true when that ask
        /// stopped. Before a draft with kept words is shown it is read again: the reason that stops an ask now, or none
        /// once one could leave. So the note never tells a person they have not given the permission they just gave
        /// (verifier on m11/sol-fixes-1 round 2, VR4), and never promises that Add more words asks Jev once permission is
        /// withdrawn. It changes the copy being shown; the draft saved keeps the reason of its last ask.</summary>
        private async Task FreshenNotReadWhyAsync(Draft d)
        {
            if (d?.NotRead == null) return;
            try
            {
                var why = await Decisions.WouldNotAskAsync(Semanticus.Decisions.DecisionRegistry.ComposeDraft, Entitlement.FeatureGrants.IsProTier(_entitlement),
                    DecisionPointEnabled(Semanticus.Decisions.DecisionRegistry.ComposeDraft)).ConfigureAwait(false);
                d.NotReadWhy = why == null ? null : DraftCopy.JevNotAsked(why);
            }
            catch { /* the gates could not be read: the reason of the last ask stands */ }
        }

        /// <summary>edit_draft: keep, drop, answer, write, pick, ask_again, check_again. Every action changes the
        /// draft only; the model changes only when the draft runs.</summary>
        public async Task<DraftView> EditDraftAsync(string draftId, string action, string step, string blank, string question, string text, string origin)
        {
            RequireProFeature();
            origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin;
            var act = (action ?? "").Trim().ToLowerInvariant();
            var p = BindDrafts();
            string sentence;
            string id;
            DraftDocument document = null;
            await _draftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = RequireDraft(p, draftId);
                id = d.Id;
                // The two actions on a draft that already ran: they touch what came after the change, never the change.
                if ((act == "try_again" || act == "keep_anyway") && RunTakenBack(d))
                    throw new InvalidOperationException(DraftCopy.TakenBack);
                if (act == "try_again" || act == "keep_anyway")
                {
                    sentence = act == "try_again" ? await TryAgainAsync(d).ConfigureAwait(false) : KeepAnyway(d);
                    WriteDraft(p, d);
                    goto changed;
                }
                // A run paused at a step someone else does takes an answer for that step or a later one, and a fresh
                // request for its assistant's step; everything else about it is fixed once it changed the model.
                if (d.Run?.Outcome == "paused" && (act == "answer" || act == "ask_again" && string.IsNullOrWhiteSpace(blank)))
                {
                    var at = d.Steps.First(s => s.Id == d.Run.PausedAt);
                    if (act == "answer")
                    {
                        var q = d.Questions.FirstOrDefault(x => string.Equals(x.Name, question?.Trim(), StringComparison.Ordinal));
                        var on = q?.Step == null ? null : d.Steps.FirstOrDefault(s => s.Id == q.Step);
                        if (q != null && (on == null || on.Number < at.Number))
                            throw new InvalidOperationException("That answer was used by a step that already ran, so it is fixed now.");
                        sentence = await AnswerAsync(p, d, question, text, origin).ConfigureAwait(false);
                    }
                    else
                    {
                        if (!at.ByAssistant || (!string.IsNullOrWhiteSpace(step) && RequireStep(d, step) != at))
                            throw new InvalidOperationException("Ask again works on the assistant's step the run waits at.");
                        await CancelStepRequestAsync(p, at, "Asked again.").ConfigureAwait(false);
                        at.Reason = null;
                        at.Attempts = 0;
                        await PostStepRequestAsync(p, d, at).ConfigureAwait(false);
                        sentence = at.RequestId == null ? (at.Reason ?? "Could not ask again.") : "Asked your assistant again. It sees this on its next call.";
                    }
                    WriteDraft(p, d);
                    goto changed;
                }
                if (act == "continue")
                    throw new InvalidOperationException("Continue is its own tool: call continue_draft with the draft id (the person's word in chat is enough).");
                if (act == "reorder")
                {
                    if (d.Run != null && d.Run.Outcome != "not_ready") throw new InvalidOperationException("It has run, so its steps are fixed now.");
                    sentence = Reorder(d, RequireStep(d, step), text);
                    WriteDraft(p, d);
                    goto changed;
                }
                if (act == "open_in_author")
                {
                    // The words no ask read come along as steps to fill in, as the Workflows door keeps them: Author must
                    // not get the drafted steps alone with no word of the rest (verifier on m11/sol-fixes-1, D6).
                    document = RenderDraftDocument(DescribedSteps.WithNotReadSteps(d));
                    sentence = "The draft as a workflow, not saved yet. Save it to keep it."
                             + (d.NotRead == null || d.NotRead.Count == 0 ? "" : " " + DraftCopy.OpenedWithNotRead(d.NotRead.Count))
                             + (DescribedSteps.WordsWithNoStep(d).Count == 0 ? "" : " " + DraftCopy.OpenedWithNoStep(DescribedSteps.WordsWithNoStep(d).Count));
                    goto changed;
                }
                // A run that kept nothing (an applier refused, a check stopped it at step 1, a restart cut it off) leaves
                // the draft open to fix; a run that changed the model fixes its steps.
                // A change History took back leaves the draft as it was before it ran: open to change, and not approved,
                // because the approval was for a run that is no longer in the model.
                if (d.Run != null && RunTakenBack(d)) { d.Run = null; d.Approved = false; d.PersonApproved = false; d.PersonApprovedDigest = null; }
                if (d.Run != null && act != "check_again")
                {
                    if (d.Run.Outcome == "paused" && !d.Run.Changed)
                        foreach (var s in d.Steps.Where(s => s.ByAssistant)) await CancelStepRequestAsync(p, s, "The draft changed.").ConfigureAwait(false);
                    if (d.Run.Outcome == "running" && !RunCutOff(d)) throw new InvalidOperationException("It is running now.");
                    if (d.Run.Changed)
                        throw new InvalidOperationException("This draft has changed the model, so its steps are fixed now. Undo the change and draft it again to change it.");
                    d.Run = null;
                }
                switch (act)
                {
                    case "keep":
                    case "drop":
                        sentence = await KeepOrDropAsync(p, d, RequireStep(d, step), act == "keep").ConfigureAwait(false);
                        break;
                    case "answer":
                        sentence = await AnswerAsync(p, d, question, text, origin).ConfigureAwait(false);
                        break;
                    case "write":
                        sentence = await WriteYourselfAsync(p, d, step, blank, text, origin).ConfigureAwait(false);
                        break;
                    case "pick":
                    case "ask_again":
                    {
                        var (st, b) = RequireBlank(d, step, blank);
                        if (act == "pick")
                        {
                            var choice = text?.Trim();
                            if (b.AskChoices == null || !b.AskChoices.Contains(choice, StringComparer.Ordinal))
                                throw new ArgumentException("Pick one of the choices your assistant offered: " + string.Join(", ", b.AskChoices ?? Array.Empty<string>()) + ".", nameof(text));
                            b.Picked = choice;
                        }
                        await CancelRequestAsync(p, b, "Asked again.").ConfigureAwait(false);
                        b.State = DraftBlankStates.Empty;
                        b.Reason = null;
                        b.NeedsWhy = null;
                        b.AskChoices = null;
                        b.Attempts = 0;
                        await PostBlankAsync(p, d, st, b).ConfigureAwait(false);
                        sentence = b.RequestId == null ? (b.Reason ?? "Could not ask again.") : "Asked your assistant again. It sees this on its next call.";
                        break;
                    }
                    case "keep_theirs":
                        sentence = await KeepTheirsAsync(p, d).ConfigureAwait(false);
                        break;
                    case "apply_draft":
                        // Writing over the person's newer edit: the person's word in chat is enough (Kane, 10 Oct 2026), and Undo brings theirs back.
                        sentence = ApplyDraftOverIt(d);
                        break;
                    case "check_again":
                        sentence = await CheckAgainAsync(p, d).ConfigureAwait(false);
                        break;
                    case "add":
                        sentence = await AddMenuStepAsync(p, d, text).ConfigureAwait(false);
                        break;
                    case "refine":
                        sentence = await RefineAsync(p, d, text).ConfigureAwait(false);
                        break;
                    default:
                        throw new ArgumentException("The action is one of: keep, drop, answer, write, pick, ask_again, check_again, add, refine, reorder, open_in_author, try_again, keep_anyway, keep_theirs, apply_draft.", nameof(action));
                }
                if (d.Approved || d.Origin == "agent") await PostOpenBlanksAsync(p, d).ConfigureAwait(false);
                else await GenerateBlanksAsync(d).ConfigureAwait(false);
                await SnapshotBaselinesAsync(d).ConfigureAwait(false);
                WriteDraft(p, d);
                d = await RunIfReadyAsync(p, d).ConfigureAwait(false);
                if (d.Run?.Outcome == "applied") sentence += " Every part was ready and the draft was approved, so the change ran.";
            changed:;
            }
            finally { _draftGate.Release(); }
            var view = await DraftChangedAsync(p, id, sentence).ConfigureAwait(false);
            if (view != null && document != null) view.Document = document;
            return view;
        }

        private static DraftStep RequireStep(Draft d, string step)
        {
            var key = step?.Trim();
            return d.Steps.FirstOrDefault(s => string.Equals(s.Id, key, StringComparison.Ordinal)
                                               || s.Number.ToString(CultureInfo.InvariantCulture) == key)
                ?? throw new ArgumentException("Name the step by its id or number: " + string.Join(", ", d.Steps.Select(s => s.Number + " (" + s.Id + ")")) + ".", nameof(step));
        }

        private static (DraftStep, DraftBlank) RequireBlank(Draft d, string step, string blank)
        {
            var candidates = d.Steps.Where(s => string.IsNullOrWhiteSpace(step) || s.Id == step.Trim() || s.Number.ToString(CultureInfo.InvariantCulture) == step.Trim())
                .SelectMany(s => s.Blanks.Select(b => (s, b)))
                .Where(x => string.IsNullOrWhiteSpace(blank) || x.b.Name == blank.Trim()).ToList();
            if (candidates.Count == 1) return candidates[0];
            throw new ArgumentException("Name the part by its step and blank: " + string.Join(", ",
                d.Steps.SelectMany(s => s.Blanks.Select(b => "step " + s.Number + " blank " + b.Name))) + ".", nameof(blank));
        }

        private async Task<string> KeepOrDropAsync(DraftPlace p, Draft d, DraftStep st, bool keep)
        {
            if (keep)
            {
                var back = d.Steps.Where(s => s == st || s.DroppedWith == st.Number).ToList();
                foreach (var s in back)
                {
                    s.Kept = true;
                    s.TickedFromWords = null;   // the person's own choice now: a later typing does not untick it
                    s.DroppedWith = null;
                    foreach (var b in s.Blanks.Where(b => !DraftBlankStates.Passed(b.State))) { b.State = DraftBlankStates.Empty; b.Reason = null; b.NeedsWhy = null; }
                }
                if (d.Approved || d.Origin == "agent") await PostOpenBlanksAsync(p, d).ConfigureAwait(false);
                return "Kept step " + st.Number.ToString(CultureInfo.InvariantCulture) + ".";
            }

            // Drop cascades to every kept step that needs it (advisor section 6).
            var dropped = new List<DraftStep> { st };
            bool grew;
            do
            {
                grew = false;
                foreach (var s in d.Steps.Where(s => s.Kept && !dropped.Contains(s)))
                    if (NeedsOf(d, s).Any(n => dropped.Any(x => x.Number == n))) { dropped.Add(s); grew = true; }
            } while (grew);
            foreach (var s in dropped)
            {
                s.Kept = false;
                s.TickedFromWords = null;
                s.DroppedWith = s == st ? (int?)null : st.Number;
                foreach (var b in s.Blanks) await CancelRequestAsync(p, b, "The step was dropped.").ConfigureAwait(false);
            }
            var also = dropped.Where(s => s != st).Select(s => s.Number).OrderBy(n => n).ToArray();
            return "Dropped step " + st.Number.ToString(CultureInfo.InvariantCulture) + "."
                   + (also.Length == 0 ? "" : " Also dropped " + (also.Length == 1 ? "step " : "steps ") + string.Join(", ", also) + ", which need it.");
        }

        private async Task<string> AnswerAsync(DraftPlace p, Draft d, string question, string text, string origin)
        {
            var q = d.Questions.FirstOrDefault(x => string.Equals(x.Name, question?.Trim(), StringComparison.Ordinal))
                ?? throw new ArgumentException("Name the question: " + string.Join(", ", d.Questions.Select(x => x.Name)) + ".", nameof(question));
            var answer = text?.Trim();
            if (string.IsNullOrEmpty(answer)) throw new ArgumentException("Give the answer.", nameof(text));
            if (q.Type == "enum" && q.Values.Length > 0 && !q.Values.Contains(answer, StringComparer.Ordinal))
                throw new ArgumentException("The answer is one of: " + string.Join(", ", q.Values) + ".", nameof(text));
            if (q.Type == "number" && !double.TryParse(answer, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                throw new ArgumentException("The answer is a number.", nameof(text));
            if (q.Type == "objectRef") await RequireObjectAnswerAsync(q, answer).ConfigureAwait(false);
            var before = AnswersOf(d);
            q.Answer = answer;
            q.AnsweredBy = origin == "agent" ? "assistant" : "you";
            ResolveSemanticusAnswers(d);
            var after = AnswersOf(d);
            // Every answer that moved, this one and any Semanticus answer filled from it.
            var moved = after.Keys.Union(before.Keys).Where(k => !before.TryGetValue(k, out var x) || !after.TryGetValue(k, out var y) || x != y).ToHashSet(StringComparer.Ordinal);
            if (moved.Count == 0) return "Answered.";
            // Changing an answer empties the parts written from it and asks again (contract 1.5, dependsOn).
            var redone = 0;
            foreach (var st in d.Steps)
                foreach (var b in st.Blanks.Where(b => (b.DependsOn ?? Array.Empty<string>()).Any(moved.Contains) && b.State != DraftBlankStates.Empty))
                {
                    await CancelRequestAsync(p, b, "The answer it depends on changed.").ConfigureAwait(false);
                    b.State = DraftBlankStates.Empty; b.Text = null; b.FilledBy = null; b.Reason = null; b.NeedsWhy = null; b.Attempts = 0;
                    redone++;
                    // A blank a generator writes is written again after this, and only posted if the generator no longer applies.
                    if (b.By == "assistant" && b.Generator == null && (d.Approved || d.Origin == "agent")) await PostBlankAsync(p, d, st, b).ConfigureAwait(false);
                }
            return redone == 0 ? "Answered." : "Answered. " + (redone == 1 ? "1 part is" : redone + " parts are") + " written again from the new answer.";
        }

        /// <summary>An object answer names an object in this model, and one of the kind the question lists (UAT S12-2-005:
        /// "42" was kept as the answer and marked answered). Checked against the whole list, not the first 300 a door
        /// shows, so a large model's objects are all answerable.</summary>
        private async Task RequireObjectAnswerAsync(DraftQuestion q, string answer)
        {
            var s = _sessions.Current;
            if (s == null) return;   // no model to check against: the run refuses a missing object then
            var (ok, example) = await s.ReadAsync(m =>
            {
                var all = q.From == null ? null : OptionsOf(m, q.From, int.MaxValue);
                if (all != null)
                    return (all.Any(o => string.Equals(o.Ref, answer, StringComparison.OrdinalIgnoreCase)), all.FirstOrDefault()?.Ref);
                object found;
                try { found = ObjectRefs.Resolve(m, answer); } catch { found = null; }
                return (found != null, m.Tables.FirstOrDefault() is TabularEditor.TOMWrapper.Table t ? ObjectRefs.For(t) : null);
            }).ConfigureAwait(false);
            if (ok) return;
            throw new ArgumentException("'" + answer + "' is not " + (q.From == null ? "an object in this model" : "one of the choices this question lists")
                + ". Answer with an object's ref" + (example == null ? "." : ", like " + example + "."), "text");
        }

        private async Task<string> WriteYourselfAsync(DraftPlace p, Draft d, string step, string blank, string text, string origin)
        {
            var (st, b) = RequireBlank(d, step, blank);
            var words = text?.Trim();
            if (string.IsNullOrEmpty(words)) throw new ArgumentException("Write the text first.", nameof(text));
            var check = await CheckDraftBlankAsync(ChangeOf(d), st.Id, b.Name, words).ConfigureAwait(false);
            if (!check.Passed) throw new InvalidOperationException("Not written: " + FailureText(check) + " Change it and write it again.");
            await CancelRequestAsync(p, b, "The person wrote it themselves.").ConfigureAwait(false);
            b.State = DraftBlankStates.Self;
            b.Text = words;
            b.FilledBy = origin == "agent" ? "you, in chat" : "you";
            b.Reason = null;
            b.NeedsWhy = null;
            b.AskChoices = null;
            b.Attempts = 0;
            return "Written and checked.";
        }

        // ---- a value changed after the draft was made (UAT S12-5-004) ------------------------------------------------

        /// <summary>Record what each property the kept change steps write says now, for every one the draft has not
        /// recorded yet. Called whenever the draft changes, so a step's object is recorded as soon as it is known (a
        /// question answered, a part written), and never again after that: a later edit to it is what the run checks
        /// for. An object not in the model yet (a step of this draft creates it) is left out.</summary>
        private async Task SnapshotBaselinesAsync(Draft d)
        {
            var s = _sessions.Current;
            if (s == null) return;
            List<(string Kind, string Ref)> lines;
            try
            {
                lines = ComposedChangeCompiler.Compile(ChangeOf(d), BlankFill.Placeholder).Steps
                    .SelectMany(st => st.Lines).Where(l => GuardedKinds.Contains(l.Item.Kind ?? ""))
                    .Select(l => (l.Item.Kind, l.Item.ObjectRef)).ToList();
            }
            catch { return; }   // a step with a value still to fill: its object is recorded once it is filled
            var wanted = lines.Where(l => d.Baselines == null || !d.Baselines.ContainsKey(BaselineKey(l.Kind, l.Ref))).Distinct().ToList();
            if (wanted.Count == 0) return;
            var now = await s.ReadAsync(m => wanted.Select(l => (l.Kind, l.Ref, Value: ValueNow(m, l.Kind, l.Ref))).ToList()).ConfigureAwait(false);
            foreach (var (kind, objRef, value) in now.Where(x => x.Value != null))
            {
                d.Baselines ??= new Dictionary<string, string>(StringComparer.Ordinal);
                d.Baselines[BaselineKey(kind, objRef)] = value;
            }
        }

        /// <summary>Keep yours: the step that would have written over the newer value is dropped (with any step that
        /// needs it), and the draft goes on without it.</summary>
        private async Task<string> KeepTheirsAsync(DraftPlace p, Draft d)
        {
            var o = d.Overwrite ?? throw new InvalidOperationException("Nothing in this draft is waiting for that choice.");
            d.Overwrite = null;
            var step = d.Steps.FirstOrDefault(s => s.Id == o.StepId);
            if (step != null && step.Kept) await KeepOrDropAsync(p, d, step, false).ConfigureAwait(false);
            return DraftCopy.KeptYours;
        }

        /// <summary>Apply the draft: the newer value is what the draft is now made against, so the run writes over it.</summary>
        private static string ApplyDraftOverIt(Draft d)
        {
            var o = d.Overwrite ?? throw new InvalidOperationException("Nothing in this draft is waiting for that choice.");
            d.Overwrite = null;
            d.Baselines ??= new Dictionary<string, string>(StringComparer.Ordinal);
            d.Baselines[BaselineKey(o.Kind, o.ObjectRef)] = o.Now;
            return DraftCopy.AppliedDraft;
        }

        private async Task<string> CheckAgainAsync(DraftPlace p, Draft d)
        {
            var back = 0;
            foreach (var st in d.Steps.Where(s => s.Kept && s.Phase == DraftPhases.Change))
                foreach (var b in st.Blanks.Where(b => b.State == DraftBlankStates.Needs && b.NeedsWhy == "evidence" && b.Text != null))
                {
                    var again = await CheckDraftBlankAsync(ChangeOf(d), st.Id, b.Name, b.Text).ConfigureAwait(false);
                    if (!again.Passed) continue;
                    b.State = b.FilledBy == "assistant" ? DraftBlankStates.Filled : DraftBlankStates.Self;
                    b.Reason = null;
                    b.NeedsWhy = null;
                    back++;
                }
            return back == 0 ? "Checked again. Nothing passes yet." : "Checked again. " + (back == 1 ? "1 part passes" : back + " parts pass") + " now.";
        }

        /// <summary>Close the open request of a blank, if it has one.</summary>
        private Task CancelRequestAsync(DraftPlace p, DraftBlank b, string why)
        {
            if (string.IsNullOrEmpty(b.RequestId)) return Task.CompletedTask;
            CloseTask(p, b.RequestId, t => { t.State = PlanTaskStates.SetAside; t.DeclineReason = why; });
            b.RequestId = null;
            return Task.CompletedTask;
        }

        /// <summary>run_draft: records the approval and puts any unsent part on the queue. Runs now when ready;
        /// otherwise the change runs when the last part passes. Refused for a held draft on every door.</summary>
        public async Task<DraftView> RunDraftAsync(string draftId, string origin)
        {
            RequireProFeature();
            origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin;
            var p = BindDrafts();
            string sentence, id;
            await _draftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = RequireDraft(p, draftId);
                id = d.Id;
                // the approval the assistant's door gave this run was for the draft as the person was shown it; edit_draft changes it after the yes,
                // and a draft is not in the model, so the revision does not say (join 6, area B)
                RequireApprovedBasis(nameof(RunDraftAsync), DraftBasis(d), "draft");
                PrepareToRunAgain(d);
                var hold = HoldOf(d);
                if (hold != null && !PersonApprovalStands(d)) throw new InvalidOperationException(DraftCopy.HeldRefusal(hold));
                d.Approved = true;
                d.ApprovedBy = origin;
                d.ApprovedUtc = DateTimeOffset.UtcNow;
                await PostOpenBlanksAsync(p, d).ConfigureAwait(false);
                WriteDraft(p, d);
                d = await RunIfReadyAsync(p, d).ConfigureAwait(false);
                sentence = d.Run?.Outcome == "applied" ? "It ran as one change."
                         : d.Run != null && d.Run.Outcome != "running" ? "It did not finish. Read the banner for why."
                         : DraftCopy.NothingChangedYet + " It runs by itself when every part passes its checks.";
                // THE ANSWER NAMES EACH KEPT ADD (the M11 join, coordinator ruling on VR19): an assistant's run_draft counts as the
                // person's review, so what the run adds is said in the answer the assistant reads, never left to be found in the model.
                var adds = KeptAddsOf(d);
                if (adds.Length > 0) sentence += " " + DraftCopy.KeptAdds(adds, d.Run?.Outcome == "applied");
            }
            finally { _draftGate.Release(); }
            return await DraftChangedAsync(p, id, sentence).ConfigureAwait(false);
        }

        /// <summary>Each kept step that adds something to the model, as the run answer names it: its title, and the words it was ticked
        /// for when the engine ticked it from the person's words.</summary>
        internal static string[] KeptAddsOf(Draft d) =>
            d.Steps.Where(s => s.Kept && s.Phase == DraftPhases.Change && s.Actions.Any(a => AddingRule.Adds(a.Op)))
                .Select(s => (string.IsNullOrWhiteSpace(s.Title) ? s.Id : s.Title)
                    + (string.IsNullOrWhiteSpace(s.TickedFromWords) ? "" : " (from \"" + s.TickedFromWords.Trim() + "\")"))
                .ToArray();

        /// <summary>Approve and run on a held draft: the person's Approve and run, or the assistant's when the person said so in chat (Kane, 10 Oct 2026). It
        /// counts only when every part has passed, so the person approves words they could read. Pressed earlier, it
        /// sends the parts to the assistant and approves nothing yet; the draft then waits for the person again.</summary>
        public async Task<DraftView> ApproveHeldDraftAsync(string draftId, string origin = "human")
        {
            RequireProFeature();
            var p = BindDrafts();
            string id, sentence;
            await _draftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = RequireDraft(p, draftId);
                id = d.Id;
                PrepareToRunAgain(d);
                d.Approved = true;
                d.ApprovedBy = string.Equals(origin, "agent", StringComparison.OrdinalIgnoreCase) ? "agent" : "human";
                d.ApprovedUtc = DateTimeOffset.UtcNow;
                await PostOpenBlanksAsync(p, d).ConfigureAwait(false);
                var passed = EveryPartPassed(d, TaskReader(p));
                d.PersonApproved = passed;
                d.PersonApprovedDigest = passed ? ApprovedContentOf(d) : null;
                WriteDraft(p, d);
                await RunIfReadyAsync(p, d).ConfigureAwait(false);
                sentence = passed ? "Approved."
                         : HoldOf(d) != null ? DraftCopy.ApproveAfterParts
                         : DraftCopy.NothingChangedYet + " It runs by itself when every part passes its checks.";
            }
            finally { _draftGate.Release(); }
            return await DraftChangedAsync(p, id, sentence).ConfigureAwait(false);
        }

        /// <summary>A draft that stopped with nothing changed, or that a restart cut off, may run again. One that
        /// kept steps may not: running it again would make those steps twice.</summary>
        private void PrepareToRunAgain(Draft d)
        {
            if (d.Run == null || d.Run.Outcome == "not_ready" || RunTakenBack(d)) { d.Run = null; return; }
            if (d.Run.Outcome == "running" && RunCutOff(d)) { d.Run = null; return; }
            // Waiting at a gated step whose guided sub-run a restart ended: cut off, like a run a restart stopped.
            if (d.Run.Outcome == "paused" && GuidedRunLost(d) && d.Steps.Any(s => s.Id == d.Run.PausedAt && s.Guided)) { d.Run = null; return; }
            if (d.Run.Outcome == "running") throw new InvalidOperationException("It is running now.");
            if (d.Run.Outcome == "paused")
                throw new InvalidOperationException("It is waiting at step " + (d.Steps.FirstOrDefault(s => s.Id == d.Run.PausedAt)?.Number ?? 0).ToString(CultureInfo.InvariantCulture)
                    + ". Continue it there, or undo what ran and draft it again.");
            if (d.Run.Outcome == "applied") throw new InvalidOperationException("It already ran. Undo the change first to run it again.");
            if (d.Run.Changed)
                throw new InvalidOperationException("Steps that ran before it stopped are kept as one change. Undo them first, then run the draft again.");
            d.Run = null;
        }

        /// <summary>After any change to a draft: the Plan moves (the page re-reads it) and the draft is pushed on
        /// its own channel. The agent door learns on its next call (golden rule 2's stated cost).</summary>
        private async Task<DraftView> DraftChangedAsync(DraftPlace p, string id, string sentence)
        {
            var d = ReadDraft(p, id);
            await FreshenNotReadWhyAsync(d).ConfigureAwait(false);
            var view = d == null ? null : ViewOf(d, TaskReader(p), sentence);
            if (view != null) await FillOptionsAsync(d, view).ConfigureAwait(false);
            try { await ComposeAsync(null, p.Bound).ConfigureAwait(false); } catch { }
            TellThePlanMoved(p.Bound);
            if (view != null) try { _sessions.Bus.PublishDraft(view); } catch { }
            return view;
        }

        private void PublishDraftQuietly(DraftPlace p, Draft d)
        {
            try { _sessions.Bus.PublishDraft(ViewOf(d, TaskReader(p), null)); } catch { }
        }

        // ---- the view (composer contract 6.1 and 10.3) -----------------------------------------------------------

        internal DraftView ViewOf(Draft d, Func<string, PlanTask> task, string sentence)
        {
            var info = StateOfDraft(d, task);
            var takenBack = RunTakenBack(d);
            var kept = d.Steps.Where(s => s.Kept).ToList();
            var change = kept.Where(s => s.Phase == DraftPhases.Change).ToList();
            var after = kept.Where(s => s.Phase == DraftPhases.After).ToList();
            var blanks = change.SelectMany(s => s.Blanks.Select(b => (Step: s, Blank: b, Now: BlankStateNow(b, task)))).ToList();
            var parts = blanks.Count(x => x.Blank.By == "assistant");
            var openParts = blanks.Count(x => !DraftBlankStates.Passed(x.Now) && x.Blank.By == "assistant");
            // Only the questions a kept step asks (or an input): a dropped step's questions are asked only if it is kept (D5).
            var openQuestions = d.Questions.Count(q => q.Required && q.Answer == null && q.By != "semanticus"
                && (q.Step == null || d.Steps.Any(s => s.Id == q.Step && s.Kept)));
            // A run History took back is shown as not run; its record comes back with a redo.
            var run = takenBack ? null : d.Run;
            var lastChange = change.Count == 0 ? 0 : change.Max(s => s.Number);
            var createsTable = change.Any(s => s.Actions.Any(a => a.Op == "create_import_table"));
            var refresh = after.FirstOrDefault(s => s.Actions.Any(a => a.Op == "refresh_partition"));
            var publish = after.FirstOrDefault(s => s.Actions.Any(a => OutwardOps.Contains(a.Op)));
            var afterChecks = kept.SelectMany(s => s.Checks).Count(c => c.Phase == "after");
            var afterRun = run?.After ?? new List<DraftAfterResult>();
            var failedCheck = run?.KeptAnyway == true ? null : afterRun.FirstOrDefault(a => a.What == "check" && a.Status == DraftAfterStatus.Failed);

            var view = new DraftView
            {
                DraftId = d.Id,
                Title = d.Title,
                Words = d.Words,
                Source = d.Source == DraftSources.Workflow ? d.Workflow : d.Source == DraftSources.Words ? "From your words" : d.Origin == "agent" ? "From your assistant" : "From the Plan",
                State = info.State,
                // A held draft reads approved only while the person's own approval stands for what it holds now.
                Approved = d.Approved && !takenBack && (info.Hold == null || PersonApprovalStands(d)),
                Held = info.Hold == null ? null : DraftCopy.Held(info.Hold),
                UndoSteps = HasStops(d) ? UndoStepsOf(d) : change.Count > 0 ? 1 : 0,
                PartsWaiting = blanks.Count(x => x.Now == DraftBlankStates.Queued || x.Now == DraftBlankStates.Writing),
                Sentence = sentence,
            };

            var pausedAt = run?.Outcome == "paused" && info.State != DraftStates.Stopped ? d.Steps.FirstOrDefault(s => s.Id == run.PausedAt) : null;
            view.PausedAt = pausedAt?.Id;
            view.CanContinue = pausedAt != null;
            view.Summary = info.State switch
            {
                _ when pausedAt != null && pausedAt.ByPerson => DraftCopy.PersonStop(pausedAt.Number, pausedAt.Title),
                _ when pausedAt != null && info.State == DraftStates.WaitingForAssistant && pausedAt.Guided => DraftCopy.GuidedStepWaiting(pausedAt.Number, pausedAt.GuidedWrites),
                _ when pausedAt != null && info.State == DraftStates.WaitingForAssistant => DraftCopy.AssistantStepWaiting(pausedAt.Number),
                _ when pausedAt != null => info.Reason,
                DraftStates.Ready when HasStops(d) => DraftCopy.ReadyInSteps(UndoStepsOf(d)),
                DraftStates.Ready => DraftCopy.ReadySentence,
                DraftStates.Running => DraftCopy.RunningSentence,
                // A step-by-step run made one undo entry per change group and per assistant's step: one Undo is not all.
                DraftStates.Done when HasStops(d) => DraftCopy.DoneInSteps(run?.UndoEntries ?? 0),
                DraftStates.Done => publish != null && afterRun.Any(a => a.StepId == publish.Id && a.Status == DraftAfterStatus.Ran)
                    ? DraftCopy.DonePublished(refresh == null ? null : TableOfAfter(refresh), StageOf(publish))
                    : refresh != null && createsTable ? DraftCopy.DoneSteps(lastChange) : DraftCopy.DoneAll(run?.AppliedStepIds.Length ?? change.Count),
                // One count everywhere (lane l9-finish): a step-by-step draft numbers every step it shows, the steps after
                // the change and a gated step included, so "of N" is that same numbering in the summary, the Plan row and
                // the banner alike.
                DraftStates.Stopped => run?.StopStep > 0 ? DraftCopy.StoppedTitle(run.StopStep, HasStops(d) ? StepCountOf(d) : change.Count) : "Stopped",
                // A draft with no step chosen reads as the reason: the menu's line on a draft from words.
                DraftStates.NeedsYou when kept.Count == 0 => info.Reason,
                // A run that stopped at a newer edit wrote nothing; the banner asks the question (UAT S12-5-004).
                DraftStates.NeedsYou when d.Overwrite != null => DraftCopy.NothingChangedYet,
                // A held draft does not run by itself, so no "runs by itself" shape: its summary is the hold.
                DraftStates.NeedsYou when info.Hold != null && info.Reason == DraftCopy.Held(info.Hold) => DraftCopy.Held(info.Hold),
                _ => DraftCopy.Summary(openQuestions, openParts),
            };

            var notes = new List<string>();
            if (takenBack) notes.Add(DraftCopy.TakenBack);
            if (info.State != DraftStates.Done && info.State != DraftStates.Stopped)
            {
                // Words past the most one draft reads stay named until Add more words reads them (Sol, finding 4).
                // Said only while Jev drafts on the Plan: with the switch off, Add more words asks no one.
                // Only the words the note names are matched: every view builds this note (the Plan's rows too), and a
                // draft can hold thousands of clauses (verifier on m11/sol-fixes-1, D3).
                if (d.NotRead != null && JevDraftsOnPlan)
                    notes.Add(DraftCopy.PlanNotRead(d.NotRead, d.NotRead.Take(DraftCopy.NamedMost).Where(NamesAStepOffTheList).ToList(), d.NotReadWhy));
                // The words the adding rule left with no step, each reason once, with the words it is for.
                if (d.NoStep != null)
                    foreach (var why in d.NoStep.GroupBy(x => x.Reason, StringComparer.Ordinal))
                        notes.Add(DraftCopy.NoStepFor(why.Select(x => x.Words).ToList(), why.Key));
                // The words whose add the adding rule offers unticked, each reason once, while the step stays unticked.
                if (d.Unticked != null)
                    foreach (var why in d.Unticked.Where(x => x.StepId == null || d.Steps.Any(st => st.Id == x.StepId && !st.Kept)).GroupBy(x => x.Reason, StringComparer.Ordinal))
                        notes.Add(DraftCopy.NotTickedFor(why.Select(x => x.Words).ToList(), why.Key));
                if (refresh != null && createsTable) notes.Add(DraftCopy.LoadingNote);
                // "One Undo takes the change back" is the one-change promise; a step-by-step draft names its stops instead.
                if (afterChecks > 0 && !HasStops(d)) notes.Add(DraftCopy.AfterChecksNote(afterChecks));
                if (pausedAt == null && run?.Outcome != "paused")
                    foreach (var s in kept.Where(s => !s.BySemanticus).OrderBy(s => s.Number))
                        notes.Add(s.ByPerson ? DraftCopy.StopsAt(s.Number) : DraftCopy.AssistantDoesStep(s.Number));
                if (publish != null) notes.Add(DraftCopy.PublishNote(StageOf(publish)));
            }
            if (info.Hold != null && !PersonApprovalStands(d) && info.State != DraftStates.Done && view.Summary != DraftCopy.Held(info.Hold))
                notes.Add(DraftCopy.Held(info.Hold));
            // "Nothing has changed yet" is true only before the run; a run paused at the assistant's step already changed
            // the model (found in the UAT lane), so it says only who it waits on.
            if (info.State == DraftStates.WaitingForAssistant && pausedAt == null) { notes.Add(DraftCopy.WaitingNote(openParts)); notes.Add(DraftCopy.OnNextCall); }
            else if (info.State == DraftStates.WaitingForAssistant) notes.Add(DraftCopy.OnNextCall);
            if (info.State == DraftStates.Running) notes.Add(DraftCopy.RunningNote);
            if ((info.State == DraftStates.Ready || info.State == DraftStates.Running) && parts > 0)
                notes.Add(DraftCopy.PartsPassed(parts, d.Questions.Count(q => q.AnsweredBy == "semanticus")));
            if (info.State == DraftStates.Done)
            {
                var ran = afterRun.Where(a => a.What == "check" && (a.Status == DraftAfterStatus.Passed || a.Status == DraftAfterStatus.Failed)).ToList();
                if (ran.Count > 0) notes.Add(DraftCopy.AfterChecksRan(ran.Count, ran.Count(a => a.Status == DraftAfterStatus.Passed)));
                notes.AddRange(afterRun.Where(a => a.Status == DraftAfterStatus.CouldNotRun || a.Status == DraftAfterStatus.CouldNotCheck || a.Status == DraftAfterStatus.NotRun)
                    .Select(a => "Step " + a.StepNumber.ToString(CultureInfo.InvariantCulture) + ": " + a.Text));
                if (run?.KeptAnyway == true) notes.Add(DraftCopy.KeptAnyway);
            }
            view.Notes = notes.Distinct(StringComparer.Ordinal).ToArray();

            if (info.State == DraftStates.Stopped && HasStops(d))
            {
                // A step-by-step run names its steps by their own numbers, so the banner does too.
                var of = StepCountOf(d);
                var ranNumbers = kept.Where(s => run?.AppliedStepIds.Contains(s.Id) == true).Select(s => s.Number).OrderBy(n => n).ToArray();
                view.Banner = info.Reason == DraftCopy.Restarted
                    ? new DraftBanner { Kind = "restarted", Title = "Stopped", Text = DraftCopy.Restarted }
                    : new DraftBanner
                    {
                        Kind = run?.Changed == true ? "stopped" : "nothing_changed",
                        Title = DraftCopy.StoppedTitle(Math.Max(1, run?.StopStep ?? 1), of),
                        Text = (run?.Reason ?? "").Trim() + (run?.Reason == null ? "" : " ") + DraftCopy.SoFar(ranNumbers, run?.StopStep ?? 0, of),
                    };
            }
            else if (info.State == DraftStates.Stopped)
            {
                var restarted = info.Reason == DraftCopy.Restarted;
                var appliedNumbers = change.Where(s => run?.AppliedStepIds.Contains(s.Id) == true).Select(s => s.Number).OrderBy(n => n).ToArray();
                view.Banner = restarted
                    ? new DraftBanner { Kind = "restarted", Title = "Stopped", Text = DraftCopy.Restarted }
                    : new DraftBanner
                    {
                        Kind = run?.Changed == true ? "stopped" : "nothing_changed",
                        Title = DraftCopy.StoppedTitle(Math.Max(1, run?.StopStep ?? 1), change.Count),
                        Text = (run?.Reason ?? "").Trim() + (run?.Reason == null ? "" : " ")
                               + DraftCopy.SoFar(appliedNumbers, run?.StopStep ?? 0, change.Count),
                    };
            }
            else if (info.State == DraftStates.Done && failedCheck != null)
                view.Banner = new DraftBanner
                {
                    Kind = "check_failed", Title = DraftCopy.CheckFailedTitle,
                    Text = HasStops(d) ? DraftCopy.CheckFailedInSteps(failedCheck.Text, run?.UndoEntries ?? 0)
                                       : DraftCopy.CheckFailedText(failedCheck.Text, lastChange, createsTable),
                };
            if (d.Overwrite != null)
            {
                var asked = DraftCopy.Overwrite(d.Overwrite.ObjectRef, d.Overwrite.Kind);
                view.Overwrite = new DraftOverwriteView
                {
                    Sentence = asked, Step = d.Overwrite.StepNumber, ObjectRef = d.Overwrite.ObjectRef,
                    Was = d.Overwrite.Was, Now = d.Overwrite.Now,
                };
                view.Banner = new DraftBanner { Kind = "overwrite", Title = DraftCopy.OverwriteTitle, Text = asked };
            }

            view.Questions = d.Questions.Select(q => new DraftQuestionView
            {
                Name = q.Name, Question = q.Question, Type = q.Type, By = q.By, Values = q.Values, Step = q.Step, Answer = q.Answer,
                AtStop = q.Step != null && d.Steps.Any(s => s.Id == q.Step && s.ByPerson),
            }).ToArray();

            view.Blanks = blanks.Select(x => new DraftBlankView
            {
                Step = x.Step.Number,
                StepId = x.Step.Id,
                Name = x.Blank.Name,
                Kind = x.Blank.Kind,
                State = x.Now,
                Line = x.Now switch
                {
                    DraftBlankStates.Queued => DraftCopy.BlankQueued,
                    DraftBlankStates.Writing => DraftCopy.BlankWriting,
                    DraftBlankStates.Filled => DraftCopy.BlankFilled(x.Blank.Text),
                    DraftBlankStates.Self => DraftCopy.BlankSelf(x.Blank.Text),
                    DraftBlankStates.Generated => DraftCopy.BlankGenerated,
                    DraftBlankStates.Needs => x.Blank.NeedsWhy == "declined" ? DraftCopy.AssistantAsks(x.Blank.Reason ?? "") : DraftCopy.BlankNeeds,
                    _ => DraftCopy.BlankEmpty,
                },
                Text = x.Blank.Text,
                FilledBy = x.Blank.FilledBy,
                RequestId = x.Now == DraftBlankStates.Queued || x.Now == DraftBlankStates.Writing ? x.Blank.RequestId : null,
                AttemptsLeft = Math.Max(0, DraftTries - x.Blank.Attempts),
                Reason = x.Blank.Reason,
                Choices = x.Blank.AskChoices,
            }).ToArray();

            view.Steps = d.Steps.Where(s => s.Phase != DraftPhases.After).Select(s => StepViewOf(d, s, info, run, task)).ToArray();

            // Copy the request (composer contract 9): each part still waiting for words, and all of them at once.
            var open = blanks.Where(x => x.Now == DraftBlankStates.Empty || x.Now == DraftBlankStates.Queued || x.Now == DraftBlankStates.Writing).ToList();
            foreach (var x in open)
            {
                var bv = view.Blanks.First(v => v.StepId == x.Step.Id && v.Name == x.Blank.Name);
                bv.Request = DraftCopy.CopyHead(1, d.Title) + "\n" + PacketOf(d, x.Step, x.Blank);
            }
            if (open.Count > 0)
                view.RequestText = DraftCopy.CopyHead(open.Count, d.Title) + "\n"
                    + string.Join("\n\n", open.Select((x, i) => "Part " + (i + 1).ToString(CultureInfo.InvariantCulture) + ". " + PacketOf(d, x.Step, x.Blank)));

            view.CanTryAgain = info.State == DraftStates.Done && afterRun.Any(LeftToTry);

            // The menu (lane L5a), while a draft from words has not run. A draft Jev drafted keeps the menu to add from,
            // and its line says what happened instead of that Jev was not sure (the line is also what offers Tell Jev
            // more, composer-model canRefine).
            if (d.Source == DraftSources.Words && run == null)
            {
                view.Menu = DraftMenu.For(d.Words, d.LastWords);
                view.MenuLine = d.JevDrafted ? (d.WordSteps > 0 ? DraftCopy.JevAndWordsMenuLine : DraftCopy.JevDraftedMenuLine)
                    : d.WordSteps > 0 ? DraftCopy.WordsDraftedMenuLine
                    : (d.JevNotSure ? DraftCopy.JevNotSure : DraftCopy.PickTheSteps);
            }
            view.After = d.Steps.Where(s => s.Phase == DraftPhases.After).Select(s => StepViewOf(d, s, info, run, task)).ToArray();

            if (run != null && run.Outcome != "running")
                view.Record = new DraftRecord
                {
                    Blanks = blanks.Where(x => DraftBlankStates.Passed(x.Blank.State)).Select(x => new DraftFillRecord
                    {
                        Blank = x.Step.Title + ": " + DraftCopy.KindWord(x.Blank.Kind),
                        FilledBy = x.Blank.FilledBy == "assistant" ? "Your assistant"
                                 : x.Blank.FilledBy == "semanticus" ? "Semanticus, fixed template"
                                 : "You, with Write it yourself",
                        Checks = "Passed its checks",
                    }).ToArray(),
                    BeforeChecks = run.BeforeChecks,
                    After = afterRun.ToArray(),
                    Ran = run.FinishedUtc?.ToString("o"),
                };

            if (run?.Changed == true && run.Outcome != "running")
                view.UndoLabel = DraftCopy.UndoLabel(run.LastUndoSteps > 0 ? run.LastUndoSteps : run.AppliedStepIds.Length, d.Source == DraftSources.Words, d.Title);

            var (word, note, _) = DraftRows.PlanWordOf(view, d, info);
            view.PlanWord = word;
            view.PlanNote = note;
            return view;
        }

        /// <summary>How many steps a step-by-step draft has, in the numbering its step list shows: the highest number of a
        /// kept step that runs (reads before the change are not steps it stops at).</summary>
        internal static int StepCountOf(Draft d) =>
            d.Steps.Where(s => s.Kept && s.Phase != DraftPhases.Prepare).Select(s => s.Number).DefaultIfEmpty(0).Max();

        private static string TableOfAfter(DraftStep st)
        {
            var a = st.Actions.FirstOrDefault(x => x.Op == "refresh_partition");
            if (a == null) return null;
            foreach (var key in new[] { "partitionRef", "tableRef", "objRef" })
                if (a.Args.TryGetValue(key, out var r) && r != null)
                {
                    var rest = r.Contains(':') ? r.Substring(r.IndexOf(':') + 1) : r;
                    return rest.Contains('/') ? rest.Substring(0, rest.IndexOf('/')) : rest;
                }
            return null;
        }

        private static string StageOf(DraftStep st)
        {
            var a = st?.Actions.FirstOrDefault(x => OutwardOps.Contains(x.Op));
            if (a == null) return null;
            foreach (var key in new[] { "stage", "targetStage", "stageName" })
                if (a.Args.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            return null;
        }

        private DraftStepView StepViewOf(Draft d, DraftStep s, DraftStateInfo info, DraftRun run, Func<string, PlanTask> task)
        {
            var first = s.Checks.FirstOrDefault();
            var v = new DraftStepView
            {
                Id = s.Id,
                Number = s.Number,
                Title = s.Title,
                Object = s.Actions.Select(ObjectNameOf).FirstOrDefault(n => n != null),
                Changes = s.ByPerson ? DraftCopy.PersonStepChanges
                        : s.Guided && !s.GuidedWrites ? DraftCopy.GuidedStepChanges
                        : s.ByAssistant ? DraftCopy.AssistantStepChanges : DraftCopy.Changes(s.Actions.Select(a => a.Op)),
                Tags = new[]
                {
                    s.ByPerson || d.Questions.Any(q => q.Step == s.Id && q.By == "you") ? "you" : null,
                    s.ByAssistant || s.Blanks.Count > 0 ? "asst" : null,
                }.Where(t => t != null).ToArray(),
                Check = first == null ? null : new DraftCheckLine { Phase = DraftCopy.PhaseWord(first.Phase), Text = DraftCopy.CheckWords(first) },
                Kept = s.Kept,
                Note = s.DroppedWith is int with ? DraftCopy.AlsoDropped(new[] { with }) : s.Guided ? (s.GuidedChecks ? DraftCopy.GuidedStep : DraftCopy.GuidedStepNoChecks) : s.ByAssistant ? DraftCopy.DirectStep : null,
                RunId = s.Guided ? run?.GuidedRunId : null,
            };
            if (!s.Kept) v.Status = "Dropped";
            else if (run != null && HasStops(d) && run.Outcome != "not_ready" && s.Phase != DraftPhases.Prepare)
            {
                // A step-by-step run: each step reads what happened to it, and the steps it has not reached yet wait.
                var ranAfter = run.After.Where(a => a.StepId == s.Id && a.What == "step").ToList();
                v.Status = run.PausedAt == s.Id && info.State != DraftStates.Stopped ? "Waiting"
                         : run.Skipped.Contains(s.Id) ? "Skipped"
                         : run.AppliedStepIds.Contains(s.Id) || run.Passed.Contains(s.Id)
                           ? (run.After.Any(a => a.StepId == s.Id && a.What == "check" && a.Status == DraftAfterStatus.Failed) ? "Failed after it ran" : "Done")
                         : ranAfter.Count > 0 ? (ranAfter.All(a => a.Status == DraftAfterStatus.Ran) ? "Done" : ranAfter.Any(a => a.Status == DraftAfterStatus.NotRun) ? "Did not run" : "Could not run")
                         : run.StopStep == s.Number && info.State == DraftStates.Stopped ? "Stopped"
                         : run.Outcome == "paused" || info.State == DraftStates.Running ? "Waiting"
                         : "Did not run";
            }
            else if (run != null && run.Outcome != "not_ready")
            {
                if (s.Phase == DraftPhases.After)
                {
                    var r = run.After.Where(a => a.StepId == s.Id && a.What == "step").ToList();
                    v.Status = info.State == DraftStates.Running ? "After the change"
                             : r.Count == 0 ? "Did not run"
                             : r.All(a => a.Status == DraftAfterStatus.Ran) ? "Done"
                             : r.Any(a => a.Status == DraftAfterStatus.NotRun) ? "Did not run" : "Could not run";
                }
                else if (s.Phase == DraftPhases.Prepare)
                {
                    var r = run.After.Where(a => a.StepId == s.Id && a.What == "read").ToList();
                    v.Status = r.Count > 0 && r.All(a => a.Status == DraftAfterStatus.Ran) ? "Done"
                             : run.StopStep == s.Number ? "Stopped" : info.State == DraftStates.Running ? "Running" : "Did not run";
                }
                else if (info.State == DraftStates.Running) v.Status = "Running";
                else if (run.AppliedStepIds.Contains(s.Id))
                    v.Status = run.After.Any(a => a.StepId == s.Id && a.What == "check" && a.Status == DraftAfterStatus.Failed) ? "Failed after it ran" : "Done";
                else if (run.StopStep == s.Number) v.Status = "Stopped";
                else v.Status = "Did not run";
            }
            else if (s.Blanks.Any(b => !DraftBlankStates.Passed(BlankStateNow(b, task)))) v.Status = "Waiting";
            else if (s.Phase == DraftPhases.After) v.Status = "After the change";
            return v;
        }

        // ---- the Plan rows (Plan-heart contract 1.3 and 14) --------------------------------------------------------

        /// <summary>One row per draft of this model, for the compose. Read without the draft gate: a row is a view,
        /// and a view read a moment early is corrected on the next call.</summary>
        internal List<ModelPlanItem> DraftPlanRows(PlanMemoryBinding bound, PlanMemoryState memory)
        {
            var p = new DraftPlace { Bound = bound, Dir = bound.Nowhere ? null : DraftStore.DirIn(SidecarDir(bound.Context)) };
            List<Draft> drafts;
            try { drafts = ReadDrafts(p); } catch { return new List<ModelPlanItem>(); }
            Func<string, PlanTask> task = id => id == null ? null : memory.FindTask(id);
            return drafts.Select(d => DraftRows.RowOf(d, ViewOf(d, task, null), bound.Nowhere, CreatedRefsOf(d).ToArray())).ToList();
        }

        /// <summary>The drafts of this model as the summary's ActiveWork carries them: every open draft.</summary>
        internal static Jev.ActiveDraftRef[] DraftBriefs(ModelPlanView plan) =>
            (plan?.Items ?? Array.Empty<ModelPlanItem>())
                .Where(i => DraftRows.IsDraftRow(i.Id) && PlanStateWords.IsOpen(i.StateWord))
                .Select(i => new Jev.ActiveDraftRef
                {
                    DraftId = i.Id.Substring(DraftRows.Prefix.Length),
                    Title = i.Title,
                    State = i.StateWord,
                    Note = i.StateNote,
                    PartsWaiting = (plan.Items ?? Array.Empty<ModelPlanItem>()).Count(t => PlanTasks.IsTaskId(t.Id)
                        && t.OriginLink?.Kind == PlanOriginKinds.Draft && t.OriginLink.Ref == i.Id.Substring(DraftRows.Prefix.Length)
                        && PlanStateWords.IsOpen(t.StateWord)),
                })
                .ToArray();
    }
}

namespace Semanticus.Engine.Composer
{
    /// <summary>A draft as a Plan row, and the one mapping from the draft's six states to the Plan's words.</summary>
    internal static class DraftRows
    {
        public const string Prefix = "draft/";

        public static bool IsDraftRow(string id) => id != null && id.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>Plan-heart contract 14: needs you is Needs you; waiting for your assistant is Ready (on its next
        /// call) or On it (a part claimed); ready is Ready with Approve and run; running is On it; done is Applied,
        /// checking, then Done; stopped is Needs you with the stop reason. The doer follows who acts next.</summary>
        internal static (string Word, string Note, string Doer) PlanWordOf(DraftView v, Draft d, LocalEngine.DraftStateInfo info)
        {
            switch (info.State)
            {
                case DraftStates.NeedsYou:
                    return (PlanStateWords.NeedsYou, info.Reason, PlanDoers.You);
                case DraftStates.WaitingForAssistant:
                    return v.Blanks.Any(b => b.State == DraftBlankStates.Writing)
                        ? (PlanStateWords.OnIt, DraftCopy.BlankWriting, PlanDoers.Assistant)
                        : (PlanStateWords.Ready, PlanTasks.OnNextCallNote, PlanDoers.Assistant);
                case DraftStates.Ready:
                    return (PlanStateWords.Ready, null, PlanDoers.Semanticus);
                case DraftStates.Running:
                    return (PlanStateWords.OnIt, DraftCopy.RunningSentence, PlanDoers.Semanticus);
                case DraftStates.Done:
                {
                    var after = d.Run?.After ?? new List<DraftAfterResult>();
                    if (d.Run?.KeptAnyway != true && after.Any(a => a.What == "check" && a.Status == DraftAfterStatus.Failed))
                        return (PlanStateWords.NeedsYou, DraftCopy.CheckFailedTitle, PlanDoers.You);
                    var open = after.FirstOrDefault(a => a.Status == DraftAfterStatus.CouldNotCheck || a.Status == DraftAfterStatus.CouldNotRun || a.Status == DraftAfterStatus.NotRun);
                    return open != null
                        ? (PlanStateWords.Checking, "Step " + open.StepNumber.ToString(CultureInfo.InvariantCulture) + ": " + open.Text, PlanDoers.Semanticus)
                        : (PlanStateWords.Done, null, PlanDoers.Semanticus);
                }
                default:
                    return (PlanStateWords.NeedsYou, info.Reason ?? v.Summary, PlanDoers.You);
            }
        }

        internal static ModelPlanItem RowOf(Draft d, DraftView v, bool notKept, string[] creates)
        {
            var done = v.PlanWord == PlanStateWords.Done;
            var (_, _, doer) = PlanWordOf(v, d, new LocalEngine.DraftStateInfo { State = v.State, Reason = v.PlanNote });
            var objects = d.Steps.Where(s => s.Kept).SelectMany(s => s.Actions)
                .SelectMany(a => a.Args.Values.Where(x => x != null && (x.StartsWith("table:", StringComparison.Ordinal) || x.StartsWith("column:", StringComparison.Ordinal) || x.StartsWith("measure:", StringComparison.Ordinal))))
                .Concat(creates).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
            var item = new ModelPlanItem
            {
                Id = Prefix + d.Id,
                Section = ModelPlanSections.Structure,
                Title = d.Title,
                Sub = notKept ? PlanTasks.NotKeptSentence : v.Summary,
                Value = string.Empty,
                Who = PlanTasks.WhoOf(doer),
                Status = done ? ModelPlanStatuses.Done : v.State == DraftStates.Running ? ModelPlanStatuses.Running : ModelPlanStatuses.Ready,
                State = done ? ModelPlanItemStates.Done : v.State == DraftStates.Done ? ModelPlanItemStates.Applied : ModelPlanItemStates.Gap,
                Why = new[]
                {
                    (d.Origin == "agent" ? "Your assistant drafted this on " : "You drafted this on ")
                    + PlanDay.Of(d.CreatedUtc) + ". Semanticus wrote every line.",
                },
                Diff = Array.Empty<string>(),
                Touches = v.Steps.Count(s => s.Kept) == 1 ? "1 step" : v.Steps.Count(s => s.Kept).ToString(CultureInfo.InvariantCulture) + " steps",
                Tables = Array.Empty<ModelPlanItemTable>(),
                Source = d.Source == DraftSources.Workflow ? PlanSources.Workflow : PlanSources.Words,
                Doer = doer,
                DoneWhen = PlanDoneWhen.ModelCheck,
                DoneText = "Semanticus runs it as one change and checks it after.",
                Objects = objects,
                OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Draft, Ref = d.Id },
                StateWord = v.PlanWord,
                // The note is the draft's own (who it waits on); a draft that is not kept says so in its sub line, the
                // place a person reads first, so the word and its note stay the draft's (contract 2.3).
                StateNote = v.PlanNote,
            };
            return item;
        }
    }
}

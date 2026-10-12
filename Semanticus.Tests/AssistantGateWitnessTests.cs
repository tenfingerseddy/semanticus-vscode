using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Join part 6, area B: the witness completeness check (the class behind items 3 to 6). An approval binds the model's revision and the call's arguments.
    /// A write that also reads something that is neither (the spec in the session's memory, a source file, a note, a draft) must declare that input and a
    /// witness for it, so the grant is for what the write will really use. Every held row is in exactly one list below, so a NEW held row cannot be added
    /// without someone deciding which it is: it reads only the model and its arguments, or it declares a witness, or it is a named, reasoned gap.
    /// </summary>
    [Collection("restore-root")]
    public sealed class AssistantGateWitnessTests : GateFixtureBase
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _out;

        public AssistantGateWitnessTests(RestoreRootFixture fixture, Xunit.Abstractions.ITestOutputHelper output) : base(fixture) { _out = output; }

        /// <summary>Held rows whose write reads state outside the model and the arguments, and which carry a witness for it (a Basis checked under the
        /// store's own lock, or the state of a path). The test below makes each one prove it.</summary>
        private static readonly string[] WitnessedByBasis =
        {
            "BuildModelFromSpecAsync", "SaveSpecAsync", "SetSpecAsync", "ClearSpecAsync", "LoadSpecAsync", "AutogenerateSpecFromModelAsync",
            "AutogenerateSpecFromFabricAsync", "OpenAsync", "CreateModelAsync", "OpenLocalAsync", "OpenLiveAsync", "ClearPlanAsync", "ProposePlanAsync", "ProposeReplaceAsync", "CherryPickAsync", "ApproveInsightAsync",
            "EditInsightAsync", "DeleteInsightAsync", "PurgeKnowledgeAsync", "ApplyPlanAsync",
            "RunDraftAsync", "ApproveHeldDraftAsync", "ContinueDraftAsync", "LoadBpaRulesAsync", "LoadReadinessRulesAsync", "DaxLibInstallAsync", "SaveEvidenceAsync", "RunInterviewAsync", "InstantiateWorkflowTemplateAsync", "AcceptPrimerSuggestionAsync", "RejectPrimerSuggestionAsync", "SetPrimerAsync", "SaveWorkflowAsync", "SaveWorkflowTemplateAsync", "SaveTestDefinitionAsync", "RunTestSuiteAsync", "GitPushAsync",
            "DeleteWorkflowAsync", "DeleteWorkflowTemplateAsync", "DeleteTestDefinitionAsync", "DeleteInterviewQuestionAsync", "AddInterviewQuestionAsync",
            "SaveLayoutAsync", "SaveWorkflowLayoutAsync", "SetReportScopeAsync", "ClearTableSourceMappingAsync", "SetTableSourceMappingAsync", "SaveSqlSourceAsync", "DeleteSqlSourceAsync",
            "LabelConnectionAsync", "RememberXmlaConnectionAsync", "SetConnectionWorkingFolderAsync", "ForgetConnectionAsync", "SetModelSqlEndpointAsync", "SetPublishDestinationAsync",
        };

        /// <summary>Held rows that write a path or the model's own files: bound to the real state of each (the path witness), every link followed.</summary>
        private static readonly string[] WitnessedByPath =
        {
            "SaveAsync", "ExportVpaxAsync", "GitCloneAsync", "GitCheckoutAsync", "GitPullAsync", "GitCommitAsync", "CreateHistoryCheckpointAsync",
            "RestoreHistoryCheckpointAsync", "GitBranchAsync", "PrepareWorkingCopyAsync", "CicdGenerateAsync", "ApplyDiffAsync",
            "StartModelFromSqlAsync",
        };

        /// <summary>Rows that read an outside input but carry the witness in their own arguments (a digest the call must present, a byte hash, a review fence).</summary>
        private static readonly string[] BoundByOwnArguments =
        {
            "ApplyPlanItemsAsync", "ModelPlanActionAsync", "EditWorkflowDocumentAsync", "EditStartPlanAsync", "WriteStartWordsAsync",
            "BuildStartPlanAsync", "DiscardStartPlanAsync", "ApproveStartPlanAsync", "UndoToEntryAsync", "RedoToEntryAsync",
            "EditStartPlansAsync",
        };

        /// <summary>Rows that read only their own arguments, each with the one line of why. The test prints them. (This list was the "known gaps" until the
        /// coordinator ordered every gap closed: a row either gets a witness above or says here why it needs none.)</summary>
        private static readonly Dictionary<string, string> ReadsOnlyOwnArguments = new(StringComparer.Ordinal)
        {
            ["SetDefaultAccountProfileAsync"] = "profileId fixes the saved identity to select; the write uses its current sign-in record, and CAS refuses a competing switch",
            ["RecordTestRunAsync"] = "it names a finished run by its unique id, and a finished run is an immutable snapshot, so the id is the content",
            ["UpgradeWorkflowAsync"] = "it applies only with expectPath and expectByteHash from the reviewed preview, an argument the approval binds, and the engine refuses when the file no longer has that hash",
            ["CaptureBaselineAsync"] = "it writes a NEW baseline under a fresh id of the values as they are when it runs and replaces nothing, so there is no earlier state for an approval to be stale against",
            // Kane, 10 Oct 2026: the Plan controls are the assistant's too. Each acts on the plan row its id names, with the values in its arguments.
            ["AddPlanTargetAsync"] = "it adds a NEW aim with the words and section in its arguments and changes no earlier row of the plan",
            ["RemovePlanTargetAsync"] = "it removes the aim its id names, the id is the argument the approval binds, and Restore puts the row back",
            ["MarkPlanTargetDoneAsync"] = "it marks the row its id names done and the id is the argument the approval binds",
            ["AddPlanTaskAsync"] = "it adds a NEW task with the words in its request and changes no earlier row of the plan",
            ["HoldPlanItemAsync"] = "it pauses or resumes the row its id names, and the id and the state are the arguments the approval binds",
            ["SetPlanItemDoerAsync"] = "it gives the row its id names to the doer in its arguments; the result is the same whatever the doer was before",
            ["MovePlanTaskUpAsync"] = "it moves the task its id names up the assistant's order and the id is the argument the approval binds; the order is a preference, not words the person wrote",
            ["SetAssistantQueueAsync"] = "it sets the queue switch to the state in its arguments; the result is the same whatever the switch was before",
            ["SetPlanTaskNoteAsync"] = "it sets the note of the row its id names to the words in its arguments, which the person is shown when asked",
            ["RemovePlanRowAsync"] = "it removes the row its id names, the id is the argument the approval binds, and Restore puts the row back",
            ["RestorePlanRowAsync"] = "it puts back the row its id names, the id is the argument the approval binds, and Remove takes it away again",
            ["AddPlanRepeatAsync"] = "it adds a NEW repeat with the settings in its arguments and changes no earlier repeat",
            ["SetPlanRepeatAsync"] = "it sets the repeat its id names to the settings in its arguments, which the person is shown when asked",
            ["DeletePlanRepeatAsync"] = "it deletes the repeat its id names, and the id is the argument the approval binds",
            ["PlanRowsAsync"] = "it acts on the plan rows its ids name, like the single plan controls, and the ids and actions are the arguments the approval binds",
        };

        /// <summary>Rows that write a store beside the model but only ADD to it: they replace and delete nothing, so there is no earlier state for an approval to
        /// be stale against. Each names the one file it appends to and why that is enough. The sweep (AssistantGateStoreWriteSweepTests) runs each through the real
        /// door and fails when the row changes any other file, or changes bytes already in this one.</summary>
        internal static readonly Dictionary<string, (string File, string Why)> AppendsToOneStore = new(StringComparer.Ordinal)
        {
            ["AddInsightAsync"] = (".semanticus/knowledge/insights.jsonl", "it appends one new note under a fresh id and rewrites no earlier line"),
            ["UpvoteInsightAsync"] = (".semanticus/knowledge/insights.jsonl", "it appends one vote line for a note; a vote is a count, and no earlier line is rewritten"),
            ["DownvoteInsightAsync"] = (".semanticus/knowledge/insights.jsonl", "it appends one vote line for a note; a vote is a count, and no earlier line is rewritten"),
        };

        /// <summary>Rows whose write can reach a store beside the model, and which refuse inside the op when that store is not what the step or the claim expects. The
        /// reason names the check and the test that proves it. (Undo and redo move the model's own step; a step that owns the Primer file puts it back, and refuses when the
        /// file moved. A reply that carries changes belongs to a claimed request, and refuses without the claim.)</summary>
        internal static readonly Dictionary<string, string> GuardedByTheOpItself = new(StringComparer.Ordinal)
        {
            ["RefreshPlanAsync"] = "refresh reads the current plan; WritePlanTasks binds the write to the same model and preserves authored rows, adding history and finished-row ids; PlanRefreshTests proves refresh keeps earlier finished rows cleared",
            ["ResolveFileRecoveryAsync"] = "expectedSession binds the immutable recovery copy loaded when that session opens; the op refuses another session and never rereads the payload from disk; UnsavedRecoveryTests proves the fence and exact restore",
            ["UndoAsync"] = "the revision binds the step the person was shown, and a step that put the Primer or its suggestions in a file refuses to undo when that file moved since (SidecarUndo.Refusal); AssistantGateStoreWriteSweepTests.Undo_and_redo_of_a_step_that_owns_the_Primer_refuse_when_the_person_changed_the_file",
            ["RedoAsync"] = "the same as undo: the revision binds the step, and a step that owns the Primer file refuses when the file moved; the same test",
            ["CompleteAssistantRequestAsync"] = "a held reply (one that carries changes) names a request and the claim token its holder was given, and the op refuses a stale claim or a run that is not paused at that step (DraftStops.cs); what it rewrites is the run's own state, not words the person wrote",
        };

        /// <summary>The list of rows that read an outside input with no witness and no reason. It must stay empty.</summary>
        private static readonly string[] KnownGaps = Array.Empty<string>();

        /// <summary>Held rows that read only the open model and the call's own arguments (what the revision and the arguments already bind): every
        /// input is in the sentence the person is asked with.</summary>
        private static readonly string[] ModelAndArguments =
        {
            "WaiveFindingsAsync", "WriteWordsAsync", "EditObjectsAsync", "CreateMeasuresAsync",
            "ApplyDaxScriptAsync", "ApplyFixAsync", "ApplySafeFixesAsync", "ApplySchemaUpdateAsync",
            "ApplyTmdlScriptAsync", "BpaFixAllAsync", "BpaFixAsync", 
            "CreateCalculatedColumnAsync", "CreateCalculatedTableAsync", "CreateCalculationGroupAsync", "CreateCalculationItemAsync",
            "CreateColumnAsync", "CreateDataSourceAsync", "CreateDirectLakeTableAsync", "CreateFieldParameterAsync", "CreateFunctionAsync",
            "CreateHierarchyAsync", "CreateImportTableAsync", "CreateLinguisticSchemaAsync", "CreateMeasureAsync", "CreateNamedExpressionAsync",
            "CreatePerspectiveAsync", "CreateRelationshipAsync", "CreateRoleAsync", "CreateTableAsync", "DaxLibUninstallAsync",
            "DefineCalendarAsync", "DefineCalendarFromTemplateAsync", "DeleteCalendarAsync", "DeleteObjectAsync",
            "DeleteObjectsAsync", "DeleteRoleAsync", "DuplicateObjectAsync", "GenerateDateTableAsync",
            "GenerateTimeIntelligenceAsync", "MakeAiReadyAsync", "MarkDateTableAsync", "OptimizeMeasureAsync", 
            "RemoveIncrementalRefreshPolicyAsync", "RemoveSafeObjectsAsync", "RenameDisplayFolderAsync",
            "RenameObjectAsync", "ReplaceInObjectAsync", "ResetBpaRulesAsync", "ResetReadinessRulesAsync", 
            "SetAiDataSchemaAsync", "SetAiInstructionsAsync", "SetCalcGroupPrecedenceAsync",
            "SetCalcItemFormatStringAsync", "SetColumnDataTypeAsync", "SetColumnMetadataAsync", "SetColumnObjectPermissionAsync",
            "SetCompatibilityLevelAsync", "SetDaxAsync", "SetDescriptionAsync", "SetDisplayFolderAsync",
            "SetDocSectionAsync", "SetIncrementalRefreshPolicyAsync", "SetMeasureFormatAsync", "SetMeasureFormatExpressionAsync",
            "SetObjectPropertiesAsync", "SetObjectPropertyAsync", "SetPartitionMAsync", "SetPerspectiveMemberAsync",
            "SetRelationshipAsync", "SetRelationshipCardinalityAsync", "SetRoleMemberAsync",
            "SetRolePermissionAsync", "SetSynonymsAsync", "SetTableObjectPermissionAsync", "SetTablePermissionAsync", 
            "SubmitWorkflowStepAsync", "TagCalendarColumnAsync", "UnwaiveFindingAsync", "UpdateNamedExpressionAsync",
            "WaiveFindingAsync",
            // The model forms of the bulk tools (lane bulk-more): each reads the open model and its items, like its single tool.
            "UnwaiveFindingsAsync", "ApplyFixesAsync", "DuplicateObjectsAsync", "SetDocSectionsAsync", "SetPerspectiveMembersAsync", "SetObjectPermissionsAsync",
        };

        /// <summary>The list above, for the sweep that measures it (AssistantGateStoreWriteSweepTests).</summary>
        internal static string[] ModelAndArgumentsRows => ModelAndArguments;

        private static IEnumerable<GateEntry> HeldRows() => GateRegistry.All.Where(r => r.Kind == GateKind.Hold || r.Choose != null);

        [Fact]
        public void Every_held_row_is_accounted_for_in_exactly_one_list()
        {
            var lists = new[] { WitnessedByBasis, WitnessedByPath, BoundByOwnArguments, ReadsOnlyOwnArguments.Keys.ToArray(), AppendsToOneStore.Keys.ToArray(), GuardedByTheOpItself.Keys.ToArray(), ModelAndArguments };
            var problems = new List<string>();
            foreach (var row in HeldRows())
            {
                var n = lists.Count(l => l.Contains(row.Method, StringComparer.Ordinal));
                if (n == 0) problems.Add(row.Method + " is a held row that is in no list: say whether it reads only the model and its arguments, or declare its witness (GateEntry.OutsideInputs and Basis)");
                if (n > 1) problems.Add(row.Method + " is in " + n + " lists");
            }
            foreach (var name in lists.SelectMany(l => l).Distinct(StringComparer.Ordinal))
                if (HeldRows().All(r => r.Method != name)) problems.Add(name + " is listed but is not a held row (any more)");
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void A_row_that_names_an_outside_input_declares_how_the_grant_is_bound_to_it_and_the_reverse()
        {
            var problems = new List<string>();
            foreach (var row in GateRegistry.All)
            {
                var bound = row.Basis != null || (row.Witness & (GateWitness.Paths | GateWitness.ModelFiles)) != 0 || row.PathArgs.Length > 0 || row.PathsOf != null;
                if (row.OutsideInputs.Length > 0 && !bound) problems.Add(row.Method + " reads " + string.Join(", ", row.OutsideInputs) + " but has no Basis and no path witness");
                if (row.Basis != null && row.OutsideInputs.Length == 0) problems.Add(row.Method + " has a Basis but does not say what outside input it is for");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void The_list_of_known_gaps_is_empty_and_each_row_that_needs_no_witness_says_why()
        {
            Assert.Empty(KnownGaps);
            foreach (var kv in ReadsOnlyOwnArguments)
            {
                _out.WriteLine(kv.Key + " reads only its own arguments: " + kv.Value);
                var row = GateRegistry.Find(kv.Key);
                Assert.True(row != null && row.Basis == null && row.OutsideInputs.Length == 0, kv.Key + " declares a witness, so it does not belong on this list");
            }
        }

        [Fact]
        public async Task Every_row_listed_as_witnessed_by_a_basis_proves_it_with_the_state_it_holds()
        {
            var (engine, _, _) = await OpenAsync("witness");
            using (engine)
            {
                // a state in which every replace/clear row is HELD: a spec, a plan line the person approved
                await engine.SetSpecAsync("{\"name\":\"S\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}", "human");
                var plan = await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", "reviewed", null, null, null, "human");
                var planLine = plan.Items.Single().Id;
                await engine.SetPlanItemAsync(planLine, null, true, "human");
                var problems = new List<string>();
                foreach (var name in WitnessedByBasis)
                {
                    var method = typeof(IEngine).GetMethods().First(m => m.Name == name);
                    var row = GateRegistry.Find(name);
                    var args = AssistantGateKit.NonsenseArgs(method, System.IO.Path.Combine(Root, "scratch.out"));
                    if (name == "ApplyPlanAsync") args[Array.FindIndex(method.GetParameters(), p => p.Name == "approvedIds")] = new[] { planLine };   // a line that is on the plan
                    var call = new GateCall { Engine = engine, Method = method, Parameters = method.GetParameters(), Args = args };
                    var chosen = row.Choose != null ? await row.Choose(call) : row;
                    if (chosen.Kind != GateKind.Hold) { problems.Add(name + " passes in a state that holds something to lose (it chose " + chosen.Kind + ")"); continue; }
                    if (chosen.Basis == null) problems.Add(name + " is held but carries no Basis");
                    if (chosen.OutsideInputs.Length == 0) problems.Add(name + " is held but names no outside input");
                    if (chosen.Basis != null && string.IsNullOrEmpty(await chosen.Basis(call))) problems.Add(name + " has a Basis that reads as empty");
                }
                Assert.True(problems.Count == 0, string.Join("\n", problems));
            }
        }

        [Fact]
        public void Every_row_listed_as_witnessed_by_a_path_binds_the_state_of_a_path()
        {
            var problems = new List<string>();
            foreach (var name in WitnessedByPath)
            {
                var row = GateRegistry.Find(name);
                if (row == null) { problems.Add(name + " has no row"); continue; }
                // a call-dependent row says so at the call; the others declare the witness on the row itself
                if (row.Choose != null) continue;
                if ((row.Witness & (GateWitness.Paths | GateWitness.ModelFiles)) == 0) problems.Add(name + " names no path witness");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void The_number_of_rows_in_each_list_is_measured_not_claimed()
        {
            var held = HeldRows().Select(r => r.Method).Distinct(StringComparer.Ordinal).Count();
            Assert.Equal(held, WitnessedByBasis.Length + WitnessedByPath.Length + BoundByOwnArguments.Length + ReadsOnlyOwnArguments.Count + AppendsToOneStore.Count + GuardedByTheOpItself.Count + ModelAndArguments.Length);
        }
    }
}

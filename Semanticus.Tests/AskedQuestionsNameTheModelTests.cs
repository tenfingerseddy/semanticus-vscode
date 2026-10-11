using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Facts;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane m11/uat7-engine, item 2 (Sol UAT-J7-S3-001 to 007). Under Locked, seven questions the assistant's held calls put to the person did not name the
    /// model they act on or the value at risk: "the model", "the start plan", "the note ki-9013828c", "your unsaved spec and 1 line on your plan". The queue is
    /// read away from the model tree, so a question must say where the change lands and what would be lost. Class: EVERY held question, which is
    /// <see cref="AssistantEngine.QuestionAsync"/> over one row of the registry, names the model it is about and says an object or a loss. The sweep renders it
    /// for every Hold row (and for the rows decided by the call, with the state that makes them hold), and the second group of tests reads the seven findings
    /// through the assistant's door under Locked, as the person meets them.
    /// </summary>
    [Collection("restore-root")]
    public sealed class AskedQuestionsNameTheModelTests : GateFixtureBase
    {
        private const string Server = "probe.datawarehouse.fabric.microsoft.com", Db = "Warehouse";
        private const string PersonSpec = "{\"name\":\"Northstar Held\",\"tables\":[{\"name\":\"Customer\",\"columns\":[]},{\"name\":\"Product\",\"columns\":[]}],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}";
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public AskedQuestionsNameTheModelTests(RestoreRootFixture fixture) : base(fixture)
        {
            ConnectionRegistry.RootOverride = Path.Combine(Root, "connections");
        }

        public override void Dispose()
        {
            ConnectionRegistry.RootOverride = _oldConnections;
            base.Dispose();
        }

        private sealed class Silent : IDecisionOracle
        {
            public bool Available => false;
            public System.Threading.Tasks.Task<DecisionAnswers> AskAsync(DecisionRequest request, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.Task.FromResult(DecisionAnswers.NotDecided(request, "no oracle in this test"));
        }

        private static FabricSchema Schema() => new FabricSchema
        {
            Server = Server, Database = Db,
            Tables = new[]
            {
                new FabricTable { Schema = "dbo", Name = "Sales", KeyColumns = new[] { "Id" }, Columns = new[] { new FabricColumn { Name = "Id", DataType = "Int64" }, new FabricColumn { Name = "Amount", DataType = "Decimal" } } },
                new FabricTable { Schema = "dbo", Name = "Customer", KeyColumns = new[] { "Id" }, Columns = new[] { new FabricColumn { Name = "Id", DataType = "Int64" }, new FabricColumn { Name = "Name", DataType = "String" } } },
            },
            ForeignKeys = Array.Empty<FabricForeignKey>(),
        };

        private async Task<(LocalEngine engine, SessionManager sessions)> StartPlanEngineAsync(string name)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, Path.Combine(Root, "ws"));
            engine.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(Schema());
            engine.UseDecisionsForTest(new DecisionService(new Silent()));
            await engine.StartModelFromSqlAsync(new StartModelRequest { Name = name, Folder = Path.Combine(Root, "started"), Server = Server, Database = Db }, "human");
            await engine.StartDecideSettledForTest(); await engine.StartPreviewSettledForTest();
            return (engine, sessions);
        }

        private static async Task ApprovedLineAsync(LocalEngine engine, string words)
        {
            var plan = await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", words, null, null, null, "human");
            await engine.SetPlanItemAsync(plan.Items.Last().Id, null, true, "human");
        }

        private static MethodInfo EngineMethod(string name) => typeof(IEngine).GetMethods().First(m => m.Name == name);

        /// <summary>Whether a question says something about an object or a loss beyond the verb: it carries a value the call was given, or an object as the
        /// person reads it ("[Total]", "the table Sales", a quoted value, a file), or what would be lost; for a call with nothing to carry, it says more than the
        /// name of the method does.</summary>
        private static bool NamesAnObjectOrALoss(string question, string method, object[] args, string[] ignore)
        {
            var cut = question.LastIndexOf(" This is about the model", StringComparison.Ordinal);
            question = cut < 0 ? question : question.Substring(0, cut);   // the model's own sentence is not the object or the loss
            var values = args.OfType<string>().Concat(args.OfType<string[]>().SelectMany(a => a)).Where(v => v.Length >= 3 && !ignore.Contains(v)).Distinct().ToList();
            if (values.Any(v => question.Contains(v, StringComparison.Ordinal))) return true;
            const string object_or_loss = @"\[[^\]]+\]|""[^""]+""|\bthe (table|column|measure|role|perspective|calculation|hierarchy|relationship|calendar|workflow|note|spec|plan|tests|folder|file|connection|test|source|package|Primer)\b|will be lost|throws? away|Undo does not|[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+";
            if (System.Text.RegularExpressions.Regex.IsMatch(question, object_or_loss)) return true;
            // a number or a choice the call carries, said as it was given ("set the compatibility level to 1")
            if (args.Where(a => a is int || a is long || a is Enum).Any(a => System.Text.RegularExpressions.Regex.IsMatch(question, @"\b" + System.Text.RegularExpressions.Regex.Escape(Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture)!) + @"\b"))) return true;
            return question.Length > ("Your assistant wants to " + GateWords.Humanize(method)).Length + 24 && values.Count == 0;
        }

        /// <summary>The rows decided by the call, resolved to the entry that holds, with arguments that make them hold.</summary>
        private static async Task<GateEntry> HoldingEntryAsync(LocalEngine engine, GateEntry row, GateCall call)
        {
            if (row.Choose == null) return row.Kind == GateKind.Hold ? row : null;
            var chosen = await row.Choose(call);
            return chosen.Kind == GateKind.Hold ? chosen : null;
        }

        [Fact]
        public async Task Every_question_for_a_row_that_holds_names_the_model_and_an_object_or_a_loss()
        {
            var (engine, _, path) = await OpenAsync("sweep");
            using (engine)
            {
                // The state that makes the rows decided by the call hold: a spec, a plan with a line the person approved, an unsaved edit.
                await engine.SetSpecAsync(PersonSpec, "human");
                await ApprovedLineAsync(engine, "Northstar discount share before posted returns.");
                await engine.SetDescriptionAsync("measure:Sales/Total", "an unsaved edit", "human");
                await engine.SetDescriptionAsync("measure:Sales/Total", "a second unsaved edit", "human");
                await engine.UndoAsync("human");   // one row taken back, so a redo has something to name
                var model = engine.GateModelName();
                Assert.False(string.IsNullOrWhiteSpace(model), "the fixture model has a name");
                var scratch = Path.Combine(Root, "scratch.out");
                var history = (await engine.GetPlanHistoryAsync()).Rows.Where(r => r.Kind == "edit").ToList();
                var liveRow = history.First(r => !r.Undone).Id;   // the walks name a row that exists: an undo walk a live one, a redo walk the one taken back
                var undoneRow = history.First(r => r.Undone).Id;

                var rendered = 0; var unrendered = new List<string>(); var wrong = new List<string>();
                foreach (var row in GateRegistry.All.Where(r => r.Kind == GateKind.Hold || r.Choose != null).OrderBy(r => r.Method, StringComparer.Ordinal))
                {
                    if (row.ModelName != null) continue;   // the start plan's rows are the next test's: their model is the plan's, not the open one
                    var method = EngineMethod(row.Method);
                    var args = AssistantGateKit.NonsenseArgs(method, scratch);
                    for (var i = 0; i < args.Length; i++)
                    {
                        var name = method.GetParameters()[i].Name;
                        if (name == "entryId") args[i] = row.Method == "RedoToEntryAsync" ? undoneRow : liveRow;
                        // arguments that mean something to the call, where nonsense would name nothing at all
                        else if (row.Method == "ApplyDaxScriptAsync" && name == "script") args[i] = "// @object measure:Sales/Total\nSUM(Sales[Amount])";
                        else if (row.Method == "ApplyTmdlScriptAsync" && name == "script") args[i] = "// @object measure:Sales/Total\nmeasure Total = SUM(Sales[Amount])";
                        else if (row.Method == "ApplyPlanAsync" && name == "approvedIds") args[i] = Array.Empty<string>();
                        else if (method.GetParameters()[i].ParameterType == typeof(ReplaceRequest)) args[i] = new ReplaceRequest { Find = "Total", Replace = "Grand Total" };
                    }
                    var call = new GateCall { Engine = engine, Method = method, Parameters = method.GetParameters(), Args = args };
                    GateEntry holding;
                    try { holding = await HoldingEntryAsync(engine, row, call); }
                    catch (Exception ex) { unrendered.Add(row.Method + " (choose: " + ex.GetType().Name + ")"); continue; }
                    if (holding == null) continue;   // with these arguments the call does not hold: nothing is asked
                    (string Question, string Model) asked;
                    try { asked = await AssistantEngine.QuestionAsync(engine, holding, call); }
                    catch (Exception ex) { unrendered.Add(row.Method + " (" + ex.GetType().Name + ": " + ex.Message + ")"); continue; }
                    rendered++;
                    // No template is ever shown: a "{x}" or a "{add?add|remove}" left in the words is a row whose placeholder the filler never read (join 8, K16). A call given text
                    // with braces of its own (a spec, a script) may show them, so only a question whose arguments carry none is held to this.
                    if ((asked.Question.Contains('{') || asked.Question.Contains('}')) && !args.OfType<string>().Any(a => a.Contains('{') || a.Contains('}')))
                    { wrong.Add(row.Method + " shows a template or a request as raw JSON: " + asked.Question); continue; }
                    // And no JSON key: a request object printed whole reads {"Find":"Total",...} (UAT of join 8, U8). Only a call given text with braces of its own may show them.
                    if (System.Text.RegularExpressions.Regex.IsMatch(asked.Question, "\"[A-Za-z]+\"\\s*:") && !args.OfType<string>().Any(a => a.Contains('{') || a.Contains('}')))
                    { wrong.Add(row.Method + " shows a JSON key: " + asked.Question); continue; }
                    if (!asked.Question.Contains("the model \"" + model + "\"", StringComparison.Ordinal)) wrong.Add(row.Method + " does not name the model: " + asked.Question);
                    else if (!NamesAnObjectOrALoss(asked.Question, row.Method, args, new[] { "agent", model })) wrong.Add(row.Method + " names neither an object nor a loss: " + asked.Question);
                }
                Assert.True(rendered > 150, "only " + rendered + " held questions were rendered");
                Assert.True(unrendered.Count == 0, "these rows could not be rendered: " + string.Join("; ", unrendered));
                Assert.True(wrong.Count == 0, string.Join("\n", wrong));
            }
        }

        [Fact]
        public async Task Every_question_for_a_start_plan_row_names_the_model_the_plan_builds()
        {
            var (engine, _) = await StartPlanEngineAsync("Northstar Locked");
            using (engine)
            {
                var scratch = Path.Combine(Root, "scratch.out");
                var rendered = 0; var wrong = new List<string>();
                foreach (var row in GateRegistry.All.Where(r => r.ModelName != null).OrderBy(r => r.Method, StringComparer.Ordinal))
                {
                    var method = EngineMethod(row.Method);
                    var args = AssistantGateKit.NonsenseArgs(method, scratch);
                    for (var i = 0; i < args.Length; i++)
                    {
                        if (method.GetParameters()[i].ParameterType == typeof(StartModelRequest))
                            args[i] = new StartModelRequest { Name = "Northstar Locked", Folder = Path.Combine(Root, "other"), Server = Server, Database = Db };
                        else if (method.GetParameters()[i].ParameterType == typeof(StartPlanEdit))
                            args[i] = new StartPlanEdit { Op = "rename", Target = "table:Sales", Value = "Northstar Sales" };
                        else if (method.GetParameters()[i].ParameterType == typeof(StartWord[]))
                            args[i] = new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Northstar sales lines include posted returns." } };
                    }
                    var call = new GateCall { Engine = engine, Method = method, Parameters = method.GetParameters(), Args = args };
                    var asked = await AssistantEngine.QuestionAsync(engine, row, call);
                    rendered++;
                    if (!asked.Question.Contains("the model \"Northstar Locked\"", StringComparison.Ordinal)) wrong.Add(row.Method + " does not name the model: " + asked.Question);
                }
                Assert.True(rendered >= 4, "only " + rendered + " start plan rows were rendered");
                Assert.True(wrong.Count == 0, string.Join("\n", wrong));
            }
        }

        /// <summary>VERIFY (m11/uatj8, U8): the start plan's rows are held to the same plain-words sweep as every other row: no brace and no JSON key in the question,
        /// unless the call's own text carries braces.</summary>
        [Fact]
        public async Task Verify_every_start_plan_question_is_plain_words_with_no_brace_and_no_json_key()
        {
            var (engine, _) = await StartPlanEngineAsync("Northstar Locked");
            using (engine)
            {
                var scratch = Path.Combine(Root, "scratch.out");
                var rendered = 0; var wrong = new List<string>();
                foreach (var row in GateRegistry.All.Where(r => r.ModelName != null).OrderBy(r => r.Method, StringComparer.Ordinal))
                {
                    var method = EngineMethod(row.Method);
                    var args = AssistantGateKit.NonsenseArgs(method, scratch);
                    for (var i = 0; i < args.Length; i++)
                    {
                        if (method.GetParameters()[i].ParameterType == typeof(StartModelRequest))
                            args[i] = new StartModelRequest { Name = "Northstar Locked", Folder = Path.Combine(Root, "other"), Server = Server, Database = Db };
                        else if (method.GetParameters()[i].ParameterType == typeof(StartPlanEdit))
                            args[i] = new StartPlanEdit { Op = "rename", Target = "table:Sales", Value = "Northstar Sales" };
                        else if (method.GetParameters()[i].ParameterType == typeof(StartWord[]))
                            args[i] = new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Northstar sales lines include posted returns." } };
                    }
                    var call = new GateCall { Engine = engine, Method = method, Parameters = method.GetParameters(), Args = args };
                    var asked = await AssistantEngine.QuestionAsync(engine, row, call);
                    rendered++;
                    var own = args.OfType<string>().Any(a => a.Contains('{') || a.Contains('}'));
                    if (!own && (asked.Question.Contains('{') || asked.Question.Contains('}'))) wrong.Add(row.Method + " shows a brace: " + asked.Question);
                    if (!own && System.Text.RegularExpressions.Regex.IsMatch(asked.Question, "\"[A-Za-z]+\"\\s*:")) wrong.Add(row.Method + " shows a JSON key: " + asked.Question);
                }
                Assert.True(rendered >= 4, "only " + rendered + " start plan rows were rendered");
                Assert.True(wrong.Count == 0, string.Join("\n", wrong));
            }
        }

        // ================================================================== the seven findings, through the door, as the person meets them

        private static async Task RefusedAsync(Func<Task> call) => await Assert.ThrowsAnyAsync<Exception>(call);

        [Fact]
        public async Task A_model_description_question_names_the_model_and_the_new_words()
        {
            var (engine, _, _) = await OpenAsync("desc");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await RefusedAsync(() => Gate(engine).SetDescriptionAsync("model:", "Northstar assistant description one.", "agent"));
                var asked = await OnlyPendingAsync(engine);
                Assert.Contains("the model \"" + engine.GateModelName() + "\"", asked.Summary);
                Assert.Contains("Northstar assistant description one.", asked.Summary);
                Assert.Equal(engine.GateModelName(), asked.Target);
            }
        }

        [Fact]
        public async Task A_find_and_replace_question_says_the_object_what_is_replaced_and_with_what_in_plain_words()
        {
            var (engine, _, _) = await OpenAsync("replace");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await RefusedAsync(() => Gate(engine).ReplaceInObjectAsync(new ReplaceRequest { Ref = "measure:Sales/Total", Field = "expression", Find = "Amount", Replace = "Net Amount", WholeWord = true }, "agent"));
                var asked = (await OnlyPendingAsync(engine)).Summary;
                Assert.DoesNotContain("{", asked); Assert.DoesNotContain("}", asked);
                Assert.DoesNotContain("\"Find\"", asked); Assert.DoesNotContain("\"Ref\"", asked);
                Assert.Contains("replace \"Amount\" with \"Net Amount\"", asked);
                Assert.Contains("[Total]", asked);
                Assert.Contains("formula", asked);
                Assert.Contains("whole word", asked);
                Assert.Contains("the model \"" + engine.GateModelName() + "\"", asked);
            }
        }

        [Fact]
        public async Task Clearing_the_spec_and_the_plan_names_the_model_and_every_line_that_would_be_lost()
        {
            var (engine, _, _) = await OpenAsync("clear");
            using (engine)
            {
                await engine.SetSpecAsync(PersonSpec, "human");
                await ApprovedLineAsync(engine, "Northstar discount share before posted returns.");
                await ApprovedLineAsync(engine, "Second approved words.");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await RefusedAsync(() => Gate(engine).ClearSpecAsync("agent"));
                await RefusedAsync(() => Gate(engine).ClearPlanAsync("agent"));
                var asked = await engine.ListPendingApprovalsAsync();
                var spec = Assert.Single(asked, p => p.Summary.Contains("clear the model spec"));
                Assert.Contains("the model \"" + engine.GateModelName() + "\"", spec.Summary);
                Assert.Contains("Northstar Held", spec.Summary);
                var plan = Assert.Single(asked, p => p.Summary.Contains("clear the whole change plan"));
                Assert.Contains("the model \"" + engine.GateModelName() + "\"", plan.Summary);
                Assert.Contains("Northstar discount share before posted returns.", plan.Summary);
                Assert.Contains("Second approved words.", plan.Summary);
            }
        }

        [Fact]
        public async Task An_open_that_would_lose_work_names_the_model_the_spec_and_the_approved_words()
        {
            var (engine, _, _) = await OpenAsync("open");
            using (engine)
            {
                await engine.SetSpecAsync(PersonSpec, "human");
                await ApprovedLineAsync(engine, "Northstar discount share before posted returns.");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await RefusedAsync(() => Gate(engine).OpenAsync(AssistantGateKit.WriteModel(Root, "open-other"), false));
                var asked = (await OnlyPendingAsync(engine)).Summary;
                Assert.Contains("the model \"" + engine.GateModelName() + "\"", asked);
                Assert.Contains("Northstar Held", asked);
                Assert.Contains("Northstar discount share before posted returns.", asked);
            }
        }

        [Fact]
        public async Task A_note_edit_question_shows_the_words_the_note_has_now_and_not_only_its_id()
        {
            var (engine, _, _) = await OpenAsync("note");
            using (engine)
            {
                var note = await engine.AddInsightAsync("Northstar returns use their posted return date.", null, "insight", "project", false, "human");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await RefusedAsync(() => Gate(engine).EditInsightAsync(note.Id, "Northstar returns use the sale date.", null, "agent"));
                var asked = (await OnlyPendingAsync(engine)).Summary;
                Assert.Contains("Northstar returns use their posted return date.", asked);
                Assert.Contains("Northstar returns use the sale date.", asked);
                Assert.Contains("the model \"" + engine.GateModelName() + "\"", asked);
            }
        }

        [Fact]
        public async Task A_build_that_is_asked_before_the_person_approved_does_not_say_they_approved_and_names_the_model_and_what_it_keeps()
        {
            var (engine, _) = await StartPlanEngineAsync("Northstar Locked");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await RefusedAsync(() => Gate(engine).BuildStartPlanAsync("digest", "agent"));
                var asked = (await OnlyPendingAsync(engine)).Summary;
                Assert.Contains("the model \"Northstar Locked\"", asked);
                Assert.DoesNotContain("you approved", asked);
                Assert.Contains("have not approved", asked);
                Assert.Contains("Sales", asked);
            }
        }

        [Fact]
        public async Task Discarding_the_start_plan_names_the_model_and_the_lines_it_keeps()
        {
            var (engine, _) = await StartPlanEngineAsync("Northstar Locked");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await RefusedAsync(() => Gate(engine).DiscardStartPlanAsync("agent"));
                var asked = (await OnlyPendingAsync(engine)).Summary;
                Assert.Contains("the model \"Northstar Locked\"", asked);
                Assert.Contains("Sales", asked);
                Assert.Contains("Customer", asked);
            }
        }
    }
}

// The Studio wire, engine half (1.4 Claude drives; contract sections 1, 4, 5, 6): show_in_studio and suggest_next on the MCP door,
// the studio/show, studio/suggest and assistant/call broadcasts, the note ring, and the driver report that decides moved or noted.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests
{
    public class StudioWireTests
    {
        private static async Task<DecisionFixture> ModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            return f;
        }

        private static string RepoFile(string relative)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException(relative);
        }

        // ---- show_in_studio: refusals -------------------------------------------------------------------------------

        [Fact]
        public async Task An_unknown_place_is_refused_with_the_list_of_places()
        {
            using var f = await ModelAsync();
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.ShowInStudio(f.Engine, "explore/nowhere", "Looking."));
            Assert.Contains("explore/lineage", ex.Message);
            Assert.Contains("publish/new-model", ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public async Task An_empty_say_is_refused_and_the_limit_is_named(string say)
        {
            using var f = await ModelAsync();
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.ShowInStudio(f.Engine, "plan", say));
            Assert.Contains("200", ex.Message);
        }

        [Fact]
        public async Task A_say_over_200_characters_is_refused_and_200_is_taken()
        {
            using var f = await ModelAsync();
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.ShowInStudio(f.Engine, "plan", new string('a', 201)));
            Assert.Contains("limit is 200", ex.Message);
            var ok = await McpTools.ShowInStudio(f.Engine, "plan", new string('a', 200));
            Assert.StartsWith("sn-", ok.Id);
        }

        [Fact]
        public async Task An_unknown_press_is_refused_with_the_list_and_a_known_one_is_kept()
        {
            using var f = await ModelAsync();
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.ShowInStudio(f.Engine, "plan", "Running it.", press: "click-everything"));
            Assert.Contains("run-plan", ex.Message);
            await McpTools.ShowInStudio(f.Engine, "plan", "Running it.", press: "run-plan");
            var view = await f.Engine.GetStudioNotesAsync(null);
            Assert.Equal("run-plan", Assert.Single(view.Notes).Press);
        }

        [Fact]
        public async Task A_focus_that_is_not_an_object_ref_is_refused()
        {
            using var f = await ModelAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => McpTools.ShowInStudio(f.Engine, "edit/properties", "Here.", focus: "Net Sales"));
        }

        // ---- show_in_studio: the happy path ---------------------------------------------------------------------------

        [Fact]
        public async Task A_show_of_the_query_page_says_the_assistants_run_is_already_a_row_there()
        {
            using var f = await ModelAsync();
            var result = await McpTools.ShowInStudio(f.Engine, "edit/queries", "Here is the query I ran.");
            Assert.False(string.IsNullOrWhiteSpace(result.Shown));
            Assert.Contains("row", result.Shown, StringComparison.Ordinal);
            Assert.Contains("paste", result.Shown, StringComparison.OrdinalIgnoreCase);
            var other = await McpTools.ShowInStudio(f.Engine, "plan", "This is your Plan.");
            Assert.Null(other.Shown);
        }

        [Fact]
        public async Task A_show_publishes_one_studio_show_with_how_filled_and_keeps_it()
        {
            using var f = await ModelAsync();
            var heard = new ConcurrentQueue<StudioNote>();
            f.Sessions.Bus.StudioShow += heard.Enqueue;
            var result = await McpTools.ShowInStudio(f.Engine, " explore/lineage ", " Here is what depends on Net Sales. ", focus: "measure:Sales/Net Sales");

            var note = Assert.Single(heard);
            Assert.Equal(result.Id, note.Id);
            Assert.Matches("^sn-[0-9a-f]{12}$", note.Id);
            Assert.Equal("explore/lineage", note.Place);
            Assert.Equal("Here is what depends on Net Sales.", note.Say);
            Assert.Equal("Open Explore, then Lineage, and pick a field.", note.How);
            Assert.Equal(note.How, result.How);
            Assert.Equal("measure:Sales/Net Sales", note.Focus);
            Assert.Equal("assistant", note.By);
            Assert.Equal(f.Sessions.SessionId, note.SessionId);
            Assert.True(DateTime.TryParse(note.At, null, System.Globalization.DateTimeStyles.RoundtripKind, out _));

            var view = await f.Engine.GetStudioNotesAsync(null);
            Assert.Equal(note.Id, Assert.Single(view.Notes).Id);
        }

        [Fact]
        public async Task The_note_ring_keeps_the_last_100()
        {
            using var f = await ModelAsync();
            for (var i = 0; i < 101; i++) await f.Engine.ShowInStudioAsync(new StudioShowRequest { Place = "plan", Say = "Note " + i });
            var notes = (await f.Engine.GetStudioNotesAsync(null)).Notes;
            Assert.Equal(100, notes.Length);
            Assert.Equal("Note 1", notes[0].Say);
            Assert.Equal("Note 100", notes[^1].Say);
        }

        [Fact]
        public async Task Without_a_Studio_attached_the_screen_is_noStudio()
        {
            using var f = await ModelAsync();
            Assert.Equal("noStudio", (await McpTools.ShowInStudio(f.Engine, "plan", "Hello.")).Screen);
        }

        // ---- suggest_next ---------------------------------------------------------------------------------------------

        private static StudioStep Step(string label = "Fix the descriptions", string prompt = "Fix the missing descriptions.") => new StudioStep { Label = label, Prompt = prompt };

        [Fact]
        public async Task Suggest_next_needs_two_or_three_steps()
        {
            using var f = await ModelAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => McpTools.SuggestNext(f.Engine, new[] { Step() }));
            await Assert.ThrowsAsync<ArgumentException>(() => McpTools.SuggestNext(f.Engine, new[] { Step(), Step(), Step(), Step() }));
            await Assert.ThrowsAsync<ArgumentException>(() => McpTools.SuggestNext(f.Engine, null));
        }

        [Theory]
        [InlineData("Go", "p")]                                   // label under 3
        [InlineData("This label is much longer than forty characters", "p")]
        [InlineData("Fix the descriptions.", "p")]                // a full stop
        [InlineData("Fix the descriptions", "")]                  // empty prompt
        public async Task A_step_out_of_range_is_refused_and_the_limit_is_named(string label, string prompt)
        {
            using var f = await ModelAsync();
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.SuggestNext(f.Engine, new[] { Step(label, prompt), Step() }));
            Assert.Matches("3 to 40|full stop|1 to 280", ex.Message);
        }

        [Fact]
        public async Task A_prompt_over_280_and_a_say_over_200_are_refused()
        {
            using var f = await ModelAsync();
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.SuggestNext(f.Engine, new[] { Step(prompt: new string('p', 281)), Step() }));
            Assert.Contains("280", ex.Message);
            var ex2 = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.SuggestNext(f.Engine, new[] { Step(), Step() }, new string('s', 201)));
            Assert.Contains("200", ex2.Message);
        }

        [Fact]
        public async Task A_suggestion_is_broadcast_once_defaults_its_lead_line_and_is_replaced_by_the_next()
        {
            using var f = await ModelAsync();
            var heard = new ConcurrentQueue<StudioSuggestion>();
            f.Sessions.Bus.StudioSuggest += heard.Enqueue;
            var first = await McpTools.SuggestNext(f.Engine, new[] { Step(), Step("Run the tests", "Run the validation tests.") });
            Assert.True(first.Shown);
            Assert.Matches("^sg-[0-9a-f]{12}$", first.Id);
            var s = Assert.Single(heard);
            Assert.Equal("Here is what I would do next.", s.Say);
            Assert.Equal(2, s.Steps.Length);

            var second = await McpTools.SuggestNext(f.Engine, new[] { Step(), Step("Run the tests", "Run the tests."), Step("Save the model", "Save it.") }, "Next:");
            var view = await f.Engine.GetStudioNotesAsync(null);
            Assert.Equal(second.Id, view.Suggestion.Id);
            Assert.Equal("Next:", view.Suggestion.Say);
            Assert.Equal(3, view.Suggestion.Steps.Length);
        }

        // ---- assistant/call ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task Every_tool_call_publishes_one_assistant_call_with_its_tool_and_call_id()
        {
            using var f = await ModelAsync();
            var heard = new ConcurrentQueue<AgentCallEvent>();
            f.Sessions.Bus.AgentCall += heard.Enqueue;
            await PlanQueueKit.CallAsync(f.Engine);   // the tool name is unknown here: an empty one
            await McpHealthAppender.InvokeAsync(f.Engine, () => new ValueTask<ModelContextProtocol.Protocol.CallToolResult>(new ModelContextProtocol.Protocol.CallToolResult()), "get_model_summary");
            var calls = heard.ToArray();
            Assert.Equal(2, calls.Length);
            Assert.Equal("get_model_summary", calls[1].Tool);
            Assert.NotNull(calls[1].Call);
            Assert.NotEqual(calls[0].Call, calls[1].Call);
            Assert.Equal(f.Sessions.SessionId, calls[1].SessionId);
        }

        [Fact]
        public async Task A_call_that_throws_still_published_its_assistant_call_first()
        {
            using var f = await ModelAsync();
            var heard = new ConcurrentQueue<AgentCallEvent>();
            f.Sessions.Bus.AgentCall += heard.Enqueue;
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await McpHealthAppender.InvokeAsync(f.Engine, () => throw new InvalidOperationException("boom"), "create_measure"));
            Assert.Equal("create_measure", Assert.Single(heard).Tool);
        }

        [Fact]
        public async Task A_call_is_never_written_to_History()
        {
            using var f = await ModelAsync();
            var before = (await f.Engine.GetPlanHistoryAsync()).Rows.Length;
            await McpTools.ShowInStudio(f.Engine, "plan", "Hello.");
            await McpHealthAppender.InvokeAsync(f.Engine, () => new ValueTask<ModelContextProtocol.Protocol.CallToolResult>(new ModelContextProtocol.Protocol.CallToolResult()), "get_model_summary");
            Assert.Equal(before, (await f.Engine.GetPlanHistoryAsync()).Rows.Length);
        }

        // ---- over the pipe ----------------------------------------------------------------------------------------------

        private sealed class StudioPipe : IAsyncDisposable
        {
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private RpcServer _server;
            private Task _serving;
            private System.IO.Pipes.NamedPipeClientStream _pipe;
            internal JsonRpc Human { get; private set; }
            internal RemoteEngine Assistant { get; private set; }
            internal PlanQueueKit.Client Agent { get; private set; }
            internal readonly ConcurrentQueue<JObject> Shows = new ConcurrentQueue<JObject>();
            internal readonly ConcurrentQueue<JObject> Suggests = new ConcurrentQueue<JObject>();
            internal readonly ConcurrentQueue<JObject> Calls = new ConcurrentQueue<JObject>();

            internal static async Task<StudioPipe> OpenAsync(DecisionFixture f, bool withHuman = true)
            {
                var name = TestPipe.Mint("semanticus-studio-");
                var p = new StudioPipe { _server = new RpcServer(f.Sessions, f.Engine, name, PlanQueueKit.Challenge) };
                p._serving = p._server.RunAsync(p._stop.Token);
                if (withHuman)
                {
                    p._pipe = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                    await p._pipe.ConnectAsync(5000);
                    await RpcHandshake.WriteAsync(p._pipe, RpcConnectionRole.Human, PlanQueueKit.Challenge);
                    await RpcHandshake.ReadAcceptedAsync(p._pipe);
                    p.Human = new JsonRpc(RpcServer.CreateHandler(p._pipe));
                    p.Human.AddLocalRpcMethod("studio/show", new Action<JObject>(p.Shows.Enqueue));
                    p.Human.AddLocalRpcMethod("studio/suggest", new Action<JObject>(p.Suggests.Enqueue));
                    p.Human.AddLocalRpcMethod("assistant/call", new Action<JObject>(p.Calls.Enqueue));
                    p.Human.StartListening();
                }
                p.Agent = await PlanQueueKit.Client.ConnectAsync(name, "agent");
                p.Assistant = await RemoteEngine.ConnectAsync(name);
                return p;
            }

            internal static async Task<T> WaitAsync<T>(ConcurrentQueue<T> q, int count)
            {
                for (var i = 0; i < 100 && q.Count < count; i++) await Task.Delay(30);
                Assert.True(q.Count >= count, "the notification did not arrive");
                return q.ToArray()[count - 1];
            }

            public async ValueTask DisposeAsync()
            {
                Assistant?.Dispose();
                Agent?.Dispose();
                try { Human?.Dispose(); } catch { }
                try { _pipe?.Dispose(); } catch { }
                _stop.Cancel();
                try { await _serving; } catch (OperationCanceledException) { }
                _server?.Dispose();
            }
        }

        [Fact]
        public async Task An_attached_assistants_show_reaches_the_Studio_as_one_studio_show_and_names_how_it_moved()
        {
            using var f = await ModelAsync();
            await using var pipe = await StudioPipe.OpenAsync(f);

            var moved = await McpTools.ShowInStudio(pipe.Assistant, "plan", "This is your Plan.");
            Assert.Equal("moved", moved.Screen);                       // a Studio is attached and has reported nothing: a fresh Studio starts in Claude drives
            var show = await StudioPipe.WaitAsync(pipe.Shows, 1);
            Assert.Equal(1, pipe.Shows.Count);
            Assert.Equal("This is your Plan.", (string)show["say"]);
            Assert.Equal("plan", (string)show["place"]);
            Assert.Equal("Open Plan. The top rows are the work worth doing next.", (string)show["how"]);
            Assert.Equal(moved.Id, (string)show["id"]);

            await pipe.Human.InvokeAsync("setStudioDriver", "claudeDrives", "personHasWheel");
            Assert.Equal("noted", (await McpTools.ShowInStudio(pipe.Assistant, "plan", "Still here.")).Screen);
            await pipe.Human.InvokeAsync("setStudioDriver", "iDrive", "idle");
            Assert.Equal("noted", (await McpTools.ShowInStudio(pipe.Assistant, "plan", "Still here.")).Screen);
            await pipe.Human.InvokeAsync("setStudioDriver", "claudeDrives", "claudeDriving");
            Assert.Equal("moved", (await McpTools.ShowInStudio(pipe.Assistant, "plan", "Back.")).Screen);
        }

        [Fact]
        public async Task With_only_an_assistant_attached_the_screen_is_noStudio()
        {
            using var f = await ModelAsync();
            await using var pipe = await StudioPipe.OpenAsync(f, withHuman: false);
            Assert.Equal("noStudio", (await McpTools.ShowInStudio(pipe.Assistant, "plan", "Anyone there?")).Screen);
        }

        [Fact]
        public async Task An_attached_assistants_suggestion_and_calls_reach_the_Studio_and_the_call_id_travels()
        {
            using var f = await ModelAsync();
            await using var pipe = await StudioPipe.OpenAsync(f);

            string minted = null;
            await McpHealthAppender.InvokeAsync(pipe.Assistant, async () =>
            {
                minted = HealthCorrelation.CurrentId;
                await McpTools.SuggestNext(pipe.Assistant, new[] { Step(), Step("Run the tests", "Run the tests.") });
                await McpTools.ShowInStudio(pipe.Assistant, "review/validation", "Here are the tests.");
                return new ModelContextProtocol.Protocol.CallToolResult();
            }, "run_tests");

            var call = await StudioPipe.WaitAsync(pipe.Calls, 1);
            Assert.Equal("run_tests", (string)call["tool"]);
            Assert.Equal(minted, (string)call["call"]);
            Assert.NotNull(minted);
            Assert.Equal(1, pipe.Calls.Count);

            var suggest = await StudioPipe.WaitAsync(pipe.Suggests, 1);
            Assert.Equal(2, ((JArray)suggest["steps"]).Count);
            var show = await StudioPipe.WaitAsync(pipe.Shows, 1);
            Assert.Equal(minted, (string)show["call"]);     // the note groups with its call even when the assistant is attached
        }

        [Fact]
        public async Task The_driver_report_is_the_persons_and_an_agent_connection_has_no_such_method()
        {
            using var f = await ModelAsync();
            await using var pipe = await StudioPipe.OpenAsync(f);
            await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => pipe.Agent.InvokeAsync<object>("setStudioDriver", "iDrive", "idle"));
            await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => pipe.Assistant.SetStudioDriverAsync("iDrive", "idle"));
            await Assert.ThrowsAnyAsync<Exception>(() => pipe.Human.InvokeAsync("setStudioDriver", "sideways", "idle"));
        }

        [Fact]
        public async Task The_Studio_reads_the_notes_on_mount_over_the_pipe()
        {
            using var f = await ModelAsync();
            await using var pipe = await StudioPipe.OpenAsync(f);
            await McpTools.ShowInStudio(pipe.Assistant, "help", "This is Help.");
            var view = await pipe.Human.InvokeAsync<StudioNotesView>("getStudioNotes", new object[] { null });
            Assert.Equal("This is Help.", Assert.Single(view.Notes).Say);
        }

        // ---- auto-follow (1.4 follow): the engine narrates by itself ---------------------------------------------------------

        private sealed class Clock
        {
            public DateTime Now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
        }

        private static async Task<(DecisionFixture F, Clock C, ConcurrentQueue<StudioNote> Heard)> FollowAsync()
        {
            var f = await ModelAsync();
            var c = new Clock();
            f.Engine.StudioClock = () => c.Now;
            f.Sessions.Bus.HumanClientAttached = () => true;   // a Studio is open
            var heard = new ConcurrentQueue<StudioNote>();
            f.Sessions.Bus.StudioShow += heard.Enqueue;
            return (f, c, heard);
        }

        [Fact]
        public async Task A_writing_tool_with_no_show_makes_one_automatic_note_for_its_place_with_the_object_from_the_call()
        {
            var (f, _, heard) = await FollowAsync();
            using var _f = f;
            await f.Engine.NoteAgentCallAsync("set_description", "measure:Sales/Net Sales");
            var note = Assert.Single(heard);
            Assert.Equal("edit/properties", note.Place);
            Assert.Equal("Your assistant is writing descriptions.", note.Say);
            Assert.Equal("measure:Sales/Net Sales", note.Focus);
            Assert.Equal("assistant", note.By);
            Assert.True(note.Auto);
            Assert.Equal("Open the Model tree and select the object. Its properties open beside it.", note.How);
            Assert.Equal(note.Id, Assert.Single((await f.Engine.GetStudioNotesAsync(null)).Notes).Id);
        }

        [Fact]
        public async Task A_tool_the_map_does_not_list_makes_no_note_and_no_Studio_means_no_note()
        {
            var (f, _, heard) = await FollowAsync();
            using var _f = f;
            await f.Engine.NoteAgentCallAsync("get_model_summary");
            await f.Engine.NoteAgentCallAsync("list_measures");
            Assert.Empty(heard);
            f.Sessions.Bus.HumanClientAttached = () => false;
            await f.Engine.NoteAgentCallAsync("set_description", "measure:Sales/Net Sales");
            Assert.Empty(heard);
        }

        [Fact]
        public async Task An_explicit_show_wins_for_ten_seconds_on_its_own_place_and_then_the_engine_narrates_again()
        {
            var (f, c, heard) = await FollowAsync();
            using var _f = f;
            await McpTools.ShowInStudio(f.Engine, "plan", "I will start with the plan.");
            Assert.Single(heard);
            c.Advance(9.9);
            await f.Engine.NoteAgentCallAsync("get_model_plan");
            Assert.Single(heard);                                   // 9.9 s after a show on the plan: the show is still speaking for it
            c.Advance(0.2);
            await f.Engine.NoteAgentCallAsync("set_description", "measure:Sales/Net Sales");
            Assert.Equal(2, heard.Count);                           // 10.1 s: the engine takes over
        }

        // Drive follow (Kane, 13 Oct: "it will be checking BPA but stay on AI readiness"). A show speaks for ITS place only. A tool that
        // works on another place inside the ten seconds is the assistant moving on, and the screen must go with it.
        [Fact]
        public async Task A_show_on_one_place_does_not_keep_the_screen_there_when_the_next_tool_works_on_another()
        {
            var (f, c, heard) = await FollowAsync();
            using var _f = f;
            await McpTools.ShowInStudio(f.Engine, "review/ai-readiness", "Checking how ready the model is for AI.");
            c.Advance(2);
            await f.Engine.NoteAgentCallAsync("bpa_scan");
            Assert.Equal(new[] { "review/ai-readiness", "review/model-quality" }, heard.Select(n => n.Place).ToArray());
            Assert.Contains("best practice", heard.Last().Say);
        }

        [Fact]
        public async Task A_scripted_assistant_calling_readiness_then_best_practice_then_the_plan_gets_one_note_for_each_home()
        {
            var (f, c, heard) = await FollowAsync();
            using var _f = f;
            foreach (var tool in new[] { "ai_readiness_scan", "bpa_scan", "get_model_plan" })
            {
                await f.Engine.NoteAgentCallAsync(tool);
                c.Advance(1);                                       // faster than the three-second spacing: a scan always gets its note
            }
            Assert.Equal(new[] { "review/ai-readiness", "review/model-quality", "plan" }, heard.Select(n => n.Place).ToArray());
            Assert.Contains("ready the model is for AI", heard.ElementAt(0).Say);
            Assert.Contains("best practice", heard.ElementAt(1).Say);
        }

        // The class, not the case: every tool in the MCP registry either has a home the Studio goes to, narrates itself after it commits
        // (and says where), or is listed as one that stays where the screen is, on purpose. A new tool with none of the three fails here.
        [Fact]
        public void Every_MCP_tool_has_a_home_or_stays_where_it_is_on_purpose()
        {
            var map = JObject.Parse(File.ReadAllText(RepoFile("Semanticus.VSCode/webview/src/studio/places.json")));
            var homes = ((JArray)map["places"]).SelectMany(p => ((JArray)p["tools"] ?? new JArray()).Select(t => (string)t)).ToList();
            var elsewhere = ((JObject)map["narratesItself"] ?? new JObject()).Properties().Select(x => x.Name).ToList();
            var stays = ((JObject)map["staysWhereItIs"] ?? new JObject()).Properties().Select(x => x.Name).ToList();
            var tools = typeof(McpTools).Assembly.GetTypes()
                .Where(t => t.IsAbstract && t.IsSealed && t.Name.StartsWith("McpTools", StringComparison.Ordinal))
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .SelectMany(m => m.GetCustomAttributes(true).Where(a => a.GetType().Name == "McpServerToolAttribute"))
                .Select(a => (string)a.GetType().GetProperty("Name").GetValue(a)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.True(tools.Count > 300, "the registry was read: " + tools.Count + " tools");
            var all = homes.Concat(elsewhere).Concat(stays).ToList();
            Assert.Empty(all.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key + " is listed twice"));
            Assert.Empty(all.Except(tools).Select(x => x + " is listed but is not an MCP tool"));
            Assert.Empty(tools.Except(all).Select(x => x + " has no home: add it to a place's tools (with a toolSays sentence), to narratesItself, or to staysWhereItIs"));
            foreach (var kv in ((JObject)map["narratesItself"] ?? new JObject()).Properties())
                Assert.True(StudioMaps.FindPlace((string)kv.Value) != null, kv.Name + " names a place the map does not have");
            foreach (var kv in ((JObject)map["staysWhereItIs"] ?? new JObject()).Properties())
                Assert.False(string.IsNullOrWhiteSpace((string)kv.Value), kv.Name + " stays on purpose, so it says why");
        }

        [Fact]
        public async Task The_same_place_and_the_same_object_as_the_last_note_adds_no_note()
        {
            var (f, c, heard) = await FollowAsync();
            using var _f = f;
            await McpTools.ShowInStudio(f.Engine, "edit/properties", "Looking at Net Sales.", focus: "measure:Sales/Net Sales");
            c.Advance(11);
            await f.Engine.NoteAgentCallAsync("set_description", "measure:Sales/Net Sales");
            Assert.Single(heard);
            await f.Engine.NoteAgentCallAsync("set_description", "measure:Sales/Cost");
            Assert.Equal(2, heard.Count);                           // another object on the same place is a new thing to look at
        }

        [Fact]
        public async Task A_burst_on_one_place_updates_the_count_of_the_one_note_and_adds_none()
        {
            var (f, c, heard) = await FollowAsync();
            using var _f = f;
            for (var i = 1; i <= 24; i++)
            {
                await f.Engine.NoteAgentCallAsync("set_description", "measure:Sales/M" + i);
                c.Advance(0.05);
            }
            var first = heard.First();
            Assert.False(first.Update);
            Assert.Equal(24, heard.Count(n => n.Id == first.Id));   // 1 note + 23 updates, all with the first note's id
            var last = heard.Last();
            Assert.Equal(first.Id, last.Id);
            Assert.True(last.Update);
            Assert.Equal(24, last.Count);
            Assert.Equal("Your assistant has written 24 descriptions so far.", last.Say);
            Assert.Equal("measure:Sales/M1", last.Focus);           // the focus stays where the burst began
            var kept = Assert.Single((await f.Engine.GetStudioNotesAsync(null)).Notes);
            Assert.Equal(24, kept.Count);
            Assert.Equal("Your assistant has written 24 descriptions so far.", kept.Say);
        }

        [Fact]
        public async Task A_change_of_place_inside_three_seconds_still_adds_a_note_for_the_new_place()
        {
            var (f, c, heard) = await FollowAsync();
            using var _f = f;
            await f.Engine.NoteAgentCallAsync("set_description", "measure:Sales/Net Sales");
            c.Advance(0.5);
            await f.Engine.NoteAgentCallAsync("preview_table");
            Assert.Equal(2, heard.Count);                           // the screen follows the tool; the Studio paces the moves, not the engine
            Assert.Equal("explore/data", heard.Last().Place);
            Assert.Equal("Your assistant is looking at the data in a table.", heard.Last().Say);
            await f.Engine.NoteAgentCallAsync("preview_table");
            Assert.True(heard.Last().Update);                       // the same place again only counts on the note it has
            Assert.Equal(2, (await f.Engine.GetStudioNotesAsync(null)).Notes.Count());
        }

        [Fact]
        public async Task A_burst_that_stopped_for_a_while_starts_a_fresh_note_with_a_fresh_count()
        {
            var (f, c, heard) = await FollowAsync();
            using var _f = f;
            await f.Engine.NoteAgentCallAsync("set_description", "measure:Sales/A");
            await f.Engine.NoteAgentCallAsync("set_description", "measure:Sales/B");
            c.Advance(60);
            await f.Engine.NoteAgentCallAsync("set_description", "measure:Sales/C");
            var notes = (await f.Engine.GetStudioNotesAsync(null)).Notes;
            Assert.Equal(2, notes.Length);
            Assert.Equal("Your assistant is writing descriptions.", notes[1].Say);
        }

        // hero15 H1: the engine's own notes never carried a press, approve_start_plan had no place, and build_start_plan was dropped as
        // "the same place as the last note", so the Claude cursor never drew for the assistant's presses.
        [Fact]
        public async Task A_call_that_presses_a_control_makes_its_own_note_with_that_press_even_on_the_same_place()
        {
            var (f, c, heard) = await FollowAsync();
            using var _f = f;
            await f.Engine.NoteAgentCallAsync("grant_start_disclosure");
            c.Advance(0.5);
            await f.Engine.NoteAgentCallAsync("approve_start_plan");
            c.Advance(0.5);
            await f.Engine.NoteAgentCallAsync("build_start_plan");
            var notes = heard.Where(n => !n.Update).ToArray();
            Assert.Equal(3, notes.Length);
            Assert.Null(notes[0].Press);
            Assert.Equal(("plan/start", "approve-build"), (notes[1].Place, notes[1].Press));
            Assert.Equal("Your assistant pressed Approve and build.", notes[1].Say);   // the note names the button it presses
            Assert.Equal(("plan/start", "approve-build"), (notes[2].Place, notes[2].Press));
            c.Advance(0.5);
            await f.Engine.NoteAgentCallAsync("start_model_from_sql");
            Assert.Equal("read-source", heard.Last().Press);
            c.Advance(0.5);
            await f.Engine.NoteAgentCallAsync("apply_plan_items");
            c.Advance(0.5);
            await f.Engine.NoteAgentCallAsync("apply_plan_items");
            var runs = heard.Where(n => n.Press == "run-plan").ToArray();
            Assert.Equal(2, runs.Length);                            // each press is its own note, never a count update
            Assert.All(runs, n => Assert.False(n.Update));
            Assert.Equal("plan", runs[0].Place);
            Assert.Equal("Your assistant pressed Run the plan.", runs[0].Say);
            await f.Engine.NoteAgentCallAsync("get_lineage");
            c.Advance(5);
            await f.Engine.NoteAgentCallAsync("get_lineage");
            Assert.Equal("Your assistant is tracing what depends on a field.", heard.Last().Say);   // a panel, not a button: the tool's own words
        }

        // h17 P8: What I did listed "Your assistant pressed Publish" for every preview. A preview is named as a preview and presses nothing;
        // the one publish is listed once.
        [Fact]
        public async Task A_publish_preview_is_named_as_a_preview_and_the_one_publish_is_listed_once()
        {
            var (f, c, heard) = await FollowAsync();
            using var _f = f;
            for (var i = 0; i < 4; i++) { await f.Engine.NoteAgentCallAsync("deploy_live", commit: false); c.Advance(4); }
            await f.Engine.NoteAgentCallAsync("deploy_live", commit: true);
            var notes = (await f.Engine.GetStudioNotesAsync(null)).Notes;
            Assert.Equal(2, notes.Length);
            Assert.Equal("Your assistant previewed the publish.", notes[0].Say);
            Assert.Null(notes[0].Press);
            Assert.Equal("Your assistant pressed Publish.", notes[1].Say);
            Assert.Equal("publish", notes[1].Press);
        }

        [Fact]
        public async Task A_press_call_after_a_show_that_named_the_same_press_adds_nothing_and_after_a_show_without_one_adds_its_press()
        {
            var (f, c, heard) = await FollowAsync();
            using var _f = f;
            await McpTools.ShowInStudio(f.Engine, "plan", "I will run the plan now.", press: "run-plan");
            c.Advance(1);
            await f.Engine.NoteAgentCallAsync("apply_plan_items");
            Assert.Single(heard);                                    // the show already pressed it
            c.Advance(1);
            await McpTools.ShowInStudio(f.Engine, "plan/start", "The start plan looks right.");
            c.Advance(1);
            await f.Engine.NoteAgentCallAsync("approve_start_plan");
            Assert.Equal(3, heard.Count);
            Assert.Equal("approve-build", heard.Last().Press);
        }

        // hero15 H6: refresh_plan after a plan read was dropped as the same place, and most plan-memory tools had no sentence at all.
        [Fact]
        public async Task A_plan_change_right_after_a_plan_read_gets_its_own_note_in_What_I_did()
        {
            var (f, c, heard) = await FollowAsync();
            using var _f = f;
            await f.Engine.NoteAgentCallAsync("get_model_plan");
            c.Advance(1);
            await f.Engine.NoteAgentCallAsync("refresh_plan");
            c.Advance(1);
            await f.Engine.NoteAgentCallAsync("set_plan_doer");
            var kept = (await f.Engine.GetStudioNotesAsync(null)).Notes;
            Assert.Equal(new[] { "Your assistant is reading your plan.", "Your assistant is refreshing your plan.", "Your assistant is choosing who does a task." },
                kept.Select(n => n.Say).ToArray());
            Assert.All(kept, n => Assert.Equal("plan", n.Place));
            foreach (var tool in new[] { "add_plan_task", "pause_plan_item", "remove_plan_row", "restore_plan_row", "move_plan_task_up", "set_plan_task_note",
                "add_plan_repeat", "set_plan_repeat", "delete_plan_repeat", "set_assistant_queue", "claim_request", "complete_request", "decline_request" })
                Assert.True(StudioMaps.AutoFor(tool)?.Always == true, tool + " is a plan change with its own note");
        }

        // The assistant's waive_finding of a Validation check (system "test") is accepting a difference in a check, on Validation. The same tool on a
        // finding still reads "accepting a finding" on Model quality.
        [Fact]
        public async Task The_assistants_waive_of_a_check_is_narrated_as_accepting_a_difference_on_Validation()
        {
            var (f, c, heard) = await FollowAsync();
            using var _f = f;
            await f.Engine.NoteAgentCallAsync("waive_finding", McpHealthAppender.CheckFocusPrefix + "c1");
            c.Advance(1);
            await f.Engine.NoteAgentCallAsync("unwaive_finding", McpHealthAppender.CheckFocusPrefix + "c1");
            c.Advance(1);
            await f.Engine.NoteAgentCallAsync("waive_finding", "measure:Sales/Net Sales");
            var kept = (await f.Engine.GetStudioNotesAsync(null)).Notes;
            Assert.Equal(new[] { "Your assistant is accepting a difference in a check.", "Your assistant is bringing a difference back in a check.", "Your assistant is accepting a finding." },
                kept.Select(n => n.Say).ToArray());
            Assert.Equal(new[] { "review/validation", "review/validation", "review/model-quality" }, kept.Select(n => n.Place).ToArray());
            Assert.Null(kept[0].Focus);
        }

        // show-checks (Kane, 12 Oct: "when the assistant runs checks in validation the screen doesn't appear to change"): a check run that came
        // within three seconds of another note, or right after a save_test on the same page, was dropped as "nothing new to look at".
        [Fact]
        public async Task A_run_of_checks_always_gets_its_own_note_even_right_after_another_note_or_on_the_same_page()
        {
            var (f, c, heard) = await FollowAsync();
            using var _f = f;
            await f.Engine.NoteAgentCallAsync("set_description", "measure:Sales/Net Sales");
            c.Advance(3.5);
            await f.Engine.NoteAgentCallAsync("save_test");
            c.Advance(0.5);
            await f.Engine.NoteAgentCallAsync("run_tests");
            c.Advance(0.5);
            await f.Engine.NoteAgentCallAsync("bpa_scan");
            c.Advance(0.5);
            await f.Engine.NoteAgentCallAsync("ai_readiness_scan");
            c.Advance(0.5);
            await f.Engine.NoteAgentCallAsync("run_interview");
            var notes = heard.Where(n => !n.Update).ToArray();
            Assert.Equal(new[] { "edit/properties", "review/validation", "review/validation", "review/model-quality", "review/ai-readiness", "review/validation" },
                notes.Select(n => n.Place).ToArray());
            Assert.Equal("Your assistant is running your checks.", notes[2].Say);
            foreach (var tool in new[] { "run_tests", "try_test", "run_interview", "bpa_scan", "ai_readiness_scan" })
                Assert.True(StudioMaps.AutoFor(tool)?.Always == true, tool + " is a run the person wants to watch, so it gets its own note");
        }

        // show-checks: the Validation page only ever showed a run its own Run button made. The assistant's run now reaches the page as a
        // "run_tests" activity (the preview_table / run_dax route), carrying the whole run, so the page opens on what the assistant measured.
        [Fact]
        public async Task The_assistants_run_of_checks_reaches_the_Studio_as_an_activity_with_the_whole_run_and_a_persons_run_does_not()
        {
            using var f = await ModelAsync();
            var heard = new ConcurrentQueue<ActivityEvent>();
            f.Sessions.Bus.Activity += heard.Enqueue;
            await f.Engine.RunTestSuiteAsync(false, "human");
            Assert.Empty(heard);
            var run = await McpToolsTesting.RunTests(f.Engine);
            var e = Assert.Single(heard);
            Assert.Equal(("run_tests", "agent", true), (e.Kind, e.Origin, e.Ok));
            Assert.Same(run, e.Result);
            Assert.Matches("^Ran your checks: ", e.Label);
        }

        [Fact]
        public async Task The_attached_door_carries_the_object_from_the_call_arguments()
        {
            var args = new System.Collections.Generic.Dictionary<string, JsonElement>
            {
                ["objRef"] = JsonDocument.Parse("\"measure:Sales/Net Sales\"").RootElement.Clone(),
            };
            Assert.Equal("measure:Sales/Net Sales", McpHealthAppender.FocusOf(args));
            Assert.Equal("table:Sales", McpHealthAppender.FocusOf(new System.Collections.Generic.Dictionary<string, JsonElement> { ["table"] = JsonDocument.Parse("\"Sales\"").RootElement.Clone() }));
            Assert.Null(McpHealthAppender.FocusOf(new System.Collections.Generic.Dictionary<string, JsonElement> { ["objRef"] = JsonDocument.Parse("\"Net Sales\"").RootElement.Clone() }));
            Assert.Null(McpHealthAppender.FocusOf(null));

            var (f, _, heard) = await FollowAsync();
            using var _f = f;
            await McpHealthAppender.InvokeAsync(f.Engine, () => new ValueTask<ModelContextProtocol.Protocol.CallToolResult>(new ModelContextProtocol.Protocol.CallToolResult()), "rename_object", args);
            Assert.Equal("measure:Sales/Net Sales", Assert.Single(heard).Focus);
        }

        [Fact]
        public void Every_tool_the_place_map_lists_is_a_real_tool_in_one_place_with_a_sentence_and_no_dash()
        {
            var real = typeof(McpTools).Assembly.GetTypes().SelectMany(t => t.GetMethods())
                .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()).Where(a => a != null).Select(a => a.Name).ToHashSet();
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var p in StudioMaps.Places)
                foreach (var t in p.Tools ?? Array.Empty<string>())
                {
                    Assert.True(real.Contains(t), t + " is not an MCP tool");
                    Assert.True(seen.Add(t), t + " is listed in two places");
                    var says = StudioMaps.AutoFor(t);
                    Assert.NotNull(says);
                    Assert.Equal(p.Id, says.Place.Id);
                    Assert.StartsWith("Your assistant", says.Say);
                    Assert.True(says.Say.Length <= LocalEngine.StudioSayMax, t);
                    Assert.DoesNotContain('—', says.Say + says.Count);
                    if (says.Count != null) Assert.Contains("{n}", says.Count);
                }
            foreach (var must in new[] { "ask_data", "ai_readiness_scan", "make_model_ai_ready", "apply_safe_fixes", "get_model_plan", "apply_plan_items", "model_plan_action",
                "start_model_from_sql", "get_start_plan", "edit_start_plan", "build_start_plan", "set_description", "rename_object", "update_measure", "set_synonyms",
                "publish_new_model", "deploy_live", "apply_model_diff" })
                Assert.True(seen.Contains(must), must + " has no place");
            Assert.Equal("explore/ask", StudioMaps.AutoFor("ask_data").Place.Id);
            Assert.Equal("plan/start", StudioMaps.AutoFor("build_start_plan").Place.Id);
            Assert.Equal("publish/new-model", StudioMaps.AutoFor("publish_new_model").Place.Id);
        }

        // ---- answers on screen -----------------------------------------------------------------------------------------------

        [Fact]
        public async Task An_answer_the_assistant_asked_for_is_published_for_the_Ask_page_and_a_persons_is_not()
        {
            using var f = new DecisionFixture();
            await f.Engine.CreateModelAsync("Contoso", 1604);
            var heard = new ConcurrentQueue<StudioAnswer>();
            f.Sessions.Bus.StudioAnswer += heard.Enqueue;
            await f.Engine.AskDataAsync(new Semanticus.Engine.Query.DataAsk { Question = "net sales by region" }, "human");
            Assert.Empty(heard);
            var asked = await f.Engine.AskDataAsync(new Semanticus.Engine.Query.DataAsk { Question = "net sales by region" }, "agent");
            var a = Assert.Single(heard);
            Assert.Matches("^sa-[0-9a-f]{12}$", a.Id);
            Assert.Equal("net sales by region", a.Question);
            Assert.Same(asked, a.Answer);
            Assert.Equal(f.Sessions.SessionId, a.SessionId);
            Assert.Equal(a.Id, (await f.Engine.GetStudioNotesAsync(null)).Answer.Id);   // the Ask page reads it on mount
        }

        // ---- the place map: one file, two readers ---------------------------------------------------------------------------

        [Fact]
        public void The_embedded_place_map_is_the_webviews_file()
        {
            var path = RepoFile(Path.Combine("Semanticus.VSCode", "webview", "src", "studio", "places.json"));
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var ids = doc.RootElement.GetProperty("places").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray();
            Assert.Equal(ids, StudioMaps.PlaceIds.ToArray());
            Assert.Equal(25, ids.Length);
        }

        /// <summary>1.6: Explore > Query replaced Ask your data and Queries. The two places keep their ids (the description and the
        /// assistant's habit depend on them) but both open the one page, called Query, and say so in their steps. No place names
        /// a page that no longer exists, and the press for Ask finds the Ask button in the Query page's bar.</summary>
        [Fact]
        public void Explore_ask_and_edit_queries_both_open_the_query_page()
        {
            foreach (var id in new[] { "explore/ask", "edit/queries" })
            {
                var place = StudioMaps.FindPlace(id);
                Assert.NotNull(place);
                Assert.Equal("query", place.Tool);
                Assert.Equal("Query", place.Label);
                Assert.Contains("Explore, then Query", place.How, StringComparison.Ordinal);
            }
            foreach (var p in StudioMaps.Places)
            {
                Assert.NotEqual("askdata", p.Tool);
                Assert.NotEqual("daxlab", p.Tool);
                Assert.DoesNotContain("Ask your data", p.How, StringComparison.Ordinal);
                Assert.DoesNotContain("Queries", p.How, StringComparison.Ordinal);
            }
            // The properties place only reveals an object in the tree, so it names a page that still exists.
            Assert.Equal("focus", StudioMaps.FindPlace("edit/properties").Target);
            Assert.Equal("instructions", StudioMaps.FindPlace("edit/properties").Tool);

            var presses = File.ReadAllText(RepoFile("Semanticus.VSCode/webview/src/studio/presses.json"));
            Assert.Contains("[data-testid=\\\"query-ask\\\"]", presses, StringComparison.Ordinal);
            Assert.DoesNotContain("askdata-", presses, StringComparison.Ordinal);
            Assert.Equal("explore/ask", StudioMaps.PressFor("ask_data").Place);
            Assert.Equal("edit/queries", StudioMaps.PressFor("explain_value").Place);
        }

        [Fact]
        public void The_tool_description_names_every_place_and_every_press()
        {
            var attr = typeof(McpTools).GetMethod("ShowInStudio").GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
            foreach (var id in StudioMaps.PlaceIds) Assert.Contains(id + (id == StudioMaps.PlaceIds.Last() ? ";" : ","), attr.Description);
            foreach (var id in StudioMaps.PressIds) Assert.Contains(id, attr.Description);
        }

        [Fact]
        public void The_tools_say_nothing_with_a_dash_or_the_words_one_click()
        {
            foreach (var name in new[] { "ShowInStudio", "SuggestNext" })
            {
                var text = typeof(McpTools).GetMethod(name).GetCustomAttribute<System.ComponentModel.DescriptionAttribute>().Description;
                Assert.DoesNotContain('—', text);
                Assert.DoesNotContain("one click", text, StringComparison.OrdinalIgnoreCase);
            }
            foreach (var p in StudioMaps.Places) Assert.DoesNotContain('—', p.How);
        }
    }
}

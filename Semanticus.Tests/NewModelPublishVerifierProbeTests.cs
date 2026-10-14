using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    // Verifier probes for m15/newmodel-publish (hero20 D7, D8). Not for merge: they prove or disprove the lane's claims.
    [Collection("deploy-serial")]
    public sealed class NewModelPublishVerifierProbeTests : IDisposable
    {
        private readonly List<int> _pauses = new();
        private long _now;

        public NewModelPublishVerifierProbeTests()
        {
            EntraToken.FabricTokenForTests = () => "test-bearer";
            NewModelPublish.NowMs = () => _now;
            NewModelPublish.Pause = (seconds, _) => { _pauses.Add(seconds); _now += seconds * 1000L; return Task.CompletedTask; };
        }

        public void Dispose()
        {
            FabricRest.TestClientFactory = null;
            EntraToken.FabricTokenForTests = null;
            NewModelPublish.CreateBudget = TimeSpan.FromMinutes(4);
            NewModelPublish.NowMs = () => Environment.TickCount64;
            NewModelPublish.Pause = (seconds, ct) => Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        }

        // The hero20 model, as the person had it on disk (Hero20Shell.bim): compatibility 1600, two import tables, no V3 marker.
        private const string HeroBim = "{\"name\":\"Hero20 Shell\",\"compatibilityLevel\":1600,\"model\":{\"culture\":\"en-US\",\"tables\":[" +
            "{\"name\":\"Products\",\"columns\":[{\"name\":\"ProductKey\",\"dataType\":\"int64\",\"sourceColumn\":\"ProductKey\"}]," +
            "\"partitions\":[{\"name\":\"Products\",\"mode\":\"import\",\"source\":{\"type\":\"m\",\"expression\":\"#table(type table [#\\\"ProductKey\\\" = number], {{1}})\"}}]}," +
            "{\"name\":\"Orders\",\"columns\":[{\"name\":\"ProductKey\",\"dataType\":\"int64\",\"sourceColumn\":\"ProductKey\"},{\"name\":\"Amount\",\"dataType\":\"double\",\"sourceColumn\":\"Amount\"}]," +
            "\"measures\":[{\"name\":\"Sales Amount\",\"expression\":\"SUM(Orders[Amount])\"}]," +
            "\"partitions\":[{\"name\":\"Orders\",\"mode\":\"import\",\"source\":{\"type\":\"m\",\"expression\":\"#table(type table [#\\\"ProductKey\\\" = number, #\\\"Amount\\\" = number], {{1, 300}})\"}}]}]," +
            "\"relationships\":[{\"name\":\"r0\",\"fromTable\":\"Orders\",\"fromColumn\":\"ProductKey\",\"toTable\":\"Products\",\"toColumn\":\"ProductKey\"}]}}";

        private sealed class Recorded : HttpMessageHandler
        {
            public readonly List<string> Calls = new();
            public Func<string, string, HttpResponseMessage> Route;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                lock (Calls) Calls.Add(req.Method.Method + " " + req.RequestUri!.AbsolutePath);
                return Task.FromResult(Route(req.Method.Method, req.RequestUri.AbsolutePath));
            }
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Bom(string body) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(body)).ToArray()) };

        private static HttpResponseMessage Accepted()
        {
            var r = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("") };
            r.Headers.Location = new Uri("https://api.fabric.microsoft.com/v1/operations/op-1");
            r.Headers.TryAddWithoutValidation("x-ms-operation-id", "op-1");
            r.Headers.TryAddWithoutValidation("Retry-After", "20");
            return r;
        }

        private Recorded Install(Func<string, string, HttpResponseMessage> route)
        {
            var h = new Recorded { Route = route };
            FabricRest.TestClientFactory = () => new HttpClient(h, disposeHandler: false) { BaseAddress = new Uri(FabricRest.BaseUrl) };
            return h;
        }

        private static Microsoft.AnalysisServices.Tabular.Database HeroDb() => NewModelPublish.Prepare(HeroBim, "Hero20 Shell (test)");

        // 1. The read path: the same bytes with and without a BOM read the same. (Run once with the TrimStart line removed to see the old loop.)
        [Fact]
        public async Task P1_the_poll_read_sees_succeeded_with_and_without_a_byte_order_mark()
        {
            foreach (var bom in new[] { false, true })
            {
                var http = new HttpClient(new Recorded { Route = (_, _) => bom ? Bom("{\"status\":\"Succeeded\"}") : Json("{\"status\":\"Succeeded\"}") }) { BaseAddress = new Uri(FabricRest.BaseUrl) };
                var st = await FabricRest.PollOperationAsync(http, "op-1", CancellationToken.None, firstDelaySeconds: 0, maxPolls: 2);
                Assert.Equal("Succeeded", st.Status);
            }
        }

        // 1b. The recorded shape: 202 + Location + Retry-After, Running (BOM), Running, Succeeded (BOM). One POST, then GETs on the Location only.
        [Fact]
        public async Task P1b_recorded_202_location_running_succeeded_with_a_bom_finishes_with_one_post()
        {
            var polls = 0;
            var h = Install((m, path) =>
            {
                if (m == "POST") return Accepted();
                if (path == "/v1/operations/op-1") { polls++; return polls == 1 ? Bom("{\"status\":\"Running\"}") : polls == 2 ? Json("{\"status\":\"Running\"}") : Bom("{\"status\":\"Succeeded\"}"); }
                return Json("{\"value\":[]}");
            });
            await NewModelPublish.CreateModelAsync(HeroDb(), "w1", "t", CancellationToken.None);
            Assert.Equal(3, polls);
            Assert.Single(h.Calls, c => c.StartsWith("POST"));
            Assert.Equal(10, _pauses[0]);   // Retry-After 20 is held to 10 s
        }

        // 2. A poll error after the 202 never re-POSTs; a listed model of another name never counts as created.
        [Fact]
        public async Task P2_a_poll_error_after_the_202_never_posts_again()
        {
            var h = Install((m, path) => m == "POST" ? Accepted() : path == "/v1/operations/op-1" ? Json("{}", HttpStatusCode.InternalServerError) : Json("{\"value\":[]}"));
            await Assert.ThrowsAnyAsync<Exception>(() => NewModelPublish.CreateModelAsync(HeroDb(), "w1", "t", CancellationToken.None));
            Assert.Single(h.Calls, c => c.StartsWith("POST"));
        }

        [Fact]
        public async Task P2b_a_listed_model_with_another_name_is_not_this_create()
        {
            var h = Install((m, path) => m == "POST" ? Accepted() : path == "/v1/operations/op-1" ? Json("{\"status\":\"Running\"}")
                : Json("{\"value\":[{\"id\":\"x\",\"displayName\":\"Hero20 Shell (test) old\",\"workspaceId\":\"w1\"},{\"id\":\"y\",\"displayName\":\"Contoso Sales\",\"workspaceId\":\"w1\"}]}"));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewModelPublish.CreateModelAsync(HeroDb(), "w1", "t", CancellationToken.None));
            Assert.StartsWith("Power BI had not finished creating the model after 4 minutes.", ex.Message);
            Assert.Single(h.Calls, c => c.StartsWith("POST"));
        }

        // 2c. A model with that name already there before the request: refused before any POST (the "created after the request" guarantee).
        [Fact]
        public async Task P2c_a_name_already_in_the_workspace_is_refused_before_any_post()
        {
            var h = Install((m, path) => m == "POST" ? Accepted() : Json("{\"value\":[{\"id\":\"m0\",\"displayName\":\"hero20 shell (TEST)\",\"workspaceId\":\"w1\"}]}"));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewModelPublish.CreateAndLoadAsync(HeroDb(), "w1", "t", "pbi", CancellationToken.None, "Semanticus Demo"));
            Assert.Contains("Pick another name", ex.Message);
            Assert.DoesNotContain(h.Calls, c => c.StartsWith("POST"));
        }

        // 3. The 4-minute stop: one plain line, and nothing after it (no refresh, no delete, no second POST).
        [Fact]
        public async Task P3_the_budget_stop_is_one_plain_line_and_nothing_runs_after_it()
        {
            var h = Install((m, path) => m == "POST" && path.EndsWith("/semanticModels") ? Accepted() : path == "/v1/operations/op-1" ? Json("{\"status\":\"Running\"}") : Json("{\"value\":[]}"));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewModelPublish.CreateAndLoadAsync(HeroDb(), "w1", "t", "pbi", CancellationToken.None, "Semanticus Demo"));
            Assert.Equal("Power BI had not finished creating the model after 4 minutes. Look in the workspace before you try again.", ex.Message);
            Assert.DoesNotContain('\n', ex.Message);
            Assert.DoesNotContain('—', ex.Message);
            Assert.Single(h.Calls, c => c.StartsWith("POST"));
            Assert.DoesNotContain(h.Calls, c => c.Contains("refreshes") || c.StartsWith("DELETE"));
        }

        // 1c (live shape). hero20's direct call: the same model WITHOUT the V3 marker came back "Failed: Import from JSON supported for V3 models
        // only" in 20 s; WITH the marker it was created in 20 s. The product's create body must carry the marker or the live create still fails.
        [Fact]
        public void P1c_the_create_body_for_the_hero_model_carries_the_v3_marker()
        {
            var body = JsonDocument.Parse(NewModelPublish.CreateBody(HeroDb()));
            var part = body.RootElement.GetProperty("definition").GetProperty("parts").EnumerateArray().First(p => p.GetProperty("path").GetString() == "model.bim");
            var bim = Encoding.UTF8.GetString(Convert.FromBase64String(part.GetProperty("payload").GetString()!));
            Assert.Contains("defaultPowerBIDataSourceVersion", bim);
            Assert.Contains("powerBI_V3", bim);
        }

        // 5. The wire: a whole publish sends the deploy_live steps the page shows, then the end event (empty note).
        [Fact]
        public async Task P5_a_whole_publish_sends_its_steps_on_the_progress_channel()
        {
            var listed = 0;
            Install((m, path) =>
            {
                if (path == "/v1/workspaces") return Json("{\"value\":[{\"id\":\"w1\",\"displayName\":\"Semanticus Demo\",\"type\":\"Workspace\"}]}");
                if (m == "POST" && path.EndsWith("/semanticModels")) return Accepted();
                if (path == "/v1/operations/op-1") return Bom("{\"status\":\"Succeeded\"}");
                if (path.EndsWith("/semanticModels")) return Json(++listed >= 2 ? "{\"value\":[{\"id\":\"m1\",\"displayName\":\"Probe (test)\",\"workspaceId\":\"w1\"}]}" : "{\"value\":[]}");
                if (m == "POST" && path.EndsWith("/refreshes")) return Json("", HttpStatusCode.Accepted);
                if (path.EndsWith("/refreshes")) return Json("{\"value\":[{\"status\":\"Completed\"}]}");
                return Json("{\"value\":[]}");
            });
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Sales", 1604);
            sessions.Require().CacheLiveToken(LocalEngine.LiveAuthKey("serviceprincipal", null, null), new Azure.Core.AccessToken("pbi-test", DateTimeOffset.UtcNow.AddHours(1)));
            var notes = new List<(string Op, string Note)>();
            sessions.Bus.Progress += p => { lock (notes) notes.Add((p.OpKey, p.Note)); };

            var r = await engine.PublishNewModelAsync("Semanticus Demo", "Probe (test)", authMode: "serviceprincipal", origin: "human", commit: true);

            Assert.True(r.Loaded, r.Note);
            var seq = notes.Where(n => n.Op == "deploy_live").Select(n => n.Note).ToList();
            Assert.Equal(new[] { "Signing in", "Finding the workspace", "Signing in", "Preparing the model", "Checking the name", "Creating the model in Power BI", "Loading its data", null }, seq);
        }

        // 4. With nothing saved, the assistant's commit ends on the one plain line (not a gate sentence, not an Entra message).
        [Fact]
        public async Task P4_an_assistant_commit_with_nothing_saved_ends_on_the_plain_line()
        {
            var names = new[] { "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AZURE_TENANT_ID", "FABRIC_CLIENT", "FABRIC_SECRET", "FABRIC_TENANT", "POWERBI_CLIENT_ID", "POWERBI_CLIENT_SECRET", "POWERBI_TENANT_ID" };
            var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var n in names) Environment.SetEnvironmentVariable(n, null);
                var h = Install((_, _) => Json("{\"value\":[]}"));
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.CreateModelAsync("Sales", 1604);
                foreach (var asked in new[] { null, "interactive", "devicecode", "azcli" })
                {
                    var ex = await Record.ExceptionAsync(() => engine.PublishNewModelAsync("Semanticus Demo", "x", authMode: asked, origin: "agent", commit: true));
                    Assert.Equal(NewModelPublish.NoSignInSaved, ex?.Message);
                }
                Assert.Empty(h.Calls);
            }
            finally { foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value); }
        }

        // 4b. The assistant door's own workspace list (the tool publish_new_model's text points to) still starts az when no mode is named.
        [Fact]
        public void P4b_list_workspaces_in_the_assistant_door_does_not_default_to_az()
        {
            foreach (var tool in new[] { "ListWorkspaces", "ListWorkspaceModels" })
            {
                var p = typeof(McpTools).GetMethod(tool)!.GetParameters().First(x => x.Name == "authMode");
                Assert.NotEqual("azcli", p.DefaultValue as string);
            }
        }
    }
}

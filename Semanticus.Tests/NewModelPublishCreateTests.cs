using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    // hero20 D7 and D8: publish_new_model never finished for a small import model (2 x 8 minutes, "did not reach a terminal state"), and the
    // assistant tried az, a browser and a device code before the saved sign-in. Offline: a recorded Fabric exchange behind FabricRest.TestClientFactory,
    // and a clock and pause the poll can be driven with, so no test waits.
    [Collection("deploy-serial")]
    public sealed class NewModelPublishCreateTests : IDisposable
    {
        private readonly List<int> _pauses = new();
        private long _now;

        public NewModelPublishCreateTests()
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

        private static TOM.Database Db(string name = "Shell (test)")
        {
            var db = new TOM.Database(name) { CompatibilityLevel = 1600, Model = new TOM.Model { Culture = "en-US" } };
            return db;
        }

        private sealed class Recorded : HttpMessageHandler
        {
            public readonly List<string> Calls = new();
            public Func<string, string, HttpResponseMessage> Route;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                var line = req.Method.Method + " " + req.RequestUri!.AbsolutePath;
                lock (Calls) Calls.Add(line);
                return Task.FromResult(Route(req.Method.Method, req.RequestUri.AbsolutePath));
            }
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Bom(string body)
        {
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(body)).ToArray();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }

        private static HttpResponseMessage Accepted(int retryAfter = 1)
        {
            var r = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("") };
            r.Headers.Location = new Uri("https://api.fabric.microsoft.com/v1/operations/op-1");
            r.Headers.TryAddWithoutValidation("x-ms-operation-id", "op-1");
            r.Headers.TryAddWithoutValidation("Retry-After", retryAfter.ToString());
            return r;
        }

        private Recorded Install(Func<string, string, HttpResponseMessage> route)
        {
            var h = new Recorded { Route = route };
            FabricRest.TestClientFactory = () => new HttpClient(h, disposeHandler: false) { BaseAddress = new Uri(FabricRest.BaseUrl) };
            return h;
        }

        private const string Listed = "{\"value\":[{\"id\":\"m1\",\"displayName\":\"Shell (test)\",\"workspaceId\":\"w1\"}]}";
        private const string Empty = "{\"value\":[]}";

        [Fact]
        public async Task A_recorded_create_202_then_running_then_succeeded_finishes_on_the_address_it_was_given()
        {
            var polls = 0;
            var h = Install((m, path) =>
            {
                if (m == "POST") return Accepted(retryAfter: 3);
                if (path.EndsWith("/operations/op-1")) return Json(++polls < 3 ? "{\"status\":\"Running\"}" : "{\"status\":\"Succeeded\",\"percentComplete\":100}");
                return Json(Empty);
            });

            await NewModelPublish.CreateModelAsync(Db(), "w1", "t", CancellationToken.None);

            Assert.Equal(3, polls);
            Assert.Equal(3, _pauses[0]);   // the first wait is the one the 202 asked for
            Assert.Equal("POST /v1/workspaces/w1/semanticModels", h.Calls[0]);
        }

        // The failing shape: Power BI's answer cannot be read (a byte-order mark, a proxy page). It used to read as "still running" for 240 polls.
        [Fact]
        public async Task An_answer_with_a_byte_order_mark_is_read_and_the_create_finishes()
        {
            Install((m, path) => m == "POST" ? Accepted() : path.EndsWith("/operations/op-1") ? Bom("{\"status\":\"Succeeded\"}") : Json(Empty));

            await NewModelPublish.CreateModelAsync(Db(), "w1", "t", CancellationToken.None);

            Assert.Single(_pauses);
        }

        [Fact]
        public async Task An_answer_nobody_can_read_stops_after_three_polls_with_one_plain_line()
        {
            Install((m, path) => m == "POST" ? Accepted() : path.EndsWith("/operations/op-1") ? Json("<html>gateway</html>") : Json(Empty));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewModelPublish.CreateModelAsync(Db(), "w1", "t", CancellationToken.None));

            Assert.Equal(NewModelPublish.UnreadableText, ex.Message);
            Assert.Equal(3, _pauses.Count);
            Assert.DoesNotContain("terminal state", ex.Message);
        }

        [Fact]
        public async Task A_failed_create_says_the_reason_the_service_gave()
        {
            Install((m, path) => m == "POST" ? Accepted() : Json("{\"status\":\"Failed\",\"error\":{\"errorCode\":\"x\",\"message\":\"Import from JSON supported for V3 models only\"}}"));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewModelPublish.CreateModelAsync(Db(), "w1", "t", CancellationToken.None));

            Assert.Equal("Power BI did not create the model: Import from JSON supported for V3 models only", ex.Message);
        }

        [Fact]
        public async Task A_create_that_never_ends_stops_at_the_budget_not_after_eight_minutes()
        {
            var polls = 0;
            Install((m, path) => m == "POST" ? Accepted() : path.EndsWith("/operations/op-1") ? Json("{\"status\":\"Running\"}") : Json(Empty));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewModelPublish.CreateModelAsync(Db(), "w1", "t", CancellationToken.None));

            Assert.Equal(NewModelPublish.StillCreatingText(TimeSpan.FromMinutes(4)), ex.Message);
            Assert.Contains("4 minutes", ex.Message);
            Assert.True(polls < 130, "polls: " + polls);
            Assert.True(_now <= (long)TimeSpan.FromMinutes(4.5).TotalMilliseconds);
        }

        // An import model can sit in "Running" after it is already in the workspace; a listed model was created.
        [Fact]
        public async Task A_model_that_is_listed_while_the_operation_still_says_running_counts_as_created()
        {
            var lists = 0;
            Install((m, path) =>
            {
                if (m == "POST") return Accepted();
                if (path.EndsWith("/operations/op-1")) return Json("{\"status\":\"Running\"}");
                return Json(++lists >= 2 ? Listed : Empty);
            });

            await NewModelPublish.CreateModelAsync(Db(), "w1", "t", CancellationToken.None);

            Assert.True(_pauses.Count <= 8, "pauses: " + _pauses.Count);
        }

        [Fact]
        public async Task A_plain_201_needs_no_polling()
        {
            var h = Install((m, path) => m == "POST" ? Json("{\"id\":\"m1\"}", HttpStatusCode.Created) : Json(Empty));
            await NewModelPublish.CreateModelAsync(Db(), "w1", "t", CancellationToken.None);
            Assert.Empty(_pauses);
            Assert.Single(h.Calls);
        }

        [Fact]
        public async Task The_whole_publish_names_each_step_for_the_page_in_order()
        {
            var steps = new List<string>();
            var listed = 0;
            Install((m, path) =>
            {
                if (m == "POST" && path.EndsWith("/semanticModels")) return Accepted();
                if (path.EndsWith("/operations/op-1")) return Json("{\"status\":\"Succeeded\"}");
                if (path.EndsWith("/semanticModels")) return Json(++listed >= 2 ? Listed : Empty);
                if (m == "POST" && path.EndsWith("/refreshes")) return Json("", HttpStatusCode.Accepted);
                if (path.EndsWith("/refreshes")) return Json("{\"value\":[{\"status\":\"Completed\"}]}");
                return Json(Empty);
            });

            var o = await NewModelPublish.CreateAndLoadAsync(Db(), "w1", "t", "pbi", CancellationToken.None, "Semanticus Demo", steps.Add);

            Assert.True(o.Loaded);
            Assert.Equal(new[] { "Checking the name", "Creating the model in Power BI", "Loading its data" }, steps);
        }

        // ---- D8: the sign-in order for the assistant ----

        [Fact]
        public void The_assistant_never_gets_a_browser_or_az_sign_in_and_with_none_saved_is_told_so_in_one_plain_line()
        {
            var names = new[] { "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AZURE_TENANT_ID", "FABRIC_CLIENT", "FABRIC_SECRET", "FABRIC_TENANT", "POWERBI_CLIENT_ID", "POWERBI_CLIENT_SECRET", "POWERBI_TENANT_ID" };
            var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var n in names) Environment.SetEnvironmentVariable(n, null);
                using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                Assert.DoesNotContain(EntraToken.ListProfiles(), p => p.SignedIn);

                foreach (var asked in new[] { null, "", "interactive", "devicecode", "azcli" })
                    Assert.Null(engine.ResolveNewModelSignIn(asked, null).Mode);
                Assert.Equal("No sign-in is saved yet. Press Connect in Studio, then ask again.", NewModelPublish.NoSignInSaved);
                Assert.DoesNotContain("az login", NewModelPublish.NoSignInSaved);

                Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "client");
                Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "secret");
                Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "tenant");
                foreach (var asked in new[] { null, "interactive", "devicecode", "azcli", "serviceprincipal" })
                    Assert.Equal("serviceprincipal", engine.ResolveNewModelSignIn(asked, null).Mode);
            }
            finally { foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value); }
        }

        [Fact]
        public async Task An_assistant_publish_with_no_sign_in_saved_stops_before_any_call_and_never_asks_for_az_login()
        {
            var names = new[] { "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AZURE_TENANT_ID", "FABRIC_CLIENT", "FABRIC_SECRET", "FABRIC_TENANT", "POWERBI_CLIENT_ID", "POWERBI_CLIENT_SECRET", "POWERBI_TENANT_ID" };
            var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var n in names) Environment.SetEnvironmentVariable(n, null);
                var calls = 0;
                FabricRest.TestClientFactory = () => new HttpClient(new Recorded { Route = (_, _) => { calls++; return Json(Empty); } }) { BaseAddress = new Uri(FabricRest.BaseUrl) };
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.CreateModelAsync("Sales", 1604);

                var ex = await Record.ExceptionAsync(() => engine.PublishNewModelAsync("Semanticus Demo", "x", authMode: "interactive", origin: "agent", commit: true));

                Assert.NotNull(ex);
                Assert.DoesNotContain("az login", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Interactive authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, calls);
            }
            finally { foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value); }
        }
    }
}

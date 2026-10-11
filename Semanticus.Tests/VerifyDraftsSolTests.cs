// Verifier check of m11/drafts-sol, 5 Oct: the builder's join item 3 ("click-only editors are not fenced by identity"). R-A says the ENGINE refuses a write that a
// draft produces when another model is open. The AI instructions editor's Save is two calls: it reads the live instructions, then writes, naming that read as the
// value it replaces. When the model switches between the two (the assistant opens B over MCP, or the person does from the tree, while the Save waits on a busy
// engine), the write names nothing the engine can tell apart: B's instructions equal A's starting value, so the words typed for A land in B.
// verify-drafts-sol.test.mjs holds the client half (the editor sends that write after the switch). Left failing on purpose: this is the defect.
// Round 2 re-check (5 Oct): R_A is RETARGETED to the person's door (RpcServer, real JSON), the design the round-2 brief ratified: the page names the model it was
// showing on the request (`modelIdentity`), and the door refuses a write for another model; a call that names none keeps today's behaviour. The steps are the same
// (A's read, B opens, A's write naming A's start value); only the entry point moved from LocalEngine to the door the editor really uses.
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifyDraftsSolTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-vdsol-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        public VerifyDraftsSolTests() => Directory.CreateDirectory(_root);
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        private string Copy(string name)
        {
            var p = Path.Combine(_root, name);
            File.Copy(TestModels.FindBim(), p);
            return p;
        }

        [Fact]
        public async Task Control_the_same_write_lands_in_A_while_A_is_open()
        {
            using var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.OpenAsync(Copy("A.bim"), discardUnsaved: true); await e.SetCompatibilityLevelAsync(1600, "human");   // AI instructions need 1465+
            var startedFrom = (await e.GetAiInstructionsAsync(null)).Instructions ?? "";
            await e.SetAiInstructionsAsync("Words typed for A", null, "human", startedFrom);
            Assert.Equal("Words typed for A", (await e.GetAiInstructionsAsync(null)).Instructions);
        }

        [Fact]
        public async Task R_A_instructions_typed_for_A_and_saved_while_B_opened_are_not_written_into_B()
        {
            const string challenge = "vdsol-ra-challenge-0123456789abcdef";
            var sessions = new SessionManager();
            using var e = new LocalEngine(sessions, TestEntitlements.Pro);
            var pipeName = TestPipe.Mint("sem-vdsol-");
            using var server = new RpcServer(sessions, e, pipeName, challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            try
            {
                await e.OpenAsync(Copy("A.bim"), discardUnsaved: true); await e.SetCompatibilityLevelAsync(1600, "human");   // AI instructions need 1465+
                var idA = (await e.SessionInfoAsync()).ModelIdentity;                              // the model the page was showing when Save was pressed
                var startedFrom = (await e.GetAiInstructionsAsync(null)).Instructions ?? "";   // the Save's read, on A
                await e.OpenAsync(Copy("B.bim"), discardUnsaved: true); await e.SetCompatibilityLevelAsync(1600, "human");   // B opens while the Save is on its way
                var bBefore = (await e.GetAiInstructionsAsync(null)).Instructions ?? "";
                Assert.Equal(startedFrom, bBefore);                                               // the two models start alike, as in Sol's F1 scenario
                Assert.NotEqual(idA, (await e.SessionInfoAsync()).ModelIdentity);

                // The Save's write, exactly as the extension sends it over the person's door: the words, no culture, the person, the value it replaces, and the page's model.
                var error = await CallOverTheDoorAsync(pipeName, challenge, "setAiInstructions", new object[] { "Words typed for A", null, "human", startedFrom }, idA);
                Assert.True(error != null && error.Contains("another model"), "the door must refuse the write; it answered: " + (error ?? "success"));
                Assert.Equal(bBefore, (await e.GetAiInstructionsAsync(null)).Instructions ?? "");
            }
            finally
            {
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                sessions.Dispose();
            }
        }

        /// <summary>One raw JSON-RPC request over the person's pipe, with `modelIdentity` as a top-level member. Returns the error message, or null on success.</summary>
        private static async Task<string> CallOverTheDoorAsync(string pipeName, string challenge, string method, object[] args, string modelIdentity)
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            await RpcHandshake.WriteAsync(pipe, RpcConnectionRole.Human, challenge);
            await RpcHandshake.ReadAcceptedAsync(pipe);
            var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = JArray.FromObject(args), ["modelIdentity"] = modelIdentity };
            var body = Encoding.UTF8.GetBytes(msg.ToString(Newtonsoft.Json.Formatting.None));
            var head = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
            await pipe.WriteAsync(head, 0, head.Length);
            await pipe.WriteAsync(body, 0, body.Length);
            await pipe.FlushAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var header = new StringBuilder();
                var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await pipe.ReadAsync(one, 0, 1, cts.Token) == 0) throw new EndOfStreamException();
                    header.Append((char)one[0]);
                }
                var length = int.Parse(System.Text.RegularExpressions.Regex.Match(header.ToString(), @"Content-Length:\s*(\d+)").Groups[1].Value);
                var buf = new byte[length];
                for (var read = 0; read < length;)
                {
                    var n = await pipe.ReadAsync(buf, read, length - read, cts.Token);
                    if (n == 0) throw new EndOfStreamException();
                    read += n;
                }
                var reply = JObject.Parse(Encoding.UTF8.GetString(buf));
                if ((int?)reply["id"] != 1) continue;   // a notification is not the answer
                return (string)reply["error"]?["message"];
            }
        }
    }
}

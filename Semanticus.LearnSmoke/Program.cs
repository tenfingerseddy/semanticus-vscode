using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using StreamJsonRpc;

namespace Semanticus.LearnSmoke
{
    /// <summary>
    /// Learning-loop L0→L2 WIRING proof (docs/learning-loop-testing.md §4; learning-loop-plan.md §7): a gate
    /// FAILS, its root cause becomes an approved insight, and recall surfaces that insight on a SAME-SHAPE model
    /// reopened over the MCP door. Proves the five stages connect across the dual-drive session, driven through
    /// the RemoteEngine proxy exactly as the --mcp host would. It does NOT measure lift — that is LearnBench (T3).
    ///
    /// Wiring mirrors Semanticus.McpSmoke:
    ///   owner engine (RpcServer) ── pipe ──┬── UI client (watches workflow/didChange — dual-drive)
    ///                                       └── RemoteEngine proxy ← McpTools.* (the actual MCP tools)
    ///
    /// Isolation: both scopes are redirected to a disposable temp tree — USERPROFILE is repointed so GLOBAL-scope
    /// recall (which recall_experience reads AND writes retrieve-deltas to) never touches the real ~/.semanticus,
    /// and the two seeds live in ONE non-ephemeral workspace dir so they share the project knowledge store.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static async Task<int> Main()
        {
            // --- Isolate every on-disk store the loop touches into a disposable temp tree ----------------------
            // The dir prefix must NOT be "semanticus-*" — that is the ephemeral-live-snapshot marker
            // (ExperienceStore.IsEphemeralAnchor), which would push the project store to the workspace fallback.
            var tempRoot = Path.Combine(Path.GetTempPath(), "learnsmoke-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            var wsDir = Path.Combine(tempRoot, "ws");
            var homeDir = Path.Combine(tempRoot, "home");
            Directory.CreateDirectory(wsDir);
            Directory.CreateDirectory(homeDir);
            // Repoint the global knowledge scope (%USERPROFILE%/.semanticus/knowledge) at the temp home so a cold
            // store is genuinely cold and no retrieve-delta lands in the real user store. Capture the original so
            // the finally can put it back — leaking a temp USERPROFILE would poison later work in the same process.
            var origHome = Environment.GetEnvironmentVariable("USERPROFILE");
            Environment.SetEnvironmentVariable("USERPROFILE", homeDir);

            // Two seeds in the SAME dir: identical structure → identical FingerprintKey; same dir → shared
            // project knowledge store. (Genuinely shape-varied same-key FAMILIES are LearnBench's job, §5.1.)
            var srcBim = FindTestBim();
            var seedA = Path.Combine(wsDir, "seedA.bim");
            var seedB = Path.Combine(wsDir, "seedB.bim");
            File.Copy(srcBim, seedA);
            File.Copy(srcBim, seedB);

            var pipeName = "semanticus-learnsmoke-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            const string uiChallenge = "learn-smoke-ui-challenge-0123456789abcdef";
            var sessions = new SessionManager();
            var owner = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro(), wsDir);
            using var server = new RpcServer(sessions, owner, pipeName, uiChallenge);
            using var cts = new CancellationTokenSource();
            var serverTask = server.RunAsync(cts.Token);

            UiClient ui = null;
            RemoteEngine claude = null;
            try
            {
                Console.WriteLine("== Learning-loop wiring smoke (L0→L2 over the MCP door) ==");

                var openA = await owner.OpenAsync(seedA);
                Check("owner opened seed A", openA.Tables > 0);

                ui = await UiClient.ConnectAsync(pipeName, uiChallenge); // the "VS Code UI" door
                claude = await RemoteEngine.ConnectAsync(pipeName);    // the IEngine the --mcp host injects

                // 1) Fingerprint present + shaped
                var fpA = await McpTools.GetModelFingerprint(claude);
                Check("get_model_fingerprint returns a stable key + shape counts",
                    fpA != null && !string.IsNullOrEmpty(fpA.FingerprintKey) && fpA.Tables > 0);
                var keyA = fpA?.FingerprintKey;

                // 2) Cold recall → the empty-state contract (no fabricated candidates)
                var cold = await McpTools.RecallExperience(claude, "optimize a measure");
                Check("cold store yields zero candidates with the honest empty-state note",
                    cold != null && cold.Candidates.Length == 0 && !string.IsNullOrEmpty(cold.Note));

                // 3) A gate FAILS — start optimize-dax, then submit step-1 with no answers. Its two REQUIRED inputs
                //    (target, originalDax) are missing, so the input gate REJECTS the submit (throws with
                //    instructive text) — the gate actually bit; not a silent pass. Register the UI's
                //    workflow/didChange waiter BEFORE start so the dual-drive broadcast is observed live.
                var wfWait = ui.Notify.WaitNextWorkflowAsync();
                var run = await McpTools.StartWorkflow(claude, "optimize-dax");
                Check("start_workflow('optimize-dax') opens a run on step-1",
                    run != null && run.Status == "active" && run.CurrentStep != null && run.CurrentStep.StepId == "step-1");
                var stepId = run.CurrentStep?.StepId ?? run.Steps.First().StepId;

                // 6-early) Cross-door visibility: the UI door saw the run start over workflow/didChange (dual-drive).
                var wfNote = await wfWait.WaitAsync(TimeSpan.FromSeconds(5));
                Check("the run broadcasts workflow/didChange to the UI door (dual-drive, golden rule 2)",
                    wfNote != null && wfNote.RunId == run.RunId);

                var gateBit = false; string gateMsg = null;
                try { await McpTools.SubmitWorkflowStep(claude, run.RunId, stepId, "{}"); }
                catch (Exception ex) { gateBit = true; gateMsg = ex.Message; }
                Check("the hard input gate blocked the empty submit with instructive evidence",
                    gateBit && gateMsg != null && gateMsg.Contains("unanswered") && gateMsg.Contains("target"));

                // 4) Post-mortem → insight (fingerprint-scoped), approve if the write-gate held it 'pending'
                var ins = await McpTools.AddInsight(claude,
                    "optimize-dax step-1 failed: submitted without the required pre-rewrite baseline (originalDax + target). Capture get_dax + benchmark_dax_coldwarm warm median BEFORE rewriting.",
                    new[] { "optimize-dax", "benchmark_delta", "missing-baseline" },
                    "post-mortem", "project", fingerprintScoped: true);
                if (!string.Equals(ins.Status, "approved", StringComparison.Ordinal))
                    ins = await McpTools.ApproveInsight(claude, ins.Id);
                Check("post-mortem insight lands approved with a provenance envelope",
                    ins.Status == "approved" && ins.Provenance != null && !string.IsNullOrEmpty(ins.Provenance.When));
                Check("insight is pinned to the current model's fingerprint",
                    string.Equals(ins.Fingerprint, keyA, StringComparison.Ordinal));

                // 5) Reopen a SAME-SHAPE model (different file, same FingerprintKey) → recall surfaces the
                //    post-mortem. THIS is the L0→L2 loop closing.
                var openB = await owner.OpenAsync(seedB);
                Check("owner opened seed B (same-shape sibling)", openB.Tables > 0);
                var fpB = await McpTools.GetModelFingerprint(claude);
                Check("seed B hashes to the SAME FingerprintKey as seed A (same shape → same key)",
                    fpB != null && string.Equals(fpB.FingerprintKey, keyA, StringComparison.Ordinal));

                var warm = await McpTools.RecallExperience(claude, "optimize a slow measure");
                var hit = warm.Candidates.FirstOrDefault(c => c.Insight != null && c.Insight.Id == ins.Id);
                Check("recall returns the insight on the reopened same-shape model", hit != null);
                Check("matched by fingerprint AND the overlapping key ('optimize' ↔ optimize-dax)",
                    hit != null && hit.FingerprintMatch && hit.MatchedKeys.Contains("optimize-dax"));
                Check("the recalled candidate is the post-mortem we captured",
                    hit != null && hit.Insight.Kind == "post-mortem");

                // 6) Counters advanced — recall bumped the retrievals counter (a delta append)
                var listed = (await McpTools.ListInsights(claude, "project", "approved"))
                    .Insights.FirstOrDefault(i => i.Id == ins.Id);
                Check("retrievals counter incremented after recall (ExpeL bookkeeping)",
                    listed != null && listed.Retrievals >= 1);
            }
            catch (Exception ex) { _failures++; Console.WriteLine("[X] threw: " + ex); }
            finally
            {
                claude?.Dispose();
                ui?.Dispose();
                cts.Cancel();
                try { await serverTask; } catch { }
                owner.Dispose();
                sessions.Dispose();
                Environment.SetEnvironmentVariable("USERPROFILE", origHome);   // restore BEFORE deleting the temp home
                try { Directory.Delete(tempRoot, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "==== LEARN SMOKE: PASS ====" : $"==== LEARN SMOKE: {_failures} CHECK(S) FAILED ====");
            return _failures == 0 ? 0 : 1;
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok) _failures++;
        }

        private static string FindTestBim()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                foreach (var root in new[] { Path.Combine("external", "TabularEditor"), "TabularEditor" })
                {
                    var candidate = Path.Combine(dir.FullName, root, "TOMWrapperTest", "TestData", "AdventureWorks.bim");
                    if (File.Exists(candidate)) return candidate;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("Could not locate AdventureWorks.bim from " + AppContext.BaseDirectory);
        }

        // --- The UI door: a plain JSON-RPC client that only listens for workflow/didChange (dual-drive proof) ---
        private static async Task ConfirmRpcRoleAsync(Stream stream)
        {
            var expected = "SEMANTICUS-RPC/1 accepted";
            var bytes = new byte[256];
            var one = new byte[1];
            var count = 0;
            while (count < bytes.Length)
            {
                if (await stream.ReadAsync(one, 0, 1) == 0) throw new EndOfStreamException("RPC server closed during role handshake.");
                if (one[0] == (byte)'\n') break;
                bytes[count++] = one[0];
            }
            if (count == bytes.Length || System.Text.Encoding.UTF8.GetString(bytes, 0, count) != expected)
                throw new InvalidDataException("RPC server rejected the role handshake.");
        }

        private sealed class UiClient : IDisposable
        {
            private readonly NamedPipeClientStream _pipe;
            private readonly JsonRpc _rpc;
            public NotifyCollector Notify { get; }

            private UiClient(NamedPipeClientStream pipe, JsonRpc rpc, NotifyCollector notify) { _pipe = pipe; _rpc = rpc; Notify = notify; }

            public static async Task<UiClient> ConnectAsync(string pipeName, string challenge)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                var preamble = System.Text.Encoding.UTF8.GetBytes($"SEMANTICUS-RPC/1 human {challenge}\n");
                await pipe.WriteAsync(preamble, 0, preamble.Length);
                await pipe.FlushAsync();
                await ConfirmRpcRoleAsync(pipe);
                var notify = new NotifyCollector();
                var rpc = new JsonRpc(RpcServer.CreateHandler(pipe));
                rpc.AddLocalRpcTarget(notify);
                rpc.StartListening();
                return new UiClient(pipe, rpc, notify);
            }

            public void Dispose() { try { _rpc.Dispose(); } catch { } try { _pipe.Dispose(); } catch { } }
        }

        private sealed class NotifyCollector
        {
            private readonly object _wfGate = new object();
            private TaskCompletionSource<WorkflowRunView> _nextWf;

            [JsonRpcMethod("model/didChange")]
            public void OnDidChange(ChangeNotification n) { /* not asserted here — model deltas covered by McpSmoke */ }

            [JsonRpcMethod("workflow/didChange")]
            public void OnWorkflowDidChange(WorkflowRunView v)
            {
                lock (_wfGate) { var t = _nextWf; _nextWf = null; t?.TrySetResult(v); }
            }

            public Task<WorkflowRunView> WaitNextWorkflowAsync()
            {
                lock (_wfGate)
                {
                    _nextWf = new TaskCompletionSource<WorkflowRunView>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _nextWf.Task;
                }
            }
        }
    }
}

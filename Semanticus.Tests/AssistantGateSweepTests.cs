using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The assistant's gate, Q5 items 2, 4 and the red proof of 2 (lane m11/uat-undo). Under Locked, with no grant, every method an
    /// assistant could write with is called through the gate with arguments that mean nothing: the inner engine hears of none of them,
    /// and the model (as TOM, as TMDL, both undo stacks, revision), its folder and the engine's home are byte-equal after. And every
    /// read, on a rich model, leaves the same things as it found them, except the cache files this test names.
    /// </summary>
    [Collection("restore-root")]
    public sealed class AssistantGateSweepTests : IDisposable
    {
        private readonly string _root;
        private readonly string _home;
        private readonly string _safeRoot;
        private readonly string _oldHome;

        public AssistantGateSweepTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "agw-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            Directory.CreateDirectory(_root);
            _home = Path.Combine(_root, "home");
            Directory.CreateDirectory(_home);
            _oldHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _home);
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _oldHome);
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private async Task<(LocalEngine engine, SessionManager sessions, string path)> OpenAsync(string tag, bool unsaved = true, bool heldContent = true)
        {
            var path = AssistantGateKit.WriteModel(_root, tag);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(path);
            if (unsaved) await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's unsaved words", "human");
            // The person's work that lives in the session's memory: a spec in the Spec tab and a plan line they approved. Replacing or clearing either is
            // held under Locked only when something is there to lose (join 6, Sol item 7), so the sweep needs something there.
            if (heldContent)
            {
                await engine.SetSpecAsync("{\"name\":\"S\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}", "human");
                var line = (await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", "reviewed words", null, null, null, "human")).Items.Single().Id;
                await engine.SetPlanItemAsync(line, null, true, "human");
            }
            return (engine, sessions, path);
        }

        /// <summary>Passes every call straight to the inner engine and records its name: the gate taken away, for the red proof.</summary>
        public class Transparent : DispatchProxy
        {
            private IEngine _inner;
            private List<string> _seen;
            public static IEngine Over(IEngine inner, List<string> seen)
            {
                var proxy = Create<IEngine, Transparent>();
                var t = (Transparent)(object)proxy;
                t._inner = inner; t._seen = seen;
                return proxy;
            }
            protected override object Invoke(MethodInfo target, object[] args)
            {
                lock (_seen) _seen.Add(target.Name);
                try { return target.Invoke(_inner, args); }
                catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            }
        }

        private sealed class Outcome
        {
            public List<string> Forwarded { get; } = new();
            public List<string> Succeeded { get; } = new();
            public List<string> Refused { get; } = new();
            public string[] ChangedFiles { get; set; } = Array.Empty<string>();
            public string[] ChangedHome { get; set; } = Array.Empty<string>();
            public bool ModelChanged { get; set; }
            public int Called { get; set; }
        }

        /// <summary>The methods an assistant could write with: every row the gate asks about, refuses or ratchets.</summary>
        private static MethodInfo[] WriteMethods() =>
            GateRegistry.All.Where(r => r.Kind is GateKind.Hold or GateKind.Ratchet or GateKind.Person)
                .Select(r => typeof(IEngine).GetMethods().First(m => m.Name == r.Method)).OrderBy(m => m.Name, StringComparer.Ordinal).ToArray();

        private async Task<Outcome> SweepAsync(IEngine target, LocalEngine engine, SessionManager sessions, string modelPath, Func<List<string>> forwarded)
        {
            var dir = Path.GetDirectoryName(modelPath)!;
            var scratch = Path.Combine(dir, "scratch.out");
            var traceBefore = await AssistantGateKit.ModelTraceAsync(engine, sessions);
            var filesBefore = AssistantGateKit.Files(dir);
            var homeBefore = AssistantGateKit.Files(_home);
            var outcome = new Outcome();
            foreach (var m in WriteMethods())
            {
                var args = AssistantGateKit.NonsenseArgs(m, scratch);
                outcome.Called++;
                try
                {
                    var call = AssistantGateKit.InvokeAsync(target, m, args);
                    var done = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(30)));
                    if (done != call) { outcome.Succeeded.Add(m.Name + " (still running after 30 seconds)"); continue; }
                    await call;
                    outcome.Succeeded.Add(m.Name);
                }
                catch (Exception) { outcome.Refused.Add(m.Name); }
            }
            outcome.Forwarded.AddRange(forwarded().Distinct(StringComparer.Ordinal));
            outcome.ModelChanged = traceBefore != await AssistantGateKit.ModelTraceAsync(engine, sessions);
            outcome.ChangedFiles = AssistantGateKit.Differences(filesBefore, AssistantGateKit.Files(dir));
            outcome.ChangedHome = AssistantGateKit.Differences(homeBefore, AssistantGateKit.Files(_home));
            return outcome;
        }

        // ---- Q5 item 2: inertness ---------------------------------------------------------------------------------------

        [Fact]
        public async Task Under_Locked_with_no_grant_no_write_method_reaches_the_inner_engine_and_nothing_is_touched()
        {
            var (engine, sessions, path) = await OpenAsync("inert");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var seen = new List<string>();
                var gate = AssistantEngine.For(engine, n => { lock (seen) seen.Add(n); });
                var outcome = await SweepAsync(gate, engine, sessions, path, () => { lock (seen) return seen.ToList(); });
                Assert.True(outcome.Called > 150, "the sweep called only " + outcome.Called + " methods");
                Assert.True(outcome.Forwarded.Count == 0, "the inner engine heard of: " + string.Join(", ", outcome.Forwarded));
                Assert.True(outcome.Succeeded.Count == 0, "these calls were not refused: " + string.Join(", ", outcome.Succeeded));
                Assert.False(outcome.ModelChanged, "the model, its undo stacks or its revision changed");
                Assert.True(outcome.ChangedFiles.Length == 0, "the model's folder changed: " + string.Join(", ", outcome.ChangedFiles));
                Assert.True(outcome.ChangedHome.Length == 0, "the engine's home changed: " + string.Join(", ", outcome.ChangedHome));
                // and the questions were really put: refusing is not the same as saying nothing
                Assert.True((await engine.ListPendingApprovalsAsync()).Length > 50, "few questions were put to the person");
            }
        }

        /// <summary>Item 7, the red proof of item 2: with the gate taken away, the same sweep reaches the inner engine with every call and
        /// leaves traces. So the sweep above is a real check, not one that cannot fail.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Red_proof_without_the_gate_the_same_sweep_names_every_write()
        {
            var (engine, sessions, path) = await OpenAsync("inert-red");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var seen = new List<string>();
                var open = Transparent.Over(engine, seen);
                var outcome = await SweepAsync(open, engine, sessions, path, () => { lock (seen) return seen.ToList(); });
                Assert.Equal(WriteMethods().Length, outcome.Forwarded.Count);
                Assert.True(outcome.Succeeded.Count > 0, "with no gate nothing succeeded, so the sweep cannot tell the gate is there");
                Assert.True(outcome.ChangedFiles.Length > 0 || outcome.ChangedHome.Length > 0 || outcome.ModelChanged,
                    "with no gate nothing changed on disk or in the model, so the sweep cannot tell the gate is there");
            }
        }

        // ---- Q5 item 4: read purity -----------------------------------------------------------------------------------------

        /// <summary>Cache files a read is allowed to make or refresh, named so a new one is a decision: the Plan keeps what it has
        /// worked out beside the model, and the engine keeps its own log of tool calls in its home.</summary>
        private static readonly string[] CacheFiles =
        {
            Path.Combine(".semanticus", "plan-memory.json"),
            // The question history beside the model: run_dax keeps the assistant's own run as a row through RecordQueryRunAsync (hero 25 H1).
            // It is history, not the model, and the gate stamps the row as the assistant's.
            Path.Combine(".semanticus", "data-questions.json"),
        };

        private static bool IsNamedCache(string relative) =>
            CacheFiles.Contains(relative, StringComparer.Ordinal) || relative.StartsWith("experience" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        private static object ReadArg(MethodInfo m, ParameterInfo p, string scratch)
        {
            switch (p.Name)
            {
                case "objRef": case "ref": return "measure:Sales/Total";
                case "measureRef": return "measure:Sales/Total";
                case "tableRef": case "table": return p.ParameterType == typeof(string) && p.Name == "table" ? "Sales" : "table:Sales";
                case "columnRef": return "column:Sales/Amount";
                case "partitionRef": return "partition:Sales/P1";
                case "parentRef": return null;
                case "expression": case "expr": case "measureExpr": return "SUM(Sales[Amount])";
                case "query": return "EVALUATE ROW(\"x\", 1)";
                case "name": return "Sales";
                case "culture": return "en-US";
                case "format": return "tmdl";
                case "text": return "total";
                case "max": case "topN": case "limit": case "maxResults": case "last": return 5;
            }
            return null;   // set by the caller when it has no better
        }

        private object[] ReadArgs(MethodInfo m, string scratch)
        {
            var ps = m.GetParameters();
            var args = AssistantGateKit.NonsenseArgs(m, scratch);
            for (var i = 0; i < ps.Length; i++)
            {
                var v = ReadArg(m, ps[i], scratch);
                if (v != null && ps[i].ParameterType.IsInstanceOfType(v)) args[i] = v;
                else if (ps[i].ParameterType == typeof(bool)) args[i] = false;   // a read's flags stay off: no commit, no persist, no apply
                else if (ps[i].ParameterType == typeof(string[]) && (ps[i].Name?.EndsWith("Refs", StringComparison.Ordinal) ?? false)) args[i] = new[] { "measure:Sales/Total" };
            }
            return args;
        }

        /// <summary>Calls each row through the gate with the arguments given and says what changed: the model, its undo stacks, its revision,
        /// the folder, the engine's home. A call that is stopped at a question it should not reach is named too.</summary>
        private async Task<(int Ran, List<string> Impure, List<string> Unnamed, List<string> ReachedAsk)> PurityAsync(
            LocalEngine engine, SessionManager sessions, string dir, IEngine gate, IEnumerable<(GateEntry Row, MethodInfo Method, object[] Args)> calls,
            bool noHeldContent = false)
        {
            var traceBefore = await AssistantGateKit.ModelTraceAsync(engine, sessions);
            var impure = new List<string>();
            var unnamed = new List<string>();
            var reachedAsk = new List<string>();
            var ran = 0;
            foreach (var (r, m, args) in calls)
            {
                // A pass branch of a replace or clear row is "nothing is held here". An earlier call in this sweep may have made a spec or a plan (autogenerate,
                // propose), which would make the next one a different question, so the person's side empties them first (through the person's own door).
                if (noHeldContent) { await engine.ClearSpecAsync("human"); await engine.ClearPlanAsync("human"); }
                var files0 = AssistantGateKit.Files(dir);
                var home0 = AssistantGateKit.Files(_home);
                try
                {
                    var call = AssistantGateKit.InvokeAsync(gate, m, args);
                    if (await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(20))) == call)
                    {
                        try { await call; }
                        catch (Exception ex) when (ex.Message.Contains("approvals queue", StringComparison.Ordinal)) { reachedAsk.Add(r.Method); }
                        catch { /* a read given nothing real may fail: it must not write */ }
                    }
                    ran++;
                }
                catch (Exception ex) when (ex.Message.Contains("approvals queue", StringComparison.Ordinal)) { reachedAsk.Add(r.Method); ran++; }
                catch { ran++; }
                var files1 = AssistantGateKit.Files(dir);
                var home1 = AssistantGateKit.Files(_home);
                var fileDiff = AssistantGateKit.Differences(files0, files1).Concat(AssistantGateKit.Differences(home0, home1).Select(h => "(home) " + h)).ToArray();
                foreach (var f in fileDiff)
                    if (!IsNamedCache(f.Replace("(home) ", ""))) unnamed.Add(r.Method + " changed " + f);
                if (traceBefore != await AssistantGateKit.ModelTraceAsync(engine, sessions))
                {
                    impure.Add(r.Method + " changed the model, its undo stacks or its revision");
                    traceBefore = await AssistantGateKit.ModelTraceAsync(engine, sessions);
                }
            }
            return (ran, impure, unnamed, reachedAsk);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_read_leaves_the_model_its_undo_stacks_and_the_disk_as_it_found_them_except_named_caches()
        {
            var (engine, sessions, path) = await OpenAsync("purity", unsaved: true);
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var dir = Path.GetDirectoryName(path)!;
                var scratch = Path.Combine(dir, "scratch.out");
                var gate = AssistantEngine.For(engine);
                // Calls that reach another system, or open a connection, are left out: a read test must not need a network.
                static bool ReachesOut(GateEntry r) => r.Why != null && (r.Why.StartsWith("A read of another system", StringComparison.Ordinal)
                    || r.Why.StartsWith("Opens or closes a connection", StringComparison.Ordinal));
                // DryRunOpAsync is in this sweep now (it was left out when the rehearsal ran on the inner engine): its own sweeps are below.
                var rows = GateRegistry.All.Where(r => r.Kind == GateKind.Pass && r.Choose == null && !ReachesOut(r)).OrderBy(r => r.Method, StringComparer.Ordinal).ToList();
                Assert.True(rows.Count > 100, "only " + rows.Count + " reads");
                Assert.Contains(rows, r => r.Method == "DryRunOpAsync");
                Assert.Contains(rows, r => r.Method == "ReplayCheckWorkflowAsync");
                var calls = rows.Select(r => { var m = typeof(IEngine).GetMethods().First(x => x.Name == r.Method); return (r, m, ReadArgs(m, scratch)); }).ToList();
                var (ran, impure, unnamed, reachedAsk) = await PurityAsync(engine, sessions, dir, gate, calls);
                Assert.True(ran > 100);
                // a row the gate calls a Pass that still ends at a sink which asks was put in the wrong kind
                Assert.True(reachedAsk.Count == 0, "these reads reached a write that asks: " + string.Join(", ", reachedAsk));
                Assert.True(impure.Count == 0, string.Join("\n", impure));
                Assert.True(unnamed.Count == 0, "a read wrote a file this test does not name as a cache:\n" + string.Join("\n", unnamed));
            }
        }

        /// <summary>The arguments that make a call take its Pass branch: every flag that makes it write is off. Overrides name the ones whose
        /// flag is a word, or is on when it makes the call a preview.</summary>
        private object[] PassBranchArgs(MethodInfo m, string scratch)
        {
            var args = ReadArgs(m, scratch);
            var ps = m.GetParameters();
            for (var i = 0; i < ps.Length; i++)
            {
                switch (m.Name + "." + ps[i].Name)
                {
                    case "ModelPlanActionAsync.action": args[i] = "cancel"; break;
                    case "UpgradeWorkflowAsync.dryRun": args[i] = true; break;
                    case "CompleteAssistantRequestAsync.text": args[i] = "a reply with no changes"; break;
                    case "SubmitWorkflowStepAsync.writesJson": args[i] = null; break;
                    case "GitBranchAsync.name": args[i] = null; break;
                }
            }
            return args;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_call_the_gate_decides_by_its_arguments_leaves_the_model_and_the_disk_as_found_on_its_pass_branch()
        {
            var (engine, sessions, path) = await OpenAsync("purity-dyn", unsaved: true, heldContent: false);   // nothing to lose, so the replace and clear rows take their pass branch
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var dir = Path.GetDirectoryName(path)!;
                var scratch = Path.Combine(dir, "scratch.out");
                var gate = AssistantEngine.For(engine);
                // LabelConnectionAsync is decided by its arguments but has no pass branch: it always holds, or refuses to change an existing label (ConnectionPublicDoorTests).
                // RunInterviewBatchAsync passes only for a round that already exists on disk, which this sweep has none of; CopilotTestTests proves both branches.
                var rows = GateRegistry.All.Where(r => r.Choose != null && r.Method != "LabelConnectionAsync" && r.Method != "RunInterviewBatchAsync").OrderBy(r => r.Method, StringComparer.Ordinal).ToList();
                Assert.True(rows.Count >= 15, "only " + rows.Count + " rows are decided by their arguments");
                var calls = new List<(GateEntry, MethodInfo, object[])>();
                var notPass = new List<string>();
                foreach (var r in rows)
                {
                    var m = typeof(IEngine).GetMethods().First(x => x.Name == r.Method);
                    var args = PassBranchArgs(m, scratch);
                    var resolved = await r.Choose(new GateCall { Engine = engine, Method = m, Parameters = m.GetParameters(), Args = args });
                    if (resolved.Kind != GateKind.Pass) { notPass.Add(r.Method + " resolved to " + resolved.Kind); continue; }
                    calls.Add((r, m, args));
                }
                Assert.True(notPass.Count == 0, "the arguments did not select the pass branch of: " + string.Join(", ", notPass));
                var (ran, impure, unnamed, reachedAsk) = await PurityAsync(engine, sessions, dir, gate, calls, noHeldContent: true);
                Assert.Equal(rows.Count, ran);
                Assert.True(reachedAsk.Count == 0, "these reached a write that asks: " + string.Join(", ", reachedAsk));
                Assert.True(impure.Count == 0, string.Join("\n", impure));
                Assert.True(unnamed.Count == 0, "a pass branch wrote a file this test does not name as a cache:\n" + string.Join("\n", unnamed));
            }
        }

        // ---- a rehearsal (dry_run) goes through the gate, so it cannot write anything the gate would not let through ----------------

        private static string ArgsJsonFor(MethodInfo tool, string scratch)
        {
            var bag = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var p in tool.GetParameters())
            {
                if (typeof(IEngine).IsAssignableFrom(p.ParameterType)) continue;
                var t = p.ParameterType;
                object v;
                if (t == typeof(string)) v = string.Equals(p.Name, "origin", StringComparison.Ordinal) ? "agent" : (p.Name.EndsWith("Path", StringComparison.OrdinalIgnoreCase) || p.Name == "path" || p.Name == "directory" || p.Name == "folder" || p.Name == "parentFolder") ? scratch : "nonsense";
                else if (t == typeof(string[])) v = new[] { "nonsense" };
                else if (t == typeof(bool)) v = true;
                else if (t == typeof(int)) v = 1;
                else if (t == typeof(long)) v = 1L;
                else if (t == typeof(double)) v = 1d;
                else continue;   // an object argument is left out: the tool says what it is missing, and that is a refusal, not a write
                bag[p.Name] = v;
            }
            return System.Text.Json.JsonSerializer.Serialize(bag);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_rehearsal_of_any_tool_forwards_only_what_a_rehearsal_may_run_and_leaves_the_disk_and_the_model_as_found()
        {
            var (engine, sessions, path) = await OpenAsync("rehearse", unsaved: true);
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var dir = Path.GetDirectoryName(path)!;
                var scratch = Path.Combine(dir, "scratch.out");
                var forwarded = new List<string>();
                var gate = AssistantEngine.For(engine, name => { lock (forwarded) forwarded.Add(name); });
                var traceBefore = await AssistantGateKit.ModelTraceAsync(engine, sessions);
                var files0 = AssistantGateKit.Files(dir);
                var home0 = AssistantGateKit.Files(_home);
                var refusedForPreview = 0;
                var tried = 0;
                // A tool that reaches another system (a workspace list, a sign-in) is left out, as the read sweep leaves it out: this test must
                // not need a network or a person's sign-in. Its row in the registry is what says so.
                var reachesOut = new HashSet<string>(GateRegistry.All
                    .Where(r => r.Kind == GateKind.Live || (r.Why != null && (r.Why.StartsWith("A read of another system", StringComparison.Ordinal)
                        || r.Why.StartsWith("Opens or closes a connection", StringComparison.Ordinal))))
                    .Select(r => r.Method), StringComparer.Ordinal);
                foreach (var tool in LocalEngine.OpSurface.Methods.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (tool.Key == "dry_run" || reachesOut.Contains(tool.Value.Name + "Async")) continue;
                    tried++;
                    var call = gate.DryRunOpAsync(tool.Key, ArgsJsonFor(tool.Value, scratch));
                    if (await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(30))) != call) continue;
                    try
                    {
                        var report = await call;
                        if ((report.Error ?? "").Contains("cannot be previewed", StringComparison.Ordinal)) refusedForPreview++;
                    }
                    catch (InvalidOperationException) { /* turned away early with a sentence that teaches */ }
                }
                Assert.True(tried > 250, "only " + tried + " tools were rehearsed");
                Assert.True(refusedForPreview > 20, "the gate refused only " + refusedForPreview + " rehearsals for writing outside the model");
                string[] mayNot;
                lock (forwarded)
                    mayNot = forwarded.Distinct(StringComparer.Ordinal).Where(n => n != "DryRunOpAsync" && GateRegistry.Find(n) is { } row && !row.CanPreview && row.Kind != GateKind.Pass).ToArray();
                Assert.True(mayNot.Length == 0, "a rehearsal let these through to the engine, though each writes outside the model: " + string.Join(", ", mayNot));
                Assert.Empty(AssistantGateKit.Differences(files0, AssistantGateKit.Files(dir)).Where(f => !IsNamedCache(f)));
                Assert.Empty(AssistantGateKit.Differences(home0, AssistantGateKit.Files(_home)).Where(f => !IsNamedCache(f)));
                Assert.Equal(traceBefore, await AssistantGateKit.ModelTraceAsync(engine, sessions));
            }
        }

        /// <summary>replay_check_workflow rehearses each step's ops through the same door: a workflow whose steps name writers outside the
        /// model, with answers to drive them, changes nothing under Locked.</summary>
        [Fact]
        public async Task A_replay_check_of_a_workflow_that_names_writers_outside_the_model_writes_nothing()
        {
            var ws = Path.Combine(_root, "replay-ws");
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            var ops = "[set_workflow_activation, set_decision_point_enabled, save_test, delete_test, add_insight, set_model_primer, save_layout, set_workflow_binding, create_measure]";
            var answers = "{\"workflow\":\"replay-flow\",\"set\":\"off\",\"name\":\"x\",\"enabled\":\"true\",\"id\":\"x\",\"text\":\"note\",\"markdown\":\"# primer\",\"op\":\"create_measure\",\"mode\":\"hard\",\"tableRef\":\"table:Sales\",\"expression\":\"1\"}";
            var file = Path.Combine(ws, ".semanticus", "workflows", "replay-flow.md");
            File.WriteAllText(file, "---\nname: replay-flow\ntitle: Replay flow\nversion: 1\nexemplar_run: wfr-9\nexemplar_answers: " + answers + "\n---\n## Step 1: Do it\nDo it.\n```yaml gate\nops: " + ops + "\n```\n");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro, ws);
            await engine.CreateModelAsync("ReplayGate", 1701);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
            var before = AssistantGateKit.Files(ws);
            var home0 = AssistantGateKit.Files(_home);
            var trace = await AssistantGateKit.ModelTraceAsync(engine, sessions);
            var forwarded = new List<string>();
            var gate = AssistantEngine.For(engine, n => { lock (forwarded) forwarded.Add(n); });
            var report = await gate.ReplayCheckWorkflowAsync("replay-flow");
            Assert.NotNull(report);
            Assert.Empty(AssistantGateKit.Differences(before, AssistantGateKit.Files(ws)));
            Assert.Empty(AssistantGateKit.Differences(home0, AssistantGateKit.Files(_home)).Where(f => !IsNamedCache(f)));
            Assert.Equal(trace, await AssistantGateKit.ModelTraceAsync(engine, sessions));
            lock (forwarded)
                Assert.DoesNotContain(forwarded, n => GateRegistry.Find(n) is { } row && !row.CanPreview && row.Kind != GateKind.Pass && n != "ReplayCheckWorkflowAsync");
            Assert.True(report.Rows.Any(r => r.Outcome == "rehearsed"), "no op was rehearsed, so the check did not reach the gate");
        }
    }
}

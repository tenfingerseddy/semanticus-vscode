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
    /// The assistant's gate, Q5 item 3 and the red proof of it (lane m11/uat-undo), and Q4, the ratchet. The gate is the only engine an
    /// assistant holds, but every place a write lands (a SINK) also refuses an assistant's write that no ticket covers, so a road
    /// around the gate fails closed. One case per sink calls the LocalEngine directly with origin "agent" and is refused. Then each
    /// sink is switched off in turn and its case fails: the cases are real checks. Q4: the assistant never gives the person's consent,
    /// never loosens a mandate the person put on it, and a CI scaffold it writes is a file write.
    /// </summary>
    [Collection("restore-root")]
    public sealed class AssistantGateSinkTests : GateFixtureBase
    {
        public AssistantGateSinkTests(RestoreRootFixture fixture) : base(fixture) { }

        public static IEnumerable<object[]> Sinks() => new[]
        {
            new object[] { "commit" }, new object[] { "model-files" }, new object[] { "path" }, new object[] { "discard" },
            new object[] { "workflow-binding" }, new object[] { "cicd" },
        };

        /// <summary>One case per sink: the write an assistant would make through it, made directly on the engine as origin "agent", and
        /// whether it landed.</summary>
        private async Task<(bool Refused, bool Landed)> CaseAsync(string sink)
        {
            if (sink == "workflow-binding") return await BindingCaseAsync();
            var (engine, sessions, path) = await OpenAsync("sink-" + sink);
            using (engine)
            {
                var dir = Path.GetDirectoryName(path)!;
                await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's unsaved words", "human");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                Exception refused = null;
                bool landed;
                switch (sink)
                {
                    case "commit":
                        try { await engine.SetDescriptionAsync("measure:Sales/Total", "the assistant's words", "agent"); } catch (Exception ex) { refused = ex; }
                        landed = await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description) == "the assistant's words";
                        break;
                    case "model-files":
                    {
                        var before = File.ReadAllBytes(path);
                        try { await engine.SaveAsync(null, "BIM", true, "agent"); } catch (Exception ex) { refused = ex; }
                        landed = !before.SequenceEqual(File.ReadAllBytes(path));
                        break;
                    }
                    case "path":
                    {
                        var target = Path.Combine(dir, "trip.vpax");
                        try { await engine.ExportVpaxAsync(target, "agent"); } catch (Exception ex) { refused = ex; }
                        landed = File.Exists(target);
                        break;
                    }
                    case "discard":
                    {
                        // by the assistant's tool, directly on the engine
                        try { await McpTools.OpenModel(engine, path, discardUnsaved: true); } catch (Exception ex) { refused = ex; }
                        landed = string.IsNullOrEmpty(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Margin"].Description));
                        break;
                    }
                    case "cicd":
                    {
                        try { await engine.CicdGenerateAsync("github", null, "PROD", true, "agent"); } catch (Exception ex) { refused = ex; }
                        landed = File.Exists(Path.Combine(dir, "parameter.yml"));
                        break;
                    }
                    default: throw new InvalidOperationException(sink);
                }
                return (refused != null, landed);
            }
        }

        /// <summary>The binding sink needs a workspace with a workflow and a mandate the person set; it is judged under the standard
        /// preset, because a loosening is the person's in EVERY preset, not only under Locked.</summary>
        private async Task<(bool Refused, bool Landed)> BindingCaseAsync()
        {
            var ws = Path.Combine(Root, "wfbind");
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "bind-vehicle.md"),
                "---\nname: bind-vehicle\ntitle: Binding vehicle\nwhenToUse: \"Author a measure the reviewed way.\"\n---\n## Step 1: Author the measure\nCreate the measure at this step.\n```yaml gate\nops: [create_measure]\n```\n## Step 2: Review\nReview what you made.\n");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro, ws);
            await engine.CreateModelAsync("BindTest", 1701);
            var t = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            await engine.SetWorkflowBindingAsync("create_measure", new[] { "bind-vehicle" }, "hard", "human");
            AgentPolicyStore.SetPreset("standard", "human");
            Exception refused = null;
            try { await engine.SetWorkflowBindingAsync("create_measure", Array.Empty<string>(), "off", "agent"); } catch (Exception ex) { refused = ex; }
            // landed: the mandate is gone, so the assistant can now create the bare measure
            var landed = true;
            try { await engine.CreateMeasureAsync("table:Facts", "Bare", "SUM(Facts[Amount])", "agent"); } catch (InvalidOperationException) { landed = false; }
            return (refused != null, landed);
        }

        [Theory]
        [MemberData(nameof(Sinks))]
        public async Task A_write_that_reaches_a_sink_without_the_gate_is_refused_and_does_not_land(string sink)
        {
            var (refused, landed) = await CaseAsync(sink);
            Assert.True(refused, sink + ": the write was not refused");
            Assert.False(landed, sink + ": the write landed anyway");
        }

        /// <summary>Item 7: switch one sink off and the case that guards it fails, so each case above depends on its sink.</summary>
        [Theory]
        [MemberData(nameof(Sinks))]
        public async Task Red_proof_with_the_sink_switched_off_its_case_fails(string sink)
        {
            GateSeams.DisabledForTest[sink] = true;
            try
            {
                var (refused, landed) = await CaseAsync(sink);
                Assert.True(landed || !refused, sink + ": the case still held with the sink switched off, so it does not test the sink");
            }
            finally { GateSeams.DisabledForTest.Clear(); }
        }

        [Fact]
        public async Task A_call_the_gate_passed_as_a_read_cannot_discard_unsaved_work_by_the_back_door()
        {
            // The open has no origin of its own, so a sink that sees an assistant's call behind it asks: a registry row that wrongly
            // called an open a Pass would still not throw the person's work away.
            var (engine, sessions, path) = await OpenAsync("ambient");
            using (engine)
            {
                await engine.SetDescriptionAsync("measure:Sales/Total", "a person's unsaved words", "human");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                using (GateContext.Enter("OpenAsync", null))
                    await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OpenAsync(path, true));
                Assert.Equal("a person's unsaved words", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description));
            }
        }

        // ---- Q4: the person's word in chat is enough for consent, in every preset (Kane, 10 Oct 2026, 1.4 chatyes) ----------------

        [Theory]
        [InlineData("open")]
        [InlineData("standard")]
        [InlineData("locked")]
        public async Task The_assistant_gives_consent_on_the_persons_word_in_chat_and_it_is_recorded_as_the_assistants(string preset)
        {
            var (engine, sessions, _) = await OpenAsync("consent-" + preset);
            using (engine)
            {
                AgentPolicyStore.SetPreset(preset, "human");
                var gate = Gate(engine);
                var fields = Semanticus.Decisions.DecisionRegistry.DeclaredFields.ToArray();
                var version = Semanticus.Decisions.DecisionRegistry.ConsentSchemaVersion;
                var done = await gate.SetConsentAsync(fields, version, "agent");
                Assert.False(done.Asked);
                Assert.True(done.Granted);
                Assert.Equal("agent", done.GrantedBy);
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                Assert.True((await engine.GetConsentAsync()).Current);
                await engine.WithdrawConsentAsync("human");
                Assert.False((await engine.GetConsentAsync()).Granted);
            }
        }

        [Fact]
        public async Task The_assistant_may_withdraw_consent_for_free_and_the_person_gives_it_without_being_asked()
        {
            var (engine, sessions, _) = await OpenAsync("consent-free");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var fields = Semanticus.Decisions.DecisionRegistry.DeclaredFields.ToArray();
                var version = Semanticus.Decisions.DecisionRegistry.ConsentSchemaVersion;
                var given = await engine.SetConsentAsync(fields, version, "human");
                Assert.True(given.Granted);
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                var gate = Gate(engine);
                var after = await gate.WithdrawConsentAsync("agent");
                Assert.False(after.Granted);
                Assert.Empty(await engine.ListPendingApprovalsAsync());
            }
        }

        // ---- Q4: the ratchet ----------------------------------------------------------------------------------------------------

        private async Task<(LocalEngine engine, SessionManager sessions, string ws)> WithMandateAsync(string mode)
        {
            var ws = Path.Combine(Root, "wf-" + mode);
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            foreach (var n in new[] { "bind-vehicle", "bind-other" })
                File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", n + ".md"),
                    "---\nname: " + n + "\ntitle: Binding vehicle\nwhenToUse: \"Author a measure the reviewed way.\"\n---\n## Step 1: Author the measure\nCreate the measure at this step.\n```yaml gate\nops: [create_measure]\n```\n## Step 2: Review\nReview what you made.\n");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, ws);
            await engine.CreateModelAsync("RatchetTest", 1701);
            await engine.SetWorkflowBindingAsync("create_measure", new[] { "bind-vehicle" }, mode, "human");
            return (engine, sessions, ws);
        }

        private static string BindingFileText(string ws) => File.ReadAllText(Path.Combine(ws, ".semanticus", "workflow-settings.json"));

        [Theory]
        [InlineData("standard")]
        [InlineData("locked")]
        public async Task The_assistant_cannot_loosen_a_mandate_the_person_set_in_any_preset(string preset)
        {
            var (engine, sessions, ws) = await WithMandateAsync("hard");
            using (engine)
            {
                AgentPolicyStore.SetPreset(preset, "human");
                var gate = Gate(engine);
                var before = BindingFileText(ws);
                // clear it, weaken it, offer another way to satisfy it
                foreach (var loosen in new Func<Task>[]
                {
                    () => gate.SetWorkflowBindingAsync("create_measure", Array.Empty<string>(), "off", "agent"),
                    () => gate.SetWorkflowBindingAsync("create_measure", new[] { "bind-vehicle" }, "warn", "agent"),
                    () => gate.SetWorkflowBindingAsync("create_measure", new[] { "bind-vehicle", "bind-other" }, "hard", "agent"),
                    () => gate.SetWorkflowBindingAsync("create_measure", new[] { "bind-other" }, "hard", "agent"),
                    () => gate.SetWorkflowEnforcementAsync("off", "agent"),
                    () => gate.SetWorkflowEnforcementAsync("warn", "agent"),
                    () => gate.SetWorkflowEnabledAsync("bind-vehicle", false, "agent"),
                    () => gate.SetWorkflowActivationAsync("bind-vehicle", "date.dayOfMonth >= 1", "off", "agent"),
                    () => gate.ActivateWorkflowProfileAsync("standard", "agent"),
                })
                {
                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(loosen);
                    Assert.Contains("person", ex.Message, StringComparison.OrdinalIgnoreCase);
                }
                Assert.Equal(before, BindingFileText(ws));
                Assert.Empty(await engine.ListPendingApprovalsAsync());   // not a question: a refusal
            }
        }

        [Fact]
        public async Task The_assistant_may_tighten_a_mandate_and_a_tightening_is_an_ordinary_write_asked_under_Locked()
        {
            var (engine, sessions, ws) = await WithMandateAsync("warn");
            using (engine)
            {
                var gate = Gate(engine);
                AgentPolicyStore.SetPreset("standard", "human");
                await gate.SetWorkflowBindingAsync("create_measure", new[] { "bind-vehicle" }, "hard", "agent");   // warn to hard: stricter, allowed
                Assert.Contains("hard", BindingFileText(ws));
                Assert.Empty(await engine.ListPendingApprovalsAsync());

                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await engine.SetWorkflowBindingAsync("create_measure", new[] { "bind-vehicle", "bind-other" }, "warn", "human");
                var refused = await RefusedAsync(() => gate.SetWorkflowBindingAsync("create_measure", new[] { "bind-vehicle" }, "hard", "agent"));
                Assert.Contains("approval", refused.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("warn", BindingFileText(ws));
                await ApproveAsync(engine);
                await gate.SetWorkflowBindingAsync("create_measure", new[] { "bind-vehicle" }, "hard", "agent");
                Assert.DoesNotContain("bind-other", BindingFileText(ws));
            }
        }

        [Fact]
        public async Task A_decision_point_may_be_switched_off_by_the_assistant_but_never_on_and_the_audit_switch_never_off()
        {
            var (engine, sessions, _) = await OpenAsync("ratchet-misc");
            using (engine)
            {
                AgentPolicyStore.SetPreset("standard", "human");
                var gate = Gate(engine);
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => gate.SetVerifiedModeAsync(false, "agent"));
                Assert.Contains("person", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.True((await gate.SetVerifiedModeAsync(true, "agent")).Enabled);
                var point = (await engine.GetDecisionPointsAsync()).First();
                ex = await Assert.ThrowsAsync<InvalidOperationException>(() => gate.SetDecisionPointEnabledAsync(point.Name, true, "agent", null));
                Assert.Contains("person", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ---- Q4: a CI scaffold is a file write ----------------------------------------------------------------------------

        [Fact]
        public async Task A_CI_scaffold_the_assistant_writes_asks_under_Locked_and_a_preview_never_does()
        {
            var (engine, sessions, path) = await OpenAsync("cicd");
            using (engine)
            {
                var dir = Path.GetDirectoryName(path)!;
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var preview = await gate.CicdGenerateAsync("github", null, "PROD", false, "agent");
                Assert.False(preview.Written);
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                await RefusedAsync(() => gate.CicdGenerateAsync("github", null, "PROD", true, "agent"));
                Assert.False(File.Exists(Path.Combine(dir, "parameter.yml")));
                var asked = await OnlyPendingAsync(engine);
                Assert.Contains("parameter.yml", asked.Summary);
                Assert.Contains("fabric-cicd.yml", asked.Summary);
                await ApproveAsync(engine);
                var written = await gate.CicdGenerateAsync("github", null, "PROD", true, "agent");
                Assert.True(written.Written, written.Error);
                Assert.True(File.Exists(Path.Combine(dir, ".github", "workflows", "fabric-cicd.yml")));
            }
        }

        // ---- words ----------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_script_is_asked_about_with_its_objects_and_text_block_by_block()
        {
            var (engine, sessions, _) = await OpenAsync("script");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var dax = "// @object measure:Sales/Total\nSUM(Sales[Amount]) * 2\n\n// @object measure:Sales/Margin\n[Total] - 1\n";
                await RefusedAsync(() => gate.ApplyDaxScriptAsync(dax, "agent"));
                var asked = await OnlyPendingAsync(engine);
                Assert.Contains("[Total]", asked.Summary);
                Assert.Contains("SUM(Sales[Amount]) * 2", asked.Summary);
                Assert.Contains("[Margin]", asked.Summary);
                Assert.Contains("[Total] - 1", asked.Summary);
                Assert.Contains("2 blocks", asked.Summary);
                await ClearQuestionsAsync(engine);
                var tmdl = "// @object measure:Sales/Total\nmeasure Total = SUM(Sales[Amount]) + 5\n";
                await RefusedAsync(() => gate.ApplyTmdlScriptAsync(tmdl, "agent"));
                asked = await OnlyPendingAsync(engine);
                Assert.Contains("[Total]", asked.Summary);
                Assert.Contains("SUM(Sales[Amount]) + 5", asked.Summary);
            }
        }

        [Fact]
        public async Task A_plain_undo_names_the_change_it_takes_back()
        {
            var (engine, sessions, _) = await OpenAsync("undo-words");
            using (engine)
            {
                await engine.SetDescriptionAsync("measure:Sales/Total", "words to take back", "human");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await RefusedAsync(() => Gate(engine).UndoAsync("agent"));
                var asked = await OnlyPendingAsync(engine);
                Assert.Contains("undo one change", asked.Summary);
                Assert.Contains("[Total]", asked.Summary);
                Assert.Contains("words to take back", asked.Summary);
            }
        }

        [Fact]
        public async Task Running_a_draft_changes_the_model_so_it_is_asked_about_under_Locked_before_anything_runs()
        {
            var (engine, sessions, _) = await OpenAsync("run-draft");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var seen = new List<string>();
                var gate = AssistantEngine.For(engine, n => seen.Add(n));
                var ex = await RefusedAsync(() => gate.RunDraftAsync("no-such-draft", "agent"));
                Assert.Contains("approval", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(seen);
                Assert.Contains("run the draft no-such-draft", (await OnlyPendingAsync(engine)).Summary);
            }
        }

        [Fact]
        public async Task The_controls_over_the_assistant_are_the_persons_through_the_gate_and_the_assistant_is_sent_to_them()
        {
            var (engine, sessions, _) = await OpenAsync("person-only");
            using (engine)
            {
                var seen = new List<string>();
                var gate = AssistantEngine.For(engine, n => seen.Add(n));
                foreach (var row in GateRegistry.All.Where(r => r.Kind == GateKind.Person))
                {
                    var m = typeof(IEngine).GetMethods().First(x => x.Name == row.Method);
                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AssistantGateKit.InvokeAsync(gate, m, AssistantGateKit.NonsenseArgs(m, "x")));
                    Assert.Contains("person", ex.Message, StringComparison.OrdinalIgnoreCase);
                }
                Assert.Empty(seen);
                Assert.Empty(await engine.ListPendingApprovalsAsync());
            }
        }

        [Fact]
        public async Task The_pipe_door_serves_an_assistant_connection_through_the_gate_and_a_person_connection_without_it()
        {
            var (engine, sessions, _) = await OpenAsync("pipe");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => new EngineRpcTarget(engine, "agent").setDescription("measure:Sales/Total", "words", "human"));
                var asked = await OnlyPendingAsync(engine);
                Assert.StartsWith("Your assistant wants to set the description of [Total] to: \"words\".", asked.Summary);   // the origin the call claims is not the one it gets
                Assert.Contains(" This is about the model \"", asked.Summary);
                Assert.True(string.IsNullOrEmpty(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description)));
                await new EngineRpcTarget(engine, "human").setDescription("measure:Sales/Total", "the person's words", "agent");   // and the person's claim of "agent" does not make theirs one
                Assert.Equal("the person's words", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description));
            }
        }

        [Fact]
        public async Task A_call_the_gate_passes_as_a_read_goes_through_under_Locked_with_nothing_asked()
        {
            var (engine, sessions, _) = await OpenAsync("reads");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                Assert.NotEmpty(await gate.ListTreeAsync(null));
                Assert.NotNull(await gate.GetObjectAsync("measure:Sales/Total"));
                Assert.Empty(await engine.ListPendingApprovalsAsync());
            }
        }

        // ---- Q5 item 3, outside the model: one chokepoint for every writer ------------------------------------------------------

        /// <summary>Every method whose row says it writes outside the model (a note, a workflow or its settings, a test, a connection
        /// setting, a working copy, a repository), the way the call is made when it resolves to a write.</summary>
        private async Task<List<(MethodInfo Method, object[] Args)>> OutsideWritersAsync(LocalEngine engine, string scratch)
        {
            var writers = new List<(MethodInfo, object[])>();
            foreach (var m in typeof(IEngine).GetMethods().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var row = GateRegistry.Find(m.Name);
                if (row == null || (row.Kind is not (GateKind.Hold or GateKind.Ratchet) && row.Choose == null)) continue;
                var args = AssistantGateKit.NonsenseArgs(m, scratch);
                var resolved = row.Choose == null ? row
                    : await row.Choose(new GateCall { Engine = engine, Method = m, Parameters = m.GetParameters(), Args = args });
                if (resolved.Kind is not (GateKind.Hold or GateKind.Ratchet) || resolved.CanPreview) continue;
                // a write that lands in the open model is the commit sink's (tested above), not this chokepoint's
                if (resolved.Capabilities.All(c => c == AgentCapability.EditLocal)) continue;
                writers.Add((m, args));
            }
            return writers;
        }

        /// <summary>Calls each writer straight on the engine as origin "agent" (or, for one with no origin, inside an assistant's call with
        /// no ticket), under Locked, and says which ones the engine itself stopped and asked about.</summary>
        private async Task<(List<string> Asked, List<string> Landed, string[] FileChanges, int Writers, List<string> Silent)> TripwireAsync(string tag)
        {
            var (engine, sessions, path) = await OpenAsync(tag);
            using (engine)
            {
                var dir = Path.GetDirectoryName(path)!;
                var scratch = Path.Combine(dir, "scratch.out");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var writers = await OutsideWritersAsync(engine, scratch);
                var files0 = AssistantGateKit.Files(dir);
                var home0 = AssistantGateKit.Files(Home);
                var asked = new List<string>();
                var landed = new List<string>();
                var silent = new List<string>();
                foreach (var (m, args) in writers)
                {
                    var hasOrigin = m.GetParameters().Any(p => p.Name == "origin");
                    Exception refused = null;
                    try
                    {
                        using (hasOrigin ? null : GateContext.Enter(m.Name, null))
                            await Task.WhenAny(AssistantGateKit.InvokeAsync(engine, m, args), Task.Delay(TimeSpan.FromSeconds(30))).Unwrap();
                    }
                    catch (Exception ex) { refused = ex; }
                    var pending = await engine.ListPendingApprovalsAsync();
                    if (pending.Length > 0) asked.Add(m.Name);
                    else if (refused == null) landed.Add(m.Name);
                    else silent.Add(m.Name + ": " + refused.Message.Split('\n')[0]);
                    await ClearQuestionsAsync(engine);
                }
                var changes = AssistantGateKit.Differences(files0, AssistantGateKit.Files(dir))
                    .Concat(AssistantGateKit.Differences(home0, AssistantGateKit.Files(Home)).Select(h => "(home) " + h)).ToArray();
                return (asked, landed, changes, writers.Count, silent);
            }
        }

        /// <summary>The engine methods that call the chokepoint, found in the source so a new writer that forgets to is a number that does not add up.</summary>
        private static string[] MethodsThatCallTheChokepoint()
        {
            var names = new List<string>();
            foreach (var file in Directory.GetFiles(Path.Combine(AssistantGateKit.RepoRoot(), "Semanticus.Engine"), "LocalEngine*.cs"))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains("GuardOutsideWrite(", StringComparison.Ordinal) || lines[i].Contains("internal void GuardOutsideWrite", StringComparison.Ordinal)) continue;
                    for (var j = i; j >= 0; j--)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(lines[j], @"^\s{8}public\s[^=(]*?\b(\w+Async)\s*\(");
                        if (m.Success) { names.Add(m.Groups[1].Value); break; }
                    }
                }
            }
            return names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        [Fact]
        public async Task Every_write_outside_the_model_is_stopped_and_asked_about_when_it_reaches_the_engine_without_the_gate()
        {
            var (asked, landedAll, changes, writers, silentRefusals) = await TripwireAsync("outside-on");
            var landed = landedAll;
            Assert.True(writers > 40, "only " + writers + " writers outside the model were found");
            // These three have a sink of their own that asks only once it has found the repository or the saved version they act on, so
            // with nothing to act on they answer without asking and without writing; the Git and history sink tests hold them.
            var findsItsTargetFirst = new[] { "GitCheckoutAsync", "GitPullAsync", "RestoreHistoryCheckpointAsync" };
            landed = landed.Where(l => !findsItsTargetFirst.Contains(l)).ToList();
            // A refusal that is not a question must be one this test knows the reason for: ApplyDiffAsync reads the file it is aimed at as a
            // model before its own path sink asks, and the nonsense path is not one.
            var refusedForAnotherReason = silentRefusals.Where(r => !r.StartsWith("ApplyDiffAsync:", StringComparison.Ordinal)).ToArray();
            Assert.True(refusedForAnotherReason.Length == 0, "these were refused, but not by asking, so no writer's sink was proved: " + string.Join("; ", refusedForAnotherReason));
            Assert.True(landed.Count == 0, "these writes outside the model went through with no approval and no question: " + string.Join(", ", landed));
            Assert.True(changes.Length == 0, "files changed although nothing was approved: " + string.Join(", ", changes));
            // every method that calls the chokepoint asked: the number that does so is read from the source
            var guarded = MethodsThatCallTheChokepoint();
            Assert.True(guarded.Length > 40, "only " + guarded.Length + " writers call the chokepoint");
            var silent = guarded.Where(g => !asked.Contains(g, StringComparer.Ordinal)).ToArray();
            Assert.True(silent.Length == 0, "these call the chokepoint but it did not ask: " + string.Join(", ", silent));
        }

        /// <summary>Item 7: with the chokepoint switched off, the writers that rely on it stop asking, so the case above depends on it.</summary>
        [Fact]
        public async Task Red_proof_with_the_chokepoint_switched_off_the_writers_that_rely_on_it_stop_asking()
        {
            var (askedOn, _, _, _, _) = await TripwireAsync("outside-ref");
            GateSeams.DisabledForTest["outside"] = true;
            try
            {
                var (askedOff, _, _, _, _) = await TripwireAsync("outside-off");
                var guarded = MethodsThatCallTheChokepoint();
                // SaveSqlSourceAsync has a second gate of its own (a host not saved yet asks in every preset, SqlHostAskTests), so it still asks here.
                var stillAsked = guarded.Where(g => g != nameof(LocalEngine.SaveSqlSourceAsync) && askedOff.Contains(g, StringComparer.Ordinal)).ToArray();
                Assert.True(stillAsked.Length == 0, "these asked with the chokepoint off, so the case does not test it: " + string.Join(", ", stillAsked));
                Assert.True(askedOn.Count - askedOff.Count >= guarded.Length - 5, "switching the chokepoint off changed too little: " + askedOn.Count + " asked on, " + askedOff.Count + " off");
            }
            finally { GateSeams.DisabledForTest.Clear(); }
        }

        [Fact]
        public async Task A_rehearsal_refuses_a_write_outside_the_model_whatever_the_policy_says()
        {
            var (engine, sessions, path) = await OpenAsync("outside-dry");
            using (engine)
            {
                AgentPolicyStore.SetPreset("open", "human");   // nothing is asked, and still a rehearsal may not write beside the model
                var dir = Path.GetDirectoryName(path)!;
                var before = AssistantGateKit.Files(dir);
                var collector = new DryRunCollector { FromTool = true };
                DryRunScope.Current = collector;
                try
                {
                    using (GateContext.Enter("SaveTestDefinitionAsync", null))
                    {
                        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.AddInsightAsync("a note", new[] { "k" }, null, null, false, "agent"));
                        Assert.Equal(GateRegistry.CannotPreviewSentence, ex.Message);
                    }
                }
                finally { DryRunScope.Current = null; }
                Assert.Empty(AssistantGateKit.Differences(before, AssistantGateKit.Files(dir)));
            }
        }

        // ---- F4: engine settings are held under Locked; below Locked the matrix is today's -----------------------------------

        [Fact]
        public async Task A_connection_setting_the_assistant_writes_asks_under_Locked_and_is_free_below_it()
        {
            var (engine, sessions, path) = await OpenAsync("settings");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var gate = Gate(engine);
                var home0 = AssistantGateKit.Files(Home);
                var refused = await RefusedAsync(() => gate.SaveSqlSourceAsync(null, "Warehouse", "srv", "db", "azcli", null, "agent"));
                Assert.Contains("approval", refused.Message, StringComparison.OrdinalIgnoreCase);
                var asked = await OnlyPendingAsync(engine);
                Assert.Contains("Warehouse", asked.Summary);
                Assert.Empty(AssistantGateKit.Differences(home0, AssistantGateKit.Files(Home)));
                await RefusedAsync(() => gate.ForgetConnectionAsync("nothing", "agent"));   // an unlabelled one asks too
                await ClearQuestionsAsync(engine);

                await RefusedAsync(() => gate.SaveSqlSourceAsync(null, "Warehouse", "srv", "db", "azcli", null, "agent"));
                await ApproveAsync(engine);
                Assert.Equal("Warehouse", (await gate.SaveSqlSourceAsync(null, "Warehouse", "srv", "db", "azcli", null, "agent")).Name);

                // below Locked the matrix is today's: nothing is asked
                AgentPolicyStore.SetPreset("standard", "human");
                await gate.SaveSqlSourceAsync(null, "Second", "srv", "db2", "azcli", null, "agent");   // a host already saved: a new host asks in every preset (SqlHostAskTests)
                Assert.False(await gate.ForgetConnectionAsync("nothing", "agent"));
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                // and the person is never asked
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await engine.SaveSqlSourceAsync(null, "Third", "srv3", "db3", "azcli", null, "human");
                Assert.Empty(await engine.ListPendingApprovalsAsync());
            }
        }
    }
}

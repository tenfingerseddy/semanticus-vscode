using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// §10.6 DYNAMIC WORKFLOW ACTIVATION (10-T2). Two layers pinned here:
    ///  (1) the shared <see cref="WorkflowPredicate"/> evaluator — grammar, operators, per-fact typing, glob,
    ///      minimal referenced-roots, and the "unknown fact ⇒ false, never throws" contract; and
    ///  (2) the engine resolver — the TOTAL precedence (binding force-active &gt; manual disable &gt; activation
    ///      rule &gt; default-on), plain-language reasons (never a predicate echo), zero-config invisibility,
    ///      fail-safe on junk, rule-off-is-startable-on-demand (D4), a live run surviving mid-run deactivation,
    ///      template instances being targetable by name AND tag, the policy lints, and the Pro-gated
    ///      set_workflow_activation round-trip + its library rebroadcast (D5).
    /// Isolation mirrors WorkflowBindingTests: a temp workspace, a gate-free vehicle, Fake entitlement.
    /// </summary>
    public sealed class WorkflowActivationTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // A gate-free vehicle (runs FREE) carrying tags — so activation is exercised in isolation from the
        // entitlement + strictness axes. A second, ops:[create_measure] vehicle drives the binding force-active cases.
        private const string ActVehicleMd = @"---
name: act-vehicle
title: Activation vehicle
tags: [client-acme, finance]
whenToUse: ""A do-nothing vehicle for activation tests.""
---
## Step 1: Do the thing
Just do it — this step gates nothing.
";
        private const string BindVehicleMd = @"---
name: bind-vehicle
title: Binding vehicle
whenToUse: ""Author a measure the reviewed way.""
---
## Step 1: Author the measure
Create the measure at this step.
```yaml gate
ops: [create_measure]
```
## Step 2: Review
Review what you made.
";

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wfact-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var wfDir = Path.Combine(ws, ".semanticus", "workflows");
            Directory.CreateDirectory(wfDir);
            File.WriteAllText(Path.Combine(wfDir, "act-vehicle.md"), ActVehicleMd);
            File.WriteAllText(Path.Combine(wfDir, "bind-vehicle.md"), BindVehicleMd);
            return ws;
        }

        private static string SettingsFile(string ws) => Path.Combine(ws, ".semanticus", "workflow-settings.json");
        private static void WriteSettings(string ws, string json) => File.WriteAllText(SettingsFile(ws), json);

        private static async Task OpenModelWithFactsAsync(LocalEngine engine)
        {
            await engine.CreateModelAsync("ActTest", 1701);
            var t = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
        }

        private static WorkflowInfo Find(WorkflowInfo[] list, string name) => list.Single(w => w.Name == name);

        // ==== (1) predicate evaluator — pure unit tests ==========================================

        [Fact]
        public void Predicate_parses_and_evaluates_all_operators()
        {
            var facts = new PredicateFacts
            {
                Model = new ModelFacts { TableCount = 10, MeasureCount = 3, HasRls = true, CompatLevel = 1601, ReadinessGrade = "C+", StorageMode = "import" },
                Date = new DateFacts { DayOfMonth = 28, MonthEndOffset = -2 },
                Git = new GitFacts { Branch = "main", Dirty = false },
            };

            // number ops
            Assert.True(Eval("model.tableCount == 10", facts));
            Assert.True(Eval("model.tableCount != 9", facts));
            Assert.True(Eval("model.tableCount >= 10", facts));
            Assert.True(Eval("model.tableCount > 9", facts));
            Assert.True(Eval("model.tableCount <= 10", facts));
            Assert.True(Eval("model.tableCount < 11", facts));
            Assert.False(Eval("model.tableCount > 10", facts));
            Assert.True(Eval("date.monthEndOffset >= -3", facts));   // -2 >= -3
            Assert.False(Eval("date.monthEndOffset >= -1", facts));

            // bool ops
            Assert.True(Eval("model.hasRls == true", facts));
            Assert.True(Eval("git.dirty == false", facts));
            Assert.False(Eval("model.hasRls != true", facts));

            // string ops
            Assert.True(Eval("git.branch == 'main'", facts));
            Assert.True(Eval("git.branch != 'develop'", facts));

            // grade LADDER: C+ ranks BELOW B, so "< 'B'" holds; "== 'C+'" holds; not above B
            Assert.True(Eval("model.readinessGrade < 'B'", facts));
            Assert.True(Eval("model.readinessGrade == 'C+'", facts));
            Assert.False(Eval("model.readinessGrade >= 'B'", facts));

            // && binds tighter than ||: (false && false) || true  ==> true
            Assert.True(Eval("model.tableCount > 99 && git.dirty == true || date.dayOfMonth == 28", facts));
            // both AND terms must hold
            Assert.True(Eval("model.tableCount == 10 && git.branch == 'main'", facts));
            Assert.False(Eval("model.tableCount == 10 && git.branch == 'develop'", facts));
        }

        [Fact]
        public void Predicate_glob_tilde_matches_workspace_pattern()
        {
            var prod = new PredicateFacts { Connection = new ConnectionFacts { Kind = "xmla", Workspace = "Contoso-Prod-01" } };
            var dev = new PredicateFacts { Connection = new ConnectionFacts { Kind = "xmla", Workspace = "Contoso-Dev" } };
            Assert.True(Eval("connection.workspace ~ '*prod*'", prod));    // case-insensitive glob
            Assert.False(Eval("connection.workspace ~ '*prod*'", dev));
            Assert.True(Eval("connection.workspace ~ 'Contoso-?rod-01'", prod));   // '?' = one char
        }

        [Fact]
        public void Predicate_unknown_fact_is_false_never_throws()
        {
            // No model root supplied ⇒ model.tableCount is unknown ⇒ the comparison is FALSE, no exception (E6).
            var empty = new PredicateFacts();
            var expr = WorkflowPredicate.Parse("model.tableCount > 0", out var err);
            Assert.Null(err);
            Assert.False(WorkflowPredicate.Evaluate(expr, empty));

            // A misspelled/unknown term parses (so it evaluates false + can be linted) but records a soft error.
            var bogus = WorkflowPredicate.Parse("model.bogus == 1", out var err2);
            Assert.NotNull(err2);
            Assert.False(WorkflowPredicate.Evaluate(bogus, empty));

            // A null when: is unconditional true.
            Assert.True(WorkflowPredicate.Evaluate(WorkflowPredicate.Parse(null, out _), empty));
        }

        [Fact]
        public void Predicate_referenced_roots_are_minimal()
        {
            var dateOnly = WorkflowPredicate.Parse("date.monthEndOffset >= -3 || date.dayOfMonth >= 28", out _);
            Assert.Equal(new[] { "date" }, WorkflowPredicate.ReferencedRoots(dateOnly).OrderBy(x => x).ToArray());

            var mixed = WorkflowPredicate.Parse("git.branch == 'main' && model.tableCount > 5", out _);
            Assert.Equal(new[] { "git", "model" }, WorkflowPredicate.ReferencedRoots(mixed).OrderBy(x => x).ToArray());

            Assert.Empty(WorkflowPredicate.ReferencedRoots(WorkflowPredicate.Parse(null, out _)));   // no rule ⇒ no roots ⇒ nothing gathered
        }

        private static bool Eval(string when, PredicateFacts f)
        {
            var expr = WorkflowPredicate.Parse(when, out var err);
            Assert.Null(err);   // these fixtures are all well-typed
            return WorkflowPredicate.Evaluate(expr, f);
        }

        // ==== (2) engine resolver ================================================================

        [Fact]
        public async Task Zero_config_all_workflows_active_no_facts_gathered()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    // No settings file at all ⇒ zero rules ⇒ every workflow Active with a null reason (E-zero).
                    var list = await engine.ListWorkflowsAsync();
                    Assert.All(list, w => Assert.True(w.Active));
                    Assert.All(list, w => Assert.Null(w.ActiveReason));
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Rule_set_off_deactivates_and_gives_plain_reason()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    WriteSettings(ws, @"{ ""activation"": [ { ""workflow"": ""act-vehicle"", ""when"": ""date.dayOfMonth >= 1"", ""set"": ""off"" } ] }");
                    var v = Find(await engine.ListWorkflowsAsync(), "act-vehicle");
                    Assert.False(v.Active);
                    Assert.False(string.IsNullOrWhiteSpace(v.ActiveReason));
                    Assert.DoesNotContain("dayOfMonth", v.ActiveReason);   // plain reason, NEVER a predicate echo
                    Assert.DoesNotContain(">=", v.ActiveReason);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Rule_reason_field_is_used_verbatim_when_supplied()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    WriteSettings(ws, @"{ ""activation"": [ { ""workflow"": ""act-vehicle"", ""when"": ""date.dayOfMonth >= 1"", ""set"": ""off"", ""reason"": ""only shown during the month-end window"" } ] }");
                    var v = Find(await engine.ListWorkflowsAsync(), "act-vehicle");
                    Assert.False(v.Active);
                    Assert.Equal("only shown during the month-end window", v.ActiveReason);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Manual_disable_beats_activation_on()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    // enabled:false AND a rule turning it on — manual disable wins (E2).
                    WriteSettings(ws, @"{ ""workflows"": { ""act-vehicle"": { ""enabled"": false } },
                                          ""activation"": [ { ""workflow"": ""act-vehicle"", ""when"": ""date.dayOfMonth >= 1"", ""set"": ""on"" } ] }");
                    var v = Find(await engine.ListWorkflowsAsync(), "act-vehicle");
                    Assert.False(v.Active);
                    Assert.Contains("turned off", v.ActiveReason);
                    Assert.False(v.Enabled);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Binding_force_activates_over_activation_off()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    // A hard binding requires bind-vehicle, but a rule sets it OFF — the requirement wins (E3).
                    WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""bind-vehicle""], ""mode"": ""hard"" } },
                                          ""activation"": [ { ""workflow"": ""bind-vehicle"", ""when"": ""date.dayOfMonth >= 1"", ""set"": ""off"" } ] }");
                    var v = Find(await engine.ListWorkflowsAsync(), "bind-vehicle");
                    Assert.True(v.Active);
                    Assert.Contains("required when", v.ActiveReason);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Binding_force_activates_over_manual_disable_and_is_startable()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    await OpenModelWithFactsAsync(engine);
                    // bind-vehicle is manually DISABLED yet a hard binding requires it — the deadlock (E1/D1).
                    WriteSettings(ws, @"{ ""workflows"": { ""bind-vehicle"": { ""enabled"": false } },
                                          ""bindings"": { ""create_measure"": { ""require"": [""bind-vehicle""], ""mode"": ""hard"" } } }");
                    // The requirement force-activates it (on the menu) AND makes it startable despite enabled:false.
                    var v = Find(await engine.ListWorkflowsAsync(), "bind-vehicle");
                    Assert.True(v.Active);
                    Assert.Contains("required when", v.ActiveReason);

                    var run = await engine.StartWorkflowAsync("bind-vehicle", "agent");   // must NOT deadlock
                    Assert.Equal("active", run.Status);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Strictness_off_does_not_change_activation()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    // The strictness kill-switch is orthogonal — it governs how gates bite, not the menu (E4).
                    WriteSettings(ws, @"{ ""strictness"": ""off"",
                                          ""activation"": [ { ""workflow"": ""act-vehicle"", ""when"": ""date.dayOfMonth >= 1"", ""set"": ""off"" } ] }");
                    Assert.False((await engine.GetWorkflowEnforcementAsync()).Enforced);
                    var v = Find(await engine.ListWorkflowsAsync(), "act-vehicle");
                    Assert.False(v.Active);   // still deactivated by the rule — strictness didn't touch it
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Rule_referencing_deleted_workflow_is_inert_and_linted()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    WriteSettings(ws, @"{ ""activation"": [ { ""workflow"": ""ghost-flow"", ""when"": ""date.dayOfMonth >= 1"", ""set"": ""off"" } ] }");
                    var list = await engine.ListWorkflowsAsync();   // no throw
                    Assert.All(list, w => Assert.True(w.Active));    // the phantom rule selects nothing (E5)

                    var policy = await engine.GetWorkflowPolicyAsync();
                    Assert.Contains(policy.Lints, l => l.Message.Contains("ghost-flow"));
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Malformed_activation_block_fails_safe()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    WriteSettings(ws, "{ this is not valid json ]");   // junk file
                    var list = await engine.ListWorkflowsAsync();
                    Assert.All(list, w => Assert.True(w.Active));   // fail-safe: every workflow active, library loads (E9)
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Deactivated_by_rule_is_startable_on_demand()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    WriteSettings(ws, @"{ ""activation"": [ { ""workflow"": ""act-vehicle"", ""when"": ""date.dayOfMonth >= 1"", ""set"": ""off"" } ] }");
                    Assert.False(Find(await engine.ListWorkflowsAsync(), "act-vehicle").Active);

                    var captured = new List<ActivityEvent>();
                    void Handler(ActivityEvent e) => captured.Add(e);
                    sessions.Bus.Activity += Handler;
                    try
                    {
                        // Activation curates the menu; it is NOT a lock — the rule-off workflow still starts on demand (D4).
                        var run = await engine.StartWorkflowAsync("act-vehicle", "agent");
                        Assert.Equal("active", run.Status);
                    }
                    finally { sessions.Bus.Activity -= Handler; }

                    var advisory = Assert.Single(captured, e => e.Kind == "start_workflow_off_menu");
                    Assert.Contains("off the current menu", advisory.Label);   // teaching note present
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Active_run_survives_mid_run_deactivation()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    var run = await engine.StartWorkflowAsync("act-vehicle", "agent");
                    Assert.Equal("active", run.Status);

                    // Deactivate it mid-run — the frozen run must not be torn (E11).
                    WriteSettings(ws, @"{ ""activation"": [ { ""workflow"": ""act-vehicle"", ""when"": ""date.dayOfMonth >= 1"", ""set"": ""off"" } ] }");
                    var still = await engine.GetWorkflowRunAsync(run.RunId);
                    Assert.Equal("active", still.Status);

                    // …and it still advances to completion.
                    var done = await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "agent");
                    Assert.Equal("completed", done.Status);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Template_instance_can_be_activation_targeted()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    // A user template carrying tags — instantiation must preserve them into the runnable instance.
                    const string tmpl = @"---
name: acme-tmpl
kind: template
title: Acme template
tags: [client-acme]
slots:
  - name: surfaceName
    question: ""Which surface?""
    example: ""the dashboard""
---
## Step 1: Work on {{surfaceName}}
Do the thing for {{surfaceName}}.
";
                    await engine.SaveWorkflowTemplateAsync("acme-tmpl", tmpl, "agent");
                    await engine.InstantiateWorkflowTemplateAsync("acme-tmpl", "acme-inst", "{\"surfaceName\":\"the FY26 board\"}", "agent");

                    // The instance kept its tags through the render (E8).
                    var def = await engine.GetWorkflowAsync("acme-inst");
                    Assert.Contains("client-acme", def.Tags);

                    // Targetable by the instance NAME…
                    WriteSettings(ws, @"{ ""activation"": [ { ""workflow"": ""acme-inst"", ""set"": ""off"" } ] }");
                    Assert.False(Find(await engine.ListWorkflowsAsync(), "acme-inst").Active);

                    // …and by its TAG (proves the tag survived + is selectable).
                    WriteSettings(ws, @"{ ""activation"": [ { ""tag"": ""client-acme"", ""set"": ""off"" } ] }");
                    Assert.False(Find(await engine.ListWorkflowsAsync(), "acme-inst").Active);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Get_workflow_policy_surfaces_active_and_lints()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    // Plant a binding↔activation contradiction (E3/§6 #4): required by a binding AND a rule sets it off.
                    WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""bind-vehicle""], ""mode"": ""hard"" } },
                                          ""activation"": [ { ""workflow"": ""bind-vehicle"", ""when"": ""date.dayOfMonth >= 1"", ""set"": ""off"" } ] }");
                    var policy = await engine.GetWorkflowPolicyAsync();

                    var entry = policy.Workflows.Single(e => e.Name == "bind-vehicle");
                    Assert.True(entry.Active);                                  // per-workflow active surfaced
                    Assert.Contains("required when", entry.ActiveReason);
                    Assert.Contains(policy.Lints, l => l.Severity == "warn" && l.Message.Contains("overridden"));
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Get_workflow_policy_lints_dead_rule_and_deadlock()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    // (3) dead rule: a rule turns act-vehicle on but it's manually off.
                    // (5) deadlock: bind-vehicle is off but required by a hard binding.
                    WriteSettings(ws, @"{ ""workflows"": { ""act-vehicle"": { ""enabled"": false }, ""bind-vehicle"": { ""enabled"": false } },
                                          ""bindings"": { ""create_measure"": { ""require"": [""bind-vehicle""], ""mode"": ""hard"" } },
                                          ""activation"": [ { ""workflow"": ""act-vehicle"", ""when"": ""date.dayOfMonth >= 1"", ""set"": ""on"" } ] }");
                    var policy = await engine.GetWorkflowPolicyAsync();
                    Assert.Contains(policy.Lints, l => l.Message.Contains("act-vehicle") && l.Message.Contains("no effect"));
                    Assert.Contains(policy.Lints, l => l.Message.Contains("bind-vehicle") && l.Message.Contains("turn"));
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Set_workflow_activation_is_pro_and_round_trips()
        {
            var ws = NewWorkspace();

            // FREE: writing a rule is refused with the free-alternative upsell.
            var freeSessions = new SessionManager();
            var free = new LocalEngine(freeSessions, new Fake(pro: false), ws);
            try
            {
                using (free)
                    await Assert.ThrowsAsync<EntitlementException>(
                        () => free.SetWorkflowActivationAsync("act-vehicle", "date.dayOfMonth >= 1", "off", "agent"));
            }
            finally { freeSessions.Dispose(); }

            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                using (engine)
                {
                    // A bad predicate is refused with the parse reason (teaching tone) — nothing written.
                    var bad = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.SetWorkflowActivationAsync("act-vehicle", "model.bogus == 1", "off", "agent"));
                    Assert.Contains("can't be used", bad.Message);

                    // A rule for a phantom workflow is refused.
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.SetWorkflowActivationAsync("ghost", "date.dayOfMonth >= 1", "off", "agent"));

                    // PRO writes it: act-vehicle goes inactive, the file carries the rule.
                    await engine.SetWorkflowActivationAsync("act-vehicle", "date.dayOfMonth >= 1", "off", "agent");
                    Assert.False(Find(await engine.ListWorkflowsAsync(), "act-vehicle").Active);
                    Assert.Contains("activation", File.ReadAllText(SettingsFile(ws)));

                    // Clearing (neither when nor set) prunes the rule + the emptied array → active again.
                    await engine.SetWorkflowActivationAsync("act-vehicle", null, null, "agent");
                    Assert.True(Find(await engine.ListWorkflowsAsync(), "act-vehicle").Active);
                    Assert.DoesNotContain("activation", File.ReadAllText(SettingsFile(ws)));
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Set_workflow_activation_rebroadcasts_library()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                using (engine)
                {
                    WorkflowInfo[] broadcast = null;
                    void Handler(WorkflowInfo[] list) => broadcast = list;
                    sessions.Bus.WorkflowLibraryChanged += Handler;
                    try { await engine.SetWorkflowActivationAsync("act-vehicle", "date.dayOfMonth >= 1", "off", "agent"); }
                    finally { sessions.Bus.WorkflowLibraryChanged -= Handler; }

                    Assert.NotNull(broadcast);   // both doors saw the recomputed library live
                    Assert.False(broadcast.Single(w => w.Name == "act-vehicle").Active);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }
    }
}

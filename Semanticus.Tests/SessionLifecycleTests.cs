using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Session/connection lifecycle invariants (from the 2026-07 lifecycle audit):
    ///  • a FAILED open must not discard the live session or its unsaved work (build-then-swap);
    ///  • a model swap must RESET model-scoped state (a plan from model A can't apply to model B);
    ///  • a model swap must DROP the live query connection (Studio must not edit B while run_dax queries A).
    /// </summary>
    public sealed class SessionLifecycleTests
    {
        private sealed class ProFake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private static string NoSuchPath() =>
            Path.Combine(Path.GetTempPath(), "sem-lifecycle-no-such-" + Guid.NewGuid().ToString("N") + ".bim");

        // ---- Finding 2: opening an invalid model must NOT destroy the current session. -----------------
        [Fact]
        public async Task A_failed_open_leaves_the_previous_session_intact()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            var a = await engine.OpenAsync(TestModels.FindBim());
            Assert.True(a.Tables > 0);

            // Attempt to open a path that cannot load — the build fails.
            await Assert.ThrowsAnyAsync<Exception>(() => engine.OpenAsync(NoSuchPath()));

            // The first session still ANSWERS: a read returns model A's data, unchanged (not discarded).
            var objs = await engine.GetModelObjectsAsync();
            Assert.NotNull(objs);
            Assert.Equal(a.Tables, objs.Tables.Length);
        }

        // A parse failure (a real file that isn't a valid model) is the other half of the same invariant.
        [Fact]
        public async Task A_failed_open_of_a_corrupt_file_leaves_the_previous_session_intact()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            var a = await engine.OpenAsync(TestModels.FindBim());

            var corrupt = Path.Combine(Path.GetTempPath(), "sem-lifecycle-corrupt-" + Guid.NewGuid().ToString("N") + ".bim");
            File.WriteAllText(corrupt, "{ this is not a valid tabular model ]");
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => engine.OpenAsync(corrupt));
                var objs = await engine.GetModelObjectsAsync();
                Assert.Equal(a.Tables, objs.Tables.Length);   // model A is still the current, working session
            }
            finally { try { File.Delete(corrupt); } catch { } }
        }

        // ---- Finding 4: a model swap must clear model-scoped state (plan store). -----------------------
        [Fact]
        public async Task Opening_another_model_clears_the_plan_from_the_previous_one()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            await engine.OpenAsync(TestModels.FindBim());

            // Propose a plan against model A — a plan now exists (StartNew always creates one).
            await engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, origin: "test");
            var before = await engine.GetPlanAsync();
            Assert.NotNull(before.PlanId);          // a plan is held for model A

            // Open a model again (any successful open is a model replacement — the reset is unconditional).
            await engine.OpenAsync(TestModels.FindBim());

            var after = await engine.GetPlanAsync();
            Assert.Null(after.PlanId);              // the previous model's plan did NOT carry over
            Assert.Empty(after.Items);
        }

        // ---- Finding 5: a model swap must drop the live query connection. ------------------------------
        [Fact]
        public async Task Opening_a_file_model_drops_the_live_query_connection()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());

            // Stand in a live query connection (as connect_xmla/open_live would), then open a plain file model.
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("local", "localhost:teststub"));
            Assert.True((await engine.ConnectionStatusAsync()).Connected);   // attached before the open

            await engine.OpenAsync(TestModels.FindBim());

            // The connection pointed at the PREVIOUS model's endpoint — it must not survive the swap, or Studio
            // would edit this model while run_dax queried the old one.
            Assert.False((await engine.ConnectionStatusAsync()).Connected);
        }

        // NOTE: the former "Xmla_connect_binding_sets_the_current_sessions_deploy_origin" pin was removed with the
        // BindLiveOriginIfCurrent method it exercised: connect_xmla is a QUERY connection and must NEVER rebind the
        // editing/deploy origin (HIGH 1). The query connection's own intent-race rejection is covered by the SwapLive
        // pins; that connect_xmla leaves LiveOrigin untouched is pinned in
        // ConnectionAccountHistoryTests.A_query_connection_never_rebinds_the_editing_origin.

        // ---- Full-context race pin: admitted model work drains before the old dispatcher is retired. ---------
        [Fact]
        public async Task A_session_swap_waits_for_admitted_old_model_work_then_rejects_stale_calls()
        {
            var sessions = new SessionManager();
            var old = await sessions.OpenAsync(TestModels.FindBim());
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var admitted = old.ReadAsync(m =>
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                return m.Tables.Count;
            });
            await entered.Task;

            var replacing = sessions.OpenAsync(TestModels.FindBim());
            for (var i = 0; i < 500 && !old.RetiredForTest; i++) await Task.Delay(10);
            Assert.True(old.RetiredForTest);                 // admission is closed at the commit boundary
            Assert.Same(old, sessions.Current);             // publication waits for the admitted read to drain
            Assert.False(replacing.IsCompleted);

            release.TrySetResult(true);
            Assert.True(await admitted > 0);
            var current = await replacing;
            Assert.Same(current, sessions.Current);

            var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => old.ReadAsync(m => m.Tables.Count));
            Assert.Contains("replaced or closed", stale.Message);
            Assert.True(await current.ReadAsync(m => m.Tables.Count) > 0);
        }

        // A disconnect retires the captured connection without tearing it out from under an admitted query/trace.
        [Fact]
        public async Task A_live_disconnect_drains_admitted_work_and_rejects_a_stale_connection_reference()
        {
            var live = LiveConnection.ForTest("xmla", "endpoint-A");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var admitted = live.RunWithLifetimeLeaseAsync(async () =>
            {
                entered.TrySetResult(true);
                await release.Task;
                return 7;
            });
            await entered.Task;

            live.Dispose();
            Assert.False(live.DisposedForTest);              // actual teardown is deferred while admitted work runs
            var stale = await Assert.ThrowsAsync<InvalidOperationException>(
                () => live.RunWithLifetimeLeaseAsync(() => Task.FromResult(0)));
            Assert.Contains("disconnected or replaced", stale.Message);

            release.TrySetResult(true);
            Assert.Equal(7, await admitted);
            Assert.True(live.DisposedForTest);
            Assert.False(live.HasConnectionStringForTest);   // the bearer-bearing source is cleared at retirement
        }

        // A query admitted before disconnect may drain safely, but its old-endpoint rows must not surface afterward.
        [Fact]
        public async Task A_query_result_from_a_replaced_connection_is_discarded()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var live = LiveConnection.ForTest("xmla", "endpoint-A", execute: _ =>
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                return new ResultSet
                {
                    Columns = new[] { new ColumnDef { Name = "[v]" } },
                    Rows = new[] { new object[] { 7 } },
                    RowCount = 1,
                };
            });
            engine.SetLiveConnectionForTest(live);

            var query = engine.RunDaxAsync("EVALUATE ROW(\"v\", 7)", 10, "human");
            await entered.Task;
            Assert.False((await engine.DisconnectAsync()).Connected);
            Assert.False(live.DisposedForTest);   // the admitted query still owns the connection lifetime
            release.TrySetResult(true);

            var result = await query;
            Assert.Contains("stale result was discarded", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(result.Rows);
            Assert.True(live.DisposedForTest);
        }

        [Fact]
        public async Task A_query_captured_before_disconnect_returns_the_not_connected_result()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-A"));
            var captured = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.LiveQueryCapturedForTest = async () =>
            {
                captured.TrySetResult(true);
                await release.Task;
            };

            var query = engine.RunDmvAsync("SELECT * FROM $SYSTEM.TMSCHEMA_TABLES", 10);
            await captured.Task;
            Assert.False((await engine.DisconnectAsync()).Connected);
            release.TrySetResult(true);

            var result = await query;
            Assert.Equal("Not connected. Call connect_xmla or connect_local first.", result.Error);
            Assert.Empty(result.Rows);
        }

        [Fact]
        public async Task Direct_session_manager_replacement_preserves_an_active_workflow_run()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new ProFake());
            await sessions.OpenAsync(TestModels.FindBim());
            var started = await engine.StartWorkflowAsync("new-measure", "human");

            await sessions.OpenAsync(TestModels.FindBim());

            var current = await engine.GetWorkflowRunAsync(started.RunId);
            Assert.Equal("active", current.Status);
            Assert.Equal(started.RunId, current.RunId);
        }

        // A plan prepared from model A must never land in model B's freshly-cleared store after an open races it.
        [Fact]
        public async Task A_stale_plan_proposal_cannot_repopulate_the_replacement_models_store()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            await engine.OpenAsync(TestModels.FindBim());
            var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.PlanSeedsReadyForTest = async () =>
            {
                prepared.TrySetResult(true);
                await release.Task;
            };

            var staleProposal = engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, origin: "test");
            await prepared.Task;
            await engine.OpenAsync(TestModels.FindBim());
            release.TrySetResult(true);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => staleProposal);
            Assert.Contains("open model changed", ex.Message, StringComparison.OrdinalIgnoreCase);
            var current = await engine.GetPlanAsync();
            Assert.Null(current.PlanId);
            Assert.Empty(current.Items);
        }

        // A spec derived from model A must not become model B's current authoring spec after a racing open.
        [Fact]
        public async Task A_stale_model_spec_cannot_repopulate_the_replacement_models_store()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            await engine.OpenAsync(TestModels.FindBim());
            var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ModelSpecReadyForTest = async () =>
            {
                prepared.TrySetResult(true);
                await release.Task;
            };

            var staleSpec = engine.AutogenerateSpecFromModelAsync("test");
            await prepared.Task;
            await engine.OpenAsync(TestModels.FindBim());
            release.TrySetResult(true);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => staleSpec);
            Assert.Contains("open model changed", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null((await engine.GetSpecAsync()).Spec);
        }

        // A capture measured from model A's live connection must not land in model B's empty baseline store.
        [Fact]
        public async Task A_stale_baseline_capture_cannot_repopulate_the_replacement_models_store()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            await engine.OpenAsync(TestModels.FindBim());
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-A", execute: _ => new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "[v]" }, new ColumnDef { Name = "[__present]" } },
                Rows = new[] { new object[] { 42.0, 1 } },
                RowCount = 1,
            }));
            var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.BaselineReadyForTest = async () =>
            {
                prepared.TrySetResult(true);
                await release.Task;
            };

            var staleCapture = engine.CaptureBaselineAsync(
                "measure:Date/Days In Current Quarter", null, null, includeDependents: false,
                maxMeasures: 25, rowCap: 2000, label: null, origin: "test");
            await prepared.Task;
            await engine.OpenAsync(TestModels.FindBim());
            release.TrySetResult(true);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => staleCapture);
            Assert.Contains("open model changed", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("not-found", (await engine.CompareBaselineAsync(null, null, "test")).Status);
        }

        // A run prepared for model A must not be registered against model B after a racing open.
        [Fact]
        public async Task A_stale_workflow_start_cannot_register_against_the_replacement_model()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            await engine.OpenAsync(TestModels.FindBim());
            var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.WorkflowStartReadyForTest = async () =>
            {
                prepared.TrySetResult(true);
                await release.Task;
            };

            var staleStart = engine.StartWorkflowAsync("new-measure", "test");
            await prepared.Task;
            await engine.OpenAsync(TestModels.FindBim());
            release.TrySetResult(true);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => staleStart);
            Assert.Contains("open model changed", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, engine.ActiveWorkflowRunsForTest);
        }

        [Fact]
        public async Task A_benign_live_reconnect_during_workflow_prescan_does_not_abort_the_start()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            await engine.OpenAsync(TestModels.FindBim());
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-A", "Model A"));
            engine.WorkflowStartReadyForTest = () =>
            {
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-B", "Model B"));
                return Task.CompletedTask;
            };

            var started = await engine.StartWorkflowAsync("new-measure", "human");

            Assert.Equal("active", started.Status);
            Assert.Equal("endpoint-B", (await engine.ConnectionStatusAsync()).DataSource);
        }

        // A late readiness scan from model A must not reactivate model B's workflow menu with A's grade.
        [Fact]
        public async Task A_stale_readiness_scan_cannot_repopulate_the_replacement_models_cache()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            await engine.OpenAsync(TestModels.FindBim());
            var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ReadinessReadyForTest = async () =>
            {
                prepared.TrySetResult(true);
                await release.Task;
            };

            var staleScan = engine.AiReadinessScanAsync();
            await prepared.Task;
            await engine.OpenAsync(TestModels.FindBim());
            release.TrySetResult(true);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => staleScan);
            Assert.Contains("open model changed", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ---- Race 1 pin: there is no invalidate-then-publish half-state. --------------------------------
        // Immediately before the atomic exchange, the old context is still complete. Immediately afterward, the
        // replacement context owns fresh model-scoped stores, preserves workflow state, and has no old live binding.
        // Readers see one complete side or the other.
        [Fact]
        public async Task A_model_replacement_atomically_exchanges_complete_session_contexts()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new ProFake());
            await engine.OpenAsync(TestModels.FindBim());
            var oldSession = sessions.Current;
            var oldContext = sessions.CurrentContext;

            await engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, origin: "test");   // model A holds a plan
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("local", "stub-A"));              // ...and a connection

            var oldStillCurrentAtCommit = false;
            var planIdAtCommit = "unset";
            var connectedAtCommit = true;
            engine.BetweenInvalidateAndCommitForTest = () =>
            {
                oldStillCurrentAtCommit = ReferenceEquals(sessions.Current, oldSession);
                planIdAtCommit = engine.GetPlanAsync().GetAwaiter().GetResult().PlanId;
                connectedAtCommit = engine.ConnectionStatusAsync().GetAwaiter().GetResult().Connected;
                engine.SetSpecAsync("{\"name\":\"old-context-only\"}", "test").GetAwaiter().GetResult();
            };
            await engine.OpenAsync(TestModels.FindBim());

            Assert.True(oldStillCurrentAtCommit);
            Assert.NotSame(oldContext, sessions.CurrentContext);
            Assert.NotNull(planIdAtCommit);           // old session + old plan remain one complete context
            Assert.True(connectedAtCommit);           // old session + old live binding remain paired
            Assert.NotNull(oldContext.Plans.Current); // retirement cannot redirect an old reference into new stores
            Assert.NotNull(oldContext.Spec.View().Spec);
            Assert.Null((await engine.GetPlanAsync()).PlanId);   // the replacement owns a fresh store
            Assert.Null((await engine.GetSpecAsync()).Spec);     // even a last-instant old-context write is isolated
            Assert.False((await engine.ConnectionStatusAsync()).Connected); // and no previous-model binding
        }

        // ---- Race 2 pin: the resurrection interleaving — a stale candidate must not displace a newer connection.
        // Models open_live(A)'s slow post-open rebind landing AFTER connect_xmla(B) published: the rebind's intent
        // ticket is OLDER, so its candidate must lose (and be discarded), leaving B attached.
        [Fact]
        public async Task A_stale_connection_candidate_cannot_displace_a_newer_connection()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            var staleTicket = engine.MintLiveIntentForTest();    // the open/rebind's intent (started first)
            var newerTicket = engine.MintLiveIntentForTest();    // the user's connect (started second)

            Assert.True(engine.TrySwapLiveForTest(LiveConnection.ForTest("xmla", "endpoint-B"), newerTicket));   // B publishes first
            Assert.False(engine.TrySwapLiveForTest(LiveConnection.ForTest("xmla", "endpoint-A"), staleTicket));  // the stale candidate lands late — refused

            var status = await engine.ConnectionStatusAsync();
            Assert.True(status.Connected);
            Assert.Equal("endpoint-B", status.DataSource);       // B survived; A was never published
        }

        // ---- Race 3 pin: a failure AT the observer step must leave the OLD session current and usable — WITH
        // its user-authored stores AND its live connection intact. The observer is the last fallible step and
        // runs BEFORE invalidation: a failed open destroys NOTHING (never "plans gone + disconnected + a bare
        // error that doesn't say so"). Model B is a different (tiny) model so a wrongly-published B is
        // detectable by its counts.
        [Fact]
        public async Task An_observer_failure_at_publish_leaves_the_old_session_current_and_usable()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new ProFake());
            var a = await engine.OpenAsync(TestModels.FindBim());
            var oldSession = sessions.Current;

            // User-authored model-scoped state that the FAILED open must not destroy.
            await engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, origin: "test");
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("local", "stub-A"));

            var dir = Path.Combine(Path.GetTempPath(), "sem-lifecycle-tiny-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var bimB = Path.Combine(dir, "Tiny.bim");
            var db = new TOM.Database("tiny") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            db.Model.Tables.Add(new TOM.Table { Name = "Only" });
            File.WriteAllText(bimB, TOM.JsonSerializer.SerializeDatabase(db));
            try
            {
                sessions.ObserverFactory = _ => throw new InvalidOperationException("observer boom");
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OpenAsync(bimB));

                Assert.Same(oldSession, sessions.Current);           // publication never happened
                sessions.ObserverFactory = null;                     // stop failing before using the engine again
                var objs = await engine.GetModelObjectsAsync();      // ...the old session still ANSWERS
                Assert.Equal(a.Tables, objs.Tables.Length);          // with model A's data, not tiny-model B's
                Assert.NotNull((await engine.GetPlanAsync()).PlanId);              // ...its PLAN was not destroyed
                Assert.True((await engine.ConnectionStatusAsync()).Connected);     // ...and its connection survives
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---- Round-3 F1 pin: the open's intent ticket is minted at OPERATION ENTRY, not at swap time. ----
        // A connect (or disconnect) the user issues WHILE an open runs is a NEWER intent than the open, so the
        // open's own drop must lose to it — the connection made mid-open survives the open. (The ObserverFactory
        // fires at step c, after the open's entry but before its drop — the exact interleaving window.) If the
        // ticket were minted at swap time instead (the bug), the open's drop would out-ticket and kill it.
        [Fact]
        public async Task A_connection_made_while_an_open_runs_survives_the_open()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new ProFake());
            await engine.OpenAsync(TestModels.FindBim());

            sessions.ObserverFactory = _ =>
            {
                // Mid-open: the user connects somewhere else — a newer intent than the running open.
                var midOpenConnect = engine.MintLiveIntentForTest();
                Assert.True(engine.TrySwapLiveForTest(LiveConnection.ForTest("xmla", "mid-open-endpoint"), midOpenConnect));
                return null;
            };
            await engine.OpenAsync(TestModels.FindBim());

            var status = await engine.ConnectionStatusAsync();
            Assert.True(status.Connected);                      // the open's (older) drop lost, as it must
            Assert.Equal("mid-open-endpoint", status.DataSource);
        }

        // ---- Round-3 F2 pin: the terminal intent is TERMINAL. ----
        // A connect racing Dispose must never leave a live connection attached after Dispose returns — even a
        // connect that MINTS its intent after Dispose (the newest ticket, which would beat any ticket-ordered
        // "terminal" swap) is rejected by the disposed flag.
        [Fact]
        public async Task A_connect_racing_dispose_never_stays_attached_after_dispose_returns()
        {
            var engine = new LocalEngine(new SessionManager(), new ProFake());
            var inFlight = engine.MintLiveIntentForTest();       // a connect already in flight when Dispose runs
            engine.Dispose();                                    // terminal: detaches and bars ALL future publishes

            Assert.False(engine.TrySwapLiveForTest(LiveConnection.ForTest("xmla", "late-A"), inFlight));   // stale ticket — rejected
            var afterDispose = engine.MintLiveIntentForTest();   // a connect that STARTS after Dispose — the NEWEST ticket
            Assert.False(engine.TrySwapLiveForTest(LiveConnection.ForTest("xmla", "late-B"), afterDispose)); // still rejected — terminal beats ticket order
            Assert.False((await engine.ConnectionStatusAsync()).Connected);
        }

        // ---- Round-4 P1 pin: an open superseded by a NEWER open must abort, never commit. ----
        // Sol's interleaving: open1 is mid-swap (built, observer attached) when open2 is issued; open2's session
        // intent is newer, so open1's live drop at step (d) would LOSE (leaving the previous connection attached)
        // while its commit at (e) would still land - Current would be open1's model with the OLD model's
        // connection still in _live, and run_dax/run_dmv would silently answer from the wrong model. The fix:
        // open1 hits the session-intent check at the point of no return and aborts honestly; open2 (parked on the
        // lifecycle gate, tickets already minted at its public entry) then commits normally. The ObserverFactory
        // fires at step c - after open1's entry, before its invalidate - the exact window sol described.
        [Fact]
        public async Task An_open_superseded_by_a_newer_open_aborts_and_the_newer_open_wins()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new ProFake());
            var a = await engine.OpenAsync(TestModels.FindBim());                        // model A current
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-A")); // ...with a connection

            // open1's target: a tiny model, so a wrongly-committed open1 is detectable by its table count.
            var dir = Path.Combine(Path.GetTempPath(), "sem-lifecycle-tiny-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var bimB = Path.Combine(dir, "Tiny.bim");
            var db = new TOM.Database("tiny") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            db.Model.Tables.Add(new TOM.Table { Name = "Only" });
            File.WriteAllText(bimB, TOM.JsonSerializer.SerializeDatabase(db));
            try
            {
                Task<OpenResult> open2 = null;
                sessions.ObserverFactory = _ =>
                {
                    sessions.ObserverFactory = null;   // one-shot: must not re-fire during open2's own swap
                    // Issued while open1 is mid-swap: open2 mints BOTH tickets synchronously at its public
                    // entry (before parking on the lifecycle gate), making open1 the stale session intent.
                    open2 = engine.OpenAsync(TestModels.FindBim());
                    return null;
                };

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OpenAsync(bimB));
                Assert.Contains("superseded", ex.Message, StringComparison.OrdinalIgnoreCase);   // open1 aborted honestly

                Assert.NotNull(open2);
                var r2 = await open2;                                    // the newer open commits normally
                var objs = await engine.GetModelObjectsAsync();
                Assert.Equal(r2.Tables, objs.Tables.Length);             // Current IS open2's model...
                Assert.Equal(a.Tables, objs.Tables.Length);              // ...(model A re-opened)...
                Assert.NotEqual(1, objs.Tables.Length);                  // ...and never tiny open1's
                Assert.False((await engine.ConnectionStatusAsync()).Connected);   // _live consistent: open2's drop won
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---- Round-4 P1 pin, both-fail leg: open2 supersedes open1, then open2's own build FAILS. ----
        // open1 aborted BEFORE any invalidation and open2 died at build (before ITS invalidation), so model A
        // must still be current WITH its stores and its connection - two failed opens destroy nothing.
        [Fact]
        public async Task A_superseded_open_plus_a_failed_superseder_leave_the_original_model_untouched()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new ProFake());
            var a = await engine.OpenAsync(TestModels.FindBim());
            var original = sessions.Current;
            await engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, origin: "test");   // model A holds a plan
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("local", "stub-A"));              // ...and a connection

            Task<OpenResult> open2 = null;
            sessions.ObserverFactory = _ =>
            {
                sessions.ObserverFactory = null;
                open2 = engine.OpenAsync(NoSuchPath());   // the superseder's build will fail
                return null;
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OpenAsync(TestModels.FindBim()));
            Assert.Contains("superseded", ex.Message, StringComparison.OrdinalIgnoreCase);   // open1: superseded, not committed
            Assert.NotNull(open2);
            await Assert.ThrowsAnyAsync<Exception>(() => open2);                             // open2: failed clean at build

            Assert.Same(original, sessions.Current);                                         // model A never moved
            Assert.Equal(a.Tables, (await engine.GetModelObjectsAsync()).Tables.Length);
            Assert.NotNull((await engine.GetPlanAsync()).PlanId);                            // its plan survives
            Assert.True((await engine.ConnectionStatusAsync()).Connected);                   // and so does its connection
        }

        // ---- Round-4 F3 pin: Session.Dispose is internally no-throw, idempotent, and ALWAYS tears down the
        // dispatcher. A throwing teardown stage (here: a disposable live credential that throws) used to abort
        // the rest of Dispose - SessionManager.Commit catches wholesale, so the dispatcher worker silently
        // survived, rooting the dead session. The dispatcher teardown now sits in a finally.
        [Fact]
        public async Task Session_dispose_tears_down_the_dispatcher_even_when_a_teardown_stage_throws()
        {
            var sessions = new SessionManager();
            var session = await sessions.OpenAsync(TestModels.FindBim());
            session.SeedLiveCredential("k", new ThrowingDisposableCredential());

            Assert.Null(Record.Exception(() => session.Dispose()));   // no-throw, whatever a stage does
            Assert.Null(Record.Exception(() => session.Dispose()));   // and idempotent: a repeat is a clean no-op

            // The dispatcher was still torn down (the finally): a post-dispose enqueue faults the task.
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.Dispatcher.RunAsync(() => 0));
        }

        private sealed class ThrowingDisposableCredential : Azure.Core.TokenCredential, IDisposable
        {
            public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, System.Threading.CancellationToken cancellationToken) => default;
            public override System.Threading.Tasks.ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, System.Threading.CancellationToken cancellationToken) => default;
            public void Dispose() => throw new InvalidOperationException("credential dispose boom");
        }

        // ---- Round-5 P1 pin: the open/create ticket PAIR mints atomically — pair ordering is the c2
        // soundness premise. Two independent increments let open A take its live ticket, get preempted, and
        // take its session ticket AFTER open B minted both: A then holds the newer session ticket with the
        // OLDER live ticket, B aborts at c2, A passes c2 — and A's _live drop loses to B's abandoned newer
        // live intent, committing A's model with the previous model's connection attached (the round-3 P1
        // reborn). The preemption itself can't be pinned deterministically, so pin the INVARIANT: pairs minted
        // from parallel tasks must have identical relative ordering on both counters.
        [Fact]
        public async Task Session_swap_intent_pairs_mint_with_identical_relative_ordering()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            var pairs = new System.Collections.Concurrent.ConcurrentBag<(long Live, long Session)>();
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await start.Task;   // release all minters together for maximum interleaving pressure
                for (var i = 0; i < 2000; i++) pairs.Add(engine.MintSessionSwapIntentsForTest());
            })).ToArray();
            start.SetResult(true);
            await Task.WhenAll(tasks);

            var bySession = pairs.OrderBy(p => p.Session).ToArray();
            for (var i = 1; i < bySession.Length; i++)
                Assert.True(bySession[i - 1].Live < bySession[i].Live,
                    $"pair ordering violated at index {i}: session {bySession[i - 1].Session}->{bySession[i].Session} but live {bySession[i - 1].Live}->{bySession[i].Live}");
        }

        // ---- Round-5 F2 pin: the auth setters are disposal-aware. ----
        // open_live seeds the token + credential AFTER the swap core returns; a newer open can commit and
        // dispose the session inside that window. A non-aware setter re-populates the DEAD session with a live
        // secret that the idempotent Dispose never cleans again (held until process exit).
        [Fact]
        public async Task Auth_setters_on_a_disposed_session_retain_nothing_and_dispose_the_offer()
        {
            var sessions = new SessionManager();
            var session = await sessions.OpenAsync(TestModels.FindBim());
            session.Dispose();

            var offered = new RecordingDisposableCredential();
            session.SeedLiveCredential("k", offered);
            Assert.True(offered.Disposed);                                        // the offer was disposed immediately...

            session.CacheLiveToken("k", new Azure.Core.AccessToken("tok", DateTimeOffset.UtcNow.AddHours(1)));
            Assert.Null(session.TryReuseLiveToken("k", TimeSpan.Zero).Token);     // ...and the token offer was dropped

            // Round-6 F3: a dead session must not manufacture live credentials at all — an uncached build would
            // have ambiguous ownership (no caller could know to dispose it). The build path refuses honestly.
            Assert.Throws<ObjectDisposedException>(
                () => session.GetOrBuildLiveCredential("k", () => new RecordingDisposableCredential()));
        }

        // ---- Round-6 F2 pin: the auth CLEAR is exception-safe — fields are nulled before the credential is
        // disposed. Disposing first meant a throwing credential Dispose exited with _liveCredential still set,
        // and since later Dispose calls retry only the dispatcher stage, it was retained for the process
        // lifetime.
        [Fact]
        public async Task A_throwing_credential_dispose_still_clears_the_auth_fields()
        {
            var sessions = new SessionManager();
            var session = await sessions.OpenAsync(TestModels.FindBim());
            session.CacheLiveToken("k", new Azure.Core.AccessToken("tok", DateTimeOffset.UtcNow.AddHours(1)));
            session.SeedLiveCredential("k", new ThrowingDisposableCredential());

            Assert.Null(Record.Exception(() => session.Dispose()));            // the credential's throw is swallowed
            Assert.False(session.HasLiveCredentialForTest);                    // the FIELD was cleared before the dispose attempt
            Assert.Null(session.TryReuseLiveToken("k", TimeSpan.Zero).Token);  // the token too
            Assert.Throws<ObjectDisposedException>(                            // and the dead session manufactures nothing
                () => session.GetOrBuildLiveCredential("k", () => new RecordingDisposableCredential()));
        }

        // ---- Round-6 F1 pin: the dispatcher teardown stage is SERIALIZED. ----
        // A volatile flag gave visibility, not mutual exclusion: N concurrent Dispose calls could all read it
        // unset and run Dispatcher.Dispose() concurrently (its concurrency safety is unproven). Under the gate,
        // the whole check/invoke/mark-success transition is one critical section: exactly one failed attempt,
        // exactly one successful retry, everyone else no-ops — and never two invocations in flight at once.
        [Fact]
        public async Task Concurrent_disposes_never_run_the_dispatcher_teardown_concurrently()
        {
            var sessions = new SessionManager();
            var session = await sessions.OpenAsync(TestModels.FindBim());
            int inFlight = 0, maxInFlight = 0, calls = 0;
            session.DisposeDispatcherForTest = () =>
            {
                var now = System.Threading.Interlocked.Increment(ref inFlight);
                int seen;   // record the high-water mark of concurrent invocations (CAS loop: no Interlocked max)
                while (now > (seen = System.Threading.Volatile.Read(ref maxInFlight))
                       && System.Threading.Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen) { }
                try
                {
                    System.Threading.Thread.Sleep(10);   // widen the window: unserialized entrants WOULD overlap here
                    if (System.Threading.Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("teardown boom");
                    session.Dispatcher.Dispose();
                }
                finally { System.Threading.Interlocked.Decrement(ref inFlight); }
            };

            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(async () => { await start.Task; session.Dispose(); })).ToArray();
            start.SetResult(true);
            await Task.WhenAll(tasks);

            Assert.Equal(1, maxInFlight);   // the teardown stage NEVER ran concurrently
            Assert.Equal(2, calls);         // serialized: one failed attempt, one successful retry, the rest no-op
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.Dispatcher.RunAsync(() => 0));   // and it ended DOWN
        }

        private sealed class RecordingDisposableCredential : Azure.Core.TokenCredential, IDisposable
        {
            public bool Disposed;
            public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, System.Threading.CancellationToken cancellationToken) => default;
            public override System.Threading.Tasks.ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, System.Threading.CancellationToken cancellationToken) => default;
            public void Dispose() => Disposed = true;
        }

        // ---- Round-5 F3 pin: a FAILED dispatcher teardown is retryable. ----
        // With a plain one-shot flag, a swallowed dispatcher-dispose failure makes every later Dispose a no-op:
        // the live worker thread roots the dead session for the process lifetime with no way back. The
        // dispatcher stage tracks its own success and is retried by later Dispose calls until it lands.
        [Fact]
        public async Task A_failed_dispatcher_teardown_is_retried_by_a_later_dispose()
        {
            var sessions = new SessionManager();
            var session = await sessions.OpenAsync(TestModels.FindBim());
            var calls = 0;
            session.DisposeDispatcherForTest = () =>
            {
                if (++calls == 1) throw new InvalidOperationException("teardown boom");
                session.Dispatcher.Dispose();
            };

            Assert.Null(Record.Exception(() => session.Dispose()));         // stage failed - swallowed, not stranded
            Assert.Equal(1, await session.Dispatcher.RunAsync(() => 1));    // the worker really is still up (the leak)
            Assert.Null(Record.Exception(() => session.Dispose()));         // a later dispose retries ONLY that stage
            Assert.Equal(2, calls);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.Dispatcher.RunAsync(() => 0));   // down now
            session.Dispose();                                              // and the stage is one-shot once it SUCCEEDED
            Assert.Equal(2, calls);
        }

        // ---- Round-4 F4 pin: a self-swap must never dispose the attached connection. ----
        // SwapLive returning the still-attached instance as Displaced (a won swap of c for c, or a LOSING swap
        // offering the already-attached instance) would have the caller dispose a connection that stays in _live:
        // still reported Connected, but every query on it would fail.
        [Fact]
        public async Task A_self_swap_never_disposes_the_attached_connection()
        {
            using var engine = new LocalEngine(new SessionManager(), new ProFake());
            var c = LiveConnection.ForTest("xmla", "endpoint-same");
            var staleTicket = engine.MintLiveIntentForTest();          // stale once the attach below mints a newer one

            engine.SetLiveConnectionForTest(c);                        // attach c
            engine.SetLiveConnectionForTest(c);                        // WON self-swap: c displaces itself
            Assert.False(c.DisposedForTest);                           // ...and must not be disposed out from under _live

            Assert.False(engine.TrySwapLiveForTest(c, staleTicket));   // LOSING swap offering the ATTACHED instance
            Assert.False(c.DisposedForTest);                           // the "loser" is _live itself - never disposed

            var status = await engine.ConnectionStatusAsync();
            Assert.True(status.Connected);                             // c is still attached and intact
            Assert.Equal("endpoint-same", status.DataSource);
        }

        // ---- Race 5 pin: dispatcher teardown is idempotent, and a post-dispose enqueue FAULTS the task
        // (async-consistent) instead of throwing synchronously into the session-swap path.
        [Fact]
        public async Task Dispatcher_double_dispose_is_a_noop_and_post_dispose_enqueue_faults_the_task()
        {
            var d = new ModelDispatcher();
            Assert.Equal(1, await d.RunAsync(() => 1));

            d.Dispose();
            d.Dispose();   // second dispose must be a clean no-op (used to hit the disposed BlockingCollection)

            Task<int> late = null;
            var sync = Record.Exception(() => { late = d.RunAsync(() => 2); });   // statement lambda: record SYNCHRONOUS throws only
            Assert.Null(sync);                                                    // never a synchronous throw
            await Assert.ThrowsAsync<InvalidOperationException>(() => late);      // the task faults, like any failed op
        }
    }
}

// The start plan's permission and approval, driven through the REAL engine (lane sf-consent, round 2): sf-core's start on a scratch
// folder with a fake schema reader, sf-core's store behind the document, and NO stand-in host. StartPlanConsentTests proves the
// rules against a stand-in; these prove the wiring (LocalEngine.StartPlanHost over the store) is what the rules run on. Each test
// ends in a positive step that only works with the adapter set, so a build with the adapter missing cannot pass by refusing everything.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class StartPlanConsentRealEngineTests : IDisposable
    {
        private const string Challenge = "start-plan-real-engine-challenge-0123456789";

        private sealed class CapturingOracle : IDecisionOracle
        {
            public bool Available => true;
            private int _count;
            public int Count => Volatile.Read(ref _count);
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                Interlocked.Increment(ref _count);
                return Task.FromResult(DecisionAnswers.NotDecided(request, "test oracle never decides"));
            }
        }

        private readonly string _root;
        private readonly string _origHome;
        private readonly string _origRegistry;
        private readonly List<IDisposable> _owned = new List<IDisposable>();
        private readonly CapturingOracle _oracle = new CapturingOracle();

        public StartPlanConsentRealEngineTests()
        {
            // A short root: the pipe tests below open domain sockets under TMPDIR.
            _root = Path.Combine(Path.GetTempPath(), "spr-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home"));
            _origRegistry = ConnectionRegistry.RootOverride;
            ConnectionRegistry.RootOverride = Path.Combine(_root, "user");
        }

        public void Dispose()
        {
            foreach (var o in _owned) { try { o.Dispose(); } catch { } }
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            ConnectionRegistry.RootOverride = _origRegistry;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static FabricSchema Schema() => new FabricSchema
        {
            Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh",
            Tables = new[]
            {
                new FabricTable
                {
                    Schema = "dbo", Name = "Sales", KeyColumns = new[] { "OrderId" },
                    Columns = new[]
                    {
                        new FabricColumn { Name = "OrderId", DataType = "Int64", SqlType = "bigint", Ordinal = 1 },
                        new FabricColumn { Name = "ProductId", DataType = "Int64", SqlType = "bigint", Ordinal = 2 },
                        new FabricColumn { Name = "Amount", DataType = "Decimal", SqlType = "decimal", Ordinal = 3 },
                    },
                },
                new FabricTable
                {
                    Schema = "dbo", Name = "Product", KeyColumns = new[] { "ProductId" },
                    Columns = new[]
                    {
                        new FabricColumn { Name = "ProductId", DataType = "Int64", SqlType = "bigint", Ordinal = 1 },
                        new FabricColumn { Name = "Name", DataType = "String", SqlType = "varchar", Ordinal = 2 },
                    },
                },
            },
            ForeignKeys = new[]
            {
                new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "ProductId", ToSchema = "dbo", ToTable = "Product", ToColumn = "ProductId" },
            },
        };

        /// <summary>A Pro engine on the real store, with a plan started from the fake schema and the model's own permission given.</summary>
        private sealed class Real
        {
            public LocalEngine Engine;
            public SessionManager Sessions;
            public StartPlanView View;
            public string Digest => View.Digest;
        }

        private async Task<Real> StartAsync(bool consent = true)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, _root);
            _owned.Add(engine);
            engine.JevBatchedForTest = _ => true;
            engine.StartDecideKickDisabledForTest = true;   // these tests ask directly through the fence; the loop must not ask beside them
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(Schema());
            engine.UseDecisionsForTest(new DecisionService(_oracle, new DecisionBudget()));
            var folder = Path.Combine(_root, "proj-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh", Folder = folder, Name = "Sales Model",
            }, "human");
            Assert.True(view.Exists);
            if (consent) await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            return new Real { Engine = engine, Sessions = sessions, View = view };
        }

        /// <summary>What a person's edit does on the real engine: change the live document through the host, which saves it and voids a
        /// stale approval. (EditStartPlanAsync belongs to another lane; the host is the one path every writer goes through.)</summary>
        private static async Task<StartPlanView> EditAsync(Real r, Action<StartPlanDoc> edit)
        {
            r.View = await r.Engine.StartPlanHost.UpdateAsync(d => { edit(d); return true; });
            return r.View;
        }

        private async Task<DecisionOutcome> AskAsync(Real r, params string[] modelTables)
        {
            var doc = r.Engine.StartPlanHost.Current();
            var scope = StartAskScope.For(doc, modelTables, r.Engine.StartPlanHost.SampleFactsOn(doc));
            const string scratch = "scratch-real";
            var fence = await r.Engine.StartAskFenceAsync(scratch, 1, () => (scratch, 1), scope);
            var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
            var snapshot = DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("measure_name", "Total Amount"),
                new KeyValuePair<string, object>("measure_formula_facts", DecisionFixture.SampleFormulaFacts("SUM")),
                new KeyValuePair<string, object>("table_name", modelTables.First()),
                new KeyValuePair<string, object>("table_columns", new List<object>
                {
                    new Dictionary<string, object> { { "name", "Amount" }, { "data_type", "Double" } },
                }),
                new KeyValuePair<string, object>("sibling_measures", new List<object>()),
            });
            var questions = MeasureDefaultsPoint.Questions(new[] { "Volume" }, true, true);
            return await r.Engine.Decisions.AskAsync(point.Name, snapshot, questions, isPro: true, enabled: true, fence: fence, ct: CancellationToken.None);
        }

        private static ManifestTable Extra(string name) => new ManifestTable
        {
            Name = name, SourceSchema = "dbo", SourceName = name, Include = true,
            Columns = new List<ManifestColumn> { new ManifestColumn { Name = "Id", SourceColumn = "Id", DataType = "Int64" } },
        };

        // ---- the adapter itself --------------------------------------------------------------------------------

        [Fact]
        public async Task The_engine_ships_with_the_host_over_the_store_and_with_it_unset_every_verb_says_there_is_no_plan()
        {
            var r = await StartAsync();
            Assert.NotNull(r.Engine.StartPlanHost);
            Assert.Equal(r.Digest, r.Engine.StartPlanHost.DigestOf(r.Engine.StartPlanHost.Current()));

            var set = r.Engine.StartPlanHost;
            r.Engine.StartPlanHost = null;
            var grant = await Assert.ThrowsAsync<InvalidOperationException>(() => r.Engine.GrantStartDisclosureAsync(r.Digest, "human"));
            var approve = await Assert.ThrowsAsync<InvalidOperationException>(() => r.Engine.ApproveStartPlanAsync(r.Digest));
            Assert.Contains("no plan", grant.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no plan", approve.Message, StringComparison.OrdinalIgnoreCase);

            r.Engine.StartPlanHost = set;
            Assert.NotNull((await r.Engine.GrantStartDisclosureAsync(r.Digest, "human")).Digest);
            Assert.NotNull((await r.Engine.ApproveStartPlanAsync(r.Digest)).Approval);
        }

        [Fact]
        public async Task The_host_hands_out_copies_and_a_change_that_returns_false_or_throws_saves_nothing()
        {
            var r = await StartAsync();
            var host = r.Engine.StartPlanHost;
            host.Current().Manifest.Tables.Clear();                                   // a copy: the live document is untouched
            Assert.Equal(2, r.Engine.StartPlanDocForTest().Manifest.Tables.Count);

            var before = r.Engine.StartPlanDocForTest().UpdatedUtc;
            await host.UpdateAsync(d => { d.Manifest.Tables.Clear(); return false; });
            Assert.Equal(2, r.Engine.StartPlanDocForTest().Manifest.Tables.Count);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.UpdateAsync(d => { d.Manifest.Tables.Clear(); throw new InvalidOperationException("no"); }));
            Assert.Equal(2, r.Engine.StartPlanDocForTest().Manifest.Tables.Count);
            Assert.Equal(before, r.Engine.StartPlanDocForTest().UpdatedUtc);
            Assert.Equal(2, StartPlanStore.Read(r.View.Id).Manifest.Tables.Count);   // and the file on disk agrees
        }

        // ---- grant, then approve -------------------------------------------------------------------------------

        [Fact]
        public async Task A_grant_then_an_approval_work_on_the_real_plan_and_survive_a_restart()
        {
            var r = await StartAsync();
            var granted = await r.Engine.GrantStartDisclosureAsync(r.Digest, "human");
            var kept = r.Engine.StartPlanDocForTest();
            Assert.NotNull(kept.Disclosure);
            Assert.Equal(new[] { "dbo.Product", "dbo.Sales" }, kept.Disclosure.IncludedTables.OrderBy(t => t, StringComparer.Ordinal).ToArray());
            Assert.Equal(r.Digest, granted.Digest);                                   // a grant is not an edit: it does not move the digest

            var approved = await r.Engine.ApproveStartPlanAsync(r.Digest);
            Assert.Equal(r.Digest, approved.Approval.Digest);
            Assert.Null(StartPlanConsent.WhyNoApproval(r.Engine.StartPlanDocForTest(), r.Digest, r.Digest));

            // On disk, so a restarted engine finds both.
            var onDisk = StartPlanStore.Read(r.View.Id);
            Assert.Equal(r.Digest, onDisk.Approval.Digest);
            Assert.NotNull(onDisk.Disclosure);

            // And the grant reaches asks: the same plan, the same ask, now goes.
            Assert.Equal(0, _oracle.Count);
            Assert.True((await AskAsync(r, "Sales")).Asked);
            Assert.Equal(1, _oracle.Count);
        }

        [Fact]
        public async Task An_approval_is_not_a_grant_and_a_grant_is_not_an_approval()
        {
            var r = await StartAsync();

            // Approved, never granted: the plan can be built from rules, but no ask may go.
            await r.Engine.ApproveStartPlanAsync(r.Digest);
            Assert.Null(r.Engine.StartPlanDocForTest().Disclosure);
            Assert.False((await AskAsync(r, "Sales")).Asked);
            Assert.Equal(0, _oracle.Count);

            // Granted, not approved (a second plan state): the ask goes, and nothing is approved by it.
            await EditAsync(r, d => d.Manifest.Tables[0].Columns[0].Hidden = !d.Manifest.Tables[0].Columns[0].Hidden);   // voids the approval above
            Assert.Null(r.Engine.StartPlanDocForTest().Approval);
            await r.Engine.GrantStartDisclosureAsync(r.Digest, "human");
            Assert.Null(r.Engine.StartPlanDocForTest().Approval);
            Assert.True((await AskAsync(r, "Sales")).Asked);
            Assert.Equal(1, _oracle.Count);
        }

        [Fact]
        public async Task A_grant_needs_the_models_own_permission_and_the_digest_the_person_saw()
        {
            var r = await StartAsync(consent: false);
            var noPermission = await Assert.ThrowsAsync<InvalidOperationException>(() => r.Engine.GrantStartDisclosureAsync(r.Digest, "human"));
            Assert.Contains("permission", noPermission.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(r.Engine.StartPlanDocForTest().Disclosure);
            Assert.False((await AskAsync(r, "Sales")).Asked);

            await r.Engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            await Assert.ThrowsAsync<InvalidOperationException>(() => r.Engine.GrantStartDisclosureAsync("a-digest-nobody-saw", "human"));
            Assert.Null(r.Engine.StartPlanDocForTest().Disclosure);

            await r.Engine.GrantStartDisclosureAsync(r.Digest, "human");
            Assert.NotNull(r.Engine.StartPlanDocForTest().Disclosure);
            Assert.True((await AskAsync(r, "Sales")).Asked);
        }

        // ---- an edit clears the approval -----------------------------------------------------------------------

        [Fact]
        public async Task An_edit_after_approval_clears_it_in_memory_and_on_disk_and_the_old_digest_cannot_be_approved_again()
        {
            var r = await StartAsync();
            var seen = r.Digest;
            await r.Engine.ApproveStartPlanAsync(seen);
            Assert.NotNull(StartPlanStore.Read(r.View.Id).Approval);

            var after = await EditAsync(r, d => d.Manifest.Tables[0].Columns[0].Hidden = !d.Manifest.Tables[0].Columns[0].Hidden);

            Assert.NotEqual(seen, after.Digest);
            Assert.Null(after.Approval);
            Assert.Null(r.Engine.StartPlanDocForTest().Approval);
            Assert.Null(StartPlanStore.Read(r.View.Id).Approval);
            Assert.False(after.CanBuild);
            Assert.Contains("not approved", StartPlanConsent.WhyNoApproval(r.Engine.StartPlanDocForTest(), after.Digest, seen), StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => r.Engine.ApproveStartPlanAsync(seen));   // what the person saw is gone

            var again = await r.Engine.ApproveStartPlanAsync(after.Digest);
            Assert.Equal(after.Digest, again.Approval.Digest);
        }

        [Fact]
        public async Task A_change_that_leaves_the_plan_as_it_was_keeps_the_approval()
        {
            var r = await StartAsync();
            await r.Engine.ApproveStartPlanAsync(r.Digest);
            var view = await EditAsync(r, d => d.Decide.Reason = "a note the digest does not cover");
            Assert.Equal(r.Digest, view.Digest);
            Assert.Equal(r.Digest, view.Approval.Digest);
        }

        // ---- a table added after the grant ---------------------------------------------------------------------

        [Fact]
        public async Task A_table_added_after_the_grant_stops_every_ask_until_it_is_granted_again()
        {
            var r = await StartAsync();
            await r.Engine.GrantStartDisclosureAsync(r.Digest, "human");
            Assert.True((await AskAsync(r, "Sales")).Asked);
            Assert.Equal(1, _oracle.Count);

            await EditAsync(r, d => d.Manifest.Tables.Add(Extra("Customer")));
            var stopped = await AskAsync(r, "Sales");
            Assert.False(stopped.Asked);                                              // even an ask about a table that WAS granted
            Assert.Equal(1, _oracle.Count);
            Assert.False((await AskAsync(r, "Customer")).Asked);
            Assert.Equal(1, _oracle.Count);

            await r.Engine.GrantStartDisclosureAsync(r.Digest, "human");              // the person says yes to the new table
            Assert.True((await AskAsync(r, "Customer")).Asked);
            Assert.Equal(2, _oracle.Count);
            Assert.Contains("dbo.Customer", r.Engine.StartPlanDocForTest().Disclosure.IncludedTables);
        }

        [Fact]
        public async Task The_sample_switched_on_after_the_grant_stops_every_ask_until_it_is_granted_again()
        {
            var r = await StartAsync();
            await r.Engine.GrantStartDisclosureAsync(r.Digest, "human");
            Assert.True((await AskAsync(r, "Sales")).Asked);

            r.Engine.NoteStartSampleRead(r.View.Id, true);                            // a sample of the source is read now
            Assert.False((await AskAsync(r, "Sales")).Asked);
            Assert.Equal(1, _oracle.Count);

            // The sample is part of the plan the person agrees to, so the digest they saw before it was read is stale: they look again, then agree.
            await Assert.ThrowsAsync<InvalidOperationException>(() => r.Engine.GrantStartDisclosureAsync(r.Digest, "human"));
            await r.Engine.GrantStartDisclosureAsync((await r.Engine.GetStartPlanAsync()).Digest, "human");
            Assert.True(r.Engine.StartPlanDocForTest().Disclosure.SampleFacts);
            Assert.True((await AskAsync(r, "Sales")).Asked);
            Assert.Equal(2, _oracle.Count);
        }

        [Fact]
        public async Task Withdrawing_the_models_permission_takes_back_the_start_plan_grant()
        {
            var r = await StartAsync();
            await r.Engine.GrantStartDisclosureAsync(r.Digest, "human");
            Assert.True((await AskAsync(r, "Sales")).Asked);

            await r.Engine.WithdrawConsentAsync("human");
            Assert.Null(r.Engine.StartPlanDocForTest().Disclosure);
            Assert.False((await AskAsync(r, "Sales")).Asked);
            Assert.Equal(1, _oracle.Count);
        }

        // ---- every writer tells both doors ---------------------------------------------------------------------

        [Fact]
        public async Task A_grant_an_approval_and_a_withdrawal_each_tell_the_doors_the_plan_moved_and_a_no_op_does_not()
        {
            var r = await StartAsync();
            await r.Engine.StartPreviewSettledForTest();   // the preview of the plan lands on its own and is announced too: let it land before counting
            var heard = new List<StartPlanView>();
            r.Sessions.Bus.StartPlanChanged += v => { lock (heard) heard.Add(v); };

            await r.Engine.GrantStartDisclosureAsync(r.Digest, "human");
            Assert.Single(heard);
            Assert.NotNull(heard[0].Decide);

            await r.Engine.ApproveStartPlanAsync(r.Digest);
            Assert.Equal(2, heard.Count);
            Assert.Equal(r.Digest, heard[1].Approval.Digest);

            await r.Engine.WithdrawConsentAsync("human");
            Assert.Equal(3, heard.Count);                                            // the plan's own copy of the grant went
            await r.Engine.WithdrawConsentAsync("human");
            Assert.Equal(3, heard.Count);                                            // nothing left to take back: nothing to announce
        }

        // ---- the approval, on both doors (Kane, 10 Oct 2026: the person's word in chat is enough) ---------------------------

        [Fact]
        public async Task An_agent_connection_approves_a_real_plan_with_the_digest_it_read_and_a_wrong_digest_is_refused()
        {
            var r = await StartAsync();
            var pipeName = TestPipe.Mint("semanticus-spr-");
            using var server = new RpcServer(r.Sessions, r.Engine, pipeName, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RpcClient agent = null;
            try
            {
                agent = await RpcClient.ConnectAsync(pipeName, "agent");
                await Assert.ThrowsAnyAsync<Exception>(() => agent.InvokeAsync<StartPlanView>("approveStartPlan", new object[] { "a-digest-nobody-saw" }));
                Assert.Null(r.Engine.StartPlanDocForTest().Approval);
                Assert.Null(StartPlanStore.Read(r.View.Id).Approval);

                var view = await agent.InvokeAsync<StartPlanView>("approveStartPlan", new object[] { r.Digest });
                Assert.Equal(r.Digest, view.Approval.Digest);
                Assert.Equal(r.Digest, r.Engine.StartPlanDocForTest().Approval.Digest);
            }
            finally
            {
                agent?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
            }
        }

        [Fact]
        public async Task The_pipe_target_approves_a_real_plan()
        {
            var r = await StartAsync();
            Assert.Null(r.Engine.StartPlanDocForTest().Approval);
            Assert.NotNull((await new EngineRpcTarget(r.Engine).approveStartPlan(r.Digest)).Approval);
        }

        private sealed class RpcClient : IDisposable
        {
            private readonly NamedPipeClientStream _pipe;
            private readonly JsonRpc _rpc;
            private RpcClient(NamedPipeClientStream pipe, JsonRpc rpc) { _pipe = pipe; _rpc = rpc; }

            internal static async Task<RpcClient> ConnectAsync(string pipeName, string role, string challenge = null)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                await RpcHandshake.WriteAsync(pipe, role == "human" ? RpcConnectionRole.Human : RpcConnectionRole.Agent, challenge);
                await RpcHandshake.ReadAcceptedAsync(pipe);
                var rpc = new JsonRpc(RpcServer.CreateHandler(pipe));
                rpc.StartListening();
                return new RpcClient(pipe, rpc);
            }

            internal Task<T> InvokeAsync<T>(string method, params object[] args) => _rpc.InvokeAsync<T>(method, args);

            public void Dispose()
            {
                try { _rpc.Dispose(); } catch { }
                try { _pipe.Dispose(); } catch { }
            }
        }
    }
}

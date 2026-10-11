// What the person lets Pro see while a new model is started from a SQL source, and who may approve the plan (lane sf-consent,
// design-spec-first-start-v2.md findings S1 and S2). The permission is checked on every ask, through the fence a start-plan ask
// carries; the oracle here records every request that REACHES it, so "sent nothing" is read off a counter, never argued.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public sealed class StartPlanConsentTests
    {
        // ---- scaffolding -----------------------------------------------------------------------------------

        /// <summary>The oracle every ask here goes to. It records the full text of each request that reaches it.</summary>
        private sealed class CapturingOracle : IDecisionOracle
        {
            public bool Available => true;
            public readonly List<string> Sent = new List<string>();
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (Sent) Sent.Add(StartPlanConsent.SendText(request));
                return Task.FromResult(DecisionAnswers.NotDecided(request, "test oracle never decides"));
            }
            public int Count { get { lock (Sent) return Sent.Count; } }
        }

        /// <summary>A stand-in for sf-core's store: one document, a digest over everything the person approves (not the approval
        /// and not the disclosure, which is the permission and not the plan), and edits under a lock.</summary>
        private sealed class FakeHost : IStartPlanConsentHost
        {
            private readonly object _gate = new object();
            public StartPlanDoc Doc;
            public bool Sample;
            public FakeHost(StartPlanDoc doc) { Doc = doc; }

            public StartPlanDoc Current() { lock (_gate) return Doc; }
            public bool SampleFactsOn(StartPlanDoc doc) => Sample;
            public string DigestOf(StartPlanDoc doc)
            {
                lock (_gate)
                {
                    var copy = JsonSerializer.Deserialize<StartPlanDoc>(JsonSerializer.Serialize(doc));
                    copy.Approval = null; copy.Disclosure = null; copy.UpdatedUtc = default;
                    var text = JsonSerializer.Serialize(copy) + "|sample=" + Sample;
                    using (var sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
                }
            }
            public Task<StartPlanView> UpdateAsync(Func<StartPlanDoc, bool> change)
            {
                lock (_gate)
                {
                    if (change(Doc)) Doc.UpdatedUtc = DateTime.UtcNow;
                    // What sf-core's store does after any change: an approval for a plan that is no longer this one is void.
                    StartPlanConsent.ClearApprovalIfMoved(Doc, DigestOf(Doc));
                    return Task.FromResult(new StartPlanView { Exists = true, Id = Doc.Id, Digest = DigestOf(Doc), Approval = Doc.Approval, Decide = Doc.Decide });
                }
            }
            /// <summary>What a person's edit does: change the plan, then the store voids a stale approval.</summary>
            public Task<StartPlanView> EditAsync(Action<StartPlanDoc> edit) => UpdateAsync(d => { edit(d); return true; });
        }

        private static ManifestTable Table(string name, string schema, bool include) => new ManifestTable
        {
            Name = name, SourceSchema = schema, SourceName = name, Include = include,
            Columns = new List<ManifestColumn> { new ManifestColumn { Name = "Id", SourceColumn = "Id", DataType = "Int64" } },
        };

        private static StartPlanDoc Plan() => new StartPlanDoc
        {
            Id = "plan-1", Version = 1,
            Source = new StartSource { Server = "wh.fabric.example", Database = "Sales", AuthMode = "azcli" },
            Manifest = new StructureManifest
            {
                Tables = new List<ManifestTable> { Table("Sales", "dbo", true), Table("Customer", "dbo", true), Table("Payroll", "hr", false) },
            },
            Decide = new StartDecide { State = StartDecideState.WaitingGrant },
        };

        private sealed class Rig : IDisposable
        {
            public DecisionFixture F = new DecisionFixture();
            public CapturingOracle Oracle = new CapturingOracle();
            public FakeHost Host;
            public LocalEngine Engine => F.Engine;
            public int Round = 1;
            public string Scratch = "scratch-1";

            public static async Task<Rig> StartAsync(bool consent = true)
            {
                var r = new Rig();
                await r.F.BuildModelAsync();
                r.Engine.UseDecisionsForTest(new DecisionService(r.Oracle, new DecisionBudget()));
                r.Host = new FakeHost(Plan());
                r.Engine.StartPlanHost = r.Host;
                if (consent) await r.F.ConsentAsync();
                return r;
            }

            public string Digest => Host.DigestOf(Host.Current());
            public Task<StartPlanView> GrantAsync(string origin = "human") => Engine.GrantStartDisclosureAsync(Digest, origin);

            public async Task<DecisionOutcome> AskAsync(string tableName, params string[] aboutTables)
            {
                var ask = StartAskScope.For(Host.Current(), aboutTables.Length == 0 ? new[] { tableName } : aboutTables, Host.Sample);
                var fence = await Engine.StartAskFenceAsync(Scratch, Round, () => (Scratch, Round), ask);
                return await SendAsync(fence, tableName);
            }

            public Task<DecisionOutcome> SendAsync(DecisionSessionFence fence, string tableName)
            {
                var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
                var snapshot = DecisionSnapshot.Build(point, new[]
                {
                    new KeyValuePair<string, object>("measure_name", "Total Amount"),
                    new KeyValuePair<string, object>("measure_formula_facts", DecisionFixture.SampleFormulaFacts("SUM")),
                    new KeyValuePair<string, object>("table_name", tableName),
                    new KeyValuePair<string, object>("table_columns", new List<object>
                    {
                        new Dictionary<string, object> { { "name", "Amount" }, { "data_type", "Double" } },
                    }),
                    new KeyValuePair<string, object>("sibling_measures", new List<object>()),
                });
                var questions = MeasureDefaultsPoint.Questions(new[] { "Volume" }, true, true);
                return Engine.Decisions.AskAsync(point.Name, snapshot, questions, isPro: true, enabled: true, fence: fence, ct: CancellationToken.None);
            }

            public void Dispose() => F.Dispose();
        }

        // ---- no ask goes before the grant ------------------------------------------------------------------

        [Fact]
        public async Task No_ask_goes_before_the_grant_even_with_the_models_own_permission()
        {
            using (var r = await Rig.StartAsync())
            {
                Assert.True((await r.Engine.GetConsentAsync()).Current);

                var outcome = await r.AskAsync("Sales");

                Assert.False(outcome.Asked);
                Assert.Contains("which tables Pro may see", outcome.SkippedReason, StringComparison.Ordinal);
                Assert.EndsWith("so nothing was sent", outcome.SkippedReason, StringComparison.Ordinal);
                Assert.Equal(0, r.Oracle.Count);
                Assert.Equal(0, r.Engine.Decisions.RequestsStarted);
            }
        }

        [Fact]
        public async Task The_grant_needs_the_models_own_permission_first_and_a_pro_session_and_the_digest_the_person_saw()
        {
            using (var r = await Rig.StartAsync(consent: false))
            {
                var noConsent = await Assert.ThrowsAsync<InvalidOperationException>(() => r.GrantAsync());
                Assert.Contains("Give permission for this model first", noConsent.Message, StringComparison.Ordinal);

                await r.F.ConsentAsync();
                var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => r.Engine.GrantStartDisclosureAsync("not-the-digest", "human"));
                Assert.Contains("changed after you looked", stale.Message, StringComparison.Ordinal);

                var view = await r.GrantAsync();
                Assert.NotNull(r.Host.Current().Disclosure);
                Assert.Equal(new[] { "dbo.Customer", "dbo.Sales" }, r.Host.Current().Disclosure.IncludedTables.OrderBy(t => t, StringComparer.Ordinal).ToArray());
                Assert.False(r.Host.Current().Disclosure.SampleFacts);
                Assert.NotNull(view);
            }

            using (var f = new DecisionFixture(TestEntitlements.Free))
            {
                await f.BuildModelAsync();
                f.Engine.StartPlanHost = new FakeHost(Plan());
                var free = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => f.Engine.GrantStartDisclosureAsync(((FakeHost)f.Engine.StartPlanHost).DigestOf(((FakeHost)f.Engine.StartPlanHost).Current()), "human"));
                Assert.Contains("Semanticus Pro", free.Message, StringComparison.Ordinal);
            }

            using (var r = await Rig.StartAsync())
            {
                r.Engine.StartPlanHost = null;
                var none = await Assert.ThrowsAsync<InvalidOperationException>(() => r.Engine.GrantStartDisclosureAsync("x", "human"));
                Assert.Contains("no plan", none.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task After_the_grant_an_ask_about_an_included_table_goes()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                var outcome = await r.AskAsync("Sales");
                Assert.Null(outcome.SkippedReason);
                Assert.Equal(1, r.Oracle.Count);
            }
        }

        // ---- an excluded table's names never appear in a recorded request ----------------------------------

        [Fact]
        public async Task An_excluded_tables_names_never_appear_in_a_recorded_request()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();

                // The asker says it is about Sales and Customer only (so the declared-scope check passes) but the snapshot carries
                // the excluded table's name: only reading the request itself catches that.
                var fence = await r.Engine.StartAskFenceAsync(r.Scratch, r.Round, () => (r.Scratch, r.Round),
                    StartAskScope.For(r.Host.Current(), new[] { "Sales" }, false));
                var leaked = await r.SendAsync(fence, "Payroll");
                Assert.False(leaked.Asked);
                Assert.Contains("Payroll", leaked.SkippedReason, StringComparison.Ordinal);
                Assert.Contains("did not let Pro see", leaked.SkippedReason, StringComparison.Ordinal);

                // By its source name too, and by its schema-qualified name.
                foreach (var spelling in new[] { "hr.Payroll", "[payroll]", "PAYROLL" })
                    Assert.False((await r.SendAsync(fence, spelling)).Asked, spelling);

                // A table that is in scope still goes, and nothing recorded names Payroll.
                var fine = await r.AskAsync("Customer");
                Assert.Null(fine.SkippedReason);
                Assert.Equal(1, r.Oracle.Count);
                Assert.All(r.Oracle.Sent, text => Assert.DoesNotContain("payroll", text, StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task A_declared_table_outside_the_grant_is_refused_without_reading_the_request()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                var outcome = await r.AskAsync("Sales", "Sales", "Payroll");
                Assert.False(outcome.Asked);
                Assert.Contains("which you have not let Pro see", outcome.SkippedReason, StringComparison.Ordinal);
                Assert.Equal(0, r.Oracle.Count);
            }
        }

        [Fact]
        public void The_name_scan_matches_whole_phrases_only_and_an_underscore_is_part_of_a_word()
        {
            Assert.True(StartPlanConsent.ContainsPhrase("the [payroll] table", "payroll"));
            Assert.True(StartPlanConsent.ContainsPhrase("hr.payroll", "payroll"));
            Assert.False(StartPlanConsent.ContainsPhrase("a catalog of things", "log"));
            Assert.False(StartPlanConsent.ContainsPhrase("\"data_type\":\"double\"", "type"));
            Assert.True(StartPlanConsent.ContainsPhrase("payroll", "payroll"));
        }

        // ---- a table added after the grant sends nothing until granted again --------------------------------

        [Fact]
        public async Task A_table_added_after_the_grant_stops_every_ask_until_it_is_granted_again()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                Assert.Null((await r.AskAsync("Sales")).SkippedReason);
                Assert.Equal(1, r.Oracle.Count);

                await r.Host.EditAsync(d => d.Manifest.Tables.Single(t => t.Name == "Payroll").Include = true);

                // Not only the ask about the new table: an ask about Sales alone stops too, because the plan is no longer the one agreed to.
                var onNewTable = await r.AskAsync("Payroll");
                var onOldTable = await r.AskAsync("Sales");
                Assert.False(onNewTable.Asked);
                Assert.False(onOldTable.Asked);
                Assert.Contains("\"hr.payroll\" was added after you agreed", onOldTable.SkippedReason, StringComparison.Ordinal);
                Assert.Equal(1, r.Oracle.Count);

                // The person agrees again to what they now see.
                await r.GrantAsync();
                Assert.Null((await r.AskAsync("Payroll")).SkippedReason);
                Assert.Equal(2, r.Oracle.Count);
            }
        }

        [Fact]
        public async Task A_table_taken_out_after_the_grant_does_not_stop_asks_and_its_name_may_not_go()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                await r.Host.EditAsync(d => d.Manifest.Tables.Single(t => t.Name == "Customer").Include = false);

                Assert.Null((await r.AskAsync("Sales")).SkippedReason);
                var leaked = await r.AskAsync("Customer", "Sales");
                Assert.False(leaked.Asked);
                Assert.Equal(1, r.Oracle.Count);
            }
        }

        // ---- the sample flag flipped after the grant ---------------------------------------------------------

        [Fact]
        public async Task The_sample_switched_on_after_the_grant_stops_every_ask_until_it_is_granted_again()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                Assert.Null((await r.AskAsync("Sales")).SkippedReason);

                r.Host.Sample = true;
                var stopped = await r.AskAsync("Sales");
                Assert.False(stopped.Asked);
                Assert.Contains("sample of rows were switched on after you agreed", stopped.SkippedReason, StringComparison.Ordinal);
                Assert.Equal(1, r.Oracle.Count);

                await r.GrantAsync();
                Assert.True(r.Host.Current().Disclosure.SampleFacts);
                Assert.Null((await r.AskAsync("Sales")).SkippedReason);
                Assert.Equal(2, r.Oracle.Count);

                // And off again: the grant said sample facts, the plan now says none, so it is asked again too.
                r.Host.Sample = false;
                Assert.False((await r.AskAsync("Sales")).Asked);
                Assert.Equal(2, r.Oracle.Count);
            }
        }

        [Fact]
        public async Task An_ask_that_carries_sample_facts_under_a_grant_without_them_is_refused()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                var ask = StartAskScope.For(r.Host.Current(), new[] { "Sales" }, sampleFacts: true);
                var fence = await r.Engine.StartAskFenceAsync(r.Scratch, r.Round, () => (r.Scratch, r.Round), ask);
                var outcome = await r.SendAsync(fence, "Sales");
                Assert.False(outcome.Asked);
                Assert.Contains("sample facts that do not match", outcome.SkippedReason, StringComparison.Ordinal);
                Assert.Equal(0, r.Oracle.Count);
            }
        }

        // ---- withdrawing mid-loop stops the next ask ---------------------------------------------------------

        [Fact]
        public async Task Withdrawing_permission_mid_loop_stops_the_next_ask_and_a_fresh_model_grant_is_not_enough()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                await r.Host.UpdateAsync(d => { d.Decide.State = StartDecideState.Deciding; return true; });
                Assert.Null((await r.AskAsync("Sales")).SkippedReason);
                Assert.Equal(1, r.Oracle.Count);

                var told = 0;
                r.Engine.StartDisclosureChanged = () => told++;
                await r.Engine.WithdrawConsentAsync("human");

                Assert.False((await r.AskAsync("Sales")).Asked);
                Assert.Equal(1, r.Oracle.Count);
                Assert.Null(r.Host.Current().Disclosure);
                Assert.Equal(StartDecideState.WaitingGrant, r.Host.Current().Decide.State);
                Assert.Contains("took permission back", r.Host.Current().Decide.Reason, StringComparison.Ordinal);
                Assert.True(told >= 1, "the decide loop was not told");

                // The model's own permission given again is a different permission: the tables still have to be agreed to.
                await r.F.ConsentAsync();
                var after = await r.AskAsync("Sales");
                Assert.False(after.Asked);
                Assert.Contains("which tables Pro may see", after.SkippedReason, StringComparison.Ordinal);
                Assert.Equal(1, r.Oracle.Count);

                await r.GrantAsync();
                Assert.Null((await r.AskAsync("Sales")).SkippedReason);
                Assert.Equal(2, r.Oracle.Count);
            }
        }

        [Fact]
        public async Task A_withdrawal_in_the_instant_before_the_send_stops_that_ask()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                r.Engine.Decisions.BeforeSendForTest = () => r.Engine.WithdrawConsentAsync("human").GetAwaiter().GetResult();
                var outcome = await r.AskAsync("Sales");
                Assert.False(outcome.Asked);
                Assert.Equal(0, r.Oracle.Count);
            }
        }

        [Fact]
        public async Task A_table_added_in_the_instant_before_the_send_stops_that_ask()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                r.Engine.Decisions.BeforeSendForTest = () =>
                    r.Host.EditAsync(d => d.Manifest.Tables.Single(t => t.Name == "Payroll").Include = true).GetAwaiter().GetResult();
                var outcome = await r.AskAsync("Sales");
                Assert.False(outcome.Asked);
                Assert.Contains("was added after you agreed", outcome.SkippedReason, StringComparison.Ordinal);
                Assert.Equal(0, r.Oracle.Count);
            }
        }

        [Fact]
        public async Task A_reply_that_lands_after_the_permission_was_taken_back_is_dropped()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                r.Engine.Decisions.BeforeReturnForTest = () => r.Engine.WithdrawConsentAsync("human").GetAwaiter().GetResult();
                var outcome = await r.AskAsync("Sales");
                Assert.False(outcome.Asked);
                Assert.Equal(1, r.Oracle.Count);
            }
        }

        // ---- the fence: scratch, round, and the person's model -----------------------------------------------

        [Fact]
        public async Task A_new_round_or_another_scratch_makes_an_ask_stale_and_sends_nothing()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                var ask = StartAskScope.For(r.Host.Current(), new[] { "Sales" }, false);

                var round = r.Round;
                var fence = await r.Engine.StartAskFenceAsync(r.Scratch, round, () => (r.Scratch, round + 1), ask);
                Assert.False((await r.SendAsync(fence, "Sales")).Asked);

                fence = await r.Engine.StartAskFenceAsync(r.Scratch, round, () => ("scratch-2", round), ask);
                Assert.False((await r.SendAsync(fence, "Sales")).Asked);

                Assert.Equal(0, r.Oracle.Count);
                Assert.True((await r.SendAsync(await r.Engine.StartAskFenceAsync(r.Scratch, round, () => (r.Scratch, round), ask), "Sales")).Asked);
            }
        }

        [Fact]
        public async Task An_ask_for_a_different_source_is_refused()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                var ask = StartAskScope.For(r.Host.Current(), new[] { "Sales" }, false);
                ask.SourceKey = "other.example/Payroll";
                var fence = await r.Engine.StartAskFenceAsync(r.Scratch, r.Round, () => (r.Scratch, r.Round), ask);
                var outcome = await r.SendAsync(fence, "Sales");
                Assert.False(outcome.Asked);
                Assert.Contains("different source", outcome.SkippedReason, StringComparison.Ordinal);

                // The plan itself pointed at another source after the grant: also stopped.
                await r.Host.EditAsync(d => d.Source.Database = "Other");
                Assert.False((await r.AskAsync("Sales")).Asked);
                Assert.Equal(0, r.Oracle.Count);
            }
        }

        // ---- F-177 replay: another model at the same path is asked again --------------------------------------

        [Fact]
        public async Task Another_model_at_the_same_path_is_asked_again()
        {
            using (var r = await Rig.StartAsync())
            {
                var path = Path.Combine(r.F.Workspace, "DecisionProof.bim");
                await r.GrantAsync();
                Assert.Null((await r.AskAsync("Sales")).SkippedReason);
                Assert.Equal(1, r.Oracle.Count);

                // A different model written over the same file, then opened at the same address.
                var otherHome = Path.Combine(r.F.Home, "other");
                Directory.CreateDirectory(otherHome);
                using (var otherSessions = new SessionManager())
                using (var other = new LocalEngine(otherSessions, TestEntitlements.Pro, otherHome))
                {
                    await other.CreateModelAsync("SomebodyElse", 1604);
                    await other.CreateTableAsync("Orders", "human");
                    await other.SaveAsync(Path.Combine(otherHome, "Other.bim"), "bim", false, "human");
                }
                File.Copy(Path.Combine(otherHome, "Other.bim"), path, overwrite: true);
                await r.Engine.OpenAsync(path, discardUnsaved: true);
                Assert.Equal("disk", (await r.Engine.GetConsentAsync()).AppliesTo.Split(':')[0]);

                // Neither the model's own permission nor the plan's grant crosses to it.
                Assert.False((await r.Engine.GetConsentAsync()).Current);
                var asked = await r.AskAsync("Sales");
                Assert.False(asked.Asked);
                Assert.Equal(1, r.Oracle.Count);

                // Even when its owner gives the model's own permission, the tables are agreed to again.
                await r.F.ConsentAsync();
                var still = await r.AskAsync("Sales");
                Assert.False(still.Asked);
                Assert.Contains("which tables Pro may see", still.SkippedReason, StringComparison.Ordinal);
                Assert.Equal(1, r.Oracle.Count);
            }
        }

        [Fact]
        public async Task A_fence_made_for_one_model_goes_stale_when_another_model_opens()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                var ask = StartAskScope.For(r.Host.Current(), new[] { "Sales" }, false);
                var fence = await r.Engine.StartAskFenceAsync(r.Scratch, r.Round, () => (r.Scratch, r.Round), ask);

                await r.Engine.CreateModelAsync("Another", 1604);

                var outcome = await r.SendAsync(fence, "Sales");
                Assert.False(outcome.Asked);
                Assert.Equal(0, r.Oracle.Count);
            }
        }

        // ---- the approval is the person's and dies with any edit ---------------------------------------------

        [Fact]
        public async Task The_person_approves_the_digest_they_saw_and_a_stale_digest_is_refused()
        {
            using (var r = await Rig.StartAsync())
            {
                var seen = r.Digest;
                var person = new HumanGovernanceRpcTarget(r.Engine);

                await Assert.ThrowsAsync<InvalidOperationException>(() => person.approveStartPlan("a-digest-nobody-saw"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => person.approveStartPlan(null));
                Assert.Null(r.Host.Current().Approval);

                var view = await person.approveStartPlan(seen);
                Assert.Equal(seen, r.Host.Current().Approval.Digest);
                Assert.Equal(seen, view.Approval.Digest);
                Assert.NotNull(r.Host.Current().Approval.Utc);
                Assert.Null(StartPlanConsent.WhyNoApproval(r.Host.Current(), r.Digest, seen));
            }
        }

        [Fact]
        public async Task Any_edit_clears_the_approval()
        {
            using (var r = await Rig.StartAsync())
            {
                var person = new HumanGovernanceRpcTarget(r.Engine);
                await person.approveStartPlan(r.Digest);
                var approved = r.Host.Current().Approval.Digest;
                Assert.Null(StartPlanConsent.WhyNoApproval(r.Host.Current(), r.Digest, approved));

                // One column hidden, however small.
                await r.Host.EditAsync(d => d.Manifest.Tables[0].Columns[0].Hidden = true);

                Assert.Null(r.Host.Current().Approval);
                Assert.NotNull(StartPlanConsent.WhyNoApproval(r.Host.Current(), r.Digest, approved));

                // And the rule does not lean on the store having cleared it: an approval that somehow survived an edit still does not stand.
                r.Host.Current().Approval = new StartApproval { Digest = approved, Utc = DateTime.UtcNow };
                Assert.Contains("changed after you approved", StartPlanConsent.WhyNoApproval(r.Host.Current(), r.Digest, approved), StringComparison.Ordinal);
                Assert.True(StartPlanConsent.ClearApprovalIfMoved(r.Host.Current(), r.Digest));
                Assert.Null(r.Host.Current().Approval);

                // A build that quotes the old digest is refused with a plain reason; the approval does not come back by itself.
                Assert.Contains("not approved", StartPlanConsent.WhyNoApproval(r.Host.Current(), r.Digest, approved), StringComparison.Ordinal);
                await Assert.ThrowsAsync<InvalidOperationException>(() => person.approveStartPlan(approved));
            }
        }

        [Fact]
        public async Task A_plan_that_moves_between_the_look_and_the_approval_is_not_approved_unseen()
        {
            using (var r = await Rig.StartAsync())
            {
                var seen = r.Digest;
                await r.Host.EditAsync(d => d.Manifest.Tables[1].Name = "Client");
                await Assert.ThrowsAsync<InvalidOperationException>(() => r.Engine.ApproveStartPlanAsync(seen));
                Assert.Null(r.Host.Current().Approval);
            }
        }

        [Fact]
        public void An_approval_that_does_not_match_the_current_digest_or_the_one_quoted_does_not_stand()
        {
            var doc = Plan();
            doc.Approval = new StartApproval { Digest = "d1", Utc = DateTime.UtcNow };
            Assert.Null(StartPlanConsent.WhyNoApproval(doc, "d1", "d1"));
            Assert.Null(StartPlanConsent.WhyNoApproval(doc, "d1", null));
            Assert.Contains("changed after you approved", StartPlanConsent.WhyNoApproval(doc, "d2", "d1"), StringComparison.Ordinal);
            Assert.Contains("not the plan you approved", StartPlanConsent.WhyNoApproval(doc, "d1", "d3"), StringComparison.Ordinal);
            Assert.NotNull(StartPlanConsent.WhyNoApproval(Plan(), "d1", "d1"));
            Assert.True(StartPlanConsent.ClearApprovalIfMoved(doc, "d2"));
            Assert.Null(doc.Approval);
        }

        // ---- no MCP twin ---------------------------------------------------------------------------------------

        [Fact]
        public void The_MCP_tool_list_has_no_approve_for_a_start_plan()
        {
            var tools = typeof(McpTools).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                .Select(m => m.GetCustomAttributes(true).FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute"))
                .Where(a => a != null)
                .Select(a => (string)a.GetType().GetProperty("Name").GetValue(a))
                .ToList();
            Assert.NotEmpty(tools);
            Assert.DoesNotContain(tools, name => name.IndexOf("approve", StringComparison.OrdinalIgnoreCase) >= 0
                && (name.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("plan", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("action", StringComparison.OrdinalIgnoreCase) < 0));
            Assert.DoesNotContain(tools, name => name.Equals("approve_start_plan", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void The_approval_is_on_the_person_only_target_and_on_no_agent_target()
        {
            Assert.NotNull(typeof(HumanGovernanceRpcTarget).GetMethod("approveStartPlan"));
            Assert.Null(typeof(EngineRpcTarget).GetMethod("approveStartPlan"));
            Assert.Null(typeof(EngineRpcTarget).GetMethod("ApproveStartPlanAsync"));
            Assert.Null(typeof(McpTools).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name.IndexOf("ApproveStartPlan", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        [Fact]
        public void No_door_an_assistant_can_reach_calls_the_approval_in_its_source()
        {
            var engineDir = Path.Combine(DecisionRegistryTests.RepoRoot(), "Semanticus.Engine");
            var reach = Directory.EnumerateFiles(engineDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).StartsWith("McpTools", StringComparison.Ordinal)
                            || Path.GetFileName(f).StartsWith("EngineRpcTarget", StringComparison.Ordinal)
                            || Path.GetFileName(f).StartsWith("McpHost", StringComparison.Ordinal)
                            || Path.GetFileName(f).StartsWith("McpDoor", StringComparison.Ordinal)
                            || Path.GetFileName(f).StartsWith("RpcServer", StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(reach);
            foreach (var file in reach)
            {
                // Code only: a comment may NAME the approval to say it is not here (McpTools.StartPlan.cs does), and that is no door to it.
                var text = string.Join("\n", File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
                Assert.DoesNotContain("ApproveStartPlan", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("approve_start_plan", text, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task An_agent_connection_cannot_see_the_approval_over_the_wire()
        {
            const string challenge = "start-plan-consent-challenge-0123456789abcdef";
            var pipeName = TestPipe.Mint("semanticus-spc-");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            using var server = new RpcServer(sessions, engine, pipeName, challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RpcClient agent = null;
            try
            {
                agent = await RpcClient.ConnectAsync(pipeName, "agent");
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => agent.InvokeAsync<StartPlanView>("approveStartPlan", "any-digest"));
                Assert.Contains("method", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                agent?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                sessions.Dispose();
            }
        }

        // ---- the record is kept beside the model's own permission, outside the model ----------------------------

        [Fact]
        public async Task The_grant_is_kept_in_the_per_user_home_and_not_beside_the_model()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                var kept = StartDisclosureStore.FilePath();
                Assert.True(File.Exists(kept));
                Assert.StartsWith(ConnectionRegistry.Root(), kept, StringComparison.Ordinal);
                Assert.Empty(Directory.EnumerateFiles(r.F.Workspace, "*disclosure*", SearchOption.AllDirectories));

                // A file that cannot be read is no grant.
                File.WriteAllText(kept, "{ not json");
                Assert.False((await r.AskAsync("Sales")).Asked);
                Assert.Equal(0, r.Oracle.Count);
            }
        }

        [Fact]
        public async Task A_grant_survives_an_engine_restart_for_the_same_model_and_plan_and_not_for_another_plan()
        {
            using (var r = await Rig.StartAsync())
            {
                await r.GrantAsync();
                var scopeKey = (await r.Engine.GetConsentAsync()).AppliesTo;
                var generation = DecisionConsentStore.Read(scopeKey).Generation;

                Assert.NotNull(StartDisclosureStore.Read(scopeKey, "plan-1", generation));
                Assert.Null(StartDisclosureStore.Read(scopeKey, "plan-2", generation));
                Assert.Null(StartDisclosureStore.Read(scopeKey, "plan-1", generation + 1));
                Assert.Null(StartDisclosureStore.Read(scopeKey + "x", "plan-1", generation));
            }
        }

        // ---- the pure rules, edge by edge -------------------------------------------------------------------------

        [Fact]
        public void The_pure_rules_say_why_in_plain_words()
        {
            var granted = new DisclosedScope("wh/Sales", new[] { "dbo.Sales", "dbo.Customer" }, false);
            var now = new DisclosedScope("wh/Sales", new[] { "dbo.Sales" }, false);

            Assert.Null(StartPlanConsent.WhyNot(granted, now, null));
            Assert.Null(StartPlanConsent.WhyNot(granted, now, new StartAskScope { SourceKey = "wh/Sales", Tables = new[] { "DBO.SALES" } }));
            Assert.Equal(StartPlanConsent.NoGrantReason, StartPlanConsent.WhyNot(null, now, null));
            Assert.Contains("\"hr.payroll\"", StartPlanConsent.WhyNot(granted, new DisclosedScope("wh/Sales", new[] { "dbo.Sales", "hr.Payroll" }, false), null), StringComparison.Ordinal);
            Assert.Equal(StartPlanConsent.WrongSourceReason, StartPlanConsent.WhyNot(granted, new DisclosedScope("elsewhere/Sales", new[] { "dbo.Sales" }, false), null));
            Assert.Equal(granted.Digest, new DisclosedScope("WH/SALES ", new[] { "dbo.customer", "DBO.SALES" }, false).Digest);
            Assert.NotEqual(granted.Digest, new DisclosedScope("wh/Sales", new[] { "dbo.Sales", "dbo.Customer" }, true).Digest);
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

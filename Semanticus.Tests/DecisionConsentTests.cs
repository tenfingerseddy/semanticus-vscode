// Consent: where it lives, what it covers, and what withdrawing it actually does.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DecisionConsentTests
    {
        [Fact]
        public async Task Consent_is_kept_OUTSIDE_the_model_files()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync();
                await f.ConsentAsync();

                var stored = DecisionConsentStore.FilePath();
                Assert.True(File.Exists(stored), "consent was not written");
                Assert.StartsWith(ConnectionRegistry.Root(), stored, StringComparison.Ordinal);

                // Nothing about consent may appear beside the model, because a model travels and a person's
                // permission does not.
                var sidecar = Path.Combine(f.Workspace, ".semanticus");
                if (Directory.Exists(sidecar))
                    Assert.Empty(Directory.EnumerateFiles(sidecar, "*consent*", SearchOption.AllDirectories));
            }
        }

        /// <summary>F-229 and F-231. A person who agreed under the version 2 card agreed to a list that sent the
        /// formula text and a card that did not mention typed questions. Version 3 changes both, so that
        /// permission stops counting and the card is shown once more. The model's marker is untouched by the
        /// bump: the new answer is kept under the SAME model, so a grant stays per model across the change.
        /// The stored record is written by hand at version 2 because this build refuses to grant at any
        /// version but its own, which is the point.</summary>
        [Fact]
        public async Task A_grant_under_the_version_2_card_asks_again_once_and_stays_with_the_same_model()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync();
                var first = await f.ConsentAsync();
                Assert.True(first.Current);
                var scope = first.AppliesTo;
                Assert.False(string.IsNullOrEmpty(scope));

                var versionTwoFields = new[]
                {
                    "ask.route:ask_text",
                    "measure.folder_and_format:measure_expression",
                    "measure.folder_and_format:measure_name",
                    "measure.folder_and_format:sibling_measures",
                    "measure.folder_and_format:table_columns",
                    "measure.folder_and_format:table_name",
                    "plan.rank:model_shape",
                    "plan.rank:plan_items",
                };
                var path = DecisionConsentStore.FilePath();
                var store = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
                var record = store["models"][scope];
                record["schemaVersion"] = 2;
                var list = new System.Text.Json.Nodes.JsonArray();
                foreach (var field in versionTwoFields) list.Add(field);
                record["approvedFields"] = list;
                File.WriteAllText(path, store.ToJsonString());

                var old = await f.Engine.GetConsentAsync();
                Assert.False(old.Current, "a grant made under the version 2 card still counts");
                Assert.Equal(2, old.SchemaVersion);
                Assert.Equal(DecisionRegistry.ConsentSchemaVersion, old.SchemaVersionInForce);
                Assert.Contains("has changed since you agreed", old.Reason, StringComparison.Ordinal);
                Assert.Equal(scope, old.AppliesTo);

                var again = await f.ConsentAsync();
                Assert.True(again.Current);
                Assert.Equal(scope, again.AppliesTo);
                Assert.Equal(DecisionRegistry.ConsentSchemaVersion, again.SchemaVersion);
            }
        }

        [Fact]
        public async Task Consent_round_trips_on_both_doors_and_carries_the_field_list_and_version()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync();

                var before = await f.Engine.GetConsentAsync();
                Assert.False(before.Granted);
                Assert.False(before.Current);
                Assert.Equal(DecisionRegistry.ConsentSchemaVersion, before.SchemaVersionInForce);
                Assert.NotEmpty(before.FieldsInForce);

                // The assistant's door agrees on the person's word in chat (1.4 chatyes): one call, no question, recorded as the assistant's.
                var granted = await McpTools.SetConsent(f.Engine, before.FieldsInForce, before.SchemaVersionInForce);
                Assert.False(granted.Asked);
                Assert.Equal("agent", granted.GrantedBy);
                Assert.True(granted.Current);
                Assert.Equal(before.FieldsInForce, granted.Fields);
                Assert.NotNull(granted.GrantedUtc);

                var rpc = new EngineRpcTarget(f.Engine);
                var seenByRpc = await rpc.getConsent();
                Assert.True(seenByRpc.Current);
                Assert.Equal(granted.Fields, seenByRpc.Fields);

                var withdrawn = await rpc.withdrawConsent();
                Assert.False(withdrawn.Granted);
                Assert.False(withdrawn.Current);
                Assert.NotNull(withdrawn.WithdrawnUtc);
                Assert.Contains("withdrew", withdrawn.Reason, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Agreeing_to_the_wrong_version_or_an_unknown_field_is_refused()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync();
                var fields = DecisionRegistry.DeclaredFields.ToArray();

                var wrongVersion = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => f.Engine.SetConsentAsync(fields, DecisionRegistry.ConsentSchemaVersion + 1, "human"));
                Assert.Contains("agree to the current list", wrongVersion.Message, StringComparison.Ordinal);

                var unknown = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => f.Engine.SetConsentAsync(new[] { "measure.folder_and_format:row_values" },
                                                   DecisionRegistry.ConsentSchemaVersion, "human"));
                Assert.Contains("Nothing in this product sends", unknown.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task A_partial_agreement_does_not_cover_a_point_that_sends_more()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();

                // Agree to every field EXCEPT one this point declares. Permission is checked per point
                // (DecisionConsent.Covers), so the field has to be one of THIS point's or the test proves
                // nothing. It used to drop the last field in the sorted list, which was this point's only
                // while the registry held one point; the moment a second point sorted last, the same line
                // left this point fully covered and the test passed for the wrong reason.
                var withheld = DecisionRegistry.MeasureFolderAndFormat + ":measure_formula_facts";
                var all = DecisionRegistry.DeclaredFields.ToArray();
                Assert.Contains(withheld, all);
                await f.Engine.SetConsentAsync(all.Where(x => x != withheld).ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");

                var oracle = f.UseEmptyRecorded();
                var proposal = await f.Engine.ProposeMeasureDefaultsAsync(measureRef);

                Assert.False(proposal.Asked);
                Assert.Contains("does not cover every detail", proposal.Reason, StringComparison.Ordinal);
                Assert.Empty(oracle.AskedDigests);
            }
        }

        // ---- permission belongs to ONE model (B1) ------------------------------------------------------

        [Fact]
        public async Task Permission_for_one_model_does_not_admit_another()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync("ModelA.bim");
                await f.ConsentAsync();
                var scopeA = await f.Engine.DecisionConsentScopeAsync();
                Assert.False(string.IsNullOrEmpty(scopeA), "a saved model has no identity to bind permission to");

                // A different model, saved somewhere else. Nothing was ever agreed for this one, and agreeing
                // on the first must not speak for it.
                var measureB = await f.BuildModelAsync("ModelB.bim");
                var scopeB = await f.Engine.DecisionConsentScopeAsync();
                Assert.NotEqual(scopeA, scopeB);

                var seenOnB = await f.Engine.GetConsentAsync();
                Assert.False(seenOnB.Granted);
                Assert.False(seenOnB.Current);

                var oracle = f.UseEmptyRecorded();
                var proposal = await f.Engine.ProposeMeasureDefaultsAsync(measureB);

                Assert.False(proposal.Asked);
                Assert.Contains("permission", proposal.Reason, StringComparison.Ordinal);
                Assert.Empty(oracle.AskedDigests);
                Assert.Equal(0, f.Engine.Decisions.RequestsStarted);

                // And the first model's permission is untouched by any of this.
                Assert.True(DecisionConsentStore.Read(scopeA).Granted, "the first model's permission was lost");
                Assert.False(DecisionConsentStore.Read(scopeB).Granted);
            }
        }

        [Fact]
        public async Task A_model_with_no_stable_identity_cannot_be_agreed_for()
        {
            using (var f = new DecisionFixture())
            {
                // Created, never saved, not connected. There is nothing to tell it apart from the next
                // brand-new model, so there is nothing a permission could be bound to.
                await f.Engine.CreateModelAsync("Unsaved", 1604);
                await f.Engine.CreateTableAsync("Sales", "human");
                await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
                var measureRef = await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");

                Assert.Null(await f.Engine.DecisionConsentScopeAsync());

                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => f.ConsentAsync());
                Assert.Contains("Save this model", refused.Message, StringComparison.Ordinal);

                var oracle = f.UseEmptyRecorded();
                var proposal = await f.Engine.ProposeMeasureDefaultsAsync(measureRef);
                Assert.False(proposal.Asked);
                Assert.Empty(oracle.AskedDigests);
            }
        }

        // ---- withdrawal cannot be raced (B2) -----------------------------------------------------------

        [Fact]
        public async Task Withdrawing_after_admission_and_before_the_send_stops_the_request()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();
                var oracle = f.UseEmptyRecorded();
                var service = f.Engine.Decisions;

                // The exact window Astra named: the gates have admitted this ask, and nothing has gone out yet.
                service.BeforeSendForTest = () => f.Engine.WithdrawConsentAsync("human").GetAwaiter().GetResult();

                var proposal = await f.Engine.ProposeMeasureDefaultsAsync(measureRef);

                Assert.False(proposal.Asked);
                Assert.Contains("withdrew permission", proposal.Reason, StringComparison.Ordinal);
                Assert.Empty(oracle.AskedDigests);
                Assert.Equal(0, service.RequestsStarted);
            }
        }

        [Fact]
        public async Task A_withdrawal_in_a_SECOND_engine_drops_a_reply_that_lands_here()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();
                // The marker travels in the model, so the second engine below only sees it once this one
                // has saved (EnsureConsentIdAsync says so in full).
                await f.SaveAsAsync("DecisionProof.bim");
                var scope = await f.Engine.DecisionConsentScopeAsync();

                var slow = new SlowOracle();
                f.Engine.UseDecisionsForTest(new DecisionService(slow));

                var asking = f.Engine.ProposeMeasureDefaultsAsync(measureRef);
                Assert.True(slow.Started.Wait(TimeSpan.FromSeconds(10)), "the ask never started");

                // A second engine on the same machine, with the same model open. It shares the consent file and
                // nothing else: this engine's own cancellation source is untouched, so only rereading the file
                // can catch the withdrawal.
                using (var otherSessions = new SessionManager())
                using (var other = new LocalEngine(otherSessions, TestEntitlements.Pro, f.Workspace))
                {
                    await other.OpenAsync(Path.Combine(f.Workspace, "DecisionProof.bim"));
                    Assert.Equal(scope, await other.DecisionConsentScopeAsync());
                    var withdrawn = await other.WithdrawConsentAsync("human");
                    Assert.False(withdrawn.Granted);
                }

                slow.ReplyAnyway();
                var proposal = await asking;

                Assert.False(proposal.Asked);
                Assert.Contains("withdrawn", proposal.Reason, StringComparison.Ordinal);
                Assert.Null(proposal.ProposedDisplayFolder);
                Assert.Null(proposal.ProposedFormatFamily);
            }
        }

        [Fact]
        public async Task Withdrawing_cancels_what_is_queued_and_drops_a_reply_that_lands_late()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();

                var slow = new SlowOracle();
                f.Engine.UseDecisionsForTest(new DecisionService(slow));

                var asking = f.Engine.ProposeMeasureDefaultsAsync(measureRef);
                Assert.True(slow.Started.Wait(TimeSpan.FromSeconds(10)), "the ask never started");

                await f.Engine.WithdrawConsentAsync("human");

                // The in-flight request was cancelled at the source.
                Assert.True(slow.Cancelled.Wait(TimeSpan.FromSeconds(10)), "withdrawing did not cancel the request");

                // And even if a reply squeaks through afterwards, it is thrown away rather than shown.
                slow.ReplyAnyway();
                var proposal = await asking;

                Assert.False(proposal.Asked);
                Assert.Contains("withdrawn", proposal.Reason, StringComparison.Ordinal);
                Assert.Null(proposal.ProposedDisplayFolder);
                Assert.Null(proposal.ProposedFormatFamily);
            }
        }

        [Fact]
        public async Task Nothing_is_sent_until_consent_is_current()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                var oracle = f.UseEmptyRecorded();

                var withoutConsent = await f.Engine.ProposeMeasureDefaultsAsync(measureRef);
                Assert.False(withoutConsent.Asked);
                Assert.Empty(oracle.AskedDigests);

                await f.ConsentAsync();
                await f.Engine.ProposeMeasureDefaultsAsync(measureRef);
                Assert.Single(oracle.AskedDigests);
            }
        }

        // ---- permission belongs to one model AT ONE MOMENT (F-177) --------------------------------------
        //
        // An address is only where a model sits. These four pin the witness that tells the model apart from
        // whatever occupies its address next, and the one thing the witness deliberately ignores.

        /// <summary>K19 (Kane, 8 Oct): permission lived only as an unsaved model annotation, so on a live model (nothing to save it
        /// into) a restart lost it and no Allow button came back. Permission is now kept on this machine under the model's address.
        /// Giving it is not a model edit, and the same address opened again, even from changed files, still has it.</summary>
        [Fact]
        public async Task K19_giving_permission_is_not_a_model_edit_and_survives_a_reopen()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync("Kept.bim");
                var undoBefore = await f.Sessions.Current.RunAsync(() => f.Sessions.Current.UndoStateNow().Steps);
                await f.ConsentAsync();

                Assert.Null(await f.Sessions.Current.ReadAsync(m => m.GetAnnotation(LocalEngine.ConsentIdAnnotation)));
                Assert.Equal(undoBefore, await f.Sessions.Current.RunAsync(() => f.Sessions.Current.UndoStateNow().Steps));

                // The files change under the same name (a restart onto a model that moved on), then open again.
                await f.Engine.CreateMeasureAsync("table:" + (await f.Sessions.Current.ReadAsync(m => m.Tables.First().Name)), "Added_After", "1", "human");
                await f.SaveAsAsync("Kept.bim");
                await f.Engine.OpenAsync(Path.Combine(f.Workspace, "Kept.bim"));

                Assert.True((await f.Engine.GetConsentAsync()).Current);
            }
        }

        [Fact]
        public async Task The_same_model_re_opened_at_the_same_address_keeps_its_permission()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync("Steady.bim");
                await f.ConsentAsync();
                await f.SaveAsAsync("Steady.bim");
                var before = await f.Engine.DecisionConsentScopeAsync();

                await f.Engine.OpenAsync(Path.Combine(f.Workspace, "Steady.bim"));

                Assert.Equal(before, await f.Engine.DecisionConsentScopeAsync());
                Assert.True((await f.Engine.GetConsentAsync()).Current);

                var oracle = f.UseEmptyRecorded();
                await f.Engine.ProposeMeasureDefaultsAsync(measureRef);
                Assert.Single(oracle.AskedDigests);
            }
        }

        [Fact]
        public async Task An_ordinary_edit_does_NOT_take_the_permission_away()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync("Working.bim");
                await f.ConsentAsync();
                var before = await f.Engine.DecisionConsentScopeAsync();

                // The deliberate insensitivity. The witness is the model's name and the shape of its table
                // names, not its counts, so a day's work does not keep asking the same question. Adding a
                // measure is the exact edit that precedes the point this permission exists for, and a witness
                // that moved here would ask for permission on almost every use.
                await f.Engine.CreateMeasureAsync("table:Sales", "Margin Percent", "DIVIDE ( 1, 2 )", "human");

                Assert.Equal(before, await f.Engine.DecisionConsentScopeAsync());
                Assert.True((await f.Engine.GetConsentAsync()).Current);
            }
        }

        [Fact]
        public async Task Withdrawing_clears_exactly_one_model()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync("First.bim");
                await f.ConsentAsync();
                var first = await f.Engine.DecisionConsentScopeAsync();

                await f.BuildModelAsync("Second.bim");
                await f.ConsentAsync();
                var second = await f.Engine.DecisionConsentScopeAsync();
                Assert.NotEqual(first, second);

                await f.Engine.WithdrawConsentAsync("human");

                Assert.False(DecisionConsentStore.Read(second).Granted);
                Assert.True(DecisionConsentStore.Read(first).Granted);

                // The second model's address now holds one record and it says no. An older witness left beside
                // it would be a grant nobody remembers giving, waiting for an undo to walk back into it.
                var identity = second;
                await f.Engine.OpenAsync(Path.Combine(f.Workspace, "Second.bim"));
                Assert.False((await f.Engine.GetConsentAsync()).Granted);
                Assert.False(DecisionConsentStore.Read(identity).Granted);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Astra_1_a_day_of_editing_keeps_the_permission()
        {
            using (var f = new DecisionFixture())
            {
                // The marker is stamped once and does not move, so ordinary work does not keep asking.
                // The two versions this replaced both failed here in one direction or the other: the
                // naming-style witness could not tell two models apart, and the names witness asked
                // again every time a column was added or renamed.
                await f.BuildModelAsync("Working.bim");
                await f.ConsentAsync();
                var before = await f.Engine.DecisionConsentScopeAsync();

                await f.Engine.CreateMeasureAsync("table:Sales", "Margin Percent", "DIVIDE ( 1, 2 )", "human");
                await f.Engine.CreateColumnAsync("table:Sales", "Region", "String", null, "human");
                await f.Engine.RenameObjectAsync("column:Sales/Amount", "Amount Sold", "human");
                await f.Engine.CreateTableAsync("Budget", "human");

                Assert.Equal(before, await f.Engine.DecisionConsentScopeAsync());
                Assert.True((await f.Engine.GetConsentAsync()).Current);
            }
        }

        [Fact]
        public async Task Astra_2_permission_meant_for_one_model_is_refused_when_another_is_open()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync("A.bim");
                var scopeA = await f.Engine.DecisionConsentScopeAsync();

                // Astra's walk, 2026-09-22: the card was opened on A and, before the person pressed OK,
                // the other door opened B. The card outlives that swap, so the answer has to carry which
                // model it was an answer FOR and the engine has to check it.
                await f.BuildModelAsync("B.bim");
                var scopeB = await f.Engine.DecisionConsentScopeAsync();
                Assert.NotEqual(scopeA, scopeB);

                var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => f.Engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(),
                        DecisionRegistry.ConsentSchemaVersion, "human", scopeA));
                Assert.Contains("not the model", refused.Message, StringComparison.Ordinal);

                // Neither model was granted anything. The refusal is a refusal, not a redirect.
                Assert.False(DecisionConsentStore.Read(scopeA).Granted);
                Assert.False(DecisionConsentStore.Read(scopeB).Granted);

                // The same answer, named for the model that IS open, is an ordinary grant. Granting marks
                // the model, so the scope it is STORED under carries the new marker rather than the
                // unstamped one the question was asked under; the answer says which.
                var granted = await f.Engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(),
                    DecisionRegistry.ConsentSchemaVersion, "human", scopeB);
                Assert.True(granted.Current);
                Assert.True(DecisionConsentStore.Read(granted.AppliesTo).Granted);
                Assert.Equal(granted.AppliesTo, await f.Engine.DecisionConsentScopeAsync());
                Assert.False(DecisionConsentStore.Read(scopeA).Granted);
            }
        }

        [Fact]
        public async Task Astra_2_an_answer_that_names_no_model_still_grants_for_the_open_one()
        {
            using (var f = new DecisionFixture())
            {
                // The agent door may leave it out, and the MCP description says so. Leaving it out is
                // the old behaviour exactly: agree for whatever is open. The check is opt-in evidence
                // that the answer was given about a particular model, not a new requirement.
                await f.BuildModelAsync("Plain.bim");
                var granted = await f.Engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(),
                    DecisionRegistry.ConsentSchemaVersion, "human");
                Assert.True(granted.Current);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Astra_3_a_folder_or_format_write_named_for_another_model_is_refused_by_the_engine()
        {
            using (var f = new DecisionFixture())
            {
                // The host checks before it writes, and that check has a window: the model can swap
                // between the check and the write. So the two setters take the same optional fence
                // set_dax has, and refuse inside the single-writer dispatch where no window is left.
                var measure = await f.BuildModelAsync("Fenced.bim");
                var stale = "a-session-that-is-not-this-one";

                var folder = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => f.Engine.SetDisplayFolderAsync(new[] { measure }, "KPIs", "human", stale));
                Assert.Contains("The model changed before this edit landed", folder.Message, StringComparison.Ordinal);

                var format = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => f.Engine.SetMeasureFormatAsync(measure, "0%", "human", stale));
                Assert.Contains("The model changed before this edit landed", format.Message, StringComparison.Ordinal);

                // Nothing was written by either refusal. Changed:true on the unfenced writes below is the
                // proof: a write that had already landed would come back Changed:false.
                Assert.True((await f.Engine.SetDisplayFolderAsync(new[] { measure }, "KPIs", "human")).Changed);
                Assert.True((await f.Engine.SetMeasureFormatAsync(measure, "0%", "human")).Changed);

                // And the fence passes when it names the session that really is open.
                var live = f.Sessions.Require().Id;
                Assert.False((await f.Engine.SetMeasureFormatAsync(measure, "0%", "human", live)).Changed);
            }
        }

        [Fact]
        public async Task Codex_1_the_marker_is_an_identity_and_never_a_permission()
        {
            using (var f = new DecisionFixture())
            {
                // The marker says WHICH model this is. It does not say anybody agreed to anything: a model
                // file carrying it, copied to another machine or committed to a repository, grants nothing
                // there, because the permission itself never leaves ~/.semanticus. That separation is the
                // founding rule of DecisionConsentStore and the marker must not quietly break it.
                await f.BuildModelAsync("Marker.bim");
                await f.ConsentAsync();
                var scope = await f.Engine.DecisionConsentScopeAsync();
                Assert.True(DecisionConsentStore.Read(scope).Granted);

                // Same model, same marker, a machine that has never seen it: no permission.
                var otherHome = Path.Combine(f.Home, "another-machine");
                var previous = ConnectionRegistry.RootOverride;
                try
                {
                    ConnectionRegistry.RootOverride = otherHome;
                    Assert.False(DecisionConsentStore.Read(scope).Granted);
                }
                finally { ConnectionRegistry.RootOverride = previous; }
            }
        }

        /// <summary>An oracle that parks until it is told to answer, so a test can act on the world while an ask
        /// is genuinely in flight. It answers "not decided" either way; what is under test is the cancellation
        /// and the late-reply drop, not the content.</summary>
        private sealed class SlowOracle : IDecisionOracle
        {
            private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public ManualResetEventSlim Started { get; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim Cancelled { get; } = new ManualResetEventSlim(false);

            public bool Available { get { return true; } }

            public void ReplyAnyway() => _release.TrySetResult(true);

            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                Started.Set();
                using (ct.Register(() => Cancelled.Set()))
                {
                    var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (ct.Register(() => cancelled.TrySetResult(true)))
                        await Task.WhenAny(_release.Task, cancelled.Task).ConfigureAwait(false);
                }
                return DecisionAnswers.NotDecided(request, "the slow oracle finished");
            }
        }
    }
}

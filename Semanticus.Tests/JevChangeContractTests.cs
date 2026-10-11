using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.Jev;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// M0a contract 2, the CHANGE CONTRACT (docs/redesign/contracts/change-contract.md).
    ///
    /// Astra's finding this pins: neither LocalEngine.MakeAiReadyAsync (LocalEngine.cs:11295, which mutates
    /// before it returns) nor LocalEngine.BuildModelFromSpecAsync (LocalEngine.cs:5196, which reports per-item
    /// errors and partial success) is an all-or-nothing preview-and-apply primitive. These tests describe the
    /// primitive both must move onto: propose writes nothing, accept is bound to the digest AND the versions,
    /// a repeated digest applies nothing a second time, a failure mid-apply rolls the whole batch back, and
    /// undo restores the declared boundary while the audit trail keeps its record that the work happened.
    /// </summary>
    public sealed class JevChangeContractTests
    {
        private static ChangeItem Rename(string target, string before, string after, string rule = "AIR-DESC-MISSING") =>
            new ChangeItem("set_description", target, before, after, new ChangeProvenance(ChangeItemKind.Rule, rule: rule));

        private static (InMemoryChangeTarget Target, InMemoryChangeSetService Service) Fixture()
        {
            var target = new InMemoryChangeTarget();
            target.Seed("measure:Sales/Margin", "");
            target.Seed("measure:Sales/Total", "");
            return (target, new InMemoryChangeSetService(target));
        }

        [Fact]
        public async Task Propose_collects_without_writing_and_stamps_the_versions_it_was_computed_against()
        {
            var (target, service) = Fixture();
            var revisionBefore = target.Revision;

            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                CatalogueVersion = "cat-1",
                Items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") },
                Refused = new[] { new ChangeRefusalItem { Target = "measure:Sales/Total", Reason = "It already has a description." } },
            });

            Assert.Equal(revisionBefore, target.Revision);
            Assert.Equal(0, target.ApplyCalls);
            Assert.Equal("", target["measure:Sales/Margin"]);
            Assert.Equal(revisionBefore, set.ModelRevision);
            Assert.Equal("src-1", set.SourceVersion);
            Assert.Equal("cat-1", set.CatalogueVersion);
            Assert.NotEmpty(set.Digest);
            // What propose deliberately left out is visible BEFORE the accept, not in a report afterwards.
            Assert.Single(set.Refused);
        }

        [Fact]
        public async Task Accept_applies_all_or_nothing_on_a_checkpoint_and_records_the_accepted_diff()
        {
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[]
                {
                    Rename("measure:Sales/Margin", "", "Margin as a share of sales."),
                    Rename("measure:Sales/Total", "", "Everything sold."),
                },
            });

            var result = await service.AcceptAsync("job-1", set.Digest);

            Assert.Equal(ChangeOutcome.Applied, result.Outcome);
            Assert.Equal(2, result.AppliedCount);
            Assert.NotNull(result.CheckpointId);
            Assert.Equal("Margin as a share of sales.", target["measure:Sales/Margin"]);
            var audit = Assert.Single(target.Audit);
            Assert.Equal(set.Digest, audit.Digest);
            Assert.Equal(2, audit.Items.Count);      // the accepted DIFF, not just a note that something happened
        }

        [Fact]
        public async Task A_double_submit_of_the_same_digest_applies_nothing_a_second_time()
        {
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") },
            });

            var first = await service.AcceptAsync("job-1", set.Digest);
            var appliesAfterFirst = target.ApplyCalls;
            var second = await service.AcceptAsync("job-1", set.Digest);

            Assert.Equal(ChangeOutcome.Applied, first.Outcome);
            Assert.Equal(ChangeOutcome.AlreadyApplied, second.Outcome);
            Assert.True(second.Ok);                                  // idempotent, not an error
            Assert.Equal(appliesAfterFirst, target.ApplyCalls);      // nothing ran twice
            Assert.Equal(first.CheckpointId, second.CheckpointId);
            Assert.Single(target.Audit);
        }

        [Fact]
        public async Task A_concurrent_edit_between_propose_and_accept_refuses_and_applies_nothing()
        {
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") },
            });

            target.ConcurrentEdit("measure:Sales/Total", "someone else typed this");
            var result = await service.AcceptAsync("job-1", set.Digest);

            Assert.Equal(ChangeOutcome.RefusedRevisionMoved, result.Outcome);
            Assert.Equal(0, target.ApplyCalls);
            Assert.Equal("", target["measure:Sales/Margin"]);
            Assert.Empty(target.Audit);
            Assert.Contains("nothing was applied", result.Message!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_moved_source_version_refuses_separately_and_says_which_one_moved()
        {
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") },
            });

            target.SourceVersion = "src-2";
            var result = await service.AcceptAsync("job-1", set.Digest);

            Assert.Equal(ChangeOutcome.RefusedSourceMoved, result.Outcome);
            Assert.Equal(0, target.ApplyCalls);
            Assert.Contains("source changed", result.Message!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task An_injected_failure_mid_apply_rolls_the_whole_batch_back_and_names_the_item()
        {
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[]
                {
                    Rename("measure:Sales/Margin", "", "Margin as a share of sales."),
                    Rename("measure:Sales/Total", "", "Everything sold."),
                    Rename("measure:Sales/Cost", "", "What it cost."),
                },
            });
            target.FailOn = item => item.Target == "measure:Sales/Total" ? "that object is locked" : null;

            var result = await service.AcceptAsync("job-1", set.Digest);

            Assert.Equal(ChangeOutcome.RolledBack, result.Outcome);
            Assert.Equal(0, result.AppliedCount);
            Assert.NotNull(result.Failure);
            Assert.Equal(1, result.Failure!.Index);
            Assert.Equal("measure:Sales/Total", result.Failure.Target);
            Assert.Equal("that object is locked", result.Failure.Message);
            // Nothing is left half applied: the first item is back to its before value.
            Assert.Equal("", target["measure:Sales/Margin"]);
            Assert.Null(target["measure:Sales/Cost"]);
            Assert.Empty(target.Audit);
        }

        [Fact]
        public async Task Undo_after_accept_restores_the_declared_boundary_and_keeps_the_audit_record()
        {
            var (target, service) = Fixture();
            target.Seed("measure:Sales/Untouched", "left alone");
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[]
                {
                    Rename("measure:Sales/Margin", "", "Margin as a share of sales."),
                    Rename("measure:Sales/Total", "", "Everything sold."),
                },
            });
            await service.AcceptAsync("job-1", set.Digest);

            await target.UndoAsync();

            // The declared boundary: the two targets the batch named, back to their before values.
            Assert.Equal("", target["measure:Sales/Margin"]);
            Assert.Equal("", target["measure:Sales/Total"]);
            // Outside the boundary, untouched.
            Assert.Equal("left alone", target["measure:Sales/Untouched"]);
            // The append-only trail legitimately records that the work happened. Undo does not erase it.
            Assert.Single(target.Audit);
        }

        [Fact]
        public async Task Cancel_changes_nothing_and_the_cancelled_digest_can_never_be_accepted_later()
        {
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") },
            });

            var cancel = await service.CancelAsync("job-1", set.Digest);
            var late = await service.AcceptAsync("job-1", set.Digest);

            Assert.Equal(ChangeOutcome.Cancelled, cancel.Outcome);
            Assert.Equal(0, target.ApplyCalls);
            Assert.Equal(ChangeOutcome.RefusedUnknownDigest, late.Outcome);
            Assert.Equal("", target["measure:Sales/Margin"]);
            Assert.Empty(target.Audit);
        }

        /// <summary>Codex on PR 384 (F-174): cancel used to drop the job and the digest independently, so a
        /// mismatched pair reported success, cancelled another job's proposal, and left the named job's real
        /// digest acceptable afterwards. A pair that does not name one open proposal is refused whole.</summary>
        [Fact]
        public async Task A_cancel_whose_digest_belongs_to_another_job_is_refused_and_cancels_neither()
        {
            var (target, service) = Fixture();
            var one = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") },
            });
            var two = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-2",
                Items = new[] { Rename("measure:Sales/Total", "", "Everything sold.") },
            });

            var cancel = await service.CancelAsync("job-1", two.Digest);

            Assert.NotEqual(ChangeOutcome.Cancelled, cancel.Outcome);
            Assert.Equal(0, target.ApplyCalls);
            // Neither proposal moved: both are still open, and the one named by the wrong digest is still
            // acceptable under its own job rather than silently unacceptable.
            Assert.NotNull(service.Open("job-1"));
            Assert.NotNull(service.Open("job-2"));
            Assert.Equal(one.Digest, service.Open("job-1").Digest);
            Assert.Equal(two.Digest, service.Open("job-2").Digest);
            Assert.Equal(ChangeOutcome.Applied, (await service.AcceptAsync("job-2", two.Digest)).Outcome);
        }

        [Fact]
        public async Task A_cancel_with_a_digest_that_is_not_the_jobs_own_leaves_that_job_acceptable()
        {
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") },
            });

            var cancel = await service.CancelAsync("job-1", "0000000000000000000000000000000000000000000000000000000000000000");

            Assert.NotEqual(ChangeOutcome.Cancelled, cancel.Outcome);
            Assert.Equal(ChangeOutcome.Applied, (await service.AcceptAsync("job-1", set.Digest)).Outcome);
            Assert.Equal("Margin as a share of sales.", target["measure:Sales/Margin"]);
        }

        [Fact]
        public async Task A_digest_that_was_never_proposed_is_refused()
        {
            var (target, service) = Fixture();
            var result = await service.AcceptAsync("job-1", "0000000000000000000000000000000000000000000000000000000000000000");

            Assert.Equal(ChangeOutcome.RefusedUnknownDigest, result.Outcome);
            Assert.Equal(0, target.ApplyCalls);
        }

        [Fact]
        public async Task A_digest_belonging_to_another_job_is_refused()
        {
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") },
            });

            var result = await service.AcceptAsync("job-2", set.Digest);

            Assert.Equal(ChangeOutcome.RefusedUnknownDigest, result.Outcome);
            Assert.Equal(0, target.ApplyCalls);
        }

        [Fact]
        public void The_digest_moves_when_the_items_or_the_versions_move()
        {
            var a = new ChangeSet("job-1", 1, "src-1", "cat-1", new[] { Rename("measure:A", "", "x") });
            var sameAgain = new ChangeSet("job-1", 1, "src-1", "cat-1", new[] { Rename("measure:A", "", "x") });
            var otherRevision = new ChangeSet("job-1", 2, "src-1", "cat-1", new[] { Rename("measure:A", "", "x") });
            var otherSource = new ChangeSet("job-1", 1, "src-2", "cat-1", new[] { Rename("measure:A", "", "x") });
            var otherItem = new ChangeSet("job-1", 1, "src-1", "cat-1", new[] { Rename("measure:A", "", "y") });

            Assert.Equal(a.Digest, sameAgain.Digest);
            Assert.NotEqual(a.Digest, otherRevision.Digest);
            Assert.NotEqual(a.Digest, otherSource.Digest);
            Assert.NotEqual(a.Digest, otherItem.Digest);
        }

        [Fact]
        public void A_proven_result_refuses_to_carry_a_confidence_number()
        {
            // Astra's third unnamed risk, as a type rule: the mockup attached Jev confidence to Author rows that
            // are proven validations. A provenance that would do that cannot be constructed.
            var ex = Assert.Throws<ArgumentException>(() =>
                new ChangeProvenance(ChangeItemKind.Proven, confidence: 0.91));
            Assert.Contains("never carries a confidence", ex.Message);

            // And the honest shapes still work.
            var proven = new ChangeProvenance(ChangeItemKind.Proven);
            Assert.Null(proven.Confidence);
            var decision = new ChangeProvenance(ChangeItemKind.Decision, point: "folder-for-new-measure", confidence: 0.91);
            Assert.Equal(0.91, decision.Confidence);
        }

        [Fact]
        public void Provenance_refuses_the_other_dishonest_shapes()
        {
            Assert.Throws<ArgumentException>(() => new ChangeProvenance(ChangeItemKind.Decision, point: null, confidence: 0.9));
            Assert.Throws<ArgumentException>(() => new ChangeProvenance(ChangeItemKind.Decision, point: "p", confidence: null));
            Assert.Throws<ArgumentException>(() => new ChangeProvenance(ChangeItemKind.Rule, rule: null));
            Assert.Throws<ArgumentException>(() => new ChangeProvenance(ChangeItemKind.Rule, rule: "r", confidence: 0.5));
            Assert.Throws<ArgumentException>(() => new ChangeProvenance(ChangeItemKind.Assistant, point: null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChangeProvenance(ChangeItemKind.Decision, point: "p", confidence: 1.5));
        }

        [Fact]
        public async Task Every_item_carries_which_point_which_confidence_and_which_rule()
        {
            var (_, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[]
                {
                    new ChangeItem("set_display_folder", "measure:Sales/Margin", "", "Internet Sales",
                        new ChangeProvenance(ChangeItemKind.Decision, point: "folder-for-new-measure", confidence: 0.62)),
                    new ChangeItem("set_description", "measure:Sales/Total", "", "Everything sold.",
                        new ChangeProvenance(ChangeItemKind.Assistant, point: "ar-4")),
                    Rename("measure:Sales/Cost", "", "What it cost."),
                },
            });

            Assert.Equal("folder-for-new-measure", set.Items[0].Provenance.Point);
            Assert.Equal(0.62, set.Items[0].Provenance.Confidence);
            Assert.Equal("ar-4", set.Items[1].Provenance.Point);
            Assert.Equal("AIR-DESC-MISSING", set.Items[2].Provenance.Rule);
            Assert.Null(set.Items[2].Provenance.Confidence);
        }

        // ---------------------------------------------------------------- Astra B3: accept is a transaction

        [Fact]
        public async Task Two_accepts_of_the_same_set_at_once_apply_it_once()
        {
            // Astra B3: the lock used to be released before the checkpoint, the writes and the audit, and
            // success was recorded only afterwards. Two accepts could both clear the checks before either
            // wrote. The whole batch, its version check and its receipt now sit inside one transaction.
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") },
            });

            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entries = 0;
            target.BeforeCheckpoint = async () =>
            {
                if (Interlocked.Increment(ref entries) == 1)
                {
                    reached.TrySetResult(true);
                    await release.Task;
                }
            };

            var first = Task.Run(() => service.AcceptAsync("job-1", set.Digest));
            await reached.Task;
            var second = Task.Run(() => service.AcceptAsync("job-1", set.Digest));
            // Inside the transaction the second accept waits and never reaches the checkpoint at all. The
            // bounded wait is the whole point: if it DOES get through, it applies a second time.
            await Task.WhenAny(second, Task.Delay(250));
            release.TrySetResult(true);
            var a = await first;
            var b = await second;

            Assert.Equal(1, target.ApplyCalls);
            Assert.Single(target.Audit);
            Assert.True(a.Ok && b.Ok);
            Assert.Equal(1, new[] { a, b }.Count(r => r.Outcome == ChangeOutcome.Applied));
            Assert.Equal(1, new[] { a, b }.Count(r => r.Outcome == ChangeOutcome.AlreadyApplied));
        }

        [Fact]
        public async Task An_edit_between_the_check_and_the_apply_refuses_before_any_write()
        {
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") },
            });
            // A second actor edits after the checks passed and while the checkpoint is being made.
            target.BeforeCheckpoint = () =>
            {
                target.ConcurrentEdit("measure:Sales/Total", "someone else typed this");
                return Task.CompletedTask;
            };

            var result = await service.AcceptAsync("job-1", set.Digest);

            Assert.Equal(ChangeOutcome.RefusedRevisionMoved, result.Outcome);
            Assert.Equal(0, target.ApplyCalls);
            Assert.Empty(target.Audit);
            Assert.Equal("", target["measure:Sales/Margin"]);
        }

        [Fact]
        public async Task A_cancel_between_the_check_and_the_apply_applies_nothing_and_says_cancelled()
        {
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") },
            });
            var cts = new CancellationTokenSource();
            target.BeforeCheckpoint = () => { cts.Cancel(); return Task.CompletedTask; };

            var result = await service.AcceptAsync("job-1", set.Digest, cts.Token);

            Assert.Equal(ChangeOutcome.Cancelled, result.Outcome);
            Assert.Equal(0, result.AppliedCount);
            Assert.Equal(0, target.ApplyCalls);
            Assert.Empty(target.Audit);
        }

        [Fact]
        public async Task A_cancel_during_the_apply_rolls_the_batch_back_and_never_leaves_it_half_applied()
        {
            // The contract's answer to "cancellation during apply": roll back, never half. The rollback runs on
            // a token of its own, so a caller who cancelled cannot also break the undo of their own batch.
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[]
                {
                    Rename("measure:Sales/Margin", "", "Margin as a share of sales."),
                    Rename("measure:Sales/Total", "", "Everything sold."),
                    Rename("measure:Sales/Cost", "", "What it cost."),
                },
            });
            var cts = new CancellationTokenSource();
            target.BeforeApply = item =>
            {
                if (item.Target == "measure:Sales/Total") cts.Cancel();
                return Task.CompletedTask;
            };

            var result = await service.AcceptAsync("job-1", set.Digest, cts.Token);

            Assert.Equal(ChangeOutcome.Cancelled, result.Outcome);
            Assert.Equal(0, result.AppliedCount);
            Assert.Equal(1, target.RollbackCalls);
            Assert.Equal("", target["measure:Sales/Margin"]);      // the first item did not survive
            Assert.Null(target["measure:Sales/Cost"]);
            Assert.Empty(target.Audit);
        }

        [Fact]
        public async Task An_audit_failure_rolls_the_whole_batch_back_rather_than_leaving_it_applied()
        {
            // Astra B3: an audit failure used to leave the batch applied with nothing on the trail to say so.
            // A write nobody can account for is worse than no write at all, so it goes back.
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[]
                {
                    Rename("measure:Sales/Margin", "", "Margin as a share of sales."),
                    Rename("measure:Sales/Total", "", "Everything sold."),
                },
            });
            target.FailAudit = _ => "the trail could not be written";

            var result = await service.AcceptAsync("job-1", set.Digest);

            Assert.Equal(ChangeOutcome.RolledBackAuditFailed, result.Outcome);
            Assert.False(result.Ok);
            Assert.Equal(0, result.AppliedCount);
            Assert.Equal(1, target.RollbackCalls);
            Assert.Equal("", target["measure:Sales/Margin"]);
            Assert.Equal("", target["measure:Sales/Total"]);
            Assert.Empty(target.Audit);
            // The receipt was never recorded either, so the next accept is a real attempt, not a false yes.
            var again = await service.AcceptAsync("job-1", set.Digest);
            Assert.NotEqual(ChangeOutcome.AlreadyApplied, again.Outcome);
        }

        // ---------------------------------------------------------------- Astra B4: stamp what was collected

        [Fact]
        public async Task Propose_stamps_the_versions_the_items_were_collected_against_not_todays()
        {
            // Astra B4: propose used to stamp the CURRENT revision onto items built earlier, so an old diff
            // sailed through accept's revision check and overwrote newer work.
            var (target, service) = Fixture();
            var collectedAtRevision = target.Revision;
            var collectedAtSource = target.SourceVersion;
            var items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") };

            // Someone else edits the very thing the items were computed from, after collection.
            target.ConcurrentEdit("measure:Sales/Margin", "someone else wrote this");

            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = items,
                CollectedAtRevision = collectedAtRevision,
                CollectedAtSourceVersion = collectedAtSource,
            });

            Assert.Equal(collectedAtRevision, set.ModelRevision);      // the OLD revision, not today's
            Assert.True(set.StaleAtProposal);
            Assert.Contains("changed", set.StaleReason, StringComparison.OrdinalIgnoreCase);

            var result = await service.AcceptAsync("job-1", set.Digest);

            Assert.Equal(ChangeOutcome.RefusedRevisionMoved, result.Outcome);
            Assert.Equal(0, target.ApplyCalls);
            Assert.Equal("someone else wrote this", target["measure:Sales/Margin"]);   // newer work survives
        }

        [Fact]
        public async Task A_set_collected_under_this_call_is_not_marked_stale()
        {
            var (target, service) = Fixture();
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") },
                CollectedAtRevision = target.Revision,
                CollectedAtSourceVersion = target.SourceVersion,
            });

            Assert.False(set.StaleAtProposal);
            Assert.Equal("", set.StaleReason);
            Assert.Equal(ChangeOutcome.Applied, (await service.AcceptAsync("job-1", set.Digest)).Outcome);
        }

        // ------------------------------------------------- the checkpoint promise outside a git repository

        [Fact]
        public async Task A_model_with_nowhere_to_checkpoint_is_refused_before_any_write()
        {
            // Accept promises a checkpoint. LocalEngine.HistoryCheckpoints.cs:54 refuses to make one outside a
            // git repository, so accept refuses too rather than writing with nothing to go back to. This stands
            // until Kane names a weaker promise.
            var (target, service) = Fixture();
            target.CanCheckpoint = false;
            target.CheckpointUnavailableReason =
                "This model is not inside a git repository, so there is no safe point to go back to.";
            var set = await service.ProposeAsync(new ChangeProposalRequest
            {
                JobId = "job-1",
                Items = new[] { Rename("measure:Sales/Margin", "", "Margin as a share of sales.") },
            });

            var result = await service.AcceptAsync("job-1", set.Digest);

            Assert.Equal(ChangeOutcome.RefusedNoCheckpoint, result.Outcome);
            Assert.False(result.Ok);
            Assert.Equal(0, target.ApplyCalls);
            Assert.Empty(target.Audit);
            Assert.Contains("git repository", result.Message!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Nothing was applied", result.Message!, StringComparison.OrdinalIgnoreCase);
        }
    }
}

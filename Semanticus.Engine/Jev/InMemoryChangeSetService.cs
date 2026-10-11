using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine.Jev
{
    /// <summary>
    /// The IN-MEMORY REFERENCE implementation of <see cref="IChangeSetService"/>. The rules live here; the later
    /// lane keeps them and replaces <see cref="IChangeTarget"/> with the real session, checkpoint and audit
    /// chain.
    ///
    /// ACCEPT IS ONE TRANSACTION (Astra B3). Propose, accept and cancel all run inside a single gate, so the
    /// version check, the checkpoint, every item write, the audit entry and the receipt cannot be interleaved
    /// with another accept, another proposal or a cancel. The old code released its lock before the checkpoint
    /// and recorded success only at the end, so two accepts could both clear the checks before either wrote.
    ///
    /// Order of checks on accept, and the order is load-bearing:
    ///   1. ALREADY APPLIED wins over everything. A repeated digest is idempotent even if the model has since
    ///      moved, because the honest answer to "did my change land?" is yes, not a refusal.
    ///   2. UNKNOWN DIGEST. Accepting something that was never proposed applies nothing.
    ///   3. REVISION MOVED, then SOURCE MOVED, each named separately so the sentence says which one.
    ///   4. CAN WE CHECKPOINT AT ALL. Accept promises a checkpoint, so a model with nowhere to put one is
    ///      refused BEFORE any write rather than written to without a way back.
    ///   5. A checkpoint, then the versions AGAIN (the checkpoint itself takes time on a real model), then the
    ///      items, all or nothing, then the audit entry, then the receipt.
    ///
    /// The two failure cases the contract now DEFINES rather than leaves open:
    ///   - AUDIT FAILURE rolls the whole batch back. A write nobody can account for is worse than no write.
    ///   - CANCELLATION rolls back, never half-applies. Once the first write has landed the recovery work runs
    ///     on CancellationToken.None, because a caller who cancelled must not also be able to break the undo of
    ///     their own batch. That was the old rollback's bug: it reused the caller's already-cancelled token.
    /// </summary>
    public sealed class InMemoryChangeSetService : IChangeSetService
    {
        private readonly object _lock = new object();
        /// <summary>The transaction gate. An async lock, because the whole batch is awaited inside it and a
        /// Monitor cannot be held across an await. Propose, accept and cancel all take it.</summary>
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly IChangeTarget _target;
        private readonly Dictionary<string, IChangeSet> _openByJob = new Dictionary<string, IChangeSet>(StringComparer.Ordinal);
        private readonly Dictionary<string, IChangeSet> _byDigest = new Dictionary<string, IChangeSet>(StringComparer.Ordinal);
        private readonly Dictionary<string, ChangeAcceptResult> _applied = new Dictionary<string, ChangeAcceptResult>(StringComparer.Ordinal);

        public InMemoryChangeSetService(IChangeTarget target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
        }

        /// <summary>
        /// PROPOSE WRITES NOTHING. It stamps the set with the versions the items were COLLECTED against and
        /// returns the set with its digest.
        ///
        /// Astra B4: this used to stamp whatever the model read at proposal time, which meant a list of items
        /// built before someone else's edit came back wearing today's revision and then sailed through accept's
        /// revision check, overwriting the newer work. Propose cannot tell a fresh collection from an old list
        /// by looking at it, so the caller declares what it collected against
        /// (<see cref="ChangeProposalRequest.CollectedAtRevision"/>). The declared versions are what get
        /// stamped, the digest therefore covers them, and accept refuses the set for the right reason. The set
        /// is also marked <see cref="IChangeSet.StaleAtProposal"/> so a door can say so straight away instead
        /// of letting a person read a diff that can never be applied.
        /// </summary>
        public async Task<IChangeSet> ProposeAsync(ChangeProposalRequest request, CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                long revision;
                string source;
                lock (_lock) { revision = _target.Revision; source = _target.SourceVersion; }

                var stampedRevision = request.CollectedAtRevision ?? revision;
                var stampedSource = request.CollectedAtSourceVersion ?? source;

                var staleReason = "";
                if (stampedRevision != revision)
                    staleReason = "The model changed after these changes were worked out, so they cannot be applied. Work them out again.";
                else if (!string.Equals(stampedSource, source, StringComparison.Ordinal))
                    staleReason = "The source changed after these changes were worked out, so they cannot be applied. Work them out again.";

                var set = new ChangeSet(request.JobId, stampedRevision, stampedSource,
                    request.CatalogueVersion, request.Items, request.Refused, staleReason);
                lock (_lock)
                {
                    _openByJob[request.JobId ?? ""] = set;
                    _byDigest[set.Digest] = set;
                }
                return set;
            }
            finally { _gate.Release(); }
        }

        public async Task<ChangeAcceptResult> AcceptAsync(string jobId, string digest, CancellationToken ct = default)
        {
            try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return CancelledBeforeAnyWrite(digest); }

            try
            {
                IChangeSet set;
                lock (_lock)
                {
                    if (_applied.TryGetValue(digest ?? "", out var already))
                        return new ChangeAcceptResult
                        {
                            Outcome = ChangeOutcome.AlreadyApplied,
                            Digest = already.Digest,
                            CheckpointId = already.CheckpointId,
                            AuditHash = already.AuditHash,
                            AppliedCount = already.AppliedCount,
                            Message = "These changes were already applied. Nothing was applied a second time.",
                        };

                    if (!_byDigest.TryGetValue(digest ?? "", out set))
                        return new ChangeAcceptResult
                        {
                            Outcome = ChangeOutcome.RefusedUnknownDigest,
                            Digest = digest,
                            Message = "That set of changes was never proposed, so nothing was applied. Propose again and accept what you see.",
                        };

                    if (!string.Equals(set.JobId ?? "", jobId ?? "", StringComparison.Ordinal))
                        return new ChangeAcceptResult
                        {
                            Outcome = ChangeOutcome.RefusedUnknownDigest,
                            Digest = digest,
                            Message = "Those changes belong to a different job, so nothing was applied.",
                        };

                    var moved = VersionRefusal(set, digest);
                    if (moved != null) return moved;

                    // Accept PROMISES a checkpoint. If the engine cannot make one for this model, refuse here,
                    // before any write, rather than writing with nothing to go back to.
                    if (!_target.CanCheckpoint)
                        return new ChangeAcceptResult
                        {
                            Outcome = ChangeOutcome.RefusedNoCheckpoint,
                            Digest = digest,
                            Message = (string.IsNullOrWhiteSpace(_target.CheckpointUnavailableReason)
                                          ? "There is nowhere to make a safe point to go back to."
                                          : _target.CheckpointUnavailableReason.Trim())
                                      + " Nothing was applied. Save the model inside a git repository first.",
                        };
                }

                if (ct.IsCancellationRequested) return CancelledBeforeAnyWrite(digest);

                // The checkpoint comes BEFORE the first write, so there is always something to roll back to.
                var checkpoint = await _target.CreateCheckpointAsync(
                    $"Before accepting {set.Items.Count} change(s) for {set.JobId}", ct).ConfigureAwait(false);

                // Making a checkpoint is real work on a real model, so the versions are read AGAIN before the
                // first write. Nothing has been written yet, so a refusal here still applies nothing.
                lock (_lock)
                {
                    var movedDuringCheckpoint = VersionRefusal(set, digest);
                    if (movedDuringCheckpoint != null)
                    {
                        movedDuringCheckpoint.CheckpointId = checkpoint;
                        return movedDuringCheckpoint;
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    var cancelled = CancelledBeforeAnyWrite(digest);
                    cancelled.CheckpointId = checkpoint;
                    return cancelled;
                }

                var applied = 0;
                for (var i = 0; i < set.Items.Count; i++)
                {
                    try
                    {
                        await _target.ApplyAsync(set.Items[i], ct).ConfigureAwait(false);
                        applied++;
                    }
                    catch (OperationCanceledException)
                    {
                        // Roll back, never half. The undo runs uncancellable on purpose.
                        await RecoverAsync(checkpoint).ConfigureAwait(false);
                        return new ChangeAcceptResult
                        {
                            Outcome = ChangeOutcome.Cancelled,
                            Digest = digest,
                            CheckpointId = checkpoint,
                            AppliedCount = 0,
                            Message = "The changes were stopped part way through, so the whole set was put back. Nothing was left half applied.",
                        };
                    }
                    catch (Exception ex)
                    {
                        await RecoverAsync(checkpoint).ConfigureAwait(false);
                        return new ChangeAcceptResult
                        {
                            Outcome = ChangeOutcome.RolledBack,
                            Digest = digest,
                            CheckpointId = checkpoint,
                            AppliedCount = 0,
                            Failure = new ChangeFailure { Index = i, Target = set.Items[i].Target, Message = ex.Message },
                            Message = $"Change {i + 1} of {set.Items.Count} on {set.Items[i].Target} failed, so the whole set was rolled back. Nothing was left half applied.",
                        };
                    }
                }

                string auditHash;
                try
                {
                    // The audit entry is part of the batch, not a note afterwards, so it runs uncancellable:
                    // the writes have landed and the trail must record them or they must go away.
                    auditHash = await _target.AppendAuditAsync(new ChangeAuditEntry
                    {
                        JobId = set.JobId,
                        Digest = set.Digest,
                        CheckpointId = checkpoint,
                        ItemCount = set.Items.Count,
                        Summary = $"Accepted {set.Items.Count} change(s).",
                        Items = set.Items,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await RecoverAsync(checkpoint).ConfigureAwait(false);
                    return new ChangeAcceptResult
                    {
                        Outcome = ChangeOutcome.RolledBackAuditFailed,
                        Digest = digest,
                        CheckpointId = checkpoint,
                        AppliedCount = 0,
                        Failure = new ChangeFailure { Index = -1, Target = "the audit trail", Message = ex.Message },
                        Message = "The changes were applied but the record of them could not be written, so the whole set was put back. Nothing was changed.",
                    };
                }

                var result = new ChangeAcceptResult
                {
                    Outcome = ChangeOutcome.Applied,
                    Digest = set.Digest,
                    CheckpointId = checkpoint,
                    AuditHash = auditHash,
                    AppliedCount = applied,
                    Message = $"Applied {applied} change(s) on a checkpoint.",
                };
                lock (_lock)
                {
                    _applied[set.Digest] = result;
                    _openByJob.Remove(set.JobId ?? "");
                }
                return result;
            }
            finally { _gate.Release(); }
        }

        /// <summary>CANCEL CHANGES NOTHING. It drops the proposal so the digest can never be accepted later.
        /// It takes the transaction gate too, so it can never land in the middle of an accept and then claim
        /// nothing was applied when a batch was already going in.</summary>
        public async Task<ChangeCancelResult> CancelAsync(string jobId, string digest, CancellationToken ct = default)
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock (_lock)
                {
                    if (_applied.ContainsKey(digest ?? ""))
                        return new ChangeCancelResult
                        {
                            Outcome = ChangeOutcome.AlreadyApplied,
                            Message = "These changes were already applied, so there was nothing to cancel. Undo them instead.",
                        };
                    // The PAIR has to name ONE open proposal. Dropping the job and the digest independently
                    // reported success for a mismatched pair: it cancelled whatever job the digest belonged
                    // to, and left the named job's real digest acceptable afterwards. (Codex on PR 384.)
                    IChangeSet open;
                    if (!_openByJob.TryGetValue(jobId ?? "", out open) || open == null)
                        return new ChangeCancelResult
                        {
                            Outcome = ChangeOutcome.RefusedUnknownDigest,
                            Message = "There are no changes waiting for that job, so nothing was cancelled.",
                        };

                    if (!string.Equals(open.Digest ?? "", digest ?? "", StringComparison.Ordinal))
                        return new ChangeCancelResult
                        {
                            Outcome = ChangeOutcome.RefusedUnknownDigest,
                            Message = "Those are not the changes waiting for that job, so nothing was cancelled.",
                        };

                    _byDigest.Remove(open.Digest ?? "");
                    _openByJob.Remove(jobId ?? "");
                    return new ChangeCancelResult
                    {
                        Outcome = ChangeOutcome.Cancelled,
                        Message = "Nothing was changed.",
                    };
                }
            }
            finally { _gate.Release(); }
        }

        /// <summary>The revision and source checks, in one place, so the pre-checkpoint and post-checkpoint
        /// reads cannot drift apart. Null means nothing moved. Call under <c>_lock</c>.</summary>
        private ChangeAcceptResult VersionRefusal(IChangeSet set, string digest)
        {
            if (set.ModelRevision != _target.Revision)
                return new ChangeAcceptResult
                {
                    Outcome = ChangeOutcome.RefusedRevisionMoved,
                    Digest = digest,
                    Message = "The model changed since these changes were worked out, so nothing was applied. Look at them again.",
                };
            if (!string.Equals(set.SourceVersion, _target.SourceVersion, StringComparison.Ordinal))
                return new ChangeAcceptResult
                {
                    Outcome = ChangeOutcome.RefusedSourceMoved,
                    Digest = digest,
                    Message = "The source changed since these changes were worked out, so nothing was applied. Look at them again.",
                };
            return null;
        }

        private static ChangeAcceptResult CancelledBeforeAnyWrite(string digest) => new ChangeAcceptResult
        {
            Outcome = ChangeOutcome.Cancelled,
            Digest = digest,
            AppliedCount = 0,
            Message = "The changes were stopped before anything was written. Nothing was applied.",
        };

        /// <summary>Put the batch back. Uncancellable by design: the caller's token may be exactly what stopped
        /// the batch, and a recovery that a cancelled token can break is not a recovery.</summary>
        private Task RecoverAsync(string checkpoint) =>
            _target.RollbackToAsync(checkpoint, CancellationToken.None);

        /// <summary>The open proposal for a job, for a door that wants to re-render the diff.</summary>
        public IChangeSet Open(string jobId)
        {
            lock (_lock) { return _openByJob.TryGetValue(jobId ?? "", out var s) ? s : null; }
        }
    }

    /// <summary>
    /// A dictionary standing in for the model, so the contract's failure cases can be tested without TOM: a
    /// concurrent edit is a revision bump, an injected failure is a predicate, and undo restores the DECLARED
    /// BOUNDARY, which is the set of targets the accepted batch named and nothing else.
    /// </summary>
    public sealed class InMemoryChangeTarget : IChangeTarget
    {
        private readonly Dictionary<string, string> _state = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _checkpoints = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private readonly List<ChangeItem> _lastBatch = new List<ChangeItem>();
        private long _revision;
        private int _checkpointSeq;

        public long Revision => _revision;
        public string SourceVersion { get; set; } = "src-1";

        /// <summary>Test seam for the git-repository case: set false to stand in for a model that has no
        /// repository to checkpoint into.</summary>
        public bool CanCheckpoint { get; set; } = true;

        public string CheckpointUnavailableReason { get; set; } =
            "This model is not inside a git repository, so there is no safe point to go back to.";

        /// <summary>Test seam: runs at the START of CreateCheckpointAsync, before the snapshot. A test uses it
        /// to block one accept while another runs, or to slip an edit in between the check and the apply.</summary>
        public Func<Task> BeforeCheckpoint { get; set; }

        /// <summary>Test seam: runs before each item is applied, so a test can cancel mid-batch.</summary>
        public Func<ChangeItem, Task> BeforeApply { get; set; }

        /// <summary>Test seam: return a message to make the audit append throw. It throws BEFORE the entry is
        /// added, so a failed append leaves the trail clean.</summary>
        public Func<ChangeAuditEntry, string> FailAudit { get; set; }

        /// <summary>How many rollbacks ran, so a test can prove the batch was undone rather than left.</summary>
        public int RollbackCalls { get; private set; }

        /// <summary>Everything ever appended. Append-only on purpose: undo does not remove the record that the
        /// work happened.</summary>
        public List<ChangeAuditEntry> Audit { get; } = new List<ChangeAuditEntry>();

        /// <summary>Injected failure: return a message to make this item throw, or null to let it through.</summary>
        public Func<ChangeItem, string> FailOn { get; set; }

        /// <summary>How many item applies actually ran, so a test can prove a second accept applied nothing.</summary>
        public int ApplyCalls { get; private set; }

        public string this[string target] => _state.TryGetValue(target, out var v) ? v : null;

        public void Seed(string target, string value)
        {
            _state[target] = value;
            _revision++;
        }

        /// <summary>A second actor editing the model between propose and accept.</summary>
        public void ConcurrentEdit(string target, string value)
        {
            _state[target] = value;
            _revision++;
        }

        public async Task<string> CreateCheckpointAsync(string label, CancellationToken ct = default)
        {
            var hook = BeforeCheckpoint;
            if (hook != null) await hook().ConfigureAwait(false);
            var id = "cp-" + (++_checkpointSeq);
            _checkpoints[id] = new Dictionary<string, string>(_state, StringComparer.Ordinal);
            return id;
        }

        public async Task ApplyAsync(ChangeItem item, CancellationToken ct = default)
        {
            var hook = BeforeApply;
            if (hook != null) await hook(item).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            ApplyCalls++;
            var fail = FailOn?.Invoke(item);
            if (fail != null) throw new InvalidOperationException(fail);
            _state[item.Target] = item.After;
            _revision++;
        }

        public Task RollbackToAsync(string checkpointId, CancellationToken ct = default)
        {
            RollbackCalls++;
            if (!_checkpoints.TryGetValue(checkpointId ?? "", out var snap))
                throw new InvalidOperationException($"No checkpoint named '{checkpointId}'.");
            _state.Clear();
            foreach (var kv in snap) _state[kv.Key] = kv.Value;
            _revision++;
            return Task.CompletedTask;
        }

        public Task<string> AppendAuditAsync(ChangeAuditEntry entry, CancellationToken ct = default)
        {
            var fail = FailAudit?.Invoke(entry);
            if (fail != null) throw new InvalidOperationException(fail);
            Audit.Add(entry);
            _lastBatch.Clear();
            _lastBatch.AddRange(entry.Items ?? Array.Empty<ChangeItem>());
            using (var sha = SHA256.Create())
            {
                var prev = Audit.Count > 1 ? Audit.Count - 1 : 0;
                var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                    entry.Digest + ":" + entry.ItemCount + ":" + prev))).ToLowerInvariant();
                return Task.FromResult(hash);
            }
        }

        /// <summary>
        /// Undo the last accepted batch. THE DECLARED BOUNDARY is the targets that batch named: each goes back
        /// to its Before value. Anything a second actor changed outside the batch is left alone, and the audit
        /// trail keeps its entry, because the work did happen.
        /// </summary>
        public Task UndoAsync(CancellationToken ct = default)
        {
            foreach (var item in _lastBatch)
            {
                if (item.Before == null) _state.Remove(item.Target);
                else _state[item.Target] = item.Before;
            }
            _lastBatch.Clear();
            _revision++;
            return Task.CompletedTask;
        }
    }
}

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
    /// M0a contract 2: THE CHANGE CONTRACT (docs/redesign/contracts/change-contract.md).
    ///
    /// Advice never writes. Build and Fix write through this one primitive: collect without writing, show the
    /// exact diff, bind acceptance to that diff AND to the model and source versions it was computed against,
    /// apply the accepted set ONCE on a checkpoint, all or nothing, and refuse if anything moved.
    ///
    /// Neither existing write path is this primitive today, which is why it is new engine work:
    ///   - LocalEngine.MakeAiReadyAsync (LocalEngine.cs:11295) MUTATES first and analyses afterwards;
    ///   - LocalEngine.BuildModelFromSpecAsync (LocalEngine.cs:5196) runs inside one MutateAsync but SWALLOWS
    ///     per-item failures into an errors array and returns partial success.
    /// The migration for both is in the design note, and it is a note, not a migration.
    /// </summary>
    public enum ChangeItemKind
    {
        /// <summary>A deterministic rule produced this item. Carries a rule id and no confidence.</summary>
        Rule,
        /// <summary>A decision point produced this item. Carries a point id and a confidence.</summary>
        Decision,
        /// <summary>The assistant supplied the words. Carries the request id in the point field.</summary>
        Assistant,
        /// <summary>A proven result: a validation, an equivalence check, a probe. CARRIES NO CONFIDENCE, ever.</summary>
        Proven,
    }

    /// <summary>
    /// Per-item provenance: which decision point, which confidence, which rule. The constructor REFUSES the
    /// combinations that would make the diff dishonest, so a bad provenance cannot exist rather than being
    /// caught by a reviewer later.
    /// </summary>
    public sealed class ChangeProvenance
    {
        public ChangeProvenance(ChangeItemKind kind, string point = null, string rule = null, double? confidence = null)
        {
            switch (kind)
            {
                case ChangeItemKind.Proven when confidence.HasValue:
                    throw new ArgumentException(
                        "A proven result never carries a confidence number. Drop the confidence or change the kind.", nameof(confidence));
                case ChangeItemKind.Decision when string.IsNullOrWhiteSpace(point):
                    throw new ArgumentException("A decision item must name the decision point that produced it.", nameof(point));
                case ChangeItemKind.Decision when !confidence.HasValue:
                    throw new ArgumentException("A decision item must carry the confidence it was produced at.", nameof(confidence));
                case ChangeItemKind.Rule when string.IsNullOrWhiteSpace(rule):
                    throw new ArgumentException("A rule item must name the rule that produced it.", nameof(rule));
                case ChangeItemKind.Rule when confidence.HasValue:
                    throw new ArgumentException("A rule is deterministic, so it carries no confidence.", nameof(confidence));
                case ChangeItemKind.Assistant when string.IsNullOrWhiteSpace(point):
                    throw new ArgumentException("An assistant item must name the request it came from.", nameof(point));
            }
            if (confidence.HasValue && (confidence.Value < 0 || confidence.Value > 1))
                throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence is between 0 and 1.");

            Kind = kind;
            Point = point;
            Rule = rule;
            Confidence = confidence;
        }

        public ChangeItemKind Kind { get; }
        public string Point { get; }
        public string Rule { get; }
        public double? Confidence { get; }

        internal string Canonical() =>
            $"{Kind}|{Point}|{Rule}|{(Confidence.HasValue ? Confidence.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "")}";
    }

    /// <summary>One change, with the exact before and after a person reviews.</summary>
    public sealed class ChangeItem
    {
        public ChangeItem(string op, string target, string before, string after, ChangeProvenance provenance)
        {
            Op = op ?? throw new ArgumentNullException(nameof(op));
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Before = before;
            After = after;
            Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        }

        public string Op { get; }
        public string Target { get; }
        public string Before { get; }
        public string After { get; }
        public ChangeProvenance Provenance { get; }

        internal string Canonical() => $"{Op}\u001f{Target}\u001f{Before}\u001f{After}\u001f{Provenance.Canonical()}";
    }

    /// <summary>Something propose looked at and deliberately left OUT, with the reason. This is where
    /// build_model_from_spec's silent Skipped list and its per-item Errors go: a person sees them BEFORE
    /// accepting, instead of reading them in a report after the write.</summary>
    public sealed class ChangeRefusalItem
    {
        public string Target { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>The proposed set. Immutable: the digest is only honest if nothing can be added after it is
    /// computed.</summary>
    public interface IChangeSet
    {
        string JobId { get; }
        string Digest { get; }
        long ModelRevision { get; }
        string SourceVersion { get; }
        string CatalogueVersion { get; }

        /// <summary>WHICH WORLD this set belongs to: the model it was worked out against and the session it
        /// was worked out in, as one opaque string. It is hashed into the digest, so a digest is only ever
        /// accepted by the thing that produced it.
        ///
        /// It exists because the digest used to cover the items and the versions and nothing else, not even
        /// the job id. Two identical copies of one model, open at the same revision with the same first
        /// change on each, produced the SAME digest, and a digest read from one would then approve the work
        /// on the other. A revision counter is per session and an ordinary file length and write time are
        /// not an identity, so no combination of the old fields could tell the two apart. Empty is legal and
        /// means "no world was declared", which is the reference implementation's case and nothing else.</summary>
        string Scope { get; }

        IReadOnlyList<ChangeItem> Items { get; }
        IReadOnlyList<ChangeRefusalItem> Refused { get; }
        /// <summary>True when the caller DECLARED the versions it collected these items against and those
        /// versions are not the ones the model carries now. The set is still returned, stamped with the OLD
        /// versions, so the digest stays honest and accept refuses it. A door shows this immediately rather
        /// than letting a person read a diff that can never be applied.</summary>
        bool StaleAtProposal { get; }
        /// <summary>Plain words for <see cref="StaleAtProposal"/>. Empty when the set is current.</summary>
        string StaleReason { get; }
    }

    public sealed class ChangeSet : IChangeSet
    {
        public ChangeSet(string jobId, long modelRevision, string sourceVersion, string catalogueVersion,
            IEnumerable<ChangeItem> items, IEnumerable<ChangeRefusalItem> refused = null,
            string staleReason = null, string scope = null)
        {
            JobId = jobId;
            ModelRevision = modelRevision;
            SourceVersion = sourceVersion ?? "";
            CatalogueVersion = catalogueVersion ?? "";
            Scope = scope ?? "";
            Items = (items ?? Array.Empty<ChangeItem>()).ToArray();
            Refused = (refused ?? Array.Empty<ChangeRefusalItem>()).ToArray();
            StaleReason = staleReason ?? "";
            StaleAtProposal = !string.IsNullOrEmpty(StaleReason);
            Digest = ComputeDigest(this);
        }

        public string JobId { get; }
        public string Digest { get; }
        public long ModelRevision { get; }
        public string SourceVersion { get; }
        public string CatalogueVersion { get; }
        public string Scope { get; }
        public IReadOnlyList<ChangeItem> Items { get; }
        public IReadOnlyList<ChangeRefusalItem> Refused { get; }
        public bool StaleAtProposal { get; }
        public string StaleReason { get; }

        /// <summary>
        /// The digest covers WHO the set belongs to, WHAT it would change and WHAT it was computed against,
        /// in that order. So a re-propose after any edit produces a different digest, and an accept quoting
        /// the old digest is refused without the service having to remember why.
        ///
        /// SCOPE AND JOB ARE IN THE HASH, and they were not always. The digest used to cover the items, the
        /// revision, the source and the catalogue, which are all facts about CONTENT. Nothing in that list
        /// says which model or which session the content belongs to, so two identical copies of one model at
        /// the same revision with the same first change on each hashed to the same digest, and a digest read
        /// from one approved the work on the other. Identity is not derivable from content, so it has to be
        /// declared and hashed with it.
        /// </summary>
        public static string ComputeDigest(IChangeSet set)
        {
            var sb = new StringBuilder();
            sb.Append(set.Scope).Append('\u001e')
              .Append(set.JobId).Append('\u001e')
              .Append(set.ModelRevision).Append('\u001e')
              .Append(set.SourceVersion).Append('\u001e')
              .Append(set.CatalogueVersion).Append('\u001e');
            foreach (var i in set.Items) sb.Append(i.Canonical()).Append('\u001e');
            using (var sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        }
    }

    public enum ChangeOutcome
    {
        Applied,
        /// <summary>The same digest was already applied. Nothing was applied a second time.</summary>
        AlreadyApplied,
        RefusedRevisionMoved,
        RefusedSourceMoved,
        RefusedUnknownDigest,
        RefusedStaleSession,
        /// <summary>The model is somewhere the engine cannot make a checkpoint, so there would be nothing to go
        /// back to. Refused BEFORE any write.</summary>
        RefusedNoCheckpoint,
        /// <summary>Every item applied, then the audit entry could not be written. The batch was rolled back,
        /// because a write nobody can account for is worse than no write at all.</summary>
        RolledBackAuditFailed,
        /// <summary>An item failed mid-apply. The batch was rolled back and the failing item is named.</summary>
        RolledBack,
        /// <summary>Cancel: nothing was applied and the proposal was dropped.</summary>
        Cancelled,
    }

    public sealed class ChangeFailure
    {
        public int Index { get; set; }
        public string Target { get; set; }
        public string Message { get; set; }
    }

    public sealed class ChangeAcceptResult
    {
        public ChangeOutcome Outcome { get; set; }
        public string Digest { get; set; }
        public string CheckpointId { get; set; }
        public string AuditHash { get; set; }
        public int AppliedCount { get; set; }
        public ChangeFailure Failure { get; set; }
        public string Message { get; set; }
        public bool Ok => Outcome == ChangeOutcome.Applied || Outcome == ChangeOutcome.AlreadyApplied;
    }

    public sealed class ChangeCancelResult
    {
        public ChangeOutcome Outcome { get; set; }
        public string Message { get; set; }
    }

    public sealed class ChangeProposalRequest
    {
        public string JobId { get; set; }
        public IReadOnlyList<ChangeItem> Items { get; set; } = Array.Empty<ChangeItem>();
        public IReadOnlyList<ChangeRefusalItem> Refused { get; set; } = Array.Empty<ChangeRefusalItem>();
        public string CatalogueVersion { get; set; } = "";

        /// <summary>
        /// The model revision the caller ACTUALLY collected these items against. Astra B4: propose used to
        /// stamp whatever the model reads at proposal time, so items computed before someone else's edit came
        /// back wearing today's revision and then passed accept's revision check. A collector that reads the
        /// model and a collector that hands over a list it built earlier are indistinguishable from inside
        /// propose, so the caller declares it. Null means "I collected these just now, under this call".
        /// </summary>
        public long? CollectedAtRevision { get; set; }

        /// <summary>The source version the caller collected against. Null means "just now". See
        /// <see cref="CollectedAtRevision"/>.</summary>
        public string CollectedAtSourceVersion { get; set; }
    }

    /// <summary>One entry on the append-only audit trail. The engine stamps the rest.</summary>
    public sealed class ChangeAuditEntry
    {
        public string JobId { get; set; }
        public string Digest { get; set; }
        public string CheckpointId { get; set; }
        public int ItemCount { get; set; }
        public string Summary { get; set; }
        /// <summary>The exact accepted diff, so the trail records what was applied and not merely that
        /// something was.</summary>
        public IReadOnlyList<ChangeItem> Items { get; set; } = Array.Empty<ChangeItem>();
    }

    /// <summary>
    /// The seam onto the real model. The later lane implements this over Session.MutateAsync, the history
    /// checkpoint (LocalEngine.HistoryCheckpoints.cs) and the verified-edits chain (VerifiedEdits.cs); the
    /// reference implementation here is a dictionary, so a test can move a revision or fail an item without TOM.
    /// </summary>
    public interface IChangeTarget
    {
        long Revision { get; }
        string SourceVersion { get; }

        /// <summary>Whether a durable checkpoint can be made at all. False for a model that is not inside a git
        /// repository: LocalEngine.HistoryCheckpoints.cs:54 already refuses that checkpoint with
        /// "Open or save a model inside a git repository first." Accept promises a checkpoint, so it refuses
        /// BEFORE writing rather than writing without one.</summary>
        bool CanCheckpoint { get; }

        /// <summary>Plain words for a false <see cref="CanCheckpoint"/>, shown to the person.</summary>
        string CheckpointUnavailableReason { get; }

        Task<string> CreateCheckpointAsync(string label, CancellationToken ct = default);
        /// <summary>Apply one item. May throw; the service rolls the whole batch back when it does.</summary>
        Task ApplyAsync(ChangeItem item, CancellationToken ct = default);
        Task RollbackToAsync(string checkpointId, CancellationToken ct = default);
        Task<string> AppendAuditAsync(ChangeAuditEntry entry, CancellationToken ct = default);
        /// <summary>Undo the last applied batch, restoring the DECLARED BOUNDARY: the targets the batch named.
        /// The audit trail and the checkpoint legitimately record that the work happened and are not undone.</summary>
        Task UndoAsync(CancellationToken ct = default);
    }

    /// <summary>propose, accept, cancel. The same three on both doors: fix_model / build_model propose,
    /// accept_change_set accepts, cancel_job cancels.</summary>
    public interface IChangeSetService
    {
        Task<IChangeSet> ProposeAsync(ChangeProposalRequest request, CancellationToken ct = default);
        Task<ChangeAcceptResult> AcceptAsync(string jobId, string digest, CancellationToken ct = default);
        Task<ChangeCancelResult> CancelAsync(string jobId, string digest, CancellationToken ct = default);
    }
}

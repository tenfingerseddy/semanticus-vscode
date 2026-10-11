// The binding between an approval and the write it authorised (docs/redesign/contracts/change-contract.md).
//
// WHY THIS IS A TYPE AND NOT A PAIR OF CHECKS BEFORE THE CALL. Approve used to read the plan, compare the
// digest, compare the revision, and THEN queue a batch. Everything it compared was already in the past by
// the time the batch ran. A set_plan_item on the other door could replace a kept description's value in
// between, without moving the model revision, and the batch applied the replacement under the digest of the
// value nobody had read. A model edit landing during the asynchronous reads the applier does before its
// batch had the same shape. Checking before queueing is not checking.
//
// So the accepted lines, their values, the digest and the versions travel WITH the work and are verified
// again on the single-writer dispatcher, inside the same transaction that writes. On that thread nothing
// else can commit, so a check that passes there is still true when the first property is set. When it does
// not pass, the guard throws, and the batch's own rollback puts back whatever it had already done. There is
// no second undo call and nothing to race with it.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>What an approval promised, carried into the write so it can be checked there.</summary>
    internal sealed class PlanApprovalGuard
    {
        /// <summary>The digest the person quoted. Only used in the refusal sentence: the values below are
        /// what the check actually compares, because a digest is a summary of them and a summary cannot say
        /// WHICH line moved.</summary>
        public string Digest;

        /// <summary>The model revision the approval was given at.</summary>
        public long Revision;

        /// <summary>The source version the approval was given at.</summary>
        public string SourceVersion;

        /// <summary>The session the approval was given in.</summary>
        public string SessionId;

        /// <summary>The plan item this approval is for. Never compared: it is only how a batch of several
        /// approvals names the one that refused.</summary>
        public string ItemId;

        /// <summary>Every accepted line, by its change plan id, with the WHOLE of what it said when it was
        /// read.
        ///
        /// NOT JUST THE VALUE AND THE FLAG, and this is the point. A change plan id is a POSITION in a list
        /// (Plan.cs: the sequence restarts at "ci-0001" every time a new plan is started), so the id on its
        /// own names nothing durable. Clear the plan on the other door, add one unrelated change, and the
        /// new first line wears the very handle the approval quoted. Give it the same value and the same
        /// approved flag and a check that compared only those two saw nothing wrong, so an old digest
        /// authorised a write to an object nobody had reviewed. The operation, the object, the property and
        /// the baseline are the rest of what the person actually read, so all of them are carried and all of
        /// them are compared.</summary>
        public Dictionary<string, ApprovedLine> Lines =
            new Dictionary<string, ApprovedLine>(StringComparer.OrdinalIgnoreCase);

        /// <summary>What the source version is RIGHT NOW. A function rather than a value because it has to
        /// be read again on the dispatcher, not compared against a reading taken earlier.</summary>
        public Func<string> ReadSourceVersion;

        /// <summary>The check, run on the single-writer dispatcher inside the transaction. Null means
        /// nothing moved; anything else is the sentence the person is shown, and the caller throws it.</summary>
        public string Refusal(Session session, IReadOnlyList<ChangeItem> targets)
        {
            if (session == null || !string.Equals(session.Id, SessionId, StringComparison.Ordinal))
                return "A different model is open now, so nothing was changed.";

            if (session.Revision != Revision)
                return "The model changed while these changes were being applied, so nothing was changed. "
                     + "Read them again and approve what you see.";

            var sourceNow = ReadSourceVersion == null ? SourceVersion : ReadSourceVersion();
            if (!string.Equals(sourceNow ?? "", SourceVersion ?? "", StringComparison.Ordinal))
                return "The file on disk changed while these changes were being applied, so nothing was "
                     + "changed. Read them again and approve what you see.";

            // Exactly the lines that were approved, still saying exactly what they said. A line that has
            // gone, a line that arrived, a value somebody re-authored and an approval somebody withdrew are
            // four different ways of applying something nobody read, and all four are refused here.
            if (targets.Count != Lines.Count)
                return ListMovedSentence;

            foreach (var target in targets)
            {
                if (!Lines.TryGetValue(target.Id ?? "", out var approved) || approved == null)
                    return "A change that was not part of what you approved appeared in the list, so nothing "
                         + "was changed. Read them again and approve what you see.";

                if (!string.Equals(target.After ?? "", approved.After ?? "", StringComparison.Ordinal))
                    return "The value of " + NameOf(target)
                         + " changed after you approved it, so nothing was changed. Read it again and approve "
                         + "what you see.";

                if (!string.Equals(target.Status ?? "", approved.Status ?? "", StringComparison.OrdinalIgnoreCase))
                    return "Whether to make the change to " + NameOf(target)
                         + " was decided again after you approved it, so nothing was changed. Read it again "
                         + "and approve what you see.";

                // THE REST OF THE LINE, and the half the handle cannot vouch for. What kind of write this
                // is, what it writes to, which property of it, and what that property said when it was read.
                // A handle that now names a different change is the list having moved underneath the
                // approval, so it is refused in exactly those words rather than in a sentence about a value:
                // nothing about this line's value moved, the line itself did.
                if (!string.Equals(target.Kind ?? "", approved.Op ?? "", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(target.ObjectRef ?? "", approved.ObjectRef ?? "", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(target.Target ?? "", approved.Property ?? "", StringComparison.Ordinal)
                    || !string.Equals(target.Before ?? "", approved.Before ?? "", StringComparison.Ordinal))
                    return ListMovedSentence;
            }

            return null;
        }

        /// <summary>The one sentence for "the set on the plan is no longer the set you approved". Shared by
        /// the count check and the per-line check so the two cannot come to word it differently.</summary>
        private const string ListMovedSentence =
            "The list of changes moved while these were being applied, so nothing was changed. "
            + "Read them again and approve what you see.";

        private static string NameOf(ChangeItem target) =>
            string.IsNullOrWhiteSpace(target.ObjectName) ? target.ObjectRef : target.ObjectName;
    }

    /// <summary>One line exactly as the person read it, carried into the write. Every field is compared
    /// inside the transaction, because any one of them differing means the write about to happen is not the
    /// one that was approved.</summary>
    internal sealed class ApprovedLine
    {
        /// <summary>The apply operation (ChangeItem.Kind).</summary>
        public string Op;

        /// <summary>The object, as the engine spells a reference.</summary>
        public string ObjectRef;

        /// <summary>The property being written (ChangeItem.Target).</summary>
        public string Property;

        /// <summary>What the model said before, which is the other half of the diff the person read.</summary>
        public string Before;

        /// <summary>What it would say.</summary>
        public string After;

        /// <summary>The approval state the line carried, which is what says the person kept it.</summary>
        public string Status;
    }

    /// <summary>Thrown inside the apply transaction when the approval no longer describes what is about to
    /// be written, or when a line the person kept could not be made. It escapes the batch delegate on
    /// purpose: the batch's own rollback is what puts the model back, which is why approve needs no undo
    /// call of its own.</summary>
    internal sealed class PlanApprovalRefusedException : Exception
    {
        public PlanApprovalRefusedException(string message) : base(message) { }

        /// <summary>The approval the refusal is about, when it is known. A batch carrying several items'
        /// approvals (apply_plan_items) uses it to name the item that stopped the batch.</summary>
        public PlanApprovalGuard Guard { get; init; }

        /// <summary>The line that could not be made, when a line is what stopped the batch.</summary>
        public ChangeItem Line { get; init; }
    }
}

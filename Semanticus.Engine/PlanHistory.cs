using System;

namespace Semanticus.Engine
{
    /// <summary>The merged History (Plan-heart contract 8.8): one time-ordered list, newest first, of edits, Plan
    /// decisions that wrote nothing, saved and restored versions, and finished workflow runs, with a parent row per
    /// cause nesting the edits it made. Both doors read this one answer (<c>get_plan_history</c>, RPC
    /// <c>getPlanHistory</c>).</summary>
    public sealed class PlanHistoryView
    {
        public string SessionId { get; set; }
        public PlanHistoryRow[] Rows { get; set; } = Array.Empty<PlanHistoryRow>();
        /// <summary>More rows exist past <c>limit</c>; ask again with <c>before</c> set to the last row's When.</summary>
        public bool More { get; set; }
        /// <summary>The divider line above every row from an earlier session (contract 8.9).</summary>
        public string SeamSentence { get; set; } = PlanHistoryRow.SeamText;
        /// <summary>False when the model has nowhere to keep its history (never saved, not live): it lasts this session.</summary>
        public bool Kept { get; set; }
    }

    public sealed class PlanHistoryRow
    {
        public const string SeamText = "Before this session. Undo is not available for these.";
        public const string RestoreSeamText = "Before the restore. Undo is not available.";

        /// <summary>The ledger entry id for an edit or a flag row (the id <c>undo_change(toEntry)</c> takes); a
        /// parent row's id is its cause ("plan_item:safefix/x"), which <c>undo_change</c> and <c>redo_change</c> take
        /// too and walk the whole group; a decision's is "decision:" and its time.</summary>
        public string Id { get; set; }
        /// <summary>"edit" | "item" | "run" | "draft" | "task" | "start_plan" | "decision" | "saved" | "restored" | "record".</summary>
        public string Kind { get; set; }
        public DateTimeOffset When { get; set; }
        /// <summary>"you" | "assistant" | "semanticus" | "workflow".</summary>
        public string By { get; set; }
        public string Title { get; set; }
        public ChangeCauseInfo Cause { get; set; }
        public ChangeDelta[] Deltas { get; set; }
        public string SessionId { get; set; }
        public long Revision { get; set; }
        /// <summary>Undo to here is offered. On a parent row it means one of its edits is undoable, and undo to it
        /// takes back the whole group from its oldest undoable edit.</summary>
        public bool Undoable { get; set; }
        /// <summary>How many undoable edits are newer: what "Undo to here" also takes back.</summary>
        public int LaterCount { get; set; }
        /// <summary>The row is undone and Redo to here is offered.</summary>
        public bool Redoable { get; set; }
        public bool Undone { get; set; }
        /// <summary>The Verified Edits word for this commit ("proven", "validated", ...), joined by session and revision.</summary>
        public string Verified { get; set; }
        /// <summary>From an earlier session: below the seam, never undoable.</summary>
        public bool BeforeThisSession { get; set; }
        /// <summary>An edit of an earlier session that was never written to the model's file: the session ended (or the engine stopped) before a save, so the model open now
        /// does not have it although the row was not taken back. The row says so in words (join 8, K4). False for an edit made in this session, for one the file holds, and
        /// for an entry written before the file record was kept (nothing is claimed for those).</summary>
        public bool NotSaved { get; set; }
        /// <summary>Older than the newest restore in this session: below the restore's seam.</summary>
        public bool BeforeRestore { get; set; }
        /// <summary>For a flag row: the checkpoint and what the restore kept; for a decision: its sentence.</summary>
        public string Detail { get; set; }
        /// <summary>For a finished run: its report as it stood when the run ended.</summary>
        public WorkflowRunReport Report { get; set; }
        /// <summary>For a "record" row: the Verified Edits record it stands for, one no edit row carries.</summary>
        public VerifiedEditRecord Record { get; set; }
        public PlanHistoryRow[] Children { get; set; }
    }
}

using System;
using System.Threading;

namespace Semanticus.Engine
{
    /// <summary>Why a commit happened (Plan-heart contract 8.2 and 8.4): the plan item, workflow run, draft or task it
    /// served. Stamped onto the notification inside the same call that writes the edit, never joined afterwards.</summary>
    public sealed class ChangeCauseInfo
    {
        /// <summary>"plan_item" | "workflow_run" | "draft" | "task" | "start_plan".</summary>
        public string Kind { get; set; }
        public string[] Ids { get; set; }
        public string Title { get; set; }
        /// <summary>"you" or "assistant" when a person or an assistant approved what Semanticus then applied; else null.</summary>
        public string ApprovedBy { get; set; }
        /// <summary>The columns a person said no measure for, in the plan this commit carried out (the start plan's base measures left out, by the column's ref). The Plan
        /// reads them from History and does not offer a measure for one again (RemovedMeasureRefs), and an undo of the commit takes them back with it, because the
        /// ledger entry is undone with the step. Null on every other cause. (UAT-J7-S2-009.)</summary>
        public string[] Declined { get; set; }
    }

    /// <summary>The cause kinds, spelled once.</summary>
    public static class ChangeCauseKinds
    {
        public const string PlanItem = "plan_item";
        public const string WorkflowRun = "workflow_run";
        public const string Draft = "draft";
        public const string Task = "task";
        /// <summary>The one step that builds a new model from its approved start plan (Semanticus.Engine/StartPlan).</summary>
        public const string StartPlan = "start_plan";
    }

    /// <summary>The four `By` words (contract 8.2). `Origin` stays as it is beside them.</summary>
    public static class ChangeBy
    {
        public const string You = "you";
        public const string Assistant = "assistant";
        public const string Semanticus = "semanticus";
        public const string Workflow = "workflow";
        /// <summary>A record the model carries from before this person opened it, written in a session this machine never saw. Nobody can say it was "you".</summary>
        public const string Earlier = "earlier";

        /// <summary>From the cause first, then the origin. A plan batch or a composer change group is Semanticus; a
        /// guided workflow step's writes are the workflow; a task is whoever is working it, so it falls to the origin.</summary>
        public static string For(ChangeCauseInfo cause, string origin)
        {
            switch (cause?.Kind)
            {
                case ChangeCauseKinds.PlanItem:
                case ChangeCauseKinds.StartPlan:
                case ChangeCauseKinds.Draft: return Semanticus;
                case ChangeCauseKinds.WorkflowRun: return Workflow;
            }
            return origin switch
            {
                "human" => You,
                "agent" => Assistant,
                _ => Semanticus,
            };
        }

        /// <summary>The approver word for an origin: a person approving in Studio, or an assistant through MCP.</summary>
        public static string ApproverFor(string origin) => origin == "human" ? You : origin == "agent" ? Assistant : null;
    }

    /// <summary>
    /// The ambient cause scope, the same shape as F-246's <see cref="ChangeWriter"/>. A writer that serves a plan item,
    /// a workflow run, a draft or a task opens it around its mutation; <c>Session.TrackAsync</c> reads it on the
    /// caller's thread, before the dispatcher hop (AsyncLocal need not reach the dispatcher's own thread), and stamps
    /// it onto that commit. An inner scope wins over an outer one and puts the outer back when it closes.
    /// </summary>
    public static class ChangeCause
    {
        private static readonly AsyncLocal<ChangeCauseInfo> Ambient = new AsyncLocal<ChangeCauseInfo>();

        public static ChangeCauseInfo Current => Ambient.Value;

        public static Scope Begin(ChangeCauseInfo cause)
        {
            var prev = Ambient.Value;
            Ambient.Value = cause;
            return new Scope(prev);
        }

        public static Scope Begin(string kind, string title, string approvedBy, params string[] ids) =>
            Begin(new ChangeCauseInfo { Kind = kind, Ids = ids ?? Array.Empty<string>(), Title = title, ApprovedBy = approvedBy });

        public readonly struct Scope : IDisposable
        {
            private readonly ChangeCauseInfo _prev;
            internal Scope(ChangeCauseInfo prev) { _prev = prev; }
            public void Dispose() => Ambient.Value = _prev;
        }
    }
}

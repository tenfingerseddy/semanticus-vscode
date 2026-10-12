using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed class PlanItemEdit
    {
        public string ItemId { get; set; }
        public string After { get; set; }
        public bool? Approved { get; set; }
        public string ExpectedAfter { get; set; }
    }

    public sealed class PlanItemAdd
    {
        public string ObjRef { get; set; }
        public string Kind { get; set; }
        public string After { get; set; }
        public string Title { get; set; }
        public string[] VerifyGroupBy { get; set; }
        public string[] VerifyFilters { get; set; }
    }

    public sealed class BatchPlanResult : BatchEditResult
    {
        public ChangePlanView Plan { get; set; }
    }

    public sealed class BatchStartPlanResult : BatchEditResult
    {
        public StartPlanView Plan { get; set; }
    }

    public sealed partial class LocalEngine
    {
        public Task<BatchPlanResult> SetPlanItemsAsync(PlanItemEdit[] items, string origin)
        {
            RequireProFeature();
            return ChangePlanBatchAsync(items, origin, x => x?.ItemId,
                (c, p, x) => SetPlanItemCoreAsync(c, p, x.ItemId, x.After, x.Approved, origin, x.ExpectedAfter), create: false);
        }

        public Task<BatchPlanResult> AddPlanItemsAsync(PlanItemAdd[] items, string origin)
        {
            RequireProFeature();
            return ChangePlanBatchAsync(items, origin, x => x?.ObjRef,
                (c, p, x) => AddPlanItemCoreAsync(c, p, x.ObjRef, x.Kind, x.After, x.Title, x.VerifyGroupBy, x.VerifyFilters, origin), create: true);
        }

        private async Task<BatchPlanResult> ChangePlanBatchAsync<T>(T[] items, string origin, Func<T, string> reference,
            Func<SessionContext, ChangePlanState, T, Task<ChangePlanView>> edit, bool create)
        {
            if (items == null) throw new ArgumentException("Pass an items list.");
            var context = _sessions.CurrentContext;
            await context.PlanGate.WaitAsync();
            try
            {
                EnsureContextCurrent(context, "Change plan items");
                var plan = (create ? context.Plans.GetOrStart() : context.Plans.Require()).Copy();
                var rejected = new List<BatchRejection>();
                var notes = new List<string>();
                var changed = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < items.Length; i++)
                {
                    var trial = plan.Copy();
                    try
                    {
                        if (items[i] == null) throw new ArgumentException("A plan item is required.");
                        var before = trial.Snapshot().ToDictionary(x => x.Id, x => System.Text.Json.JsonSerializer.Serialize(x));
                        var view = await edit(context, trial, items[i]);
                        if (view.Note != null) notes.Add(view.Note);
                        foreach (var line in trial.Snapshot())
                            if (!before.TryGetValue(line.Id, out var was) || was != System.Text.Json.JsonSerializer.Serialize(line)) changed.Add(line.Id);
                        plan = trial;
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        rejected.Add(new BatchRejection { Index = i, ObjRef = reference(items[i]), Reason = ex.Message });
                    }
                }
                var revision = context.RequireSession().Revision;
                EnsureContextCurrent(context, "Change plan items");
                if (changed.Count > 0) context.Plans.Replace(plan);
                var final = changed.Count > 0 ? PublishPlan(plan, revision) : BuildView(plan, revision);
                NoteBatch(context.RequireSession().Id, origin, changed.Count, "changed", "plan items", "plan", undo: false);
                return new BatchPlanResult { Revision = revision, Applied = changed.Count, Rejected = rejected.ToArray(),
                    Warning = notes.Count == 0 ? null : string.Join(" ", notes.Distinct()), Plan = final };
            }
            finally { context.PlanGate.Release(); }
        }
        private async Task<ChangePlanView> AddPlanItemCoreAsync(SessionContext context, ChangePlanState plan, string objRef, string kind, string after, string title, string[] verifyGroupBy, string[] verifyFilters, string origin)
        {
            var s = context.RequireSession();
            // Normalize the kind ONCE at entry: lowercase-invariant, not just trimmed. Every kind switch
            // (TargetForKind / CategoryForKind / RiskForKind / RequiresAfter / DefaultTitle) matches lowercase
            // literals and is case-SENSITIVE, and dedupe compares Target with Ordinal (case-sensitive) — so an
            // upper/mixed-case kind like "DELETE_IF_UNUSED" would fall to every switch's default (a garbage Target =
            // the raw kind), evade the dedupe, and carry inconsistent metadata. One normalization keeps all of them honest.
            kind = (kind ?? "").Trim().ToLowerInvariant();
            // A retired id (enable_qna) is stored as the operation it became, so the plan never shows the old name.
            kind = OpAliases.CanonicalPlanKind(kind);
            var ctx = await s.ReadAsync(m => ReadItemContext(m, objRef, kind));
            EnsureContextCurrent(context, "Change plan item");
            var tgt = TargetForKind(kind);
            // Dedupe by (ref, kind, target): an IDENTICAL pending duplicate (proposed / needs_content /
            // approved, same proposed value) is a no-op returning the existing plan — a double-clicked
            // "Review as a plan" (or any retrying caller, on either door) must not stack N copies the
            // human then rejects one by one. A DIFFERENT After is a different proposal and still adds;
            // applied / failed / skipped / rejected items never block a fresh attempt.
            var dup = plan.Snapshot().FirstOrDefault(x =>
                (x.Status == "proposed" || x.Status == "needs_content" || x.Status == "approved")
                && string.Equals(x.ObjectRef, objRef, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Kind, kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Target, tgt, StringComparison.Ordinal)
                && string.Equals(x.After ?? "", after ?? "", StringComparison.Ordinal));
            if (dup != null) return BuildView(plan, s.Revision);   // nothing changed → nothing to broadcast
            string status = (after == null && RequiresAfter(kind)) ? "needs_content"
                : OptInKind(kind, verifyGroupBy) ? "proposed"   // risky / unproven → opt-in (explicit approve required)
                : "approved";
            var source = string.Equals(origin, "agent", StringComparison.OrdinalIgnoreCase) ? "ai" : "deterministic";
            var risk = RiskForKind(kind);
            if (source == "ai" && risk == "safe") risk = "ai";
            var it = new ChangeItem
            {
                ObjectRef = objRef,
                ObjectName = ctx.name,
                Kind = kind,
                Source = source,
                Category = CategoryForKind(kind),
                Severity = "Medium",
                Risk = risk,
                RuleId = null,
                Target = tgt,
                Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle(kind) : title,
                Rationale = "Added to the plan.",
                Before = ctx.before,
                After = after,
                VerifyGroupBy = verifyGroupBy,   // null = no gate (opt-in apply); [] = scalar equivalence; [...] = matrix equivalence
                VerifyFilters = verifyFilters,
                Status = status,
                Generated = after != null,
                // R2: a removal is reviewed against a set of checked reports. Bind that basis to the proposal
                // so apply can tell whether the person is applying the answer they actually read.
                ReviewedScope = string.Equals(kind, "delete_if_unused", StringComparison.Ordinal)
                    ? ReportScopeSignature(s) : null,
            };
            plan.Add(it, s.Revision, SourceVersionOf(s));
            return BuildView(plan, s.Revision);
        }

        private async Task<ChangePlanView> SetPlanItemCoreAsync(SessionContext context, ChangePlanState plan, string itemId, string after, bool? approved, string origin, string expectedAfter)
        {
            var it = plan.Find(itemId) ?? throw new InvalidOperationException($"Plan item '{itemId}' not found. Run get_plan to see the current plan's item ids.");
            if (after != null) GuardExpectedValue("planned value", expectedAfter, it.After, after);   // the editor's starting point against what the line holds NOW, inside the gate
            var byAssistant = string.Equals(origin, "agent", StringComparison.OrdinalIgnoreCase);
            var jevFormat = IsJevFormatLine(it);
            string said = null;
            if (after != null && byAssistant && jevFormat)
                throw new InvalidOperationException("This line is Jev's suggested format, and Semanticus works it out again whenever the model changes, so a value written here would be lost without a word. "
                    + "To choose a different format, set it on the measure or column itself (set_measure_format, or set_property on FormatString).");
            // A line an accepted finding left out stays left out while it is filled or ticked: the assistant works from ids it read
            // before the person accepted the finding, and a tick or a new value must neither clear the reason nor put the line
            // back in the list to review, nor make it read "approved" under a note that says it will not be made. It is worked
            // out as the status the line would have, then held again below, unless the new value is no longer one that is
            // declined (a clearer name after the table's own name was accepted). Only a rejection moves it.
            var wasLeftOut = IsLeftOut(it);
            if (wasLeftOut) it.Status = RestoredStatus(it);
            if (after != null)
            {
                it.After = after;
                if (IsReplacedSynonyms(it) && context.Session != null)
                {
                    // A re-authored find and replace item is the list the person wrote: it clears the words of Before
                    // the list leaves out, and its After shows what that write leaves (the name term always stays), so
                    // the plan says what apply will do and undo and redo find the item by that reading.
                    var list = after;
                    var shown = await context.Session.ReadAsync(m =>
                        ObjectRefs.Resolve(m, it.ObjectRef) is TabularNamedObject o && ReplacedSynonymsCulture(m, it) is Culture c
                            ? LsdlSynonyms.PreviewSynonyms(o, c, list, RemovedSynonyms(it.Before, list)) : null);
                    it.After = shown ?? list;
                    it.Args = new Dictionary<string, string>(it.Args) { [ReplacedListArg] = list };   // Clone shares Args: a new one
                }
                it.Generated = true;
                // A value written over a Jev line survives the family working its lines out again (Sol 6.1, finding 1.3).
                if (JevFamily.IsJevLine(it) && !jevFormat) it.AuthoredAfter = it.After;
                it.VerifyState = null; it.Note = null;   // a re-authored value invalidates any prior verify/apply note
                // Class-driven gate integrity: an OPT-IN kind stays opt-in through ANY value revision —
                // including one made after approval (the human consented to the OLD value; a new value needs
                // fresh consent). Authoring only ever auto-approves the safe kinds.
                if (OptInKind(it.Kind, it.VerifyGroupBy)) { if (it.Status != "rejected") it.Status = "proposed"; }
                else if (it.Status == "needs_content" || it.Status == "proposed") it.Status = "approved";
            }
            // A re-authored value is a new proposal, and a tick covered the old one.
            if (after != null) { it.KeptByPerson = false; it.KeptAgainst = null; it.KeptDigest = null; }
            if (approved == true)
            {
                // What the object computes at this moment, once: the line's own stamp is compared with it (a tick on a line worked out
                // for an older version of the object is refused), and it is what a tick records.
                var computesNow = !byAssistant && jevFormat ? await DigestOfAsync(context, it.ObjectRef) : null;
                if (!byAssistant && jevFormat && it.WorkedOutDigest != null && !string.Equals(it.WorkedOutDigest, computesNow, StringComparison.Ordinal))
                {
                    // A TICK IS FOR WHAT THE PERSON SEES, and what they see was worked out for another version of this measure or column
                    // (addendum 4 item 23). Recording it against what the object computes now would write a format Jev never looked at, so
                    // the tick is refused in words and the line says it is out of date. Nothing else moves.
                    if (Semanticus.Engine.Decisions.JevFormatSuggestion.IsSuggestion(it.CheckText))
                        it.CheckText = Semanticus.Engine.Decisions.JevFormatSuggestion.StaleNote;
                    said = "Not ticked: Jev worked this format out before the measure or column, or something it reads, changed, so it may not fit now. Nothing was changed. "
                        + "Wait for Jev to work it out again, or set the format yourself.";
                }
                else if (byAssistant && jevFormat && it.KeptByPerson)
                {
                    // An assistant's approve never erases a person's tick: the line stays as the person kept it.
                    said = "The person has already ticked this line, so it stays as they kept it.";
                }
                else if (byAssistant && jevFormat && Semanticus.Engine.Decisions.JevFormatSuggestion.IsSuggestion(it.CheckText))
                {
                    // A suggestion is only ever ticked by the person (THE FORMAT FILL RULE, addendum 2 item 8). Recording the
                    // assistant's approve as approved would show the line as kept and stop nothing at write time but the row,
                    // so it is not recorded, and the words say what really happened.
                    said = "Not approved: this is a format Semanticus could not prove fits, and only the person can tick a suggestion like this on the Plan. Nothing was changed. "
                        + "To set a format yourself, use set_measure_format or set_property on FormatString for that measure or column.";
                }
                else
                {
                    // "approved" must mean apply-ready — refuse to approve an item that still needs content.
                    it.Status = (RequiresAfter(it.Kind) && string.IsNullOrEmpty(it.After)) ? "needs_content" : "approved";
                    // WHO TICKED IT. Only the person's own door records a keep: an assistant's approval is not the person's,
                    // and the engine's own approval on arrival is neither. THE FORMAT FILL RULE writes an unproven format
                    // only for a line a person kept (see JevFormatWriteRefusal), and a re-sync carries only that. A keep
                    // belongs to the object and what it computes (addendum 3 item 12), so the digest is taken now.
                    var byPerson = it.Status == "approved" && !byAssistant;
                    it.KeptByPerson = byPerson;
                    it.KeptAgainst = byPerson ? it.Before : null;
                    it.KeptDigest = byPerson && jevFormat ? computesNow : null;
                    it.DroppedBy = null;
                }
            }
            else if (approved == false)
            {
                // An assistant's drop of a line the person had ticked takes their tick away (a drop writes nothing, so it is allowed), and the
                // words say so, never in silence (addendum 4 item 28, L5).
                if (byAssistant && jevFormat && it.KeptByPerson)
                    said = "This line had been ticked by the person. It is dropped now, and their tick is gone. Nothing was changed in the model; the person can keep it again on the Plan.";
                it.Status = "rejected"; it.KeptByPerson = false; it.KeptAgainst = null; it.KeptDigest = null;
                it.DroppedBy = byAssistant ? "assistant" : "person";
            }
            if (wasLeftOut)
            {
                var cover = approved == false || context.Session == null ? Cover.None
                    : await context.Session.ReadAsync(m => new SeedWaivers(m).Coverage(it));
                if (cover != Cover.None)
                {
                    it.HeldFromStatus = it.Status;
                    it.Status = "skipped";
                    it.Note = NoteFor(cover);
                }
                else { it.HeldFromStatus = null; it.Note = null; }   // rejected is rejected, and a value nothing declined is a line to review
            }
            // A Jev line another line cannot stand without moves that line with it (ChangeItem.Needs): drop a base
            // measure and its format, description, hide and Excel lines go too; keep the hide and its measure comes
            // back. On this one path, so both doors get the same pairs.
            if (approved.HasValue && IsPendingLine(it))
                PlanLineExecutor.FollowKeep(it, plan.Snapshot().Where(IsPendingLine), keep: approved.Value);
            // Any keep, drop or edit is the person (or the assistant they told) LOOKING at the line: a cleanup line that
            // arrived ticked as a suggestion is theirs now, and Run the plan may apply it.
            if (approved.HasValue || after != null) it.Suggested = false;
            var view = BuildView(plan, context.Session?.Revision ?? 0);
            return said == null ? view : WithNote(view, said);
        }
    }
}

// The target: what this model should become (the Fix milestone, One Plan option A, Kane 23 September 2026).
//
// The plan has always been able to say what is WRONG. It could not say what the model is FOR, so Model Spec
// sat beside it as a stranger and the plan's own aim was implicit in the rules. This file makes the aim
// explicit, and it does it in two halves that are deliberately not the same kind of thing:
//
//   THE RULE AIMS ARE DERIVED, one per section, from that section's own "why" line. They are not stored,
//   they cannot be edited, and there is always exactly one of them per section. They are what the rules
//   have always been aiming at, written down at last.
//
//   A TYPED TARGET IS THE PERSON'S OWN INTENT and is the only half that is remembered. It is stored in the
//   plan memory under the model's durable identity, beside the skips, and it is FREE on every tier: a target
//   is a sentence a person wrote about their own model, not work the product did for them.
//
// A TYPED TARGET BECOMES AN ITEM, AND THE ITEM IS HONEST ABOUT WHAT IT DOES NOT KNOW. The engine cannot read
// "a marked date table with Fiscal Calendar as a second calendar" and work out the change; nothing in the
// model matches it yet, by definition, because it is a target. So every typed target yields exactly ONE item
// in its section, in state "target", owned by "you", whose title is the person's own words and whose sub
// line says nothing exists yet. Its diff is EMPTY and that is not a bug: an empty diff on this item means
// the plan does not know the change, and inventing one would be the plan claiming knowledge it does not
// have. The jump comes from the SAME word routing the ask box uses (AskRoutePoint.MatchWords), so the place
// a target sends you to and the place those words send you to cannot disagree; when the words name no clear
// place there is no jump, and the item says so rather than guessing a page.
//
// DONE MEANS TWO DIFFERENT THINGS AND BOTH ARE THE PERSON'S CALL. Removing the target takes it off the plan
// entirely (they changed their mind about the aim). Marking the item done leaves the target on the list,
// recorded as reached (they did the work by hand). Neither writes to the model, which is why
// MarkPlanTargetDoneAsync returns Performed = false: the result's Performed field means the MODEL changed,
// and it did not.
//
// WHY THE VERB IS ROUTED AT THE DOORS AND NOT INSIDE ModelPlanActionAsync. model_plan_action(id, "done") is
// the wire shape, and both doors route a target id to MarkPlanTargetDoneAsync before the general path. That
// split is a lane boundary and not a design: the change-set lane owns ModelPlanActionAsync's verb switch in
// this same milestone. When both land, this routing belongs at the head of that switch, and the door bodies
// go back to one call each.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>Where a target came from. Only <see cref="You"/> is ever stored; <see cref="Rules"/> is
    /// derived on every call, exactly as the gaps are.</summary>
    public static class ModelPlanTargetSources
    {
        /// <summary>Read off the section's own "why" line. One per section, always.</summary>
        public const string Rules = "rules";

        /// <summary>A sentence the person typed. The only half kept on disk.</summary>
        public const string You = "you";
    }

    /// <summary>One line of "what this model should become".</summary>
    public sealed class ModelPlanTarget
    {
        /// <summary>Stable across refreshes because it comes from the TEXT, never from a counter. A rule aim
        /// is "rules/structure"; a typed one is a slug of the words the person wrote, so re-typing the same
        /// target is the same target rather than a second copy of it.</summary>
        public string Id { get; set; }

        /// <summary>The one line a person reads. Their own words on a typed target.</summary>
        public string Text { get; set; }

        /// <summary>One of <see cref="ModelPlanSections"/>.</summary>
        public string Section { get; set; }

        /// <summary>One of <see cref="ModelPlanTargetSources"/>.</summary>
        public string Source { get; set; }

        /// <summary>When the person added it. Null on a rule aim, which nobody added.</summary>
        public DateTimeOffset? When { get; set; }

        /// <summary>When the person marked it reached. Null while it is still open, and null on a rule aim,
        /// which is never marked: a rule aim is reached when its section's gaps are gone.</summary>
        public DateTimeOffset? DoneUtc { get; set; }

        /// <summary>The plan item this target shows up as, or null on a rule aim, which is not an item. Sent
        /// on the wire so a caller never has to rebuild the id by string surgery.</summary>
        public string ItemId { get; set; }
    }

    /// <summary>The target half of the plan: the rule aims, the person's typed targets, and the one item
    /// each typed target becomes. Static and pure so every line of it is testable without an engine.</summary>
    public static class PlanTargets
    {
        /// <summary>The family prefix on a target item's id, in the same "family/subject" shape every other
        /// item uses.</summary>
        public const string ItemPrefix = "target/";

        /// <summary>The longest target a person may type. A refusal, not a trim: a cut sentence is a
        /// different aim wearing the same words, and the plan would then show one the person never wrote.</summary>
        public const int MaxTextChars = 200;

        /// <summary>The one verb this family answers on <c>model_plan_action</c>.</summary>
        public const string DoneVerb = "done";

        /// <summary>The aim each section is for, as a statement, paired with the "why" line the page shows
        /// beside the section name. The why lines are copied VERBATIM from SECTIONS in
        /// Semanticus.VSCode/webview/src/plan-model.ts, the same way AskRouteCatalog copies help.tsx, and
        /// PlanTargetTests holds the copy honest against that file so a reword fails instead of drifting.
        /// The aim is that question turned into the answer, and nothing more: a richer sentence would be the
        /// engine inventing an aim rather than saying the one the rules already have.</summary>
        private static readonly (string Section, string Why, string Aim)[] Aims =
        {
            (ModelPlanSections.Structure, "is the model built right", "The model is built right."),
            (ModelPlanSections.Meaning, "does it make sense to people and to AI", "The model makes sense to people and to AI."),
            (ModelPlanSections.Numbers, "are the measures right and tidy", "The measures are right and tidy."),
            (ModelPlanSections.Trust, "can the numbers be checked", "The numbers can be checked."),
            (ModelPlanSections.Ship, "can it go to the reports", "It can go to the reports."),
        };

        /// <summary>The why line the page shows beside a section name, or null for a name that is not a
        /// section. Public so a test can hold it to the page's own copy.</summary>
        public static string WhyOf(string section) =>
            Aims.FirstOrDefault(a => string.Equals(a.Section, section, StringComparison.Ordinal)).Why;

        /// <summary>The rule-derived aims, one per section, in section order. Never empty, and never fewer
        /// than one per section: these are what the rules aim at whether or not anybody wrote a target.</summary>
        public static ModelPlanTarget[] RuleAims() =>
            Aims.Select(a => new ModelPlanTarget
            {
                Id = "rules/" + a.Section.ToLowerInvariant(),
                Text = a.Aim,
                Section = a.Section,
                Source = ModelPlanTargetSources.Rules,
            }).ToArray();

        public static bool IsTargetItemId(string id) =>
            id != null && id.StartsWith(ItemPrefix, StringComparison.Ordinal);

        /// <summary>True when this pair is a target's own verb on <c>model_plan_action</c>. Both doors ask
        /// this before the general path, so the agent and the page take the same branch.</summary>
        public static bool IsTargetAction(string id, string action) =>
            (IsTargetItemId(id) || PlanTasks.IsTaskId(id))
            && string.Equals((action ?? string.Empty).Trim(), DoneVerb, StringComparison.OrdinalIgnoreCase);

        public static string ItemIdFor(string targetId) => ItemPrefix + targetId;

        /// <summary>The target id, whether the caller passed the target's id or the id of the item it became.
        /// Both are shown to a person, so both are accepted rather than one of them being a trap.</summary>
        public static string TargetIdFrom(string id) =>
            IsTargetItemId(id) ? id.Substring(ItemPrefix.Length) : id;

        /// <summary>The section name as the plan spells it, or null when the name is not one of the five.
        /// Case is forgiven because a person types "structure"; a wrong word is not, because filing a target
        /// under a section nobody asked for is worse than refusing it.</summary>
        public static string NormaliseSection(string section) =>
            string.IsNullOrWhiteSpace(section)
                ? null
                : ModelPlanSections.InOrder.FirstOrDefault(
                    s => string.Equals(s, section.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>The five section names, listed the way a refusal names them.</summary>
        public static string SectionList() => string.Join(", ", ModelPlanSections.InOrder);

        /// <summary>An id built from the words themselves, so the same target typed twice is one target.
        /// Letters and digits survive, everything else becomes a single dash, and a target written entirely
        /// in punctuation or in a script with no ASCII letters falls back to a hash of its own text rather
        /// than to an empty id.
        ///
        /// A CUT ID CARRIES A HASH OF THE WHOLE TEXT, and that suffix is not decoration. The id is what
        /// "the same target typed twice is one target" is decided by, and sixty characters of a long
        /// sentence is not the sentence: two different aims that happened to open with the same sixty
        /// characters got the SAME id, and AddPlanTargetAsync then read the second one as the first one
        /// coming back. It replaced the first one's words, refiled it into whatever section was named this
        /// time and cleared its done mark, so one target silently became the other. The readable prefix is
        /// kept because a person reads these ids, and the suffix is FNV-1a over the whole normalised text,
        /// so the same words still give the same id on every machine and on every run.</summary>
        public static string IdFor(string text)
        {
            var raw = (text ?? string.Empty).Trim().ToLowerInvariant();
            var slug = new StringBuilder();
            var read = 0;
            foreach (var c in raw)
            {
                read++;
                if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9') slug.Append(c);
                else if (slug.Length > 0 && slug[slug.Length - 1] != '-') slug.Append('-');
                if (slug.Length >= 60) break;
            }

            var id = slug.ToString().Trim('-');
            if (id.Length == 0) return "t" + Hash(raw);
            // Cut only when characters were actually LEFT BEHIND. A text whose last character is the one
            // that reached sixty lost nothing, so it keeps the plain id it has always had.
            return read < raw.Length ? id + "-" + Hash(raw) : id;
        }

        /// <summary>FNV-1a, eight hex digits. Not a security hash and not asked to be one: it only has to
        /// give the same id for the same words on every machine and every run, which GetHashCode does not.</summary>
        private static string Hash(string text)
        {
            unchecked
            {
                var h = 2166136261u;
                foreach (var c in text ?? string.Empty) { h ^= c; h *= 16777619u; }
                return h.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>The one item a typed target becomes.
        ///
        /// It is filed in the target's own section, owned by "you", and its diff is empty on purpose: the
        /// plan does not know what change this needs, and the sub line says exactly that instead of the
        /// page having to infer it from an empty array.</summary>
        public static ModelPlanItem ItemFor(ModelPlanTarget target)
        {
            if (target == null) return null;
            var done = target.DoneUtc.HasValue;
            var jump = JumpFor(target.Text);

            var why = new List<string>
            {
                target.When.HasValue
                    ? "You added this aim on " + Day(target.When.Value) + "."
                    : "You added this aim.",
                jump == null
                    ? "Nothing in the model matches it yet, so Semanticus cannot work out the change. "
                      + "The words also do not name a page to do it on."
                    : "Nothing in the model matches it yet, so Semanticus cannot work out the change. "
                      + "Do it yourself on " + jump.Label + ".",
            };
            if (done) why.Add("You marked it done on " + Day(target.DoneUtc.Value) + ".");

            return new ModelPlanItem
            {
                Id = ItemIdFor(target.Id),
                Section = target.Section,
                Title = target.Text,
                Sub = done
                    ? "You added this aim. You marked it done."
                    : "You added this aim. Nothing in the model does it yet.",
                Value = string.Empty,
                Who = ModelPlanOwners.You,
                Status = done ? ModelPlanStatuses.Done : ModelPlanStatuses.Target,
                Confidence = 0,
                Why = why.ToArray(),
                Diff = Array.Empty<string>(),
                Touches = done ? "nothing to do" : "nothing yet",
                Tables = Array.Empty<ModelPlanItemTable>(),
                Jump = jump,
            };
        }

        /// <summary>Where a target is done by hand, from the pages' own words, matched locally, no service and no
        /// permission. Null when no place clearly wins. The catalogue only: a target is a task, so the data-question
        /// shape ("X by Y") must not send "sort Month Name by Month Number" to Ask your data. And never Ask your data
        /// at all: it answers questions and changes nothing, so a task is never done there.
        ///
        /// THE FIELD AND THE ROW READ THE SAME FUNCTION. This is AskRoutePoint.TaskPlace, which is also the task
        /// reading the Plan's field shows (AskRoutePoint.Readings), so the two cannot name different places or
        /// disagree about there being none. They did until fix-askdata-r6: the field read "Ask your data is the place
        /// for that." over a line whose row read "Semanticus does not know a page for this one" (verify-askdata-r5 F1).
        /// With permission the field may also name a page the SERVICE chose for words that name none. That is the
        /// task's page too, and a task row keeps it from the moment it is added (PlanTask.FieldPage, verify-askdata-r6
        /// F1); this function, reading words only, is what every row falls back to.</summary>
        public static ModelPlanJump JumpFor(string text)
        {
            var tool = AskRoutePoint.TaskPlace(text);
            return tool == null ? null : new ModelPlanJump { Tool = tool.Id, Label = tool.Label };
        }

        /// <summary>What the target is worth as one line: the grade now, the grade the safe fixes reach, and,
        /// when the person has typed targets of their own, that those come after. Every part is a fact the
        /// worth already carries; nothing here works out a grade.</summary>
        public static string TargetLine(ModelPlanWorth worth, int typedCount)
        {
            var grade =
                worth?.From == null ? "No grade yet"
                : worth.Pending ? worth.From + " now, and the grade the safe fixes reach is still being worked out"
                : worth.Unchanged ? "Stays " + worth.From
                : worth.To == null ? worth.From + " now, and the grade after the fixes could not be worked out"
                : worth.From + ", then " + worth.To;

            return grade + (typedCount > 0 ? ", then what you asked for." : ".");
        }

        /// <summary>Put the target half onto a composed plan: the aims and the typed targets on
        /// <see cref="ModelPlanView.Targets"/>, one item per typed target in its own section, and the worth's
        /// target line. The ONE place LocalEngine.Plan.cs calls into this file.
        ///
        /// An open target item goes to the HEAD of its section, because a target is what the section is for
        /// and the gaps under it are the distance still to go. A target the person marked done goes to the
        /// tail of the list with the other done items, so it leaves the working list the way they do.</summary>
        ///
        /// SINCE PLAN AT THE HEART (contract 15) A TYPED TARGET IS AN AIM TASK: a PlanTask with a section, a
        /// random id and its old ids kept as aliases. This fold takes those tasks. The row each one becomes is
        /// the task's own row (PlanTasks.ItemFor), so an aim and any other asked item are one kind of thing.</summary>
        public static ModelPlanView Fold(ModelPlanView view, IReadOnlyList<PlanTask> aims, bool notKept = false,
                                         IReadOnlyList<PlanSkip> skips = null)
        {
            if (view == null) return null;
            var mine = (aims ?? Array.Empty<PlanTask>())
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Words))
                .ToList();
            // A target whose section was lost or was never one of the five still has to live somewhere a
            // person can see it, and Structure is the first section on the page.
            foreach (var t in mine) t.Section = NormaliseSection(t.Section) ?? ModelPlanSections.Structure;

            view.Targets = RuleAims().Concat(mine.Select(PlanTasks.TargetOf)).ToArray();

            var items = view.Items?.ToList() ?? new List<ModelPlanItem>();
            foreach (var aim in mine)
            {
                var skip = (skips ?? Array.Empty<PlanSkip>()).FirstOrDefault(s =>
                    string.Equals(s.ItemId, aim.Id, StringComparison.Ordinal)
                    || (aim.Aliases ?? Array.Empty<string>()).Contains(s.ItemId, StringComparer.Ordinal));
                var item = PlanTasks.ItemFor(aim, notKept, skip);
                // An id already on the plan is never added twice: React keys on it, and two rows with one
                // key is the same class of bug the done-item de-duplication fixed (Astra 12 on PR 385).
                if (items.Any(i => string.Equals(i.Id, item.Id, StringComparison.Ordinal))) continue;

                var at = aim.State == PlanTaskStates.Done
                    ? -1
                    : items.FindIndex(i => string.Equals(i.Section, item.Section, StringComparison.Ordinal));
                if (at < 0) items.Add(item); else items.Insert(at, item);
            }

            view.Items = items.ToArray();
            if (view.Worth != null)
                view.Worth.TargetLine = TargetLine(view.Worth, mine.Count(t => t.State != PlanTaskStates.Done));
            return view;
        }

        private static string Day(DateTimeOffset when) => PlanDay.Of(when);
    }

    public sealed partial class LocalEngine
    {
        /// <summary>Add one line to "what this model should become". Free on every tier: a target is the
        /// person's own intent, not work the product did for them.
        ///
        /// Typing the same target twice is not two targets. The id comes from the words, so a repeat lands on
        /// the same entry, refiles it into whatever section was named this time, and clears any "done" mark,
        /// which is what somebody re-adding a target they had ticked off actually means.</summary>
        public async Task<ModelPlanView> AddPlanTargetAsync(string text, string section, string origin)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException(
                    "Say what this model should become, in your own words. For example: every measure has a "
                    + "description and a check.", nameof(text));

            var trimmed = text.Trim();
            if (trimmed.Length > PlanTargets.MaxTextChars)
                throw new ArgumentException(
                    "That aim is longer than " + PlanTargets.MaxTextChars + " characters. Shorten it to one "
                    + "line, or add it as two aims.", nameof(text));

            var filed = PlanTargets.NormaliseSection(section);
            if (filed == null)
                throw new ArgumentException(
                    "Say which goal this aim is for, so the plan can put it with the work that gets it "
                    + "there. One of: " + PlanTargets.SectionList() + ".", nameof(section));

            // Bound to the model the person is looking at, BEFORE the compose awaits anything. Without
            // this the target is written under whichever model happens to be open when the wait returns.
            var bound = BindPlanMemory();

            // An aim task now (contract 15), still matched by its words: the slug of the words is kept as an
            // alias on the aim, so typing the same target twice lands on the same aim, as it always did.
            var id = PlanTargets.IdFor(trimmed);
            return await ComposeAsync(memory =>
            {
                var existing = memory.Tasks.FirstOrDefault(t => PlanTasks.IsAim(t)
                    && ((t.Aliases ?? Array.Empty<string>()).Contains(id, StringComparer.Ordinal)
                        || string.Equals(PlanTargets.IdFor(t.Words), id, StringComparison.Ordinal)));
                if (existing != null)
                {
                    existing.Words = trimmed;
                    existing.Section = filed;
                    existing.State = PlanTaskStates.Open;
                    existing.DoneUtc = null;
                    existing.DoneBy = null;
                    return;
                }

                memory.Tasks.Add(new PlanTask
                {
                    Id = PlanTasks.NewIdFor(memory),
                    Words = trimmed,
                    Source = PlanSources.Words,
                    Doer = PlanDoers.You,
                    DoneWhen = PlanDoneWhen.YouMark,
                    OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Words },
                    Section = filed,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    State = PlanTaskStates.Open,
                    Aliases = new[] { id, PlanTargets.ItemIdFor(id) },
                });
            }, bound).ConfigureAwait(false);
        }

        /// <summary>Take one typed target off the plan, and its item with it. Free, and it changes nothing in
        /// the model. Takes either the target's id or the id of the item it became, because a person sees
        /// both and neither should be a trap.</summary>
        public async Task<ModelPlanView> RemovePlanTargetAsync(string id, string origin)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException(
                    "Name the aim to remove. Call get_model_plan and read the ids off 'targets'.", nameof(id));

            // One binding for the whole operation: the question "does this model have that target" and the
            // removal that follows must be about the same model, or a removal meant for one lands on another.
            var bound = BindPlanMemory();
            var key = id.Trim();
            if (!HasTypedTarget(bound, key))
                throw new ArgumentException(
                    "No aim has that id. You can remove only the aims you added, "
                    + "not the tasks Semanticus found. Call get_model_plan and read the ids off 'targets'.", nameof(id));

            return await ComposeAsync(memory =>
            {
                var aim = FindAim(memory, key);
                if (aim != null) memory.Tasks.Remove(aim);
            }, bound).ConfigureAwait(false);
        }

        /// <summary>Mark a typed target's item as done: the person did the work by hand and says so. The
        /// target stays on the list, recorded as reached, until they remove it.
        ///
        /// PERFORMED IS FALSE HERE AND THAT IS NOT A FAILURE. Performed means the MODEL changed, and nothing
        /// in the model changed: the plan wrote down what the person told it. Saying true would claim an edit
        /// nobody made, which is the same lie "started" was invented to avoid on a run that fixed nothing.</summary>
        public async Task<ModelPlanActionResult> MarkPlanTargetDoneAsync(string id, string origin)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Name the task to mark done. Call get_model_plan for the ids.", nameof(id));

            var bound = BindPlanMemory();
            var key = id.Trim();
            var s = bound.Session;

            // Any task answers "done" here, an aim or an asked item (contract 12): the person finished it.
            var state = ReadPlanState(bound);
            var found = state.FindTask(key) ?? state.FindTask(PlanTargets.TargetIdFrom(key));
            if (found == null)
                return new ModelPlanActionResult
                {
                    ItemId = id,
                    Action = PlanTargets.DoneVerb,
                    Performed = false,
                    Revision = s.Revision,
                    Plan = await ComposeAsync(null, bound).ConfigureAwait(false),
                    Sentence = "No aim you added has that id. Only an aim you typed can be "
                             + "marked done here.",
                };

            var by = string.Equals(origin, "agent", StringComparison.OrdinalIgnoreCase) ? PlanDoers.Assistant : PlanDoers.You;
            WritePlanTasks(bound, memory =>
            {
                var task = memory.FindTask(found.Id);
                if (task == null) return;
                task.State = PlanTaskStates.Done;
                task.DoneUtc = DateTimeOffset.UtcNow;
                task.DoneBy = by;
                task.Claim = null;
            });
            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);

            return new ModelPlanActionResult
            {
                ItemId = found.Id,
                Action = PlanTargets.DoneVerb,
                Performed = false,
                Revision = plan.Revision,
                Plan = plan,
                Sentence = PlanTasks.IsAim(found)
                    ? "Marked done. Nothing in the model changed. This only notes that you finished the aim. "
                      + "Remove the aim to take it off the plan."
                    : found.DoneWhen == PlanDoneWhen.ModelCheck || found.DoneWhen == PlanDoneWhen.HasWords
                        ? "Marked done. Nothing in the model changed. The task shows as done once "
                          + "Semanticus finds the model really has it."
                        : "Marked done on the plan. Nothing in the model changed.",
            };
        }

        /// <summary>Whether the BOUND model has a target with this id. Read off the sidecar the binding
        /// names, under the identity the binding names, so the answer is about the model the caller asked
        /// about and not about whichever one is open by the time the answer is used. A model with nowhere to
        /// keep anything simply has none.</summary>
        private bool HasTypedTarget(PlanMemoryBinding bound, string id) =>
            FindAim(ReadPlanState(bound), id) != null;

        /// <summary>The aim a caller named: by its own id, by its old target id, or by the old item id
        /// "target/slug", all of which a person or an assistant may still hold.</summary>
        private static PlanTask FindAim(PlanMemoryState memory, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            var task = memory.FindTask(id) ?? memory.FindTask(PlanTargets.TargetIdFrom(id));
            return PlanTasks.IsAim(task) ? task : null;
        }
    }
}

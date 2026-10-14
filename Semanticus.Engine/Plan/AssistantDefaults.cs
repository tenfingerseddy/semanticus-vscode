using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>
    /// What an assistant row says about finishing itself (Kane, 13 Oct 2026: "the person is the last resort, automate everything").
    /// Hero run 19 stopped at six questions on rows a tool can do. Every row the assistant holds now carries two plain lines
    /// in its brief (never in the reasons the person reads): when it is done, and the choice to make without asking. Undo takes each back, so none of them
    /// is a question. The assistant asks only for a sign-in, a fact no tool can find, or a step Undo cannot take back.
    /// </summary>
    internal static class AssistantDefaults
    {
        internal const string DoneWhenLead = "Done when: ";
        internal const string DefaultLead = "Default, no need to ask: ";
        /// <summary>Hero run 20, J2: the drive ended Best practice F 38 because nothing re-read it, and four rows were left with no reason on them.
        /// Every assistant brief ends with the finishing rule, so a row picked up alone still knows it.</summary>
        internal const string FinishLead = "Finish: ";
        internal const string FinishRule = "do the next Ready row until no row a tool can do is left. Then read AI readiness and Best practice again and work any finding your own changes caused. "
            + "Report both grades. If you cannot do this row, say why on the row (decline_request with the reason, or complete_request with a reply), in plain words; never leave it Ready or Waiting with no reason.";

        /// <summary>Sets every assistant row's brief. Idempotent: a row that already carries the lines is left alone.</summary>
        internal static void Apply(IEnumerable<ModelPlanItem> items)
        {
            foreach (var item in items ?? Array.Empty<ModelPlanItem>())
            {
                if (item == null || !string.Equals(item.Who, ModelPlanOwners.Assistant, StringComparison.Ordinal)) continue;
                if (!string.IsNullOrEmpty(item.Brief) && item.Brief.StartsWith(DoneWhenLead, StringComparison.Ordinal)) continue;
                var (done, choice) = For(item.Id);
                // The assistant's brief, not the row's reasons: the person reads Why in Details next to their own Done when, and tool names are not for them.
                item.Brief = DoneWhenLead + done + " " + DefaultLead + choice + " " + FinishLead + FinishRule;
            }
        }

        internal static (string Done, string Choice) For(string id)
        {
            id ??= string.Empty;
            const string undo = " One Undo takes it back, so say what you did in chat and do not ask first.";
            switch (id)
            {
                case LocalEngine.CompatItemId:
                    return ("the compatibility level is at least the one this row names (set_compatibility_level).",
                            "raise it and say so in chat. Undo takes it back in this session.");
                case LocalEngine.CoverageBpaItemId:
                    return ("the Model quality scan lists none of this row's findings, or each one left is waived with a reason (waive_findings).",
                            "apply each finding's own fix (apply_fixes). Where a fix needs a choice, take the safest one. Waive a finding only when changing it would make the model worse. This row is a tool row: do it, do not leave it Ready. Read Best practice again when the plan is done, because hiding or deleting things can make new findings." + undo);
                case LocalEngine.CoverageAiItemId:
                    return ("AI readiness lists none of this row's findings, or each one left is waived with a reason (waive_findings).",
                            "write the words, set the settings and add the synonyms with the bulk tools (write_words, edit_objects)." + undo);
                case "cleanup/model":
                    return ("every column and table this row lists is gone, or kept with a line saying what uses it.",
                            "First find the model's reports (local folders and published ones), then set_report_scope and check_reports; if the model has none, call set_report_scope with an empty list, which counts as checked. Then read unused_objects, then REMOVE what it marks safe with remove_safe_objects (it re-checks each one and the chosen reports, so a column or table that a hierarchy, sort-by, role, calculation item, measure, relationship or report uses is never removed). A table nothing uses (no relationship, no measure, no report) is removed the same way, then reported: do not ask whether to keep it. Do not hide an unused column: a hidden unused column is still a Best practice finding. A visible column it marks visibleAttribute is not swept by remove_safe_objects: remove it with delete_object only when it is marked visibleAttribute, which it is only after at least one report is chosen and every chosen report is checked (or the model has no reports). With no report checked, hide it and write on the row why. Say in chat what went. Only a table or column something uses is kept; leave a kept table out of what Copilot sees and write why on the row." + undo);
                case "datetable/model":
                case "calendar/model":
                    return ("one table is marked as the date table, and the calendar exists.",
                            "mark the table the model's own dates use. If the model has a date column but no date table, add one. At compatibility level 1701 or higher, use define_calendar_from_template (it makes the table and its calendar), mark it with mark_date_table, and add a hierarchy of Year, Year Quarter, Year Month and Date with create_hierarchy. Below 1701, use generate_date_table (it marks it). Then relate the table to that column with create_relationship."
                            +" If no table holds a date column, say that on this row in one sentence. That is the one case to ask the person about.");
                case "ship/unsaved":
                    return ("the changes are saved and the person can still go back.",
                            "save to the same file when it is in git or you made a backup first. Otherwise save a copy next to it and say so. Never write over the person's only copy without a way back.");
                case "proposal/duplicate-measures":
                    return ("each set of duplicates is one measure and nothing points at the ones that went.",
                            "keep the measure that is used. Run check_reports first so a chosen report counts, and read get_lineage for the model's own use. Remove a duplicate with remove_safe_objects only when nothing in the model and no report uses it. If a report or another measure uses a duplicate, hide it (edit_objects, hidden) and write on the row why. If none is used, keep the one with the clearest name." + undo);
                case "proposal/collisions":
                    return ("no word is used by more than one visible field.",
                            "read unused_objects. Remove the generic or technical field with remove_safe_objects when it marks the field safe; hide it (edit_objects, hidden) when something uses it or it marks it visibleAttribute, and write on the row why. Keep the business one visible. Rename only when both are real business fields." + undo);
                case "proposal/visibility":
                    return ("none of the technical fields stays visible.", "read unused_objects. Remove each field it marks safe with remove_safe_objects; hide (edit_objects, hidden) a field something uses or one it marks visibleAttribute, and write on the row why. Hiding an unused column makes a new Best practice finding." + undo);
                case "proposal/synonyms":
                    return ("the synonyms on this row's fields do what they say.",
                            "add the words people would type for each field and drop any that point at another field (write_words)." + undo);
                case "proposal/descriptions":
                    return ("every field on this row has a description.", "write one plain sentence per field from its name, its formula and its data (write_words)." + undo);
                case "proposal/relationships":
                    return ("each relationship has no model warning.", "fix it the way the warning says (one direction, single filter, or the right key)." + undo);
                case "proposal/limits":
                    return ("the model is inside the limit this row names.",
                            "trim the part with the least use: hide or remove unused fields first, then shorten long text. Do not ask which part." + undo);
                case "proposal/dates":
                    return ("the date settings no longer warn.", "set the date table and sort columns the model's own dates use." + undo);
                default:
                    return ("the finding this row names is gone from its page, or waived with a reason.",
                            "use the safest fix the finding's own tool offers." + undo);
            }
        }
    }
}

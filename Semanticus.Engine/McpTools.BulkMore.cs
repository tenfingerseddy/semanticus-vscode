using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    public static partial class McpTools
    {
        [McpServerTool(Name = "plan_rows"), Description("Semanticus Pro feature: change many Plan rows in one call, with one final recompose and broadcast. Each item names id and action: pause, resume, note (text), doer (doer), skip (text reason), unskip, remove, restore or done. Uses the same row controls as their single tools. Bad items are named in rejected; applied counts changed rows. Plan controls do not create a model undo step. Restore removed rows with action=restore. Use apply_plan_items to run many rows. On Free use pause_plan_item, set_plan_task_note or the other single Plan controls.")]
        public static Task<BatchPlanRowsResult> PlanRows(IEngine engine, [Description("The row controls, in order.")] PlanRowEdit[] items)
            => engine.PlanRowsAsync(items, "agent");

        [McpServerTool(Name = "unwaive_findings"), Description("Semanticus Pro feature: Remove many air or bpa waivers. One engine call and one undo step. Bad items are left out and named in rejected (index, objRef, reason). applied counts changed items. Unchanged items do not count. On Free use unwaive_finding.")]
        public static Task<BatchEditResult> UnwaiveFindings(IEngine engine, [Description("The items to change, in order.")] FindingFixItem[] items)
            => engine.UnwaiveFindingsAsync(items, "agent");

        [McpServerTool(Name = "apply_fixes"), Description("Semanticus Pro feature: Apply selected deterministic air or bpa fixes. Each item names system, ruleId and objRef. The same checks as apply_fix apply. One engine call and one undo step. Bad items are left out and named in rejected (index, objRef, reason). applied counts changed items. Unchanged items do not count. On Free use apply_fix.")]
        public static Task<BatchEditResult> ApplyFixes(IEngine engine, [Description("The items to change, in order.")] FindingFixItem[] items)
            => engine.ApplyFixesAsync(items, "agent");

        [McpServerTool(Name = "duplicate_objects"), Description("Semanticus Pro feature: Duplicate many objects. Each item names objRef, optional newName and optional targetRef. Returns created refs in input order. One engine call and one undo step. Bad items are left out and named in rejected (index, objRef, reason). applied counts changed items. Unchanged items do not count. On Free use duplicate_object.")]
        public static Task<BatchEditResult> DuplicateObjects(IEngine engine, [Description("The items to change, in order.")] ObjectDuplicateItem[] items)
            => engine.DuplicateObjectsAsync(items, "agent");

        [McpServerTool(Name = "set_doc_sections"), Description("Semanticus Pro feature: Write many documentation sections. Each item names objRef, sectionKey and markdown. Empty or null markdown clears the section. expectedMarkdown optionally checks the starting value. One engine call and one undo step. Bad items are left out and named in rejected (index, objRef, reason). applied counts changed items. Unchanged items do not count. On Free use set_doc_section.")]
        public static Task<BatchEditResult> SetDocSections(IEngine engine, [Description("The items to change, in order.")] DocSectionItem[] items)
            => engine.SetDocSectionsAsync(items, "agent");

        [McpServerTool(Name = "set_perspective_members"), Description("Semanticus Pro feature: Set many perspective cells. Each item names perspectiveRef, objRef and include. A table cell also sets its children, just as set_perspective_member does. One engine call and one undo step. Bad items are left out and named in rejected (index, objRef, reason). applied counts changed items. Unchanged items do not count. On Free use set_perspective_member.")]
        public static Task<BatchEditResult> SetPerspectiveMembers(IEngine engine, [Description("The items to change, in order.")] PerspectiveMemberItem[] items)
            => engine.SetPerspectiveMembersAsync(items, "agent");

        [McpServerTool(Name = "set_object_permissions"), Description("Semanticus Pro feature: Set many table or column OLS cells. Each item names roleName, objRef and permission (Default, None or Read). Default clears the override. One engine call and one undo step. Bad items are left out and named in rejected (index, objRef, reason). applied counts changed items. Unchanged items do not count. On Free use set_table_ols or set_column_ols.")]
        public static Task<BatchEditResult> SetObjectPermissions(IEngine engine, [Description("The items to change, in order.")] ObjectPermissionItem[] items)
            => engine.SetObjectPermissionsAsync(items, "agent");

        [McpServerTool(Name = "set_plan_items"), Description("Semanticus Pro feature: Set authored values or approval flags on many pending change-plan items. Each item names itemId, optional after, approved and expectedAfter. Use apply_plan to apply these model changes in one undo step. One call and one plan broadcast. Bad items are named in rejected. applied counts changed items. Plan edits do not create a model undo step. On Free use set_plan_item.")]
        public static Task<BatchPlanResult> SetPlanItems(IEngine engine, [Description("The plan edits, in order.")] PlanItemEdit[] items)
            => engine.SetPlanItemsAsync(items, "agent");

        [McpServerTool(Name = "add_plan_items"), Description("Semanticus Pro feature: Add many pending change-plan items. Each item names objRef, kind, optional after, title, verifyGroupBy and verifyFilters. Use apply_plan to apply these model changes in one undo step. One call and one plan broadcast. Bad items are named in rejected. applied counts changed items. Plan edits do not create a model undo step. On Free use add_plan_item.")]
        public static Task<BatchPlanResult> AddPlanItems(IEngine engine, [Description("The plan edits, in order.")] PlanItemAdd[] items)
            => engine.AddPlanItemsAsync(items, "agent");

        [McpServerTool(Name = "edit_start_plans"), Description("Semanticus Pro feature: Edit many start-plan choices or answers. Each item names op, target and value, as in edit_start_plan. Writes the plan once, clears approval and returns the new plan. The model stays unchanged. One call and one plan broadcast. Bad items are named in rejected. applied counts changed items. Plan edits do not create a model undo step. On Free use edit_start_plan.")]
        public static Task<BatchStartPlanResult> EditStartPlans(IEngine engine, [Description("The plan edits, in order.")] StartPlan.StartPlanEdit[] items)
            => engine.EditStartPlansAsync(items, "agent");

    }
}

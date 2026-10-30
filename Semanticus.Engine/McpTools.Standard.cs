// "Your standard" (1.7): the assistant's four tools for the standard file. The page is the other door onto the same engine.
// Grades stay absolute: a standard decides what reaches the Plan and what is changed now, never how a grade is worked out.

using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    public static partial class McpTools
    {
        [McpServerTool(Name = "get_standard"), Description("Read the standard this model is held to: one YAML file that says how a model should look (checks, names, formats, folders, descriptions, synonyms, keys-and-totals, AI instructions). It is found in this order: the model's own .semanticus/standard.yaml (it can point at a shared file with use:), then your default at ~/.semanticus/standard.yaml, then Balanced, built in. The result says which one is in force, where it came from, every part as held now, the lines written in the file, and the three presets (light, balanced, strict). A shared file that could not be read is named in notes and the rest still works. Free and read-only.")]
        public static Task<GetStandardResult> GetStandard(IEngine engine) => engine.GetStandardAsync();

        [McpServerTool(Name = "set_standard"), Description("Change the standard in words. 'patch' is a JSON object using the file's own keys, for example {\"names\":\"title-case\",\"formats\":{\"money\":\"$#,0.00\"},\"checks\":{\"off\":[\"REL-SNOWFLAKE\"],\"level\":\"strict\"}}. A part set to null goes back to what the preset gives. 'preset' (light, balanced or strict) changes what it starts from. Values: checks.level light|balanced|strict; names leave|title-case|title-case-no-prefix; formats by kind money|percent|count (leave = do not touch); folders none|'by-subject, over N'|'by-subject, always'; descriptions none|measures|'measures, tables'|all-visible; synonyms none|'N, measures and tables'|'N, all-visible'; keys-and-totals leave|'hide keys, no-sum ids'|'hide keys and technical, no-sum ids'; ai-instructions none|a Markdown file. A value it cannot read changes nothing and the answer says which one. It writes the standard file only (beside the model by default, or your default with target 'your-default'); the model is not touched. Call preview_standard next to see what it would do.")]
        public static Task<SetStandardResult> SetStandard(IEngine engine,
            [Description("A JSON object of the parts to change, using the standard file's keys.")] string patch,
            [Description("Optional. 'model' (the file beside the model; the default when the model is saved) or 'your-default' (~/.semanticus/standard.yaml, for every model with no file of its own).")] string target = null)
            => engine.SetStandardAsync(patch, target, "agent");

        [McpServerTool(Name = "preview_standard"), Description("See what holding this model to its standard would do, part by part, before anything is written: for each part the choice in plain words, the changes Semanticus would make now (changesNow), the tasks that would go to the Plan (tasks) and up to three before and after examples. Also the grade now and the grade after the changes made now. Grades are absolute: the standard never moves a grade by itself. Nothing is written and no revision moves. Free and read-only.")]
        public static Task<PreviewStandardResult> PreviewStandard(IEngine engine,
            [Description("Optional. The same JSON patch set_standard takes. The preview then shows the standard WITH that patch on top, for example {\"preset\":\"strict\"} to compare presets. Nothing is saved.")] string patch = null)
            => engine.PreviewStandardAsync(patch);

        [McpServerTool(Name = "apply_standard"), Description("Hold the open model to its standard. The sure changes are made now as ONE undo step: formats for a known kind of measure (money, percent, count), display folders by subject, key columns hidden, ids that should not add up set to no-sum. Renames, descriptions, other names (synonyms) and the AI instructions are NOT written: they go to the Plan as tasks for you, because a rename can break a report built on the model and the engine writes no words. It also records which standard the model was checked against, and merges the standard's extra best-practice rules file. No confirm: call preview_standard first to see the counts; undo_change puts everything back. Then pick up the Plan tasks with list_assistant_requests.")]
        public static Task<ApplyStandardResult> ApplyStandard(IEngine engine) => engine.ApplyStandardAsync("agent");
    }
}

// The assistant's two voices in the Studio (1.4, Claude drives; contract sections 1 and 6). Both are free on every tier, change
// nothing in the model, its files or the settings, and appear in no History. The Studio decides whether its screen moves: the
// person can always take the wheel, so these tools say what the assistant is doing and never force a page open.

using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    public static partial class McpTools
    {
        [McpServerTool(Name = "show_in_studio"), Description("Show the person what you are about to do or have just done, in the Studio window they are looking at: it opens a page, selects an object and says one sentence. Call it BEFORE you start a piece of work and again when you show a result, so the person follows along instead of reading a log. 'say' is required: one plain sentence for the person, 1 to 200 characters, no jargon, no tool names. Semanticus adds its own line under it, 'To do this yourself: ...', worked out from the place, so do not write the steps. 'place' is one of: plan, plan/start, plan/history, plan/changes, explore/data, explore/ask, explore/diagram, explore/lineage, explore/size, edit/find, edit/queries, edit/properties, edit/instructions, edit/advanced, edit/power-query, review/model-quality, review/ai-readiness, review/validation, review/docs, publish/changes, publish/new-model, publish/data-agent, workflows, connections, help; an unknown place is refused and the answer lists them. 'focus' is optional: an object to select, like 'measure:Sales/Net Sales', 'table:Sales' or 'model:'; an object that is gone shows the place with nothing selected. 'press' is optional: a control to show being pressed, one of read-source, approve-build, run-plan, undo, save, publish, publish-new, ask, explain, lineage. The result has 'screen': 'moved' means the Studio went to the place; 'noted' means the person has the wheel (they clicked, or chose to drive themselves), so your sentence waits in their note strip with a Show me button and the screen did not move; 'noStudio' means no Studio window is open. Tell the person in chat what you are doing in every case, because 'noted' and 'noStudio' mean they may not see it. It changes nothing in the model and is not a reason to wait: carry on with the work.")]
        public static Task<StudioShowResult> ShowInStudio(IEngine engine,
            [Description("Where to look: a place id from the list above, e.g. 'plan' or 'explore/lineage'.")] string place,
            [Description("One plain sentence for the person, 1 to 200 characters. Required.")] string say,
            [Description("Optional. An object to select, in ref form: 'measure:Sales/Net Sales', 'table:Sales', 'model:'.")] string focus = null,
            [Description("Optional. A control to show being pressed: read-source, approve-build, run-plan, undo, save, publish, publish-new, ask, explain or lineage.")] string press = null)
            => engine.ShowInStudioAsync(new StudioShowRequest { Place = place, Say = say, Focus = focus, Press = press });

        [McpServerTool(Name = "suggest_next"), Description("After you finish a piece of work, offer the person 2 or 3 next steps. The Studio shows them as buttons under your last note; pressing one adds a task for you on the Plan, and you pick it up with list_assistant_requests. ALSO list the same steps in your chat reply, numbered, so the person can answer with a number. Each step has a 'label' (3 to 40 characters, starts with a verb, no full stop: 'Fix the missing descriptions') and a 'prompt' (1 to 280 characters: the words the person would type to you for that step). 'say' is an optional lead line, 200 characters at most; it defaults to 'Here is what I would do next.'. Call it again to replace the suggestion. Out of range is refused with the limit named. It changes nothing in the model.")]
        public static Task<StudioSuggestResult> SuggestNext(IEngine engine,
            [Description("2 or 3 steps, each { label, prompt }.")] StudioStep[] steps,
            [Description("Optional lead line, 200 characters at most.")] string say = null)
            => engine.SuggestNextAsync(new StudioSuggestRequest { Steps = steps, Say = say });

        [McpServerTool(Name = "get_next_step"), Description("Read the Next line the person sees under the Studio's driver bar: where they are on the journey (stage 1 to 7: open the model, get it AI ready, check the numbers, save, publish to test, publish to production, keep it healthy) and the one next step Semanticus suggests, with why, where it came from (the rules, Jev on Pro, or your own suggest_next) and whether Semanticus can do it alone (doIt). 'on' false means the person turned suggestions off. It changes nothing.")]
        public static Task<NextStepView> GetNextStep(IEngine engine) => engine.GetNextStepAsync();

        [McpServerTool(Name = "set_next_step"), Description("Change the person's Next line suggestions when they ask you to. 'action' is one of: hide (arg = the step id from get_next_step; hides that step until it changes), unhide (arg = the id), stopKind (arg = the step kind, e.g. 'save'; never suggest that kind again), resumeKind (arg = the kind), turnOff (no suggestions at all), turnOn. stopKind, turnOff and turnOn are kept for the person across sessions. It returns the Next line as it is now. It changes nothing in the model.")]
        public static Task<NextStepView> SetNextStep(IEngine engine,
            [Description("hide, unhide, stopKind, resumeKind, turnOff or turnOn.")] string action,
            [Description("The step id for hide and unhide, the kind for stopKind and resumeKind; empty for turnOff and turnOn.")] string arg = null)
            => engine.SetNextStepAsync(action, arg);
    }
}

// The reading card's two tools (Kane's pick A, 12 Oct 2026). The Plan page shows the card on every open of a model that has issues; these are the
// assistant's side of it, through the same IEngine calls (readingCard and fixThem on EngineRpcTarget), so the person and the assistant read one grade and
// press one Fix them. The person's word in chat is the approval; there is no question to ask first.

using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    public static partial class McpTools
    {
        [McpServerTool(Name = "reading_card"), Description("Read the same card the Plan page shows when a model opens: the AI readiness grade and score, the grade the safe fixes would reach ('gradeAfter'), and the top three issues in plain words. Each issue says how many things it covers ('count'), who fixes it ('who': semanticus fixes it itself, assistant means you write it, you means the person decides) and its 'state' (open, or queued when a writing task for it is already on the plan). 'show' is true while the person still sees the card. Reads only: nothing changes. Use fix_them to act on it.")]
        public static Task<ReadingCardView> ReadingCard(IEngine engine,
            [Description("true (the default) also works out the grade the safe fixes would reach. false is faster and leaves gradeAfter empty.")] bool withAfter = true)
            => engine.ReadingCardAsync(withAfter);

        [McpServerTool(Name = "fix_them"), Description("The card's Fix them, the same as the Plan page's button. It applies every safe fix as ONE undo step, then adds the writing that is left to the plan as tasks for you (descriptions, then other names, then anything else that needs words): take each with claim_request and write it. The answer says how many things were fixed, the grade before and after (worked out on the real model), and the tasks it added, each with its plan row id. Undo takes back the fixes (undo); remove_plan_row takes back a task that has not started. Pressing it again adds no second task for work already on the plan.")]
        public static Task<FixThemResult> FixThem(IEngine engine)
            => engine.FixThemAsync("agent");
    }
}

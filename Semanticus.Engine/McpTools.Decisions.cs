// The agent door's half of the decision surface (Jev M0a).
//
// Seven operations, and the RPC door in EngineRpcTarget.cs exposes the same seven under camelCase names.
// Both call the same IEngine methods and return the same DTOs, so the two doors cannot answer differently.
//
// The copy here follows docs/product-copy-style.md: no engine jargon, no em dashes, honest over smooth. The
// descriptions say what leaves the machine, because an assistant reading this surface is the one person most
// likely to be asked "what does this send?".

using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    public static partial class McpTools
    {
        [McpServerTool(Name = "get_decision_points"), Description("List every place this product can ask an outside service for a suggestion. Each one shows exactly what it would send, how sure it has to be, what happens when it is not sure enough, and whether it is switched on for this model. A point with 'live' false is one this build never asks, whatever 'enabled' says: its replay bar is unmet, so nothing is sent for it and the switch cannot be turned on. This list is the whole audit surface: nothing can ask anything that is not on it. Reads only.")]
        public static Task<DecisionPointInfo[]> GetDecisionPoints(IEngine engine)
            => engine.GetDecisionPointsAsync();

        [McpServerTool(Name = "set_decision_point_enabled"), Description("Switch one suggestion on or off for the open model. Off means it is never asked, whatever the permission says. Each model has its own switches. A saved model keeps them in its folder, so they travel with the project and not with the person; a model open live keeps them in this workspace; a model not saved yet keeps them until its first save. When a switch was decided by something other than this model's own switch, 'note' says what. You can switch a suggestion off. Switching one on is the person's choice: it sends model details out, so the assistant is told to ask them.")]
        public static Task<DecisionPointInfo[]> SetDecisionPointEnabled(IEngine engine,
            [Description("The point name from get_decision_points, e.g. 'measure.folder_and_format'")] string name,
            [Description("true to allow it, false to switch it off")] bool enabled)
            => engine.SetDecisionPointEnabledAsync(name, enabled, "agent");

        [McpServerTool(Name = "get_consent"), Description("Show whether the person has agreed that THIS model's details may be sent for suggestions, which details that covers, and whether that agreement still stands. Permission belongs to one model at a time, so agreeing on one model says nothing about another. 'fieldsInForce' is the list to agree to. Nothing is ever sent without a current agreement. Reads only.")]
        public static Task<DecisionConsentInfo> GetConsent(IEngine engine)
            => engine.GetConsentAsync();

        [McpServerTool(Name = "set_consent"), Description("Ask the person to agree that the listed details of THE MODEL THAT IS OPEN NOW may be sent for suggestions. Only the person can agree: this call puts their own question, with the list of details, in the Waiting for you list, and answers asked true with the question. Nothing is agreed until they say yes there. After that, call set_consent again with the same list and the agreement is written. It covers that one model and no other. Pass the exact 'fieldsInForce' list and 'schemaVersionInForce' from get_consent. If the list of details changes in a later version, the agreement stops counting and has to be given again. A model that has never been saved and is not open live cannot be agreed for, because there is no name to keep the agreement under. The agreement is kept outside the model files, so it never travels in a model or a commit. A marker annotation, Semanticus_ConsentId, is written into the model so the agreement can find it again; it carries no agreement and shows in a commit diff.")]
        public static Task<DecisionConsentInfo> SetConsent(IEngine engine,
            [Description("The fields being agreed to, exactly as get_consent returned them in 'fieldsInForce'")] string[] fields,
            [Description("The 'schemaVersionInForce' value from get_consent")] int schemaVersion,
            [Description("Optional. The 'appliesTo' value get_consent returned when the question was asked. Pass it and the agreement is refused if a different model is open by the time it arrives, so an answer meant for one model can never land on another. Leave it out to agree for whatever is open now.")] string expectedScope = null)
            => engine.SetConsentAsync(fields, schemaVersion, "agent", expectedScope);

        [McpServerTool(Name = "withdraw_consent"), Description("Stop this model's details being sent for suggestions. Anything already on its way is cancelled, a reply that arrives late is thrown away, and every suggestion held in memory is dropped. It covers the model that is open now; another model's permission is left as it was. The model itself is untouched and every other operation keeps working exactly as before.")]
        public static Task<DecisionConsentInfo> WithdrawConsent(IEngine engine)
            => engine.WithdrawConsentAsync("agent");

        [McpServerTool(Name = "get_decision_budget"), Description("What the suggestions have used, in tokens and in US dollars. Two counts, and they are different numbers: the plain fields are THIS session on this machine (a count only: a session has no cap, capInputTokens and remainingInputTokens are null, and usedUsd is what it cost at Jev's price of USD 0.042 per million input tokens, output free), and the monthly ones are the whole licence's allowance for the calendar month (monthlyUsedUsd of monthlyCapUsd), read live from the service. A null monthly figure means it could not be read and monthlyReason says why in plain words; never read a null as a zero. Counts, money and a date only, never any content and never a credential. When the month's allowance runs out the product stops suggesting and behaves as it does with no service at all; nothing you can do offline is affected. Reads only.")]
        public static Task<DecisionBudgetInfo> GetDecisionBudget(IEngine engine)
            => engine.GetDecisionBudgetAsync();

        [McpServerTool(Name = "propose_measure_defaults"), Description("Suggest a display folder and a number format for a measure that was created without either. It suggests only: nothing is written, and the measure is exactly as you left it. When it cannot suggest, 'reason' says why in plain words (no service set up, no permission for this model, not Pro, switched off, or nothing sure enough). The formula itself is never sent, only facts worked out from it on this computer. Use set_display_folder and set_measure_format to act on a suggestion. Reads only.")]
        public static Task<MeasureDefaultsProposal> ProposeMeasureDefaults(IEngine engine,
            [Description("Measure ref, e.g. 'measure:Sales/Total Sales' (see list_measures)")] string objRef)
            => engine.ProposeMeasureDefaultsAsync(objRef);
    }
}

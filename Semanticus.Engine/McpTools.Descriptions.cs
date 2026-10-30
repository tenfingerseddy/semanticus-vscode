using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine;

public static partial class McpTools
{
    [McpServerTool(Name = "get_measure_description"), Description("Read a measure's full and one-line Description, person or AI provenance, match score and draft progress. Match scoring is on by default: 70 and up Matches, 50 to 69 Could be improved, below 50 Does not match. An unavailable score reads not scored yet. With version 7 permission, the score can run on Jev. The Description score sends the name, Description and formula to Jev. The model's decision setting can turn scoring off. Poll this while a draft is waiting.")]
    public static Task<MeasureDescription> GetMeasureDescription(IEngine engine, string measureRef) => engine.GetMeasureDescriptionAsync(measureRef);

    [McpServerTool(Name = "draft_measure_description"), Description("Request a missing Description from the connected assistant. Sends the formula, name, folder, related measures and known report visuals through the assistant request queue. Returns progress immediately. Claim the request, then complete_request with only the proposed Description text. Nothing changes in the model until keep_measure_description. Default give-up is 60 seconds.")]
    public static Task<DescriptionProposal> DraftMeasureDescription(IEngine engine, string measureRef, int timeoutSeconds = 60) => engine.DraftMeasureDescriptionAsync(measureRef, timeoutSeconds);

    [McpServerTool(Name = "propose_measure_description"), Description("Propose a better Description through the same assistant request queue. Includes the current Description and match reason. Complete the request with only the proposed Description text. Nothing changes in the model until keep_measure_description.")]
    public static Task<DescriptionProposal> ProposeMeasureDescription(IEngine engine, string measureRef, int timeoutSeconds = 60) => engine.ProposeMeasureDescriptionAsync(measureRef, timeoutSeconds);

    [McpServerTool(Name = "keep_measure_description"), Description("Keep the returned Description proposal in one undoable edit. Records AI provenance. Refuses an expired proposal or a measure that changed since the request. A later person's edit records person provenance.")]
    public static Task<SetResult> KeepMeasureDescription(IEngine engine, string proposalId) => engine.KeepMeasureDescriptionAsync(proposalId, "agent");
}

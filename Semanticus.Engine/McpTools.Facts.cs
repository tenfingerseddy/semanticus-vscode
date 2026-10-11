// The agent door's half of the model facts snapshot (M4 groundwork contract, section 1).
//
// The RPC door in EngineRpcTarget.cs exposes the same snapshot as getModelFacts. Both call the one IEngine method
// and return the one DTO, so the two doors cannot answer differently. The one difference is who is asking: this
// door is the agent: on a model connected to data it is refused only when the person has explicitly denied it
// QueryData on that target, and it never raises an approval request. The person on the RPC door is never gated.

using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Engine.Facts;

namespace Semanticus.Engine
{
    // Written Facts.ModelFacts throughout: Semanticus.Engine already has a ModelFacts (the workflow predicate's).
    public static partial class McpTools
    {
        [McpServerTool(Name = "get_model_facts"), Description("The open model's facts in one snapshot: every table and column with its settings (type, hidden, summarize by, category, sort by, format, key, relationship side), how many measures use each column, whether checked reports use it, and, when the model is connected to its own data, facts DERIVED from that data: distinct count, blank share, whole numbers, unique on every row, looks like a year, month, day or quarter, shares that look like places, links, image links or email addresses, same-table columns that pair one to one, storage bytes and cardinality. Also the storage findings the Storage tab shows. Never contains a row, a raw value, a minimum, a maximum or a sample. data is live, local or none; with none there are no measured blocks, which means unknown, not zero. A large table is measured on a bounded sample and says sampled. producers says where each fact came from, how much it covers and how fresh it is. Cached per model revision and report selection and rebuilt in the background after a change. No approval is asked for: the facts are counts and patterns, not rows. Refused only when the person has denied your data reads (QueryData) on the connected target. Reads only. Free.")]
        public static Task<Facts.ModelFacts> GetModelFacts(IEngine engine) => engine.GetModelFactsAsync(origin: "agent");
    }
}

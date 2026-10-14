// The agent door's half of the spec-first start (lane sf-doors; design-spec-first-start-v2.md).
//
// Eight operations. EngineRpcTarget.cs exposes the same eight as startModelFromSql, getStartPlan, editStartPlan,
// grantStartDisclosure, getStartWords, writeStartWords, buildStartPlan and discardStartPlan, and every one lands on the
// same IEngine method. McpTools.LeanResults.cs registers the compact MCP projections; these full-result helpers
// keep the existing .NET callers and the Studio on one StartPlanView.
//
// APPROVE IS approve_start_plan (Kane, 10 Oct 2026: the person's word in chat is enough). The approval is bound to the plan's
// digest, so any edit voids it. build_start_plan builds the approved plan, and is refused until it is approved.
//
// Every operation is free. Jev deciding for the person is the paid part, and it is drawn inside the engine, so a free
// session still gets the plan, its own assistant's words and one build. The copy follows docs/product-copy-style.md:
// no em dashes, plain words, and each description says what the operation will NOT do.

using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Engine.StartPlan;

namespace Semanticus.Engine
{
    public static partial class McpTools
    {
        public static Task<StartPlanView> StartModelFromSql(IEngine engine,
            string server,
            string database,
            string folder,
            string name,
            string storageMode = "import",
            string authMode = null,
            string tenantId = null)
            => engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = server, Database = database, Folder = folder, Name = name,
                StorageMode = string.IsNullOrWhiteSpace(storageMode) ? "import" : storageMode,
                AuthMode = string.IsNullOrWhiteSpace(authMode) ? null : authMode,
                TenantId = tenantId,
            }, "agent");

        public static Task<StartPlanView> GetStartPlan(IEngine engine)
            => engine.GetStartPlanAsync();

        public static Task<StartPlanView> EditStartPlan(IEngine engine,
            string op,
            string target,
            string value = null)
            => engine.EditStartPlanAsync(new StartPlanEdit { Op = op, Target = target, Value = value }, "agent");

        public static Task<StartPlanView> GrantStartDisclosure(IEngine engine,
            string digest)
            => engine.GrantStartDisclosureAsync(digest, "agent");

        public static Task<StartWordsPage> GetStartWords(IEngine engine,
            int max = StartPlanDoors.WordsPageDefault,
            int offset = 0)
            => engine.GetStartWordsAsync(StartPlanDoors.WordsPageSize(max), StartPlanDoors.WordsOffset(offset));

        public static Task<StartWordsResult> WriteStartWords(IEngine engine,
            StartWord[] words)
            => engine.WriteStartWordsAsync(words ?? System.Array.Empty<StartWord>(), "agent");

        public static Task<StartPlanView> ApproveStartPlan(IEngine engine,
            string digest)
            => engine.ApproveStartPlanAsync(digest, "agent");

        public static Task<StartPlanBuildResult> BuildStartPlan(IEngine engine,
            string digest)
            => engine.BuildStartPlanAsync(digest, "agent");

        public static Task<StartPlanView> DiscardStartPlan(IEngine engine)
            => engine.DiscardStartPlanAsync("agent");
    }
}

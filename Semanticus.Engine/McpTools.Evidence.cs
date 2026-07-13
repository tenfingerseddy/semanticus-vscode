using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    /// <summary>The agent door for the same model-scoped evidence library Studio shows inside Tests.</summary>
    [McpServerToolType]
    public static class McpToolsEvidence
    {
        [McpServerTool(Name = "list_evidence"), Description("EVIDENCE: list the sealed Test and Workflow evidence saved with the open model under `.semanticus/evidence/<model-identity>`. Each item includes type, producer, verdict with coverage, content signature, paths, and verification state. A changed or conflicted record remains listed as invalid and is never trusted or hidden. Free, read-only. Next: get_evidence(id) to review one; save_evidence(source) to explicitly retain the latest Test report or a terminal Workflow run.")]
        public static Task<EvidenceLibrary> ListEvidence(IEngine engine) => engine.ListEvidenceAsync();

        [McpServerTool(Name = "get_evidence"), Description("EVIDENCE: open one current-model record from list_evidence as canonical JSON plus its deterministic self-contained HTML view. The engine re-verifies the signature and HTML pairing before returning it; invalid evidence is refused with the exact reason. Free, read-only.")]
        public static Task<Semanticus.Engine.Evidence.EvidenceArtifact> GetEvidence(IEngine engine,
            [Description("Evidence id from list_evidence")] string id)
            => engine.GetEvidenceAsync(id);

        [McpServerTool(Name = "save_evidence"), Description("EVIDENCE: explicitly save an engine-owned artifact with the open model for source control and team review. source='tests' saves the latest current-model Test report and keeps its existing soft Pro boundary; source='workflow' saves a terminal Workflow run (sourceId may be omitted for the latest). The engine regenerates and verifies the artifact itself, then atomically writes canonical JSON plus matching HTML under `.semanticus/evidence/<model-identity>`. Re-saving the same source is idempotent. Nothing is written by export alone.")]
        public static async Task<EvidenceSaveResult> SaveEvidence(IEngine engine,
            [Description("'tests' for the latest Test run, or 'workflow' for a terminal Workflow run")] string source,
            [Description("Terminal workflow run id; omit for tests or the latest workflow run")] string sourceId = null)
        {
            var r = await engine.SaveEvidenceAsync(source, sourceId, "agent");
            McpTools.Emit(engine, new ActivityEvent
            {
                Kind = "save_evidence",
                Origin = "agent",
                Label = r.Saved ? "Saved evidence with the model" : r.Error != null ? "Evidence save failed" : "Evidence save refused",
                Target = r.Item?.Id,
                Ok = r.Saved,
                Error = r.Saved ? null : r.Error ?? r.Note,
                Result = r.Saved ? $"Saved {r.Item?.Kind} evidence '{r.Item?.Id}' with signature {r.Item?.ContentHash}. Next: list_evidence or get_evidence." : r.Note,
            });
            return r;
        }
    }
}

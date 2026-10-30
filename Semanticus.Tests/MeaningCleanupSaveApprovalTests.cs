using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class MeaningCleanupSaveApprovalTests : GateFixtureBase
{
    public MeaningCleanupSaveApprovalTests(RestoreRootFixture fixture) : base(fixture) { }

    [Fact]
    public async Task An_approved_save_includes_annotation_cleanup_without_another_question()
    {
        var (engine, sessions, _) = await OpenAsync("Contoso");
        using (engine)
        using (sessions)
        {
            await sessions.Current.MutateAsync("human", "Old annotations", model => {
                foreach (var measure in model.AllMeasures)
                {
                    measure.SetAnnotation("Semanticus.Checked.Meaning", "{}");
                    measure.SetAnnotation("Semanticus.Checked.MeaningWords", "Contoso sales");
                }
            });
            AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
            var assistant = Gate(engine);
            await Assert.ThrowsAnyAsync<System.InvalidOperationException>(() => assistant.SaveAsync(null, "bim", false, "agent"));
            await ApproveAsync(engine);
            var result = await assistant.SaveAsync(null, "bim", false, "agent");
            Assert.Contains("Undo", result.Note);
            Assert.Empty(await engine.ListPendingApprovalsAsync());
            Assert.True(await sessions.Current.ReadAsync(model => model.AllMeasures.All(m =>
                !m.HasAnnotation("Semanticus.Checked.Meaning") && !m.HasAnnotation("Semanticus.Checked.MeaningWords"))));
        }
    }
}

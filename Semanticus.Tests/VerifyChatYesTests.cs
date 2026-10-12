using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier for 1.4 chatyes (3a4dc874). The happy path end to end (the assistant's set_consent, then Jev answers), the strict
    /// readings that must survive (a hand-edited label nobody understands, on a loopback address too), and a prod-labelled target
    /// reached by a written variant of its own address.
    /// </summary>
    [Collection("restore-root")]
    public sealed class VerifyChatYesTests : GateFixtureBase
    {
        public VerifyChatYesTests(RestoreRootFixture fixture) : base(fixture) { }

        private static Semanticus.Engine.AgentPolicy Standard() => AgentPolicyPresets.Build("standard");

        [Theory]
        [InlineData("standard")]
        [InlineData("locked")]
        public async Task After_the_assistants_set_consent_Jev_answers(string preset)
        {
            using (var f = new DecisionFixture())
            {
                var measure = await f.BuildModelAsync();
                AgentPolicyStore.SetPreset(preset, "human");
                var gate = AssistantEngine.For(f.Engine);
                var info = await gate.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "agent");
                Assert.True(info.Current, info.Reason);
                Assert.Equal("agent", info.GrantedBy);

                var digest = await f.DiscoverDigestAsync(measure);
                f.UseRecorded(DecisionFixture.ProofFixture(digest, "Volume", 0.93, "currency", 0.97));
                var p = await McpTools.ProposeMeasureDefaults(gate, measure);
                Assert.True(p.Asked, p.Reason);
                Assert.Equal("Volume", p.ProposedDisplayFolder);
                Assert.Equal("currency", p.ProposedFormatFamily);
            }
        }

        [Fact]
        public void A_hand_edited_unknown_label_on_a_loopback_address_is_prod_not_local()
        {
            var endpoint = "localhost:5" + new Random().Next(1000, 9999);
            var r = ConnectionRegistry.Remember("xmla", endpoint, "Demo", "Demo", null, "azcli");
            // A hand edit of the saved file: SetLabel refuses a label outside the four, so write it the way a person in an editor would.
            var path = ConnectionRegistry.StatePath();
            var root = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path));
            Assert.True(SetLabelOn(root, r.Id, "staging"), "the record was not found in the file");
            System.IO.File.WriteAllText(path, root.ToJsonString());
            Assert.Equal("staging", ConnectionRegistry.Find(r.Id).Label);
            Assert.Equal("prod", LocalEngine.ResolveTargetLabel(endpoint, "Demo"));
        }

        // The person labelled the target production. The assistant writes the same address with a trailing slash or stray space:
        // before 1.4 a miss in FindByEndpoint read as prod (the strictest), so a variant was still held. Now a miss reads as dev.
        [Theory]
        [InlineData("/")]
        [InlineData(" ")]
        public void A_prod_labelled_target_written_with_a_trailing_variant_is_still_prod(string tail)
        {
            var endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Contoso Prod " + Guid.NewGuid().ToString("N").Substring(0, 6);
            var r = ConnectionRegistry.Remember("xmla", endpoint, "Sales", "Sales", null, "azcli");
            ConnectionRegistry.SetLabel(r.Id, "prod", "human");
            Assert.Equal("prod", LocalEngine.ResolveTargetLabel(endpoint, "Sales"));
            var label = LocalEngine.ResolveTargetLabel(endpoint + tail, "Sales");
            Assert.Equal(GateOutcome.Ask, AgentPolicyGuard.Decide(AgentCapability.DeployLive, label, "agent", true, Standard()).Outcome);
            Assert.Equal("prod", label);
        }

        private static bool SetLabelOn(System.Text.Json.Nodes.JsonNode n, string id, string label)
        {
            if (n is System.Text.Json.Nodes.JsonObject o)
            {
                foreach (var kv in o)
                    if (string.Equals(kv.Key, "id", StringComparison.OrdinalIgnoreCase) && kv.Value?.ToString() == id)
                    {
                        var key = o.Select(x => x.Key).FirstOrDefault(k => string.Equals(k, "label", StringComparison.OrdinalIgnoreCase)) ?? "Label";
                        o[key] = label;
                        return true;
                    }
                foreach (var kv in o.ToList()) if (kv.Value != null && SetLabelOn(kv.Value, id, label)) return true;
            }
            else if (n is System.Text.Json.Nodes.JsonArray a)
                foreach (var x in a) if (x != null && SetLabelOn(x, id, label)) return true;
            return false;
        }
    }
}

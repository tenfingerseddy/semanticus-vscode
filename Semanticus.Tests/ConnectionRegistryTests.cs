using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The one registry doing double duty: the connection history every surface reads (so nothing asks the user to
    /// retype an endpoint), and the target registry the agent-permissions matrix gates on. Both were about to be built
    /// separately, which would have guaranteed they drift.
    /// </summary>
    [Collection("restore-root")]   // mutates the static ConnectionRegistry root the gate family reads — serialize it
    public sealed class ConnectionRegistryTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public ConnectionRegistryTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-conn-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static ModelConnectionRecord Remember(string endpoint, string db = "DS", string authMode = "azcli")
            => ConnectionRegistry.Remember("xmla", endpoint, db, db, null, authMode);

        [Fact]
        public async Task Engine_can_import_legacy_xmla_history_without_connecting_or_storing_a_secret()
        {
            using var engine = new LocalEngine(new SessionManager());

            var imported = await engine.RememberXmlaConnectionAsync(" powerbi://x/legacy ", " Sales ", "Sales model", "interactive", "human");

            Assert.Equal("powerbi://x/legacy", imported.Endpoint);
            Assert.Equal("Sales", imported.Database);
            Assert.Equal("interactive", imported.AuthMode);
            Assert.True(ConnectionRegistry.IsUnlabelled(imported));
            Assert.Contains(await engine.ListConnectionsAsync(), r => r.Id == imported.Id);
            await Assert.ThrowsAsync<ArgumentException>(() => engine.RememberXmlaConnectionAsync("powerbi://x/bad", null, null, "password", "human"));
        }

        // ---- FAIL CLOSED. An unlabelled target is production. This is the ONE inference we make, and it is safe
        // because it is the strictest. ----
        [Fact]
        public void An_unlabelled_target_is_production()
        {
            var r = Remember("powerbi://api.powerbi.com/v1.0/myorg/Anything");
            Assert.True(ConnectionRegistry.IsUnlabelled(r));
            Assert.Equal("prod", ConnectionRegistry.EffectiveLabel(r));
        }

        [Fact]
        public void A_null_record_is_also_production_never_a_permissive_default()
        {
            Assert.Equal("prod", ConnectionRegistry.EffectiveLabel(null));
        }

        // ---- NEVER infer a label from an endpoint's NAME. Kane's real client endpoint is `.../UAT_SEM`, and for that
        // client UAT *is* dev — no DEV workspace exists. A name-based matcher would nag on every write to a sandbox.
        // Conversely someone's `SEM_TEST` could be the model the board reads. A name is a convention, not a fact. ----
        [Theory]
        [InlineData("powerbi://api.powerbi.com/v1.0/myorg/UAT_SEM")]
        [InlineData("powerbi://api.powerbi.com/v1.0/myorg/DEV_workspace")]
        [InlineData("powerbi://api.powerbi.com/v1.0/myorg/sandbox-test-local")]
        [InlineData("localhost:51234")]
        public void A_label_is_never_inferred_from_the_endpoint_name(string endpoint)
        {
            Assert.Equal("prod", ConnectionRegistry.EffectiveLabel(Remember(endpoint)));
        }

        // ---- An agent cannot label a target. Its own permissions are gated on the label, so relabelling prod as dev
        // would defeat the matrix that restrains it. ----
        [Fact]
        public void An_agent_cannot_label_a_target()
        {
            var r = Remember("powerbi://x/prod");
            var ex = Assert.Throws<InvalidOperationException>(() => ConnectionRegistry.SetLabel(r.Id, "dev", origin: "agent"));
            Assert.Contains("Only a human can label", ex.Message);
            Assert.Equal("prod", ConnectionRegistry.EffectiveLabel(ConnectionRegistry.Find(r.Id)));   // unchanged
        }

        // ---- The origin default is FAIL-CLOSED: a caller that omits it is treated as the agent, not the human. A
        // forgotten argument must never grant human privileges. ----
        [Fact]
        public void Labelling_with_an_unrecognised_origin_is_refused_like_an_agent()
        {
            var r = Remember("powerbi://x/origin");
            Assert.Throws<InvalidOperationException>(() => ConnectionRegistry.SetLabel(r.Id, "dev", origin: null));
            Assert.Throws<InvalidOperationException>(() => ConnectionRegistry.SetLabel(r.Id, "dev", origin: "rpc-but-not-declared-human"));
        }

        [Fact]
        public void A_human_can_label_and_clear_a_target()
        {
            var r = Remember("powerbi://x/one");
            Assert.Equal("dev", ConnectionRegistry.SetLabel(r.Id, "DEV", "human").Label);       // normalized
            Assert.Equal("dev", ConnectionRegistry.EffectiveLabel(ConnectionRegistry.Find(r.Id)));

            ConnectionRegistry.SetLabel(r.Id, null, "human");                                   // cleared
            Assert.Equal("prod", ConnectionRegistry.EffectiveLabel(ConnectionRegistry.Find(r.Id)));   // back to strictest
        }

        // ---- A typo must not silently become a label nobody's policy matches. ----
        [Fact]
        public void An_unknown_label_is_refused()
        {
            var r = Remember("powerbi://x/typo");
            Assert.Throws<ArgumentException>(() => ConnectionRegistry.SetLabel(r.Id, "produciton", "human"));
            Assert.Null(ConnectionRegistry.Find(r.Id).Label);
        }

        // ---- A label hand-edited into the file that is NOT one we understand reads as prod, not as itself: a policy
        // switch must never receive a value it never expected. ----
        [Theory]
        [InlineData("root")]
        [InlineData("Production")]   // ToLower → "production", not the whitelisted "prod"
        [InlineData("staging")]
        [InlineData("")]
        public void An_out_of_whitelist_label_on_disk_reads_as_prod(string raw)
        {
            var rec = new ModelConnectionRecord { Label = raw };
            Assert.Equal("prod", ConnectionRegistry.EffectiveLabel(rec));
            Assert.True(ConnectionRegistry.IsUnlabelled(rec));   // ...and counts as unlabelled for retention/telemetry
        }

        [Fact]
        public void A_recognised_label_with_stray_case_and_space_still_reads_correctly()
        {
            Assert.Equal("uat", ConnectionRegistry.EffectiveLabel(new ModelConnectionRecord { Label = "  UAT " }));
        }

        // ---- Re-connecting must NEVER unlabel a target: that would turn a declaration back into an inference. ----
        [Fact]
        public void Reconnecting_preserves_the_label_and_the_working_folder()
        {
            var r = Remember("powerbi://x/keep");
            ConnectionRegistry.SetLabel(r.Id, "uat", "human");
            ConnectionRegistry.SetWorkingFolder(r.Id, _root);

            Remember("powerbi://x/keep");   // connect again

            var again = ConnectionRegistry.Find(r.Id);
            Assert.Equal("uat", again.Label);
            Assert.Equal(Path.GetFullPath(_root), again.WorkingFolder);
            Assert.Equal(2, again.UseCount);
        }

        [Fact]
        public void Desktop_source_can_link_a_separate_xmla_publish_target_without_touching_model_files()
        {
            var source = ConnectionRegistry.Remember("localDesktop", "localhost:51234", "Local", "Local source");
            var publish = Remember("powerbi://x/published");

            ConnectionRegistry.SetWorkingCopy(source.Id, _root, publish.Id);

            var linked = ConnectionRegistry.Find(source.Id);
            Assert.Equal(Path.GetFullPath(_root), linked.WorkingFolder);
            Assert.Equal(publish.Id, linked.PublishConnectionId);
            Assert.Empty(Directory.GetFiles(_root, "*.bim", SearchOption.AllDirectories));

            Assert.True(ConnectionRegistry.Forget(publish.Id, "agent"));
            Assert.Null(ConnectionRegistry.Find(source.Id).PublishConnectionId);
        }

        // ---- IdFor is delimiter-safe: no two distinct targets collide by moving a separator across the boundary. ----
        [Fact]
        public void Targets_that_differ_only_by_where_a_pipe_sits_are_distinct()
        {
            var a = ConnectionRegistry.Remember("xmla", "powerbi://x/a|b", "c");
            var b = ConnectionRegistry.Remember("xmla", "powerbi://x/a", "b|c");
            Assert.NotEqual(a.Id, b.Id);

            ConnectionRegistry.SetLabel(a.Id, "prod", "human");
            ConnectionRegistry.SetLabel(b.Id, "dev", "human");
            Assert.Equal("prod", ConnectionRegistry.EffectiveLabel(ConnectionRegistry.Find(a.Id)));
            Assert.Equal("dev", ConnectionRegistry.EffectiveLabel(ConnectionRegistry.Find(b.Id)));   // a's label did NOT bleed onto b
        }

        // ---- An unreadable EXISTING registry must not be overwritten by the next connect — that would destroy labels
        // on a transient read error. It is moved aside, and its bytes survive for recovery. ----
        [Fact]
        public void A_corrupt_registry_is_preserved_not_overwritten_on_the_next_write()
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, "connections.json");
            File.WriteAllText(path, "{ not json — but the user's labels were in here");

            Remember("powerbi://x/after");   // a connect triggers a write

            Assert.Single(ConnectionRegistry.List());                        // fresh history
            Assert.Contains(Directory.GetFiles(_root), f => f.Contains(".corrupt-"));   // the old bytes were kept
        }

        [Fact]
        public void The_same_target_is_one_record_and_the_newest_use_is_first()
        {
            var a = Remember("powerbi://x/a");
            var b = Remember("powerbi://x/b");
            Remember("powerbi://x/a");   // a used again

            var all = ConnectionRegistry.List();
            Assert.Equal(2, all.Count);
            Assert.Equal(a.Id, all[0].Id);   // most recent first
            Assert.Equal(b.Id, all[1].Id);
        }

        // ---- The dataset is part of a target's identity: two datasets on one endpoint are two targets, and may carry
        // different labels. ----
        [Fact]
        public void Two_datasets_on_one_endpoint_are_two_targets()
        {
            var a = ConnectionRegistry.Remember("xmla", "powerbi://x/ws", "SalesProd");
            var b = ConnectionRegistry.Remember("xmla", "powerbi://x/ws", "SalesScratch");
            Assert.NotEqual(a.Id, b.Id);

            ConnectionRegistry.SetLabel(b.Id, "dev", "human");
            Assert.Equal("prod", ConnectionRegistry.EffectiveLabel(ConnectionRegistry.Find(a.Id)));
            Assert.Equal("dev", ConnectionRegistry.EffectiveLabel(ConnectionRegistry.Find(b.Id)));
        }

        // ---- Retention must never evict a target the user LABELLED. Dropping a `prod` declaration would silently turn
        // it back into an inference — the one thing this file exists to prevent. ----
        [Fact]
        public void Retention_never_evicts_a_labelled_target()
        {
            var precious = Remember("powerbi://x/precious");
            ConnectionRegistry.SetLabel(precious.Id, "prod", "human");

            for (var i = 0; i < ConnectionRegistry.MaxRecords + 5; i++) Remember($"powerbi://x/filler{i}");

            Assert.NotNull(ConnectionRegistry.Find(precious.Id));
            Assert.Equal("prod", ConnectionRegistry.Find(precious.Id).Label);
        }

        [Fact]
        public void Retention_evicts_the_least_recently_used_unlabelled_target()
        {
            var oldest = Remember("powerbi://x/oldest");
            for (var i = 0; i < ConnectionRegistry.MaxRecords + 2; i++) Remember($"powerbi://x/filler{i}");
            Assert.Null(ConnectionRegistry.Find(oldest.Id));
        }

        // ---- No secrets on disk, ever. authMode is a mode NAME. ----
        [Fact]
        public void The_registry_file_holds_no_secret()
        {
            ConnectionRegistry.Remember("xmla", "powerbi://x/ws", "DS", "DS", "tenant-123", "serviceprincipal");
            var text = File.ReadAllText(Path.Combine(_root, "connections.json"));
            Assert.Contains("serviceprincipal", text);   // the mode NAME is fine — it is not a credential

            // Assert on the property NAMES the record persists, not on substrings of the file: an endpoint is free text
            // and could contain any word, so a substring scan tests the fixture rather than the schema.
            var keys = System.Text.Json.JsonDocument.Parse(text).RootElement
                .EnumerateArray().SelectMany(o => o.EnumerateObject().Select(p => p.Name.ToLowerInvariant())).ToHashSet();

            Assert.DoesNotContain("password", keys);
            Assert.DoesNotContain("token", keys);
            Assert.DoesNotContain("accesstoken", keys);
            Assert.DoesNotContain("secret", keys);
            Assert.DoesNotContain("clientsecret", keys);
            Assert.DoesNotContain("connectionstring", keys);
        }

        // ---- A corrupt registry degrades to "no history" — the pre-registry behaviour — rather than breaking connect
        // on a path the user cannot fix from inside the app. ----
        [Fact]
        public void A_corrupt_registry_does_not_break_connecting()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "connections.json"), "{ this is not json");

            Assert.Empty(ConnectionRegistry.List());
            var r = Remember("powerbi://x/after-corruption");   // must not throw
            Assert.Equal("prod", ConnectionRegistry.EffectiveLabel(r));
        }

        [Fact]
        public void Forget_removes_exactly_one()
        {
            var a = Remember("powerbi://x/forget-me");
            Remember("powerbi://x/keep-me");
            Assert.True(ConnectionRegistry.Forget(a.Id, "human"));
            Assert.False(ConnectionRegistry.Forget(a.Id, "human"));   // already gone
            Assert.Single(ConnectionRegistry.List());
        }

        // ---- An agent may forget an unlabelled scratch connection (harmless — fails closed), but NOT a labelled one:
        // its label is governance data, not the agent's to drop. ----
        [Fact]
        public void An_agent_can_forget_an_unlabelled_connection_but_not_a_labelled_one()
        {
            var scratch = Remember("powerbi://x/scratch");
            Assert.True(ConnectionRegistry.Forget(scratch.Id, "agent"));   // unlabelled → fine

            var labelled = Remember("powerbi://x/governed");
            ConnectionRegistry.SetLabel(labelled.Id, "prod", "human");
            Assert.Throws<InvalidOperationException>(() => ConnectionRegistry.Forget(labelled.Id, "agent"));
            Assert.NotNull(ConnectionRegistry.Find(labelled.Id));          // still there

            Assert.True(ConnectionRegistry.Forget(labelled.Id, "human"));  // the human can
        }
    }
}

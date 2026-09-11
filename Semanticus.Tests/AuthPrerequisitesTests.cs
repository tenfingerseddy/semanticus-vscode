using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// probe_auth_prerequisites: a read-only PREVIEW of whether an ADVANCED sign-in mode can actually authenticate on
    /// this machine, so a picker can warn BEFORE a connect attempt rather than failing after one. PRESENCE + NAMES
    /// ONLY — never a secret value — and the probe itself never triggers an interactive prompt or a network sign-in.
    /// Covers 'serviceprincipal' (env-var presence, sharing EntraToken's one source of truth for the accepted names
    /// with the real credential build) and the safe fallback for every other mode. The 'azcli' branch shells out to
    /// the local CLI and is genuinely environment-dependent, so it only gets a no-throw/no-hang contract check here —
    /// never a test that depends on a real `az login`.
    /// </summary>
    public sealed class AuthPrerequisitesTests
    {
        // The full accepted-name surface this probe reads, saved/restored around every test so a real developer's or
        // CI's own value is never clobbered and no mutation leaks past this test — mirrors the save/restore pattern
        // this suite already uses for other process-wide environment state (e.g. USERPROFILE in ExperienceLogTests).
        private static readonly string[] AllEnvNames =
        {
            "AZURE_CLIENT_ID", "FABRIC_CLIENT", "POWERBI_CLIENT_ID",
            "AZURE_CLIENT_SECRET", "FABRIC_SECRET", "POWERBI_CLIENT_SECRET",
            "AZURE_TENANT_ID", "FABRIC_TENANT", "POWERBI_TENANT_ID",
        };

        private static Dictionary<string, string> SnapshotEnv()
            => AllEnvNames.ToDictionary(n => n, Environment.GetEnvironmentVariable);

        private static void RestoreEnv(Dictionary<string, string> snapshot)
        {
            foreach (var kv in snapshot) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }

        private static void ClearAllEnv()
        {
            foreach (var n in AllEnvNames) Environment.SetEnvironmentVariable(n, null);
        }

        [Fact]
        public async Task Serviceprincipal_reports_all_three_missing_when_no_env_is_set()
        {
            var snap = SnapshotEnv();
            try
            {
                ClearAllEnv();
                using var engine = new LocalEngine(new SessionManager());
                var result = await engine.ProbeAuthPrerequisitesAsync("serviceprincipal", null);

                Assert.Equal("serviceprincipal", result.Mode);
                Assert.False(result.Ready);
                Assert.False(result.KeyVaultSupported);
                Assert.Equal(3, result.Requirements.Length);
                Assert.All(result.Requirements, r => Assert.False(r.Present));
                Assert.Contains(result.Requirements, r => r.Label == "Client ID");
                Assert.Contains(result.Requirements, r => r.Label == "Client secret");
                Assert.Contains(result.Requirements, r => r.Label == "Tenant");
            }
            finally { RestoreEnv(snap); }
        }

        [Fact]
        public async Task Serviceprincipal_becomes_ready_when_all_three_env_vars_are_set()
        {
            var snap = SnapshotEnv();
            try
            {
                ClearAllEnv();
                Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "11111111-1111-1111-1111-111111111111");
                Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "not-a-real-secret-value");
                Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "22222222-2222-2222-2222-222222222222");
                using var engine = new LocalEngine(new SessionManager());
                var result = await engine.ProbeAuthPrerequisitesAsync("sp", null);   // the 'sp' alias for serviceprincipal

                Assert.True(result.Ready);
                Assert.All(result.Requirements, r => Assert.True(r.Present));
            }
            finally { RestoreEnv(snap); }
        }

        [Fact]
        public async Task An_alias_env_name_satisfies_presence_exactly_like_the_primary_azure_name()
        {
            var snap = SnapshotEnv();
            try
            {
                ClearAllEnv();
                // FABRIC_CLIENT / FABRIC_SECRET / FABRIC_TENANT are accepted aliases, not just the AZURE_* names.
                Environment.SetEnvironmentVariable("FABRIC_CLIENT", "client-alias");
                Environment.SetEnvironmentVariable("FABRIC_SECRET", "secret-alias");
                Environment.SetEnvironmentVariable("FABRIC_TENANT", "tenant-alias");
                using var engine = new LocalEngine(new SessionManager());
                var result = await engine.ProbeAuthPrerequisitesAsync("serviceprincipal", null);

                Assert.True(result.Ready);
                Assert.All(result.Requirements, r => Assert.True(r.Present));
            }
            finally { RestoreEnv(snap); }
        }

        [Fact]
        public async Task A_supplied_tenant_id_satisfies_the_tenant_requirement_even_with_no_env_var_set()
        {
            var snap = SnapshotEnv();
            try
            {
                ClearAllEnv();
                Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "client");
                Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "secret");
                // No AZURE_TENANT_ID / FABRIC_TENANT / POWERBI_TENANT_ID set at all — only the caller-supplied tenantId.
                using var engine = new LocalEngine(new SessionManager());
                var result = await engine.ProbeAuthPrerequisitesAsync("serviceprincipal", "contoso.onmicrosoft.com");

                Assert.True(result.Ready);
                var tenantReq = result.Requirements.Single(r => r.Label == "Tenant");
                Assert.True(tenantReq.Present);
            }
            finally { RestoreEnv(snap); }
        }

        [Fact]
        public async Task The_result_never_carries_a_secret_value_only_names_and_presence_flags()
        {
            var snap = SnapshotEnv();
            try
            {
                ClearAllEnv();
                var secretValue = "SUPER-SECRET-" + Guid.NewGuid().ToString("N");
                Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "client-id-value");
                Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", secretValue);
                Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "tenant-value");
                using var engine = new LocalEngine(new SessionManager());
                var result = await engine.ProbeAuthPrerequisitesAsync("serviceprincipal", null);

                // Serialize the whole payload (as either door would put it on the wire) and scan for the secret VALUE.
                var json = JsonSerializer.Serialize(result);
                Assert.DoesNotContain(secretValue, json);
                Assert.DoesNotContain("client-id-value", json);   // not just the secret — no env VALUE at all, only names/labels/flags
                Assert.DoesNotContain("tenant-value", json);
                // Every EnvNames entry is an accepted NAME, never the value we set behind it.
                foreach (var r in result.Requirements)
                    Assert.All(r.EnvNames, n => Assert.DoesNotContain(secretValue, n));
            }
            finally { RestoreEnv(snap); }
        }

        [Fact]
        public async Task Every_mode_other_than_serviceprincipal_or_azcli_reports_ready_with_no_requirements()
        {
            using var engine = new LocalEngine(new SessionManager());
            foreach (var mode in new[] { "interactive", "devicecode", "token", "somethingElse" })
            {
                var result = await engine.ProbeAuthPrerequisitesAsync(mode, null);
                Assert.True(result.Ready);
                Assert.False(result.KeyVaultSupported);
            }
        }

        [Fact]
        public async Task Mode_is_normalized_for_case_and_surrounding_whitespace()
        {
            var snap = SnapshotEnv();
            try
            {
                ClearAllEnv();
                using var engine = new LocalEngine(new SessionManager());
                var result = await engine.ProbeAuthPrerequisitesAsync("  ServicePrincipal  ", null);
                Assert.Equal("serviceprincipal", result.Mode);
                Assert.Equal(3, result.Requirements.Length);
            }
            finally { RestoreEnv(snap); }
        }

        [Fact]
        public async Task An_empty_mode_defaults_to_the_azcli_probe()
        {
            using var engine = new LocalEngine(new SessionManager());
            var result = await engine.ProbeAuthPrerequisitesAsync("", null);
            Assert.Equal("azcli", result.Mode);   // never a blank mode name in the returned DTO
        }

        // The azcli branch shells out to the local CLI and is genuinely environment-dependent (this machine may or may
        // not have `az` installed / signed in) — we only assert the CONTRACT: it completes, never throws, never hangs
        // the engine, and Ready is a genuine bool either way. This deliberately never depends on / drives a real `az
        // login` — CLAUDE.md's golden rule and the task spec both require the probe to stay silent and local-only.
        [Fact]
        public async Task Azcli_probe_never_throws_and_returns_a_well_formed_result_regardless_of_this_machines_cli_state()
        {
            using var engine = new LocalEngine(new SessionManager());
            var result = await engine.ProbeAuthPrerequisitesAsync("azcli", null);
            Assert.Equal("azcli", result.Mode);
            Assert.False(result.KeyVaultSupported);
            if (!result.Ready) Assert.False(string.IsNullOrWhiteSpace(result.Detail));   // an honest, actionable note when not ready
        }

        [Fact]
        public async Task The_probe_is_reachable_from_the_MCP_door()
        {
            var snap = SnapshotEnv();
            try
            {
                ClearAllEnv();
                Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "x");
                Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "y");
                Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "z");
                using var engine = new LocalEngine(new SessionManager());
                var result = await McpTools.ProbeAuthPrerequisites(engine, "serviceprincipal", null);
                Assert.True(result.Ready);
                Assert.Equal(3, result.Requirements.Length);
            }
            finally { RestoreEnv(snap); }
        }
    }
}

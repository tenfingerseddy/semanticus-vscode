using System;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Verified Mode — the human-controlled Pro toggle. Turning it ON is Pro-gated; when ON, a mutating DAX
    /// edit must validate before it commits (v1 verification floor — invalid DAX refused). All deterministic + offline.</summary>
    public sealed class VerifiedModeTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }
        private static async Task<LocalEngine> OpenAsync(bool pro)
        {
            var e = new LocalEngine(new SessionManager(), new Fake(pro));
            await e.OpenAsync(TestModels.FindBim());
            return e;
        }
        private static async Task<string> FirstMeasureRefAsync(LocalEngine e)
        {
            var ms = await e.ListMeasuresAsync();
            Assert.NotEmpty(ms);
            return ms[0].Ref;
        }
        private static async Task<string> FirstTableRefAsync(LocalEngine e)
        {
            var ms = await e.ListMeasuresAsync();
            Assert.NotEmpty(ms);
            return "table:" + ms[0].Table;   // a real, resolvable table so a create test can't pass for the wrong reason
        }

        [Fact]
        public async Task Free_cannot_enable_verified_mode()
        {
            using var e = await OpenAsync(pro: false);
            await Assert.ThrowsAsync<EntitlementException>(() => e.SetVerifiedModeAsync(true, "human"));
            Assert.False((await e.GetVerifiedModeAsync()).Enabled);   // stayed off (thrown before the flip)
        }

        [Fact]
        public async Task Free_may_disable_verified_mode()
        {
            using var e = await OpenAsync(pro: false);
            var r = await e.SetVerifiedModeAsync(false, "human");   // turning OFF is always allowed
            Assert.False(r.Enabled);
        }

        [Fact]
        public async Task Pro_can_enable_verified_mode()
        {
            using var e = await OpenAsync(pro: true);
            var r = await e.SetVerifiedModeAsync(true, "human");
            Assert.True(r.Enabled);
            Assert.True(r.Available);
        }

        [Fact]
        public async Task Verified_mode_refuses_invalid_dax()
        {
            using var e = await OpenAsync(pro: true);
            await e.SetVerifiedModeAsync(true, "human");
            var r = await FirstMeasureRefAsync(e);
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.SetDaxAsync(r, "SUM(", "agent"));
        }

        [Fact]
        public async Task Verified_mode_allows_valid_dax()
        {
            using var e = await OpenAsync(pro: true);
            await e.SetVerifiedModeAsync(true, "human");
            var r = await FirstMeasureRefAsync(e);
            var res = await e.SetDaxAsync(r, "1 + 1", "agent");   // valid → must not throw
            Assert.NotNull(res);
        }

        [Fact]
        public async Task Mode_off_lets_invalid_dax_through_as_before()
        {
            using var e = await OpenAsync(pro: true);   // Verified Mode OFF by default
            var r = await FirstMeasureRefAsync(e);
            var res = await e.SetDaxAsync(r, "SUM(", "agent");   // no verification when off — today's behavior
            Assert.NotNull(res);
        }
    }
}

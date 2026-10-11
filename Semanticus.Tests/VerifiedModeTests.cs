using System;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Record verified edits (wire name set_verified_mode): the human-controlled audit switch, free for
    /// everyone since 2026-09-15. Every mutating DAX edit must validate before it commits whatever the switch says;
    /// ON adds the audit record. All deterministic + offline.</summary>
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

        /// <summary>The toggle used to stop here on free. The free tier gets the whole floor now, not just the flag:
        /// the mode turns on, reports itself available, and a free invalid-DAX edit is refused by it.</summary>
        [Fact]
        public async Task Free_enables_verified_mode_and_gets_the_verification_floor()
        {
            using var e = await OpenAsync(pro: false);
            var state = await e.SetVerifiedModeAsync(true, "human");
            Assert.True(state.Enabled);
            Assert.True(state.Available);
            Assert.True((await e.GetVerifiedModeAsync()).Enabled);

            var r = await FirstMeasureRefAsync(e);
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.SetDaxAsync(r, "SUM(", "agent"));
        }

        /// <summary>Kane, 24 Sep: the switch only adds an audit record, so both of its notes name it for that and
        /// say every write is checked either way.</summary>
        [Fact]
        public async Task Notes_name_the_switch_Record_verified_edits()
        {
            using var e = await OpenAsync(pro: false);
            foreach (var on in new[] { true, false })
            {
                var note = (await e.SetVerifiedModeAsync(on, "human")).Note;
                Assert.Contains("Record verified edits", note, StringComparison.Ordinal);
                Assert.DoesNotContain("Verified Mode", note, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("every DAX write", note, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Free_may_disable_verified_mode()
        {
            using var e = await OpenAsync(pro: false);
            var r = await e.SetVerifiedModeAsync(false, "human");
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
        public async Task Mode_off_still_refuses_invalid_syntax()
        {
            using var e = await OpenAsync(pro: true);   // Verified Mode OFF by default
            var r = await FirstMeasureRefAsync(e);
            var before = await e.GetDaxAsync(r);
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.SetDaxAsync(r, "SUM(", "agent"));
            Assert.Equal(before, await e.GetDaxAsync(r));
        }
    }
}

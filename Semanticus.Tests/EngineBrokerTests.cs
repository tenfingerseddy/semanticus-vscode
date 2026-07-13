using System;
using System.Diagnostics;
using System.IO;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// EngineBroker liveness — the owner-election spine. Pinned here (sol review follow-up): PID existence is not
    /// liveness (the OS recycles pids), and a NAME check alone is unreliable in both launch shapes (dev is
    /// dotnet-hosted, so EVERY .NET process is "dotnet"). The recorded (pid, process-START-TIME) pair is the
    /// standard PID-reuse killer; a record WITHOUT the identity fields is a legacy engine's and falls back to the
    /// old pid+name check so an upgrade never orphans a running old-version owner.
    /// </summary>
    public sealed class EngineBrokerTests
    {
        private static EngineInfo SelfInfo(string startUtc = null, string exePath = null) => new EngineInfo
        {
            PipeName = "semanticus-test",
            Pid = Environment.ProcessId,
            StartedUtc = DateTime.UtcNow.ToString("o"),
            Workspace = "test",
            ProcessStartUtc = startUtc,
            ExePath = exePath,
        };

        [Fact]
        public void IsAlive_rejects_a_recycled_pid_via_a_mismatched_start_time()
        {
            // Same pid as THIS live process (so the pid check passes), but a start time an hour off — exactly what a
            // recycled pid looks like. The old name-only check would say alive (dev: everything is "dotnet").
            var wrong = Process.GetCurrentProcess().StartTime.ToUniversalTime().AddHours(-1).ToString("o");
            Assert.False(EngineBroker.IsAlive(SelfInfo(startUtc: wrong)));

            // The true start time (small tolerance) verifies: this really is the recorded process.
            var right = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o");
            Assert.True(EngineBroker.IsAlive(SelfInfo(startUtc: right)));

            // A new-format record whose identity can't even be parsed is untrustworthy — not alive.
            Assert.False(EngineBroker.IsAlive(SelfInfo(startUtc: "not-a-timestamp")));
        }

        [Fact]
        public void IsAlive_legacy_record_falls_back_to_pid_plus_name()
        {
            // No identity fields = a record written by a PRE-upgrade engine. It must still be honored via the old
            // pid+name check — an upgrade must not orphan a running old-version owner.
            Assert.True(EngineBroker.IsAlive(SelfInfo()));

            // And a dead/nonexistent pid stays dead on any format.
            Assert.False(EngineBroker.IsAlive(new EngineInfo { Pid = int.MaxValue - 7 }));
            Assert.False(EngineBroker.IsAlive(null));
        }

        [Fact]
        public void WriteInfo_stamps_the_owner_identity_and_round_trips()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-broker-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                EngineBroker.WriteInfo(ws, new EngineInfo
                {
                    PipeName = "semanticus-test",
                    Pid = Environment.ProcessId,
                    StartedUtc = DateTime.UtcNow.ToString("o"),
                    Workspace = ws,
                });
                var read = EngineBroker.ReadInfo(ws);
                Assert.NotNull(read);
                Assert.False(string.IsNullOrEmpty(read.ProcessStartUtc));   // identity stamped at the write chokepoint
                Assert.True(EngineBroker.IsAlive(read));                    // and it verifies against the live process
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- an EMPTY (not just null) identity field is still stamped ------------------------------------------------
        // Review follow-up (sol): `??=` only stamps a NULL field, so an empty-string ProcessStartUtc slipped through
        // unstamped and IsAlive's empty check routed it to the LEGACY pid+name fallback (losing the PID-reuse killer).
        // IsNullOrEmpty guards now stamp an empty field too. Neuter: swap the guards back to `??=` and the empty string
        // round-trips empty → the stamped assertion (and the identity-path IsAlive) fail.
        [Fact]
        public void WriteInfo_stamps_identity_even_when_the_field_is_empty_not_just_null()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-broker-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                EngineBroker.WriteInfo(ws, new EngineInfo
                {
                    PipeName = "semanticus-test",
                    Pid = Environment.ProcessId,
                    StartedUtc = DateTime.UtcNow.ToString("o"),
                    Workspace = ws,
                    ProcessStartUtc = "",   // present but empty — the bypass case
                    ExePath = "",
                });
                var read = EngineBroker.ReadInfo(ws);
                Assert.NotNull(read);
                Assert.False(string.IsNullOrEmpty(read.ProcessStartUtc));   // stamped despite the empty input
                Assert.False(string.IsNullOrEmpty(read.ExePath));
                Assert.True(EngineBroker.IsAlive(read));                    // verifies via the identity path, not the legacy fallback
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }
    }
}

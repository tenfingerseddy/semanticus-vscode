using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        // ---- D-118: a live OWNER must not read as dead because its executable name contains a dot ------------------
        // The packaged engine on Unix is a self-contained apphost named "Semanticus.Engine" with NO extension. The
        // name check derived the expected name with Path.GetFileNameWithoutExtension, which treats ".Engine" as an
        // extension and yields "Semanticus" — so IsAlive reported a LIVE owner as dead, and the agent door could
        // never join it (measured on Linux, 2026-09-11). The Windows apphost carries ".exe", so the same code
        // matched there and hid the defect from every Windows run.
        [Fact]
        public void IsAlive_accepts_a_live_owner_whose_executable_file_name_contains_a_dot()
        {
            if (OperatingSystem.IsWindows())
                return;   // the Windows apphost is "Semanticus.Engine.exe"; the bare dotted name is a Unix shape.

            // A real, live process whose executable file is named like the product's Unix apphost. Its identity
            // (pid + start time) is genuine, which is what makes this a faithful reproduction of the door's gate.
            var exe = CopyLongLivedBinary("Semanticus.Engine");
            Assert.NotNull(exe);
            Process child = null;
            try
            {
                child = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = "60", UseShellExecute = false });
                Assert.NotNull(child);
                var info = new EngineInfo
                {
                    PipeName = "semanticus-test",
                    Workspace = "test",
                    Pid = child.Id,
                    ProcessStartUtc = child.StartTime.ToUniversalTime().ToString("o"),
                    ExePath = exe,
                };
                Assert.True(EngineBroker.IsAlive(info), "a live process named 'Semanticus.Engine' was reported dead");
            }
            finally
            {
                try { if (child != null && !child.HasExited) child.Kill(); } catch { }
                try { child?.Dispose(); } catch { }
                try { Directory.Delete(Path.GetDirectoryName(exe), recursive: true); } catch { }
            }
        }

        // The spawn above is Unix-only (the Windows apphost carries .exe), so the derivation rule is pinned here on
        // BOTH platforms: what name counts as a match for a given executable path, and which differences are real.
        [Fact]
        public void Process_name_matching_handles_the_apphost_extension_rule_and_kernel_truncation()
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows reports "Semanticus.Engine" for the apphost file "Semanticus.Engine.exe" — matching, and
                // the bare stem "Semanticus" is NOT a match (it must not accept an unrelated process).
                Assert.True(EngineBroker.ProcessNameMatches(@"C:\app\Semanticus.Engine.exe", "Semanticus.Engine"));
                Assert.False(EngineBroker.ProcessNameMatches(@"C:\app\Semanticus.Engine.exe", "Semanticus"));
            }
            else
            {
                // Unix keeps the dotted file name and compares it whole.
                Assert.True(EngineBroker.ProcessNameMatches("/opt/semanticus/Semanticus.Engine", "Semanticus.Engine"));
                // A 15-character kernel record is the same process (the truncation the old check could not see).
                Assert.True(EngineBroker.ProcessNameMatches("/opt/semanticus/Semanticus.Engine", "Semanticus.Engi"));
                // An unrelated shorter name is a genuine mismatch, not truncation.
                Assert.False(EngineBroker.ProcessNameMatches("/opt/semanticus/Semanticus.Engine", "Semanticus"));
                Assert.False(EngineBroker.ProcessNameMatches("/opt/semanticus/Semanticus.Engine", "Something Else"));
            }

            // Absent evidence never contradicts: a record with no source, or a process the OS won't name.
            Assert.True(EngineBroker.ProcessNameMatches(null, "anything"));
            Assert.True(EngineBroker.ProcessNameMatches("", "anything"));
            Assert.True(EngineBroker.ProcessNameMatches("/opt/semanticus/Semanticus.Engine", null));
        }

        /// <summary>Copies a long-lived system binary to <paramref name="name"/> in a temp folder so a test can run
        /// a real process under that exact executable file name. Null when the platform has no such binary.</summary>
        private static string CopyLongLivedBinary(string name)
        {
            var source = new[] { "/bin/sleep", "/usr/bin/sleep" }.FirstOrDefault(File.Exists);
            if (source == null) return null;
            var dir = Path.Combine(Path.GetTempPath(), "smx-name-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, name);
            File.Copy(source, target);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return target;
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

        [Fact]
        public void Executable_provenance_distinguishes_legacy_records_from_genuine_mismatches()
        {
            var current = EngineBroker.CurrentExecutablePath();
            Assert.False(string.IsNullOrWhiteSpace(current));
            Assert.True(EngineBroker.HasExecutableProvenance(SelfInfo(exePath: current)));
            Assert.True(EngineBroker.ExecutableMatches(SelfInfo(exePath: current)));

            var different = Path.Combine(Path.GetDirectoryName(current) ?? Path.GetTempPath(), "other-" + Path.GetFileName(current));
            Assert.True(EngineBroker.HasExecutableProvenance(SelfInfo(exePath: different)));
            Assert.False(EngineBroker.ExecutableMatches(SelfInfo(exePath: different)));
            Assert.False(EngineBroker.HasExecutableProvenance(SelfInfo(exePath: "")));
            Assert.False(EngineBroker.HasExecutableProvenance(null));
            Assert.False(EngineBroker.ExecutableMatches(SelfInfo(exePath: "")));
            Assert.False(EngineBroker.ExecutableMatches(null));

            if (OperatingSystem.IsWindows())
                Assert.True(EngineBroker.ExecutableMatches(SelfInfo(exePath: current.ToUpperInvariant())));
        }

        [Fact]
        public void WriteInfo_stamps_the_absolute_pipe_path_from_this_process_temp_directory()
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
                Assert.Equal(EngineBroker.PipePathFor("semanticus-test"), read.PipePath);
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public void Attach_failure_advice_does_not_tell_you_to_delete_the_lock_when_the_owner_is_alive()
        {
            var alive = EngineBroker.McpAttachFailureMessage(TimeSpan.FromSeconds(5), ownerAlive: true);
            Assert.DoesNotContain("engine.lock", alive, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("delete", alive, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("already open", alive, StringComparison.OrdinalIgnoreCase);

            var cold = EngineBroker.McpAttachFailureMessage(TimeSpan.FromSeconds(5), ownerAlive: false);
            Assert.DoesNotContain("engine.lock", cold, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("delete", cold, StringComparison.OrdinalIgnoreCase);
        }
    }
}

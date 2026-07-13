using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Learning Loop L0 — CAPTURE. The contract pinned here: a host-attached ExperienceTee tees the
    /// dual-drive ChangeBus stream (changes + activity, incl. the apply_plan report digest that used to
    /// be discarded) to `.semanticus/experience.jsonl` beside the model; every record carries the
    /// provenance envelope; capture is best-effort (no on-disk anchor → no write, never a throw) and
    /// strictly opt-in (no tee → no file — which is also why the rest of the suite stays clean).
    /// Tests copy the fixture to a temp dir so capture never dirties the vendored TestData.
    /// </summary>
    public sealed class ExperienceLogTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private static string TempCopyOfFixture()
        {
            // NOT "semanticus-*": that %TEMP% prefix is the ephemeral live-snapshot marker (IsEphemeralAnchor).
            var dir = Path.Combine(Path.GetTempPath(), "smx-xp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "AdventureWorks.bim");
            File.Copy(TestModels.FindBim(), bim);
            return bim;
        }

        private static string[] LogLines(string bimPath)
        {
            var file = ExperienceStore.FileFor(bimPath);
            return file != null && File.Exists(file)
                ? File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray()
                : Array.Empty<string>();
        }

        [Fact]
        public async Task Change_and_activity_stream_lands_in_the_sidecar_with_the_envelope()
        {
            var bim = TempCopyOfFixture();
            var sessions = new SessionManager();
            using var tee = new ExperienceTee(sessions);
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.OpenAsync(bim);

            await engine.SetDescriptionAsync("measure:Date/Days In Current Quarter", "Days elapsed in the current quarter.", "agent");
            sessions.Bus.PublishActivity(new ActivityEvent { Kind = "run_dax", Origin = "agent", Label = "EVALUATE {1}", Ok = true, ElapsedMs = 5 });

            var lines = LogLines(bim);
            Assert.True(lines.Length >= 2, "expected a change record and an activity record");

            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);   // every record is one valid JSON line
                var root = doc.RootElement;
                Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.False(string.IsNullOrEmpty(root.GetProperty("when").GetString()));
                Assert.False(string.IsNullOrEmpty(root.GetProperty("kind").GetString()));
                Assert.False(string.IsNullOrEmpty(root.GetProperty("sessionId").GetString()));
                Assert.False(string.IsNullOrEmpty(root.GetProperty("modelFingerprint").GetString()));
            }

            var change = lines.Select(l => JsonDocument.Parse(l).RootElement)
                              .First(r => r.GetProperty("kind").GetString() == "change");
            Assert.Equal("agent", change.GetProperty("origin").GetString());
            Assert.True(change.GetProperty("revision").GetInt64() > 0);

            // Tight selection (a First(...) would tolerate duplicate or mislabelled records): exactly ONE run_dax
            // activity record, and the ONLY other activity kind this flow may legitimately tee is the PRO
            // health-delta probe's "health_delta" evidence ride-along for the set_description commit above —
            // anything else is an unexpected record and must fail here.
            var activities = lines.Select(l => JsonDocument.Parse(l).RootElement)
                                  .Where(r => r.GetProperty("kind").GetString() == "activity")
                                  .ToList();
            var runDax = Assert.Single(activities, r => r.GetProperty("op").GetString() == "run_dax");
            Assert.True(runDax.GetProperty("ok").GetBoolean());
            Assert.All(activities, r =>
                Assert.Contains(r.GetProperty("op").GetString(), new[] { "run_dax", "health_delta" }));
        }

        [Fact]
        public async Task Apply_plan_report_is_captured_instead_of_discarded()
        {
            var bim = TempCopyOfFixture();
            var sessions = new SessionManager();
            using var tee = new ExperienceTee(sessions);
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.OpenAsync(bim);

            var view = await engine.AddPlanItemAsync("measure:Date/Days In Current Quarter", "set_description",
                "Days elapsed so far in the current quarter.", "Describe the measure", null, null, "agent");
            var item = view.Items.Single();
            await engine.SetPlanItemAsync(item.Id, null, approved: true, "human");
            var report = await engine.ApplyPlanAsync(new[] { item.Id }, "agent");
            Assert.Equal(1, report.AppliedCount);

            var applyRec = LogLines(bim).Select(l => JsonDocument.Parse(l).RootElement)
                .Single(r => r.GetProperty("kind").GetString() == "activity" && r.GetProperty("op").GetString() == "apply_plan");
            var result = applyRec.GetProperty("result");
            Assert.Equal(1, result.GetProperty("applied").GetInt32());
            Assert.False(string.IsNullOrEmpty(result.GetProperty("gradeBefore").GetString()));   // the before→after delta survives
            Assert.False(string.IsNullOrEmpty(result.GetProperty("gradeAfter").GetString()));
            Assert.Equal(1, result.GetProperty("items").GetArrayLength());
        }

        [Fact]
        public async Task No_tee_means_no_capture_and_a_disposed_tee_stops_capturing()
        {
            var bim = TempCopyOfFixture();
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.OpenAsync(bim);

            await engine.SetDescriptionAsync("measure:Date/Days In Current Quarter", "No tee attached.", "agent");
            Assert.Empty(LogLines(bim));   // capture is strictly host-attached, never ambient

            var tee = new ExperienceTee(sessions);
            await engine.SetDescriptionAsync("measure:Date/Days In Current Quarter", "Tee attached.", "agent");
            var captured = LogLines(bim).Length;
            Assert.True(captured >= 1);

            tee.Dispose();
            await engine.SetDescriptionAsync("measure:Date/Days In Current Quarter", "Tee detached.", "agent");
            Assert.Equal(captured, LogLines(bim).Length);
        }

        [Fact]
        public void Append_without_an_on_disk_anchor_is_a_quiet_no_op()
        {
            Assert.False(ExperienceStore.Append(null, new { x = 1 }));
            Assert.Null(ExperienceStore.FileFor(null));
            Assert.Null(ExperienceStore.FingerprintFor(null));
        }

        [Fact]
        public async Task Live_snapshot_sessions_fall_back_to_the_workspace_sidecar()
        {
            // A live/local XMLA session's on-disk anchor is an EPHEMERAL %TEMP%\semanticus-* snapshot dir
            // (LocalEngine.OpenLiveAsync) — the log must land in the durable workspace instead, or the
            // highest-value sessions would be captured into a dir that evaporates.
            var snapDir = Path.Combine(Path.GetTempPath(), "semanticus-live", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(snapDir);
            var bim = Path.Combine(snapDir, "Model.bim");
            File.Copy(TestModels.FindBim(), bim);
            Assert.True(ExperienceStore.IsEphemeralAnchor(bim));

            var workspace = Path.Combine(Path.GetTempPath(), "smx-xp-ws-" + Guid.NewGuid().ToString("N"));
            var sessions = new SessionManager();
            using var tee = new ExperienceTee(sessions, workspace);
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.OpenAsync(bim);
            await engine.SetDescriptionAsync("measure:Date/Days In Current Quarter", "Captured via the workspace fallback.", "agent");

            Assert.False(File.Exists(ExperienceStore.FileFor(bim)));   // nothing written into the doomed snapshot dir
            var wsFile = Path.Combine(workspace, ".semanticus", ExperienceStore.FileName);
            Assert.True(File.Exists(wsFile), "expected the workspace fallback log");
            Assert.Contains("\"kind\":\"change\"", File.ReadAllText(wsFile));
        }

        [Fact]
        public async Task Live_snapshot_with_no_workspace_falls_back_to_the_global_store()
        {
            // The worst case the fix targets: an EPHEMERAL anchor (live/XMLA/unsaved) AND no workspace open. Capture used
            // to be silently DROPPED (_fallbackFile == null) — the single highest-value session (a live XMLA model, no
            // folder open) captured nothing. It must now land in the durable GLOBAL %USERPROFILE%/.semanticus log instead.
            var snapDir = Path.Combine(Path.GetTempPath(), "semanticus-live", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(snapDir);
            var bim = Path.Combine(snapDir, "Model.bim");
            File.Copy(TestModels.FindBim(), bim);
            Assert.True(ExperienceStore.IsEphemeralAnchor(bim));

            var home = Path.Combine(Path.GetTempPath(), "smx-xp-home-" + Guid.NewGuid().ToString("N"));
            var origHome = Environment.GetEnvironmentVariable("USERPROFILE");
            try
            {
                Directory.CreateDirectory(home);
                Environment.SetEnvironmentVariable("USERPROFILE", home);   // redirect global before the tee resolves its fallback

                var sessions = new SessionManager();
                using var tee = new ExperienceTee(sessions);   // NO workspaceDir → the previously-dropped case
                using var engine = new LocalEngine(sessions, new Fake());
                await engine.OpenAsync(bim);
                await engine.SetDescriptionAsync("measure:Date/Days In Current Quarter", "Captured into the global fallback.", "agent");

                Assert.False(File.Exists(ExperienceStore.FileFor(bim)));   // nothing written into the doomed snapshot dir
                var globalFile = ExperienceStore.GlobalFile();
                Assert.Equal(Path.Combine(home, ".semanticus", ExperienceStore.FileName), globalFile);   // resolved PATH
                Assert.True(File.Exists(globalFile), "expected the global fallback log — capture must not be dropped");
                Assert.Contains("\"kind\":\"change\"", File.ReadAllText(globalFile));
            }
            finally
            {
                Environment.SetEnvironmentVariable("USERPROFILE", origHome);
                try { Directory.Delete(home, true); } catch { }
                try { Directory.Delete(snapDir, true); } catch { }
            }
        }

        [Fact]
        public void Fallback_resolution_pins_the_three_targets_beside_workspace_global()
        {
            // Pure path resolution (no model, no live connection): a file anchor stays beside the model; the ephemeral
            // %TEMP%\semanticus-* prefix is classified live; and the global fallback resolves under %USERPROFILE%.
            var fileAnchor = Path.Combine(Path.GetTempPath(), "smx-xp-fa-" + Guid.NewGuid().ToString("N"), "Model.bim");
            Assert.False(ExperienceStore.IsEphemeralAnchor(fileAnchor));   // durable → beside the model
            Assert.Equal(Path.Combine(Path.GetDirectoryName(fileAnchor), ".semanticus", ExperienceStore.FileName),
                         ExperienceStore.FileFor(fileAnchor));

            var ephAnchor = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "semanticus-x", "Model.bim");
            Assert.True(ExperienceStore.IsEphemeralAnchor(ephAnchor));   // ephemeral → workspace or global fallback

            var home = Path.Combine(Path.GetTempPath(), "smx-xp-gh-" + Guid.NewGuid().ToString("N"));
            var origHome = Environment.GetEnvironmentVariable("USERPROFILE");
            try
            {
                Environment.SetEnvironmentVariable("USERPROFILE", home);
                Assert.Equal(Path.Combine(home, ".semanticus", ExperienceStore.FileName), ExperienceStore.GlobalFile());
            }
            finally { Environment.SetEnvironmentVariable("USERPROFILE", origHome); }
        }

        [Fact]
        public void Fingerprint_is_stable_per_model_and_differs_across_models()
        {
            var a = TempCopyOfFixture();
            var b = TempCopyOfFixture();
            Assert.Equal(ExperienceStore.FingerprintFor(a), ExperienceStore.FingerprintFor(a));
            Assert.NotEqual(ExperienceStore.FingerprintFor(a), ExperienceStore.FingerprintFor(b));
            Assert.Equal(16, ExperienceStore.FingerprintFor(a).Length);   // 8 bytes hex
        }

        // ---- attribution: an activity FROZEN to another session is dropped, never recorded under this model -------
        // Audit finding: the tee resolved the session at write time (Current), so a model swap mid-op recorded the
        // result under the NEW model. Now the event carries the session id frozen at emit, and a mismatch is DROPPED.
        // Neuter: change the tee back to CurrentFor(null) and the stale event is written here — this test fails.
        [Fact]
        public async Task Activity_frozen_to_another_session_is_dropped_not_misattributed()
        {
            var bim = TempCopyOfFixture();
            var sessions = new SessionManager();
            using var tee = new ExperienceTee(sessions);
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.OpenAsync(bim);

            // Frozen to a DIFFERENT session (as if emitted before a swap) → must NOT land in this model's log.
            sessions.Bus.PublishActivity(new ActivityEvent { Kind = "run_dax", Origin = "agent", Label = "stale-elsewhere", Ok = true, SessionId = "some-other-session" });
            Assert.DoesNotContain(LogLines(bim), l => l.Contains("stale-elsewhere"));

            // Frozen to THIS session → lands (and an unstamped event still falls back to Current, preserved above).
            var cur = (await engine.SessionInfoAsync()).SessionId;
            sessions.Bus.PublishActivity(new ActivityEvent { Kind = "run_dax", Origin = "agent", Label = "mine-here", Ok = true, SessionId = cur });
            Assert.Contains(LogLines(bim), l => l.Contains("mine-here"));
        }

        // ---- an UNSTAMPED publish is stamped at the bus boundary and attributed to the current session --------------
        // Review follow-up (sol): the direct Bus.PublishActivity emitters (binding warnings, enforcement/enable/
        // binding/activation writes) bypassed PublishActivityAsync's stamp. The bus now stamps every publish at its
        // single boundary — so subscribers (the RPC broadcast, future async consumers) see the frozen identity, and
        // the tee's null-id fallback to Current is pinned explicitly. Neuter: drop the ??= in ChangeBus.PublishActivity
        // and the captured event's SessionId is null — this test fails.
        [Fact]
        public async Task An_unstamped_publish_is_stamped_at_the_bus_and_attributed_to_the_current_session()
        {
            var bim = TempCopyOfFixture();
            var sessions = new SessionManager();
            using var tee = new ExperienceTee(sessions);
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.OpenAsync(bim);
            var cur = (await engine.SessionInfoAsync()).SessionId;

            string seenBySubscriber = null;
            void Capture(ActivityEvent e) { if (e.Label == "unstamped-fallback") seenBySubscriber = e.SessionId; }
            sessions.Bus.Activity += Capture;
            try { sessions.Bus.PublishActivity(new ActivityEvent { Kind = "run_dax", Origin = "agent", Label = "unstamped-fallback", Ok = true }); }
            finally { sessions.Bus.Activity -= Capture; }

            Assert.Equal(cur, seenBySubscriber);   // stamped AT THE BUS — every subscriber sees the frozen identity

            // …and the record lands attributed to the current session (the fallback the tee's comment promises).
            var rec = LogLines(bim).Select(l => JsonDocument.Parse(l).RootElement)
                .Single(r => r.GetProperty("kind").GetString() == "activity"
                          && r.TryGetProperty("label", out var lb) && lb.GetString() == "unstamped-fallback");
            Assert.Equal(cur, rec.GetProperty("sessionId").GetString());
        }

        // ---- the per-line cap is enforced at the WRITE chokepoint, even for a caller that supplies no fallback -----
        // Audit finding: the change tee passed no payload-free fallback, so an oversized change record bypassed the
        // 32 KB cap. Now the cap is enforced in AppendLine itself. Neuter: gate the truncation on recordWithoutPayload
        // again and this line blows past the cap — this test fails.
        [Fact]
        public void An_oversized_record_is_capped_at_the_write_chokepoint_even_without_a_fallback()
        {
            var dir = Path.Combine(Path.GetTempPath(), "smx-xp-cap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, ExperienceStore.FileName);
            try
            {
                var huge = new { kind = "change", blob = new string('x', 64 * 1024) };   // far over the cap, NO fallback supplied
                Assert.True(ExperienceStore.AppendLine(file, huge));
                var line = File.ReadAllText(file).TrimEnd('\n');
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 32 * 1024, "the write chokepoint must cap the line");
                Assert.Contains("oversized", line);   // summarized there, regardless of caller
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}

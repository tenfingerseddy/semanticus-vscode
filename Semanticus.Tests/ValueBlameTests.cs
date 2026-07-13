using System;
using System.Collections.Generic;
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
    /// The number time-machine (feature #3). Contracts pinned here:
    ///  • VERDICT HONESTY (FABLE-c): single-edit window → "attributed"; multi-edit → "interval" with
    ///    candidates RANKED (formula-changed above data-only, then dependency overlap — FABLE-b), never
    ///    a causal claim; identical formulas + a moved value → "data-suspected"; thin history →
    ///    "inconclusive" — never a guess.
    ///  • CAPTURE: FABLE-a cone-skip (an edit's impact cone decides which measures get VALUES; the
    ///    expression snapshot always lands, even OFFLINE), host-attached opt-in (tests stay clean),
    ///    dry-run suppressed, silent skip on free.
    ///  • STORE: vitals.jsonl append/read round-trip, retention prune (reported, not silent), corrupt
    ///    lines counted not thrown.
    ///  • GATE (soft, Kane's line): free tier gets Status="pro" + a plain invitation — NEVER a throw,
    ///    and ambient capture writes NOTHING.
    /// </summary>
    public sealed class ValueBlameTests
    {
        private const string M = "measure:Date/Days In Current Quarter";   // known AdventureWorks measure (BaselineTests uses it)
        private const string Ctx = "(model context)";

        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static string TempCopyOfFixture()
        {
            // NOT "semanticus-*": that %TEMP% prefix is the ephemeral live-snapshot marker (IsEphemeralAnchor).
            var dir = Path.Combine(Path.GetTempPath(), "smx-vb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "AdventureWorks.bim");
            File.Copy(TestModels.FindBim(), bim);
            return bim;
        }

        private static string VitalsFile(string bim)
            => Path.Combine(LayoutStore.DirFor(bim), VitalsStore.SubDir, VitalsStore.FileName);

        // ---- record builders for the pure analysis ----

        private static VitalsRecord Rec(long rev, string op, string when, string[] changedRefs = null, params VitalsMeasure[] measures) => new VitalsRecord
        {
            When = when, SessionId = "s1", Revision = rev, Op = op, Origin = "agent",
            ChangedRefs = changedRefs ?? Array.Empty<string>(), Measures = measures,
        };

        private static VitalsMeasure Meas(string exprHash, string expr, object value, bool hasValue = true, string[] cone = null) => new VitalsMeasure
        {
            Ref = M, Name = "Days In Current Quarter", ExprHash = exprHash, Expr = expr,
            Cone = cone ?? new[] { "column:Date/Date" },
            Contexts = hasValue ? new[] { new VitalsCell { Key = Ctx, Value = value } } : Array.Empty<VitalsCell>(),
        };

        // ================================ pure analysis (ValueBlame) ================================

        [Fact]
        public void No_history_is_inconclusive()
        {
            var core = ValueBlame.Analyze(new List<VitalsRecord>(), M, Ctx, null);
            Assert.NotNull(core.Inconclusive);
        }

        [Fact]
        public void A_single_observed_value_is_inconclusive_not_a_guess()
        {
            var recs = new List<VitalsRecord> { Rec(1, "save_model", "2026-07-01T10:00:00Z", null, Meas("h1", "1", 10.0)) };
            var core = ValueBlame.Analyze(recs, M, Ctx, null);
            Assert.NotNull(core.Inconclusive);
        }

        [Fact]
        public void An_unmoved_series_is_inconclusive_with_the_steady_value_named()
        {
            var recs = new List<VitalsRecord>
            {
                Rec(1, "apply_plan", "2026-07-01T10:00:00Z", null, Meas("h1", "1", 10.0)),
                Rec(2, "apply_plan", "2026-07-01T11:00:00Z", null, Meas("h1", "1", 10.0)),
            };
            var core = ValueBlame.Analyze(recs, M, Ctx, null);
            Assert.NotNull(core.Inconclusive);
            Assert.Contains("10", core.Inconclusive);
        }

        [Fact]
        public void Latest_movement_window_spans_a_valueless_gap_and_includes_the_gap_edits()
        {
            var recs = new List<VitalsRecord>
            {
                Rec(1, "apply_plan", "2026-07-01T10:00:00Z", null, Meas("h1", "1", 10.0)),
                // an OFFLINE capture in between: formulas recorded, no value — an honest gap
                Rec(2, "save_model", "2026-07-01T11:00:00Z", null, Meas("h1", "1", null, hasValue: false)),
                Rec(3, "apply_plan", "2026-07-01T12:00:00Z", new[] { M }, Meas("h1", "1", 12.0)),
            };
            var core = ValueBlame.Analyze(recs, M, Ctx, null);
            Assert.Null(core.Inconclusive);
            Assert.Equal(1, core.From.Revision);
            Assert.Equal(3, core.To.Revision);
            Assert.Equal(2, core.Window.Count);          // the gap edit AND the endpoint edit are both candidates
            Assert.Equal(10.0, (double)core.Before, 6);
            Assert.Equal(12.0, (double)core.After, 6);
        }

        [Fact]
        public void Formula_diff_between_the_endpoints_is_deterministic_and_carries_both_bodies()
        {
            var recs = new List<VitalsRecord>
            {
                Rec(1, "apply_plan", "2026-07-01T10:00:00Z", null, Meas("h1", "OLD BODY", 10.0)),
                Rec(2, "apply_plan", "2026-07-01T11:00:00Z", new[] { M }, Meas("h2", "NEW BODY", 12.0)),
            };
            var core = ValueBlame.Analyze(recs, M, Ctx, null);
            Assert.True(core.FormulaChanged);
            var d = Assert.Single(core.ExprDiffs);
            Assert.Equal(M, d.Ref, ignoreCase: true);
            Assert.Equal("OLD BODY", d.Before);
            Assert.Equal("NEW BODY", d.After);
            Assert.True(core.CandidateFormulaChanged[core.Window[0]]);
        }

        [Fact]
        public void Identical_formulas_report_no_formula_change()
        {
            var recs = new List<VitalsRecord>
            {
                Rec(1, "apply_plan", "2026-07-01T10:00:00Z", null, Meas("h1", "SAME", 10.0)),
                Rec(2, "save_model", "2026-07-01T11:00:00Z", null, Meas("h1", "SAME", 12.0)),
            };
            var core = ValueBlame.Analyze(recs, M, Ctx, null);
            Assert.False(core.FormulaChanged);
            Assert.Empty(core.ExprDiffs);
        }

        [Fact]
        public void Since_a_named_revision_compares_against_the_latest_point()
        {
            var recs = new List<VitalsRecord>
            {
                Rec(5, "apply_plan", "2026-07-01T10:00:00Z", null, Meas("h1", "1", 10.0)),
                Rec(6, "apply_plan", "2026-07-01T11:00:00Z", null, Meas("h1", "1", 11.0)),
                Rec(7, "apply_plan", "2026-07-01T12:00:00Z", null, Meas("h1", "1", 11.0)),
            };
            var core = ValueBlame.Analyze(recs, M, Ctx, "5");
            Assert.Null(core.Inconclusive);
            Assert.Equal(5, core.From.Revision);
            Assert.Equal(7, core.To.Revision);           // latest point, even though 6→7 didn't move
            Assert.Equal(2, core.Window.Count);

            Assert.NotNull(ValueBlame.Analyze(recs, M, Ctx, "999").Inconclusive);   // unknown point → honest refusal
            Assert.NotNull(ValueBlame.Analyze(recs, M, Ctx, "7").Inconclusive);     // nothing newer to compare
        }

        [Fact]
        public void Untracked_session_edits_inside_the_window_are_counted()
        {
            var recs = new List<VitalsRecord>
            {
                Rec(1, "apply_plan", "2026-07-01T10:00:00Z", null, Meas("h1", "1", 10.0)),
                Rec(9, "apply_plan", "2026-07-01T12:00:00Z", new[] { M }, Meas("h1", "1", 12.0)),
            };
            var core = ValueBlame.Analyze(recs, M, Ctx, null);
            // revisions 1→9 span 8 tracked mutations; only 1 is described by a recorded point
            Assert.Equal(7, core.UntrackedEdits);
        }

        // ================================ the store (vitals.jsonl) ================================

        [Fact]
        public void Append_and_read_round_trip_preserves_cells_and_normalizes_values()
        {
            var file = Path.Combine(Path.GetTempPath(), "smx-vb-store-" + Guid.NewGuid().ToString("N"), "vitals.jsonl");
            Assert.True(VitalsStore.Append(file, Rec(1, "apply_plan", "2026-07-01T10:00:00Z", new[] { M }, Meas("h1", "BODY", 42.5))));
            var (recs, bad) = VitalsStore.Read(file, null);
            Assert.Equal(0, bad);
            var r = Assert.Single(recs);
            Assert.Equal("apply_plan", r.Op);
            var cell = Assert.Single(Assert.Single(r.Measures).Contexts);
            Assert.Equal(Ctx, cell.Key);
            Assert.Equal(42.5, (double)VitalsStore.NormalizeValue(cell.Value), 6);
        }

        [Fact]
        public void Retention_prunes_oldest_and_reports_the_prune_on_the_record_that_caused_it()
        {
            var file = Path.Combine(Path.GetTempPath(), "smx-vb-prune-" + Guid.NewGuid().ToString("N"), "vitals.jsonl");
            for (var i = 0; i < VitalsStore.MaxRecords + 3; i++)
                VitalsStore.Append(file, Rec(i, "save_model", DateTime.UtcNow.AddMinutes(i).ToString("o")));
            var (recs, _) = VitalsStore.Read(file, null);
            Assert.Equal(VitalsStore.MaxRecords, recs.Count);
            Assert.Equal(3, recs[0].Revision);                    // the 3 oldest are gone
            Assert.Equal(1, recs[recs.Count - 1].Pruned);         // ...and each overflowing append SAID so
        }

        [Fact]
        public void Corrupt_lines_are_counted_not_thrown_and_foreign_fingerprints_are_filtered()
        {
            var file = Path.Combine(Path.GetTempPath(), "smx-vb-bad-" + Guid.NewGuid().ToString("N"), "vitals.jsonl");
            var mine = Rec(1, "save_model", "2026-07-01T10:00:00Z"); mine.ModelFingerprint = "aaaa";
            var theirs = Rec(2, "save_model", "2026-07-01T11:00:00Z"); theirs.ModelFingerprint = "bbbb";
            VitalsStore.Append(file, mine);
            VitalsStore.Append(file, theirs);
            File.AppendAllText(file, "{not json\n");
            var (recs, bad) = VitalsStore.Read(file, "aaaa");
            Assert.Equal(1, bad);
            Assert.Single(recs);
            Assert.Equal("aaaa", recs[0].ModelFingerprint);
        }

        // ================================ FABLE-a: capture cone-skip ================================

        [Fact]
        public async Task Snapshot_gives_values_only_to_measures_the_edits_cone_reaches_and_formulas_to_all()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(true));
            await e.OpenAsync(TestModels.FindBim());
            var snap = await sm.Current.ReadAsync(m =>
                VitalSigns.Snapshot(m, new[] { M }, null, topN: 25, coneCap: 64, entryCap: 80, captureAllValues: false));

            Assert.NotEmpty(snap.Entries);
            Assert.All(snap.Entries, en => Assert.False(string.IsNullOrEmpty(en.ExprHash)));
            // The changed measure itself is value-targeted (it is in its own impact cone)...
            Assert.Contains(snap.ValueTargets, t => string.Equals(t.Ref, M, StringComparison.OrdinalIgnoreCase));
            // ...but the cone does NOT reach every top-N measure — untouched measures get no value cell.
            Assert.True(snap.ValueTargets.Count < snap.Entries.Count(x => x.Cone != null),
                "expected at least one top-N measure OUTSIDE the edit's impact cone to be skipped for values");

            var all = await sm.Current.ReadAsync(m =>
                VitalSigns.Snapshot(m, Array.Empty<string>(), null, 25, 64, 80, captureAllValues: true));
            Assert.Equal(all.Entries.Count(x => x.Cone != null), all.ValueTargets.Count);   // a deploy captures every top-N
        }

        // ================================ FABLE-b: candidate ranking ================================

        [Fact]
        public async Task Candidates_rank_formula_changes_first_then_dependency_overlap()
        {
            var bim = TempCopyOfFixture();
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(true));
            await e.OpenAsync(bim);

            var recs = new[]
            {
                Rec(1, "apply_plan", "2026-07-01T10:00:00Z", null, Meas("h1", "OLD", 10.0)),
                // rev 2: formula-changing edit, but its recorded refs resolve to NOTHING on this model → overlap 0
                Rec(2, "apply_plan", "2026-07-01T11:00:00Z", new[] { "measure:__gone/Deleted" }, Meas("h2", "NEW", null, hasValue: false)),
                // rev 3: data-only edit (formula unchanged since rev 2) whose refs DO overlap the measure's cone
                Rec(3, "apply_plan", "2026-07-01T12:00:00Z", new[] { M }, Meas("h2", "NEW", 12.0)),
                // rev 4: AFTER the movement window (valueless, unrelated) — must not appear as a candidate
                Rec(4, "save_model", "2026-07-01T13:00:00Z", new[] { "measure:__gone/Other" }, Meas("h2", "NEW", null, hasValue: false)),
            };
            foreach (var r in recs) VitalsStore.Append(VitalsFile(bim), r);

            var blame = await e.BlameValueAsync(M, null, null, "agent");
            Assert.Equal("ok", blame.Status);
            Assert.Equal("interval", blame.Verdict);
            Assert.Equal(2, blame.Candidates.Length);
            Assert.True(blame.Candidates[0].FormulaChanged, "the formula-changing edit must rank first");
            Assert.Equal(2, blame.Candidates[0].Revision);
            Assert.False(blame.Candidates[1].FormulaChanged);
            Assert.True(blame.Candidates[1].OverlapScore > 0, "the cone-overlapping edit scores > 0");
            Assert.Contains("NOT a causal claim", blame.Note);
        }

        // ================================ verdict semantics end-to-end ================================

        [Fact]
        public async Task Single_edit_window_is_attributed_with_the_formula_cause()
        {
            var bim = TempCopyOfFixture();
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(true));
            await e.OpenAsync(bim);
            VitalsStore.Append(VitalsFile(bim), Rec(1, "apply_plan", "2026-07-01T10:00:00Z", null, Meas("h1", "OLD", 10.0)));
            VitalsStore.Append(VitalsFile(bim), Rec(2, "apply_plan", "2026-07-01T11:00:00Z", new[] { M }, Meas("h2", "NEW", 12.0)));

            var blame = await e.BlameValueAsync(M, null, null, "agent");
            Assert.Equal("attributed", blame.Verdict);
            Assert.Equal("formula", blame.Cause);
            Assert.Single(blame.Candidates);
            var d = Assert.Single(blame.ExprDiffs);
            Assert.Equal("OLD", d.Before);
            Assert.Equal("NEW", d.After);

            // The shared evidence record landed on the audit trail (the compare_baseline precedent).
            var chain = await e.ListVerifiedEditsAsync();
            var rec = chain.Records.LastOrDefault(r => r.Op == "blame_value");
            Assert.NotNull(rec);
            Assert.Equal("attributed", rec.Verdict);
            Assert.Equal(0, rec.Revision);   // analysis mutates nothing — must never badge a timeline row
        }

        [Fact]
        public async Task Identical_formulas_with_a_moved_value_is_data_suspected_and_says_why_it_cant_prove_it()
        {
            var bim = TempCopyOfFixture();
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(true));
            await e.OpenAsync(bim);
            VitalsStore.Append(VitalsFile(bim), Rec(1, "deploy_live", "2026-07-01T10:00:00Z", null, Meas("h1", "SAME", 10.0)));
            VitalsStore.Append(VitalsFile(bim), Rec(2, "deploy_live", "2026-07-01T11:00:00Z", new[] { "measure:__gone/Unrelated" }, Meas("h1", "SAME", 12.0)));

            var blame = await e.BlameValueAsync(M, null, null, "agent");
            Assert.Equal("data-suspected", blame.Verdict);
            Assert.Contains("suspicion", blame.Note);        // never asserted as a finding — the shadow instance is v2
            Assert.Empty(blame.ExprDiffs);
        }

        [Fact]
        public async Task No_history_is_inconclusive_over_the_engine_door_too()
        {
            var bim = TempCopyOfFixture();
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(true));
            await e.OpenAsync(bim);
            var blame = await e.BlameValueAsync(M, null, null, "agent");
            Assert.Equal("ok", blame.Status);
            Assert.Equal("inconclusive", blame.Verdict);
            // an inconclusive analysis claims nothing — it must not spam the audit trail
            var chain = await e.ListVerifiedEditsAsync();
            Assert.DoesNotContain(chain.Records, r => r.Op == "blame_value");
        }

        [Fact]
        public async Task Value_history_lists_observed_points_and_honest_gaps()
        {
            var bim = TempCopyOfFixture();
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(true));
            await e.OpenAsync(bim);
            VitalsStore.Append(VitalsFile(bim), Rec(1, "apply_plan", "2026-07-01T10:00:00Z", null, Meas("h1", "1", 10.0)));
            VitalsStore.Append(VitalsFile(bim), Rec(2, "save_model", "2026-07-01T11:00:00Z", null, Meas("h1", "1", null, hasValue: false)));

            var hist = await e.ListValueHistoryAsync(M, null);
            Assert.Equal("ok", hist.Status);
            Assert.Equal(2, hist.Points.Length);
            Assert.Equal("10", hist.Points[0].Value);
            Assert.Null(hist.Points[1].Value);               // the offline point is a gap, not a zero
            Assert.Equal("save_model", hist.Points[1].CheckpointOp);
        }

        // ================================ the soft Pro gate + ambient capture ================================

        [Fact]
        public async Task Free_tier_gets_status_pro_with_the_invitation_and_never_throws_and_writes_nothing()
        {
            var bim = TempCopyOfFixture();
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(pro: false)) { AmbientVitalsEnabled = true };
            await e.OpenAsync(bim);

            var blame = await e.BlameValueAsync(M, null, null, "agent");
            Assert.Equal("pro", blame.Status);
            Assert.Equal("pro", blame.Verdict);
            Assert.Contains("free", blame.Note);             // the invitation names the free manual alternative

            var hist = await e.ListValueHistoryAsync(M, null);
            Assert.Equal("pro", hist.Status);

            await e.SaveAsync(null, "bim");                  // a checkpoint moment on the FREE tier...
            Assert.False(File.Exists(VitalsFile(bim)), "free-tier ambient capture must write NOTHING (silent skip)");
        }

        [Fact]
        public async Task Ambient_save_capture_is_host_attached_and_snapshots_expressions_even_offline()
        {
            var bim = TempCopyOfFixture();
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(true));   // NOT host-enabled
            await e.OpenAsync(bim);
            await e.SaveAsync(null, "bim");
            Assert.False(File.Exists(VitalsFile(bim)), "without the host opt-in, ambient capture must stay off (the ExperienceTee precedent)");

            e.AmbientVitalsEnabled = true;                        // the owner host's switch
            await e.SaveAsync(null, "bim");
            Assert.True(File.Exists(VitalsFile(bim)));
            var (recs, bad) = VitalsStore.Read(VitalsFile(bim), null);
            Assert.Equal(0, bad);
            var rec = Assert.Single(recs);
            Assert.Equal("save_model", rec.Op);
            Assert.NotEmpty(rec.Measures);                        // the formula snapshot lands OFFLINE...
            Assert.All(rec.Measures, m => Assert.False(string.IsNullOrEmpty(m.ExprHash)));
            Assert.All(rec.Measures, m => Assert.Empty(m.Contexts));   // ...but no values (no changed refs, no live)
            Assert.True(rec.Revision >= 0);
            Assert.Equal(Array.Empty<string>(), rec.ChangedRefs);
        }

        [Fact]
        public async Task Offline_checkpoint_with_changed_refs_records_the_skipped_value_half_honestly()
        {
            var bim = TempCopyOfFixture();
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(true)) { AmbientVitalsEnabled = true };
            await e.OpenAsync(bim);

            await e.CaptureVitalsAsync(sm.Current, "apply_plan", "agent", new[] { M }, liveReflectsSession: false);
            var (recs, _) = VitalsStore.Read(VitalsFile(bim), null);
            var rec = Assert.Single(recs);
            Assert.True(rec.ValuesSkippedOffline, "value targets existed but there was no live connection — the record must say so");
            Assert.False(rec.ValuesSkippedStale);    // offline ≠ stale — the two skip reasons are distinct
            Assert.All(rec.Measures, m => Assert.Empty(m.Contexts));
            Assert.Equal(new[] { M }, rec.ChangedRefs);
        }

        // The PR #86 review finding (Codex), pinned: an apply/optimize/save checkpoint has by definition
        // just mutated the session, so a connected live model still serves the PREVIOUS deployed state —
        // its numbers must NEVER be paired with this record's post-edit expression snapshot. Values are
        // observed ONLY when the live model provably reflects the session: the committed-deploy checkpoint.
        [Fact]
        public void Values_are_observed_only_when_the_live_model_reflects_the_session()
        {
            // The gate's full truth table (pure — no live rig needed).
            Assert.Equal(VitalsValueDecision.Observe, VitalSigns.DecideValueCapture(liveConnected: true, liveReflectsSession: true, valueTargets: 3));
            Assert.Equal(VitalsValueDecision.SkipStale, VitalSigns.DecideValueCapture(liveConnected: true, liveReflectsSession: false, valueTargets: 3));
            Assert.Equal(VitalsValueDecision.SkipOffline, VitalSigns.DecideValueCapture(liveConnected: false, liveReflectsSession: false, valueTargets: 3));
            Assert.Equal(VitalsValueDecision.SkipOffline, VitalSigns.DecideValueCapture(liveConnected: false, liveReflectsSession: true, valueTargets: 3));
            Assert.Equal(VitalsValueDecision.NoTargets, VitalSigns.DecideValueCapture(liveConnected: true, liveReflectsSession: true, valueTargets: 0));

            // The checkpoint → gate mapping: ONLY a committed deploy claims live == session.
            static VerifiedEditRecord R(string op, string verdict, long rev) => new VerifiedEditRecord { Op = op, Verdict = verdict, Revision = rev };
            Assert.True(LocalEngine.VitalsLiveReflectsSession(R("deploy_live", "deployed", 0)));
            Assert.False(LocalEngine.VitalsLiveReflectsSession(R("apply_plan", "batch", 7)));      // just mutated — live lags
            Assert.False(LocalEngine.VitalsLiveReflectsSession(R("optimize_measure", "proven", 7)));
            Assert.False(LocalEngine.VitalsLiveReflectsSession(R("deploy_live", "overridden", 0))); // gate-override record ≠ a completed sync
            // ...while all of these remain CHECKPOINTS (the expression snapshot still lands):
            Assert.True(LocalEngine.IsVitalsCheckpoint(R("apply_plan", "batch", 7)));
            Assert.True(LocalEngine.IsVitalsCheckpoint(R("optimize_measure", "proven", 7)));
            Assert.True(LocalEngine.IsVitalsCheckpoint(R("deploy_live", "deployed", 0)));
            Assert.False(LocalEngine.IsVitalsCheckpoint(R("blame_value", "attributed", 0)));
        }

        [Fact]
        public async Task Dry_run_scope_captures_nothing()
        {
            var bim = TempCopyOfFixture();
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(true)) { AmbientVitalsEnabled = true };
            await e.OpenAsync(bim);

            DryRunScope.Current = new DryRunCollector();
            try { await e.CaptureVitalsAsync(sm.Current, "apply_plan", "agent", new[] { M }, false); }
            finally { DryRunScope.Current = null; }
            Assert.False(File.Exists(VitalsFile(bim)), "a rehearsal must never leave a vitals record");
        }

        // The completeness gap the fix targets: an EPHEMERAL anchor (live/XMLA/unsaved) AND no workspace open. The
        // vitals store used to resolve to null and capture was silently DROPPED — exactly the live models Pro users
        // connect to. It must now land in the durable GLOBAL %USERPROFILE%/.semanticus store — mirroring the SENSITIVE
        // half of PR #112 (the project-insight store): a PER-FINGERPRINT file, so it can't cross-prune or cross-read.
        private static string EphemeralBim()
        {
            var snapDir = Path.Combine(Path.GetTempPath(), "semanticus-live", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(snapDir);
            var bim = Path.Combine(snapDir, "Model.bim");
            File.Copy(TestModels.FindBim(), bim);
            return bim;
        }

        [Fact]
        public async Task Live_ephemeral_session_with_no_workspace_captures_into_a_per_fingerprint_global_file()
        {
            var bim = EphemeralBim();
            Assert.True(ExperienceStore.IsEphemeralAnchor(bim));   // classified live → no durable on-disk anchor
            var home = Path.Combine(Path.GetTempPath(), "smx-vb-home-" + Guid.NewGuid().ToString("N"));
            var origHome = Environment.GetEnvironmentVariable("USERPROFILE");
            try
            {
                Directory.CreateDirectory(home);
                Environment.SetEnvironmentVariable("USERPROFILE", home);   // redirect the global root before capture

                var sm = new SessionManager();
                using var e = new LocalEngine(sm, new Fake(true)) { AmbientVitalsEnabled = true };   // NO workspaceDir
                await e.OpenAsync(bim);
                var origin = new LiveOrigin("smx-endpoint:1234", "SalesDW", null);   // simulate a live XMLA session (fingerprintable)
                sm.Current.LiveOrigin = origin;
                await e.CaptureVitalsAsync(sm.Current, "save_model", "agent", Array.Empty<string>(), liveReflectsSession: false);

                var fp = ExperienceStore.FingerprintForLive(origin);
                Assert.False(string.IsNullOrEmpty(fp));
                var expected = Path.Combine(home, ".semanticus", VitalsStore.SubDir, VitalsStore.GlobalFileName(fp));
                Assert.True(File.Exists(expected), "expected the PER-FINGERPRINT global vitals file — capture must not be dropped");
                // The shared vitals.jsonl must NOT be used — that shape is what cross-prunes / cross-reads across models.
                Assert.False(File.Exists(Path.Combine(home, ".semanticus", VitalsStore.SubDir, VitalsStore.FileName)));
                Assert.False(File.Exists(VitalsFile(bim)), "nothing may land in the doomed ephemeral snapshot dir");

                var (recs, bad) = VitalsStore.Read(expected, fp);
                Assert.Equal(0, bad);
                var rec = Assert.Single(recs);
                Assert.Equal(fp, rec.ModelFingerprint);            // the record carries the key its file is named for
                Assert.Equal("save_model", rec.Op);
                Assert.NotEmpty(rec.Measures);                     // the expression snapshot round-trips
                Assert.All(rec.Measures, m => Assert.False(string.IsNullOrEmpty(m.ExprHash)));
            }
            finally
            {
                Environment.SetEnvironmentVariable("USERPROFILE", origHome);
                try { Directory.Delete(Path.GetDirectoryName(bim), true); } catch { }
                try { Directory.Delete(home, true); } catch { }
            }
        }

        [Fact]
        public void Per_fingerprint_global_files_are_distinct_so_no_cross_prune_or_cross_read()
        {
            // The per-fingerprint filename closes both bot findings BY CONSTRUCTION: two models = two files, so
            // MaxRecords/MaxBytes retention is per-model (Codex P2) and one model's read can't see another's (Copilot).
            var baselines = Path.Combine(Path.GetTempPath(), "smx-vb-gfp-" + Guid.NewGuid().ToString("N"), VitalsStore.SubDir);
            var fileA = Path.Combine(baselines, VitalsStore.GlobalFileName("aaaa1111"));
            var fileB = Path.Combine(baselines, VitalsStore.GlobalFileName("bbbb2222"));
            Assert.NotEqual(fileA, fileB);

            var recA = Rec(1, "save_model", "2026-07-01T10:00:00Z"); recA.ModelFingerprint = "aaaa1111";
            Assert.True(VitalsStore.Append(fileA, recA));
            // Model B churns well past retention — it must prune ONLY its own file, never touch A's history.
            for (var i = 0; i < VitalsStore.MaxRecords + 5; i++)
            {
                var r = Rec(i, "save_model", DateTime.UtcNow.AddMinutes(i).ToString("o")); r.ModelFingerprint = "bbbb2222";
                VitalsStore.Append(fileB, r);
            }

            var (aRecs, _) = VitalsStore.Read(fileA, "aaaa1111");
            Assert.Single(aRecs);                                  // no cross-prune: A survived B's churn intact
            Assert.Equal("aaaa1111", aRecs[0].ModelFingerprint);
            var (bRecs, _) = VitalsStore.Read(fileB, "bbbb2222");
            Assert.Equal(VitalsStore.MaxRecords, bRecs.Count);     // B pruned within its OWN file only
            Assert.DoesNotContain(bRecs, r => r.ModelFingerprint == "aaaa1111");   // no cross-read
            try { Directory.Delete(Path.GetDirectoryName(baselines), true); } catch { }
        }

        [Fact]
        public async Task Null_fingerprint_ephemeral_session_writes_no_global_file()
        {
            // Ephemeral anchor + NO LiveOrigin → no stable identity → fingerprint null. Such a session can't be keyed
            // or read back reliably, so it must NOT be written to the shared global home (it would leak into every
            // model's read). Skipping it is a no-regression — it was dropped before this PR too.
            var bim = EphemeralBim();
            Assert.True(ExperienceStore.IsEphemeralAnchor(bim));
            var home = Path.Combine(Path.GetTempPath(), "smx-vb-home-" + Guid.NewGuid().ToString("N"));
            var origHome = Environment.GetEnvironmentVariable("USERPROFILE");
            try
            {
                Directory.CreateDirectory(home);
                Environment.SetEnvironmentVariable("USERPROFILE", home);

                var sm = new SessionManager();
                using var e = new LocalEngine(sm, new Fake(true)) { AmbientVitalsEnabled = true };   // NO workspaceDir
                await e.OpenAsync(bim);
                Assert.Null(sm.Current.LiveOrigin);                // no live identity → VitalsFingerprintFor is null
                await e.CaptureVitalsAsync(sm.Current, "save_model", "agent", Array.Empty<string>(), liveReflectsSession: false);

                Assert.False(Directory.Exists(Path.Combine(home, ".semanticus", VitalsStore.SubDir)),
                    "a null-fingerprint session must create NO global vitals file (zero pollution of any model's history)");
                Assert.False(File.Exists(VitalsFile(bim)), "and nothing beside the doomed ephemeral snapshot dir either");
            }
            finally
            {
                Environment.SetEnvironmentVariable("USERPROFILE", origHome);
                try { Directory.Delete(Path.GetDirectoryName(bim), true); } catch { }
                try { Directory.Delete(home, true); } catch { }
            }
        }
    }
}

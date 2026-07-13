using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Hole #3 (docs/strategy/04): the dual-drive invariants — interleaved human+agent undo on ONE shared
    /// timeline, and FormulaFixup rewriting a renamed reference across a measure AND the governance-critical
    /// RLS filter — had only happy-path smoke coverage. These are the first real (xUnit) correctness tests.
    /// They drive the same public <see cref="IEngine"/> both doors use, so they assert the engine-level
    /// guarantee (not a UI path).
    /// </summary>
    public sealed class DualDriveUndoTests : IAsyncLifetime
    {
        private SessionManager _sessions = null!;
        private LocalEngine _engine = null!;
        private string _table = null!;

        public async Task InitializeAsync()
        {
            _sessions = new SessionManager();
            _engine = new LocalEngine(_sessions);
            await _engine.OpenAsync(FindTestBim());
            _table = (await _engine.ListMeasuresAsync()).First().Table;   // a real table to hang objects on
        }

        public Task DisposeAsync() { _engine.Dispose(); return Task.CompletedTask; }

        [Fact]
        public async Task Undo_then_redo_round_trips_a_measure_create()
        {
            const string name = "Test_UndoRoundTrip";
            await _engine.CreateMeasureAsync("table:" + _table, name, "1", "agent");
            Assert.Contains(await _engine.ListMeasuresAsync(), m => m.Name == name);

            var u = await _engine.UndoAsync("human");
            Assert.DoesNotContain(await _engine.ListMeasuresAsync(), m => m.Name == name);
            Assert.True(u.CanRedo);

            await _engine.RedoAsync("human");
            Assert.Contains(await _engine.ListMeasuresAsync(), m => m.Name == name);
        }

        [Fact]
        public async Task One_agent_edit_publishes_exactly_one_origin_tagged_broadcast_with_a_delta_and_a_bumped_revision()
        {
            // The dual-drive BROADCAST invariant (golden rule #2): a change must broadcast model/didChange so BOTH
            // doors see it live. Undo is covered; the Publish itself was unasserted, so a dropped broadcast would
            // silently leave the other door stale. Subscribe to the SAME in-process ChangeBus the RpcHost fans out
            // to, and prove ONE agent set_dax fires EXACTLY ONE notification: origin=agent, bumped revision, a delta.
            var mref = await _engine.CreateMeasureAsync("table:" + _table, "Bcast_Probe", "1", "agent");

            // Subscribe AFTER the create so the only counted notification is the one set_dax under test.
            var seen = new List<ChangeNotification>();
            Action<ChangeNotification> handler = n => seen.Add(n);
            _sessions.Bus.Changed += handler;
            try
            {
                var before = _sessions.Current.Revision;
                var res = await _engine.SetDaxAsync(mref, "2", "agent");

                Assert.True(res.Changed);                       // the edit really mutated (1 != 2)
                Assert.Single(seen);                            // EXACTLY one broadcast — not zero (dropped), not duplicated
                var n = seen[0];
                Assert.Equal("agent", n.Origin);                // origin tagged so the other door attributes it
                Assert.True(n.Revision > before);               // revision bumped — the other door's stale check trips
                Assert.Equal(res.Revision, n.Revision);         // the broadcast carries the revision the caller got
                Assert.NotEmpty(n.Deltas);                      // non-empty deltas — the other door knows WHAT changed
                Assert.Contains(n.Deltas, d => d.Ref == mref && d.Props != null && d.Props.Contains("Expression"));
            }
            finally { _sessions.Bus.Changed -= handler; }
        }

        [Fact]
        public async Task Interleaved_human_and_agent_edits_undo_on_one_shared_timeline_last_first()
        {
            // The single-writer dispatcher gives ONE shared timeline regardless of which door made the edit.
            await _engine.CreateMeasureAsync("table:" + _table, "Iv_Human_A", "1", "human");
            await _engine.CreateMeasureAsync("table:" + _table, "Iv_Agent_B", "2", "agent");

            await _engine.UndoAsync("human");   // reverts the LAST edit (the agent's B), even though a human undid
            var afterFirst = await _engine.ListMeasuresAsync();
            Assert.DoesNotContain(afterFirst, m => m.Name == "Iv_Agent_B");
            Assert.Contains(afterFirst, m => m.Name == "Iv_Human_A");

            await _engine.UndoAsync("agent");   // reverts A — one timeline, not two per-origin stacks
            Assert.DoesNotContain(await _engine.ListMeasuresAsync(), m => m.Name == "Iv_Human_A");
        }

        [Fact]
        public async Task Rename_reports_changed_only_when_the_name_actually_differs()
        {
            // Pins the IRenameService contract (ModelSession.cs): the rename seam is a no-op that reports
            // Changed=false when the new name equals the current one, and Changed=true on a real rename. The
            // engine relies on this to avoid spurious revisions / fixup churn on idempotent renames.
            var mref = await _engine.CreateMeasureAsync("table:" + _table, "Rn_Seam", "1", "agent");

            var noop = await _engine.RenameObjectAsync(mref, "Rn_Seam", "agent");
            Assert.False(noop.Changed);

            var real = await _engine.RenameObjectAsync(mref, "Rn_Seam_2", "agent");
            Assert.True(real.Changed);
            Assert.Contains(await _engine.ListMeasuresAsync(), m => m.Name == "Rn_Seam_2");
        }

        [Fact]
        public async Task FormulaFixup_rewrites_a_renamed_column_in_a_measure_AND_an_RLS_filter()
        {
            // The adversarial scenario doc 02 §5.3 calls the worst silent-breakage case: a column referenced by
            // BOTH a measure and a governance-critical RLS filter. A rename that forgets the RLS filter dangles a
            // reference exactly where it's most dangerous. AutoFixup must rewrite both.
            var t = "'" + _table.Replace("'", "''") + "'";
            var colRef = await _engine.CreateCalculatedColumnAsync("table:" + _table, "FF_Col", "1", "agent");
            await _engine.CreateMeasureAsync("table:" + _table, "FF_Measure", $"SUM({t}[FF_Col])", "agent");
            await _engine.CreateRoleAsync("FF_Role", "Read", "agent");
            await _engine.SetTablePermissionAsync("FF_Role", "table:" + _table, $"{t}[FF_Col] > 0", "agent");

            await _engine.RenameObjectAsync(colRef, "FF_Col_Renamed", "agent");

            // 1) the measure's DAX is rewritten to the new column name (and the OLD bare ref is gone).
            var meas = (await _engine.ListMeasuresAsync()).First(m => m.Name == "FF_Measure");
            Assert.Contains("FF_Col_Renamed", meas.Expression);
            Assert.DoesNotContain("[FF_Col]", meas.Expression);

            // 2) the RLS filter is ALSO rewritten — serialize to TMDL and assert the role carries the new ref, not the old.
            var outDir = Path.Combine(Path.GetTempPath(), "semanticus_fftest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                await _engine.SaveAsync(outDir, "TMDL");
                var tmdl = Directory.EnumerateFiles(outDir, "*.tmdl", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();
                Assert.Contains(tmdl, f => f.Contains("FF_Role") && f.Contains("FF_Col_Renamed"));
                Assert.DoesNotContain(tmdl, f => f.Contains("FF_Role") && f.Contains("[FF_Col] >"));   // old RLS ref is gone
            }
            finally { try { Directory.Delete(outDir, true); } catch { /* best-effort temp cleanup */ } }
        }

        [Fact]
        public async Task Health_delta_rides_the_didChange_broadcast_both_doors_watch()
        {
            // Feature #4 dual-drive: the chip payload is a FIELD on the same ChangeNotification both doors
            // subscribe to — one broadcast, two renderings (native chip / MCP block). Self-contained Pro engine:
            // the class fixture runs on ambient entitlement, which must stay tier-agnostic.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            try
            {
                await engine.OpenAsync(FindTestBim());
                var table = (await engine.ListMeasuresAsync()).First().Table;

                var seen = new List<ChangeNotification>();
                Action<ChangeNotification> handler = n => seen.Add(n);
                sessions.Bus.Changed += handler;
                try
                {
                    await engine.CreateMeasureAsync("table:" + table, "Hd_Probe", "1", "agent");
                }
                finally { sessions.Bus.Changed -= handler; }

                var n = Assert.Single(seen);
                Assert.Equal("agent", n.Origin);
                Assert.NotNull(n.Health);                             // an undescribed measure = net-new Warning+ finding
                Assert.Contains("DESC-MEASURE", n.Health.New);
            }
            finally { engine.Dispose(); }
        }

        private static string FindTestBim() => TestModels.FindBim();
    }
}

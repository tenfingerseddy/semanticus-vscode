using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// #140 — a multi-object property edit (the property-grid multi-select) is ONE atomic, undoable change:
    /// one undo entry, one model/didChange broadcast, one "batch" audit record; a failure at any object rolls the
    /// WHOLE batch back to exactly the pre-gesture state and the error NAMES the object that failed. This
    /// generalizes the set_display_folder / apply_dax_script atomic-batch mechanism (a foreach inside one
    /// MutateAsync) to the generic property setter — it is not a parallel path. The single-object call stays the
    /// un-audited path (SetObjectPropertyAsync's behavior). dry_run rehearses it like any mutating op. These drive
    /// the public IEngine both doors share, so they assert the engine guarantee, not a UI path.
    /// </summary>
    public sealed class BatchPropertyEditsTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // Free engine on purpose: set_properties is FREE (like set_display_folder), so a Free tier proves it never gates.
        private static async Task<(LocalEngine engine, SessionManager sm, string[] measures)> FreshAsync()
        {
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, new Fake(false));
            await engine.CreateModelAsync("Batch", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            var m1 = await engine.CreateMeasureAsync(t, "Total", "1", "human");
            var m2 = await engine.CreateMeasureAsync(t, "Margin", "2", "human");
            var m3 = await engine.CreateMeasureAsync(t, "Cost", "3", "human");
            return (engine, sm, new[] { m1, m2, m3 });
        }

        private static async Task<string> HiddenAsync(LocalEngine e, string objRef)
            => (await e.GetObjectPropertiesAsync(objRef)).First(p => p.Name == "IsHidden").Value;

        [Fact]
        public async Task Batch_sets_a_property_on_many_objects_as_one_undo_step()
        {
            var (engine, _, ms) = await FreshAsync();
            using (engine)
            {
                var r = await engine.SetObjectPropertiesAsync(ms, "IsHidden", "true", "human");
                Assert.True(r.Changed);
                foreach (var m in ms) Assert.Equal("True", await HiddenAsync(engine, m));

                // ONE undo restores EVERY object — the whole point of batching over per-object set_property.
                await engine.UndoAsync("human");
                foreach (var m in ms) Assert.Equal("False", await HiddenAsync(engine, m));
            }
        }

        [Fact]
        public async Task Batch_is_exactly_one_broadcast_and_one_batch_audit_record()
        {
            var (engine, sm, ms) = await FreshAsync();
            using (engine)
            {
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);   // measure creates don't audit

                // Subscribe AFTER setup so the ONLY counted notification is the set_properties under test.
                var seen = new List<ChangeNotification>();
                Action<ChangeNotification> handler = n => seen.Add(n);
                sm.Bus.Changed += handler;
                try
                {
                    var before = sm.Current.Revision;
                    var r = await engine.SetObjectPropertiesAsync(ms, "IsHidden", "true", "human");
                    Assert.True(r.Changed);

                    // EXACTLY one model/didChange for the 3-object gesture — not three (the bug), not zero (dropped).
                    var n = Assert.Single(seen);
                    Assert.Equal("human", n.Origin);                // origin tagged so the other door attributes it
                    Assert.True(n.Revision > before);               // one revision bump
                    Assert.Equal(r.Revision, n.Revision);           // the broadcast carries the revision the caller got
                    Assert.NotEmpty(n.Deltas);                      // the other door learns WHAT changed
                }
                finally { sm.Bus.Changed -= handler; }

                // EXACTLY one audit record — the "batch" verdict for the whole atomic gesture, never three.
                var chain = await engine.ListVerifiedEditsAsync();
                var rec = Assert.Single(chain.Records);
                Assert.Equal("set_properties", rec.Op);
                Assert.Equal("batch", rec.Verdict);
                Assert.Equal("human", rec.Origin);
                Assert.True(rec.Revision > 0);
                Assert.True(chain.ChainIntact);

                // The record is NON-undoable: one undo restores all three objects, yet the record survives.
                await engine.UndoAsync("human");
                foreach (var m in ms) Assert.Equal("False", await HiddenAsync(engine, m));
                Assert.Single((await engine.ListVerifiedEditsAsync()).Records);
            }
        }

        [Fact]
        public async Task Failure_at_object_k_rolls_the_whole_batch_back_and_names_it()
        {
            var (engine, sm, ms) = await FreshAsync();
            using (engine)
            {
                var revBefore = sm.Current.Revision;

                // Object #3 does not exist — the batch must abort with #1 and #2 NEVER applied (all-or-nothing).
                var badRefs = new[] { ms[0], ms[1], "measure:Sales/Nope" };
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetObjectPropertiesAsync(badRefs, "IsHidden", "true", "human"));
                Assert.Contains("measure:Sales/Nope", ex.Message);   // the error NAMES the failing object...
                Assert.Contains("all-or-nothing", ex.Message);       // ...and states the guarantee (contract §1)

                // State is exactly pre-gesture: the objects that "would have" changed did not.
                Assert.Equal("False", await HiddenAsync(engine, ms[0]));
                Assert.Equal("False", await HiddenAsync(engine, ms[1]));
                // Byte-level: serialize to TMDL — a rolled-back IsHidden=true leaves NO `isHidden` marker anywhere.
                Assert.DoesNotContain("isHidden", await ToTmdlAsync(engine));
                Assert.Equal(revBefore, sm.Current.Revision);                    // no revision bump ⇒ no orphan undo entry
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);   // a rolled-back batch leaves no phantom record
            }
        }

        [Fact]
        public async Task Bad_value_names_the_object_and_changes_nothing()
        {
            var (engine, _, ms) = await FreshAsync();
            using (engine)
            {
                // 'IsHidden' is a bool — "maybe" cannot convert; the batch aborts, naming the row, changing nothing.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetObjectPropertiesAsync(ms, "IsHidden", "maybe", "human"));
                Assert.Contains("IsHidden", ex.Message);
                Assert.Contains("all-or-nothing", ex.Message);
                foreach (var m in ms) Assert.Equal("False", await HiddenAsync(engine, m));
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);
            }
        }

        [Fact]
        public async Task Single_object_batch_keeps_existing_behavior_and_writes_no_batch_record()
        {
            var (engine, _, ms) = await FreshAsync();
            using (engine)
            {
                var r = await engine.SetObjectPropertiesAsync(new[] { ms[0] }, "IsHidden", "true", "human");
                Assert.True(r.Changed);
                Assert.Equal("True", await HiddenAsync(engine, ms[0]));
                // A single-object call is not a batch — it stays the un-audited path, matching set_property.
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);

                // One ref DELEGATES to SetObjectPropertyAsync, so error behavior is byte-identical to set_property —
                // same exception type, same message shape (the batch path's "all-or-nothing" wording never leaks in).
                var viaSingle = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetObjectPropertyAsync("measure:Sales/Nope", "IsHidden", "true", "human"));
                var viaBatch = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetObjectPropertiesAsync(new[] { "measure:Sales/Nope" }, "IsHidden", "true", "human"));
                Assert.Equal(viaSingle.Message, viaBatch.Message);
                Assert.DoesNotContain("all-or-nothing", viaBatch.Message);
            }
        }

        [Fact]
        public async Task Setter_failure_names_the_object_and_the_real_reason()
        {
            var (engine, _, ms) = await FreshAsync();
            using (engine)
            {
                // "Total" already exists — the wrapper's Name setter throws its own validation error (a duplicate
                // measure name) from INSIDE reflection. The batch must surface the failing REF + the setter's real
                // reason, never a bare TargetInvocationException, and roll the earlier rename back.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetObjectPropertiesAsync(new[] { ms[1], ms[2] }, "Name", "Total", "human"));
                Assert.Contains(ms[1], ex.Message);            // the failing object (the FIRST rename collides)
                Assert.Contains("Total", ex.Message);          // the setter's real reason names the duplicate
                Assert.Contains("all-or-nothing", ex.Message);

                // Exactly-pre-gesture: all three measures still carry their original names.
                var names = (await engine.ListMeasuresAsync()).Select(m => m.Name).ToArray();
                Assert.Contains("Total", names);
                Assert.Contains("Margin", names);
                Assert.Contains("Cost", names);
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);
            }
        }

        [Fact]
        public async Task Mid_batch_setter_failure_rolls_back_the_already_applied_change()
        {
            var (engine, sm, ms) = await FreshAsync();
            using (engine)
            {
                var revBefore = sm.Current.Revision;
                // Both renamed to the same FRESH name (no pre-existing collision): setter #1 (Margin -> Fresh)
                // SUCCEEDS, setter #2 (Cost -> Fresh) throws the wrapper's duplicate-name validation. This is the
                // only shape where pass 2 has PARTIALLY applied changes before the abort — pass-1 failures apply
                // nothing — so it is THE mid-batch rollback proof.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetObjectPropertiesAsync(new[] { ms[1], ms[2] }, "Name", "Fresh", "human"));
                Assert.Contains(ms[2], ex.Message);        // the SECOND ref is the failing object...
                Assert.Contains("Fresh", ex.Message);      // ...with the setter's real reason (the duplicate name)
                Assert.Contains("all-or-nothing", ex.Message);

                // Measure #1's ALREADY-APPLIED rename was rolled back — exactly-pre-gesture state.
                var names = (await engine.ListMeasuresAsync()).Select(m => m.Name).ToArray();
                Assert.Contains("Margin", names);
                Assert.Contains("Cost", names);
                Assert.DoesNotContain("Fresh", names);

                // No orphan undo entry and no phantom record: the revision never bumped, and the NEXT undo pops the
                // pre-gesture op (the Cost measure's create) — nothing from the failed batch sits on the stack.
                Assert.Equal(revBefore, sm.Current.Revision);
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);
                await engine.UndoAsync("human");
                var afterUndo = (await engine.ListMeasuresAsync()).Select(m => m.Name).ToArray();
                Assert.DoesNotContain("Cost", afterUndo);
                Assert.Contains("Margin", afterUndo);
            }
        }

        [Fact]
        public async Task Refs_resolve_pre_gesture_so_an_earlier_rename_cannot_stale_a_later_ref()
        {
            var (engine, _, ms) = await FreshAsync();
            using (engine)
            {
                // The table renames FIRST in pass 2, after which the measure's ref string ("measure:Sales/Total")
                // names a table that no longer exists — lazy per-iteration resolution would abort "not found".
                // Pass 1 resolved every ref against the PRE-gesture model, so the batch succeeds: the documented
                // pre-gesture-resolution semantics, pinned. (A table and a measure may share a name — measure
                // uniqueness is checked against measures model-wide and same-table columns only.)
                var r = await engine.SetObjectPropertiesAsync(new[] { "table:Sales", ms[0] }, "Name", "Duo", "human");
                Assert.True(r.Changed);
                var renamed = (await engine.ListMeasuresAsync()).Single(m => m.Name == "Duo");
                Assert.Equal("Duo", renamed.Table);   // the measure renamed AND rides the renamed table

                // Still one atomic gesture: a single undo restores BOTH names.
                await engine.UndoAsync("human");
                Assert.Contains(await engine.ListMeasuresAsync(), m => m.Name == "Total" && m.Table == "Sales");
            }
        }

        [Fact]
        public async Task Duplicate_refs_are_refused_by_resolved_identity()
        {
            var (engine, _, ms) = await FreshAsync();
            using (engine)
            {
                // The same ref twice — refused before anything is set (a duplicate would double-apply the write).
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetObjectPropertiesAsync(new[] { ms[0], ms[0] }, "IsHidden", "true", "human"));
                Assert.Contains(ms[0], ex.Message);
                Assert.Contains("duplicate", ex.Message);
                Assert.Equal("False", await HiddenAsync(engine, ms[0]));

                // The detection is by RESOLVED IDENTITY, not string compare: AS name lookup is case-insensitive, so
                // a case-variant spelling resolves to the SAME object and must be refused as a duplicate too.
                var variant = ms[0].Replace("measure:Sales/", "measure:SALES/");
                var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetObjectPropertiesAsync(new[] { ms[0], variant }, "IsHidden", "true", "human"));
                Assert.Contains("resolves to the same object", ex2.Message);
                Assert.Contains(ms[0], ex2.Message);           // ...naming the first spelling it collided with
                Assert.Equal("False", await HiddenAsync(engine, ms[0]));
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);
            }
        }

        [Fact]
        public async Task Already_equal_objects_produce_an_honest_M_of_N_record_and_a_noop_records_nothing()
        {
            var (engine, _, ms) = await FreshAsync();
            using (engine)
            {
                // m1 is already hidden (via the un-audited single-object path), m2 is not: the batch changes ONLY m2.
                await engine.SetObjectPropertiesAsync(new[] { ms[0] }, "IsHidden", "true", "human");
                var r = await engine.SetObjectPropertiesAsync(new[] { ms[0], ms[1] }, "IsHidden", "true", "human");
                Assert.True(r.Changed);

                // The record must certify what HAPPENED (1 of 2), never the 2-object change that didn't occur.
                var rec = Assert.Single((await engine.ListVerifiedEditsAsync()).Records);
                Assert.Equal("set_properties", rec.Op);
                Assert.Contains("1 of 2", rec.Summary);
                Assert.Contains("\"attempted\":2", rec.Evidence);
                Assert.Contains("\"changed\":1", rec.Evidence);
                Assert.Contains(ms[1], rec.Evidence);          // changedRefs names the object that actually moved

                // Re-running the same batch is a no-op: Changed=false and NO second record (no phantom certificate).
                var noop = await engine.SetObjectPropertiesAsync(new[] { ms[0], ms[1] }, "IsHidden", "true", "human");
                Assert.False(noop.Changed);
                Assert.Single((await engine.ListVerifiedEditsAsync()).Records);
            }
        }

        [Fact]
        public async Task Empty_refs_teaches_a_recovery_instead_of_silently_succeeding()
        {
            var (engine, _, _) = await FreshAsync();
            using (engine)
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetObjectPropertiesAsync(Array.Empty<string>(), "IsHidden", "true", "human"));
                Assert.Contains("list_objects", ex.Message);   // the failure names a discovery op (contract §1)
            }
        }

        [Fact]
        public async Task Dry_run_of_set_properties_rehearses_without_committing()
        {
            var (engine, sm, ms) = await FreshAsync();
            using (engine)
            {
                var revBefore = sm.Current.Revision;
                var argsJson = System.Text.Json.JsonSerializer.Serialize(new { objRefs = ms, propertyName = "IsHidden", value = "true" });
                var rpt = await engine.DryRunOpAsync("set_properties", argsJson);

                Assert.True(rpt.WouldSucceed);
                Assert.Null(rpt.Error);
                Assert.NotEmpty(rpt.Deltas);       // the would-be change set
                Assert.NotEmpty(rpt.Mutations);    // the mutation label was rehearsed on the real code path
                Assert.Contains("Rehearsal only", rpt.Note);

                // Nothing committed: objects still visible, no revision bump, no audit record.
                foreach (var m in ms) Assert.Equal("False", await HiddenAsync(engine, m));
                Assert.Equal(revBefore, sm.Current.Revision);
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);
            }
        }

        // Serialize the model to TMDL and return the concatenated text — the suite's byte-level "model unchanged"
        // oracle (DualDriveUndoTests uses the same save-and-inspect idiom).
        private static async Task<string> ToTmdlAsync(LocalEngine engine)
        {
            var dir = Path.Combine(Path.GetTempPath(), "batchprop_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                await engine.SaveAsync(dir, "TMDL");
                return string.Concat(Directory.EnumerateFiles(dir, "*.tmdl", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText));
            }
            finally { try { Directory.Delete(dir, true); } catch { /* best-effort temp cleanup */ } }
        }
    }
}

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The Verified Edits AUDIT LAYER + ACCOUNTABLE CHECKPOINT — the Pro "referee memory". The contract these tests
    /// pin down: a verified op leaves an APPEND-ONLY, hash-chained record that survives undo (the audit write is NON-
    /// undoable, so unlike a waiver it can't be rolled back); the chain self-checks and fails LOUD on tampering / a
    /// corrupt blob; the trail records ONLY real evidence (mode-off / empty / unproven-offline record nothing); and the
    /// two accountable checkpoints — apply_plan overrideIds and the deploy_live gate — write the reasoned override to
    /// the chain BEFORE anything ships, never as a silent suppression and never a hard wall. All deterministic + offline
    /// (the only network is an intentional connection-refused to localhost:59999, which fails AFTER the checkpoint).
    /// </summary>
    public sealed class VerifiedEditsAuditTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // Tests construct the SessionManager so a test can reach the open Session (sm.Current) for raw model access
        // (ReadAsync on the dispatcher thread) when it needs to inspect or TAMPER with the audit annotation.
        private static async Task<(LocalEngine engine, SessionManager sm)> OpenAsync(bool pro)
        {
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, new Fake(pro));
            await engine.OpenAsync(TestModels.FindBim());
            return (engine, sm);
        }

        private static async Task<string> FirstMeasureRefAsync(LocalEngine e)
        {
            var ms = await e.ListMeasuresAsync();
            Assert.NotEmpty(ms);
            return ms[0].Ref;
        }

        // A fresh, deterministic RED-gate model: 2 visible measures with NO descriptions ⇒ >50% undescribed ⇒ the
        // DESC-MEASURE hard gate fires (verified offline in this file's design). Zero BPA violations, so the ONLY
        // blocker is the readiness gate — the cheapest reproducible RED state through public ops.
        private static async Task<(LocalEngine engine, SessionManager sm, string m1, string m2)> OpenRedGateModelAsync()
        {
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, new Fake(true));
            await engine.CreateModelAsync("GateTest", 1567);
            var t = await engine.CreateTableAsync("Facts", "human");
            var m1 = await engine.CreateMeasureAsync(t, "Sales Amount", "1", "human");   // human-readable, NO description
            var m2 = await engine.CreateMeasureAsync(t, "Total Cost", "1", "human");
            return (engine, sm, m1, m2);
        }

        // ============================================================================================================
        // A. The append-only guarantee — the HEADLINE. A verified edit's record survives undo (a waiver-style
        //    undoable write would not), and the audit write is not itself an undo step.
        // ============================================================================================================

        [Fact]
        public async Task Record_survives_undo_change()
        {
            var (engine, _) = await OpenAsync(pro: true);
            using (engine)
            {
                var mref = await FirstMeasureRefAsync(engine);
                var original = await engine.GetDaxAsync(mref);

                await engine.SetVerifiedModeAsync(true, "human");
                await engine.SetDaxAsync(mref, "1 + 1", "agent");   // valid ⇒ a "validated" record is appended

                var before = await engine.ListVerifiedEditsAsync();
                Assert.Single(before.Records);
                var rec = before.Records[0];
                Assert.Equal("validated", rec.Verdict);
                Assert.Equal("set_dax", rec.Op);
                Assert.Equal("agent", rec.Origin);
                Assert.Equal(mref, rec.ObjectRef);
                Assert.False(string.IsNullOrEmpty(rec.When));   // engine-stamped time
                Assert.True(rec.Revision > 0);
                Assert.True(before.ChainIntact);

                await engine.UndoAsync("human");

                // THE test: undo restored the DAX, but the audit record is STILL there and the chain still intact —
                // proving the record was written through the NON-undoable seam (a waiver-style undoable write fails here).
                Assert.Equal(original, await engine.GetDaxAsync(mref));
                var after = await engine.ListVerifiedEditsAsync();
                Assert.Single(after.Records);
                Assert.True(after.ChainIntact);
                Assert.Equal(rec.Hash, after.Records[0].Hash);   // same record, untouched
            }
        }

        [Fact]
        public async Task Record_write_is_not_an_undo_step()
        {
            var (engine, _) = await OpenAsync(pro: true);
            using (engine)
            {
                var mref = await FirstMeasureRefAsync(engine);
                var original = await engine.GetDaxAsync(mref);

                await engine.SetVerifiedModeAsync(true, "human");
                await engine.SetDaxAsync(mref, "1 + 1", "agent");
                Assert.Single((await engine.ListVerifiedEditsAsync()).Records);

                // A SINGLE undo must restore the original body. If the audit write had inserted itself as an extra
                // undo step, one undo would only pop the phantom and leave "1 + 1" in place.
                await engine.UndoAsync("human");
                Assert.Equal(original, await engine.GetDaxAsync(mref));
            }
        }

        // ============================================================================================================
        // B. Chain mechanics — links, sequence, tamper detection, loud recovery from a corrupt blob.
        // ============================================================================================================

        [Fact]
        public async Task Chain_links_and_seq()
        {
            var (engine, _) = await OpenAsync(pro: true);
            using (engine)
            {
                var ms = await engine.ListMeasuresAsync();
                Assert.True(ms.Length >= 2);
                await engine.SetVerifiedModeAsync(true, "human");
                await engine.SetDaxAsync(ms[0].Ref, "1 + 1", "agent");
                await engine.SetDaxAsync(ms[1].Ref, "2 + 2", "agent");

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.Equal(2, chain.Records.Length);
                Assert.Equal(1, chain.Records[0].Seq);
                Assert.Equal(2, chain.Records[1].Seq);
                Assert.Equal(chain.Records[0].Hash, chain.Records[1].PrevHash);   // each record links the previous by hash
                Assert.True(chain.ChainIntact);
                Assert.Equal(0, chain.FirstBrokenSeq);
            }
        }

        [Fact]
        public async Task Tamper_is_detected()
        {
            var (engine, sm) = await OpenAsync(pro: true);
            using (engine)
            {
                var ms = await engine.ListMeasuresAsync();
                await engine.SetVerifiedModeAsync(true, "human");
                await engine.SetDaxAsync(ms[0].Ref, "1 + 1", "agent");
                await engine.SetDaxAsync(ms[1].Ref, "2 + 2", "agent");   // >= 2 records

                // Tamper with record #2's Summary in the raw JSON, via the same NON-undoable seam the store uses.
                // Model access must be on the dispatcher thread — do the Get/modify/Set inside ReadAsync.
                await sm.Require().ReadAsync(m =>
                {
                    var raw = AuditAnnotations.Get(m, VerifiedEditsStore.Annotation);
                    var recs = JsonSerializer.Deserialize<System.Collections.Generic.List<VerifiedEditRecord>>(raw);
                    recs[1].Summary = "edited after the fact";   // change the field WITHOUT recomputing its Hash
                    AuditAnnotations.Set(m, VerifiedEditsStore.Annotation, JsonSerializer.Serialize(recs));
                    return true;
                });

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.False(chain.ChainIntact);
                Assert.Equal(2, chain.FirstBrokenSeq);   // the self-check flags exactly the tampered record
            }
        }

        [Fact]
        public async Task Corrupt_blob_preserved_and_chain_restarts()
        {
            var (engine, sm) = await OpenAsync(pro: true);
            using (engine)
            {
                // Set the audit annotation to garbage (not JSON) through the seam the store reads.
                await sm.Require().ReadAsync(m =>
                {
                    AuditAnnotations.Set(m, VerifiedEditsStore.Annotation, "not json");
                    return true;
                });

                // The next verified edit must append fine: it preserves the damaged blob verbatim and opens a fresh
                // chain with an explicit chain-reset record — loud + lossless, never a silent degrade-to-empty.
                var mref = await FirstMeasureRefAsync(engine);
                await engine.SetVerifiedModeAsync(true, "human");
                await engine.SetDaxAsync(mref, "1 + 1", "agent");

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.True(chain.ChainIntact);
                Assert.True(chain.Records.Length >= 2);
                Assert.Equal("chain-reset", chain.Records[0].Op);
                Assert.Equal("set_dax", chain.Records[1].Op);   // the real edit follows the reset

                // The garbage was preserved verbatim under the Damaged annotation (never overwritten).
                var damaged = await sm.Require().ReadAsync(m => AuditAnnotations.Get(m, VerifiedEditsStore.Damaged));
                Assert.Contains("not json", damaged);
            }
        }

        // ============================================================================================================
        // C. Honest-recording negatives — the trail must NEVER contain manufactured evidence.
        // ============================================================================================================

        [Fact]
        public async Task Mode_off_records_nothing()
        {
            var (engine, _) = await OpenAsync(pro: true);   // Pro, but Verified Mode is OFF by default
            using (engine)
            {
                var mref = await FirstMeasureRefAsync(engine);
                await engine.SetDaxAsync(mref, "1 + 1", "agent");   // committed, but not a VERIFIED edit
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);   // recording rides the Verified-Mode toggle
            }
        }

        [Fact]
        public async Task Empty_expression_records_nothing()
        {
            var (engine, _) = await OpenAsync(pro: true);
            using (engine)
            {
                var table = "table:" + (await engine.ListMeasuresAsync())[0].Table;
                await engine.SetVerifiedModeAsync(true, "human");
                await engine.CreateMeasureAsync(table, "Empty_body_measure", "", "agent");   // empty body was never validated
                // An unvalidated body must NEVER yield a "validated" record — that is exactly the manufactured
                // evidence an audit trail exists to prevent.
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);
            }
        }

        [Fact]
        public async Task Optimize_offline_records_nothing()
        {
            var (engine, _) = await OpenAsync(pro: true);
            using (engine)
            {
                var mref = await FirstMeasureRefAsync(engine);
                // Offline: no live connection to PROVE equivalence, so no evidence was produced. The verdict degrades
                // to "unproven-offline" and the chain stays empty — a proof-shaped op records only when it proved.
                var res = await engine.OptimizeMeasureAsync(mref, new[] { "1", "2" }, new[] { "'Date'[Year]" }, null, true, "human");
                Assert.Equal("unproven-offline", res.Verdict);
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);
            }
        }

        // ============================================================================================================
        // D. apply_plan accountable override — a reasoned override ships an unverifiable item + is recorded.
        // ============================================================================================================

        [Fact]
        public async Task Override_without_reason_is_refused()
        {
            var (engine, _) = await OpenAsync(pro: true);
            using (engine)
            {
                // An override with no reason is an unexplained silent suppression — refused before anything runs.
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    engine.ApplyPlanAsync(null, "human", overrideIds: new[] { "x" }, overrideReason: null));
            }
        }

        [Fact]
        public async Task Override_ships_unverified_item_and_is_recorded()
        {
            var (engine, _) = await OpenAsync(pro: true);
            using (engine)
            {
                var mref = await FirstMeasureRefAsync(engine);   // measure:Date/Days In Current Quarter
                var original = await engine.GetDaxAsync(mref);
                var newBody = original + " + 0";                 // valid, DIFFERENT DAX (same result, but the audit doesn't care)

                // A set_dax item WITH a (non-empty) verify matrix ⇒ auto-approved; but with NO live connection the
                // equivalence gate can't run, so apply_plan normally SKIPS it as "unverified".
                var view = await engine.AddPlanItemAsync(mref, "set_dax", newBody, "override test",
                    new[] { "'Date'[Date]" }, null, "human");
                var itemId = view.Items.Single().Id;
                await engine.SetPlanItemAsync(itemId, null, approved: true, "human");

                var rep = await engine.ApplyPlanAsync(null, "human",
                    overrideIds: new[] { itemId }, overrideReason: "accepting risk: same value, provable only live");

                // The item shipped despite the unprovable verdict — VerifyState stays honest, Note flags the override.
                var applied = rep.Items.Single();
                Assert.Equal("applied", applied.Status);
                Assert.Equal("unverified", applied.VerifyState);
                Assert.StartsWith("override accepted", applied.Note);
                Assert.Equal(newBody, await engine.GetDaxAsync(mref));   // the DAX actually changed

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.True(chain.ChainIntact);
                // A per-item override record (the accountable, per-object queryable act)...
                var item = chain.Records.Single(r => r.Op == "apply_plan-item");
                Assert.Equal("overridden", item.Verdict);
                Assert.Equal("accepting risk: same value, provable only live", item.OverrideReason);
                Assert.False(string.IsNullOrEmpty(item.BodyHash));
                // ...plus the batch summary whose Evidence records the overridden denominator.
                var batch = chain.Records.Single(r => r.Op == "apply_plan");
                Assert.Equal("batch", batch.Verdict);
                Assert.Contains("\"overridden\":1", batch.Evidence);
            }
        }

        [Fact]
        public async Task No_override_skips_as_before()
        {
            var (engine, _) = await OpenAsync(pro: true);
            using (engine)
            {
                var mref = await FirstMeasureRefAsync(engine);
                var original = await engine.GetDaxAsync(mref);
                var newBody = original + " + 0";

                var view = await engine.AddPlanItemAsync(mref, "set_dax", newBody, "no-override test",
                    new[] { "'Date'[Date]" }, null, "human");
                var itemId = view.Items.Single().Id;
                await engine.SetPlanItemAsync(itemId, null, approved: true, "human");

                var rep = await engine.ApplyPlanAsync(null, "human");   // NO overrideIds

                // Unprovable offline + not overridden ⇒ skipped, honest VerifyState, DAX unchanged. A batch that
                // applied nothing records nothing (no phantom "batch" record for a no-op).
                var it = rep.Items.Single();
                Assert.Equal("skipped", it.Status);
                Assert.Equal("unverified", it.VerifyState);
                Assert.Equal(original, await engine.GetDaxAsync(mref));
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);
            }
        }

        // ============================================================================================================
        // E. deploy_live checkpoint — the gate PAUSES a red-gate commit; a reasoned override is recorded BEFORE the
        //    push (so the record travels inside the deployed artifact). The AdventureWorks fixture is inherently RED
        //    (138 blocking BPA errors + the undescribed-measures gate); a fresh 2-measure model is a cleaner RED
        //    (only the readiness gate). Both use localhost:59999 — the connection is refused AFTER the checkpoint.
        // ============================================================================================================

        [Fact]
        public async Task Red_gate_blocks_commit_without_reason()
        {
            var (engine, _, _, _) = await OpenRedGateModelAsync();
            using (engine)
            {
                // A committed deploy into a RED gate with no reason is PAUSED (not a hard wall — a reason unblocks it).
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    engine.DeployLiveAsync("localhost:59999", "db", null, null, null, commit: true, "human", overrideReason: null));
                Assert.Contains("blocked by the deploy gate", ex.Message);
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);   // paused before anything shipped ⇒ no record
            }
        }

        [Fact]
        public async Task Red_gate_override_is_recorded_before_the_push()
        {
            var (engine, _, _, _) = await OpenRedGateModelAsync();
            using (engine)
            {
                // Same RED state + a reason: the call still THROWS (localhost:59999 has no server — the connection
                // fails AFTER the checkpoint), but the override record was already written. Catch ANY exception.
                Exception caught = null;
                try
                {
                    await engine.DeployLiveAsync("localhost:59999", "db", null, null, null, commit: true, "human",
                        overrideReason: "ship it — demo");
                }
                catch (Exception ex) { caught = ex; }

                Assert.NotNull(caught);
                Assert.DoesNotContain("blocked by the deploy gate", caught.Message);   // it got PAST the gate

                var chain = await engine.ListVerifiedEditsAsync();
                var rec = chain.Records.Single(r => r.Op == "deploy_live");
                Assert.Equal("overridden", rec.Verdict);
                Assert.Equal("ship it — demo", rec.OverrideReason);
                Assert.Contains("undescribed", rec.Evidence);   // Evidence carries the blockers — the record is written pre-push
            }
        }

        [Fact]
        public async Task Green_gate_needs_no_reason()
        {
            // A cheap PASSING gate: the same fresh 2-measure model with descriptions added ⇒ 0% undescribed, 0 BPA ⇒
            // the gate passes. (Making the AdventureWorks fixture green is impractical — 138 blocking BPA errors — so
            // the checkpoint's green path is exercised on a purpose-built model instead.)
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, new Fake(true));
            using (engine)
            {
                await engine.CreateModelAsync("GateTest", 1567);
                var t = await engine.CreateTableAsync("Facts", "human");
                var m1 = await engine.CreateMeasureAsync(t, "Sales Amount", "1", "human");
                var m2 = await engine.CreateMeasureAsync(t, "Total Cost", "1", "human");
                await engine.SetDescriptionAsync(m1, "The total sales amount in reporting currency.", "human");
                await engine.SetDescriptionAsync(m2, "The total cost of goods sold in reporting currency.", "human");

                // Commit with NO reason: the gate is GREEN so the checkpoint waves it through — the call then fails on
                // the (refused) localhost connection, NOT on the gate, and no override record is written.
                Exception caught = null;
                try
                {
                    await engine.DeployLiveAsync("localhost:59999", "db", null, null, null, commit: true, "human", overrideReason: null);
                }
                catch (Exception ex) { caught = ex; }

                Assert.NotNull(caught);
                Assert.DoesNotContain("blocked by the deploy gate", caught.Message);
                var chain = await engine.ListVerifiedEditsAsync();
                Assert.DoesNotContain(chain.Records, r => r.Verdict == "overridden");   // a green gate needs no override, so none is recorded
            }
        }

        // ============================================================================================================
        // F. Export — Pro-gated; renders both markdown (human) and json (CI, round-trippable).
        // ============================================================================================================

        [Fact]
        public async Task Export_is_pro_gated()
        {
            var (engine, _) = await OpenAsync(pro: false);
            using (engine)
            {
                await Assert.ThrowsAsync<EntitlementException>(() => engine.ExportVerifiedEditsAsync("md"));
            }
        }

        [Fact]
        public async Task Export_renders()
        {
            var (engine, _) = await OpenAsync(pro: true);
            using (engine)
            {
                var mref = await FirstMeasureRefAsync(engine);
                await engine.SetVerifiedModeAsync(true, "human");
                await engine.SetDaxAsync(mref, "1 + 1", "agent");   // >= 1 record to render

                var md = await engine.ExportVerifiedEditsAsync("md");
                Assert.Contains("Verified Edits", md);
                Assert.Contains("set_dax", md);   // the op name is in the rendered trail

                // The json export is camelCase — the SAME shape list_verified_edits emits over the doors, so one
                // CI consumer can parse either surface.
                var json = await engine.ExportVerifiedEditsAsync("json");
                Assert.Contains("\"records\"", json);
                var round = JsonSerializer.Deserialize<VerifiedEditsChain>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Assert.NotNull(round);
                Assert.Equal((await engine.ListVerifiedEditsAsync()).Records.Length, round.Records.Length);   // same record count
            }
        }

        // ============================================================================================================
        // G. Adversarial-review fixes — pinning tests. Each pins a concrete hole the review closed; every one PASSES
        //    against current code (a failure here means the fix regressed, not the test). All deterministic + offline.
        // ============================================================================================================

        // --- G1. The audit-dirty bit — a verified record leaves the session unsaved even when undo returns the model
        //     to its checkpoint. Without this bit, save/git read CLEAN and silently drop the (non-undoable) record. ---

        [Fact]
        public async Task Audit_record_marks_session_dirty_even_after_undo()
        {
            var (engine, sm) = await OpenAsync(pro: true);
            using (engine)
            {
                // AdventureWorks opens CLEAN (the ctor sets a checkpoint) — a clean baseline is the whole point here.
                Assert.False((await engine.SessionInfoAsync()).HasUnsavedChanges);

                var mref = await FirstMeasureRefAsync(engine);
                await engine.SetVerifiedModeAsync(true, "human");
                await engine.SetDaxAsync(mref, "1 + 1", "agent");   // appends a "validated" record (marks audit-dirty)
                await engine.UndoAsync("human");                    // undo restores the DAX → model is back AT checkpoint

                // The model is at its checkpoint (TE2's dirty flag reads clean), but the append-only trail is unsaved —
                // the audit-dirty bit OR-es in so save/git can't silently drop the record. Check both surfaces.
                Assert.True((await engine.SessionInfoAsync()).HasUnsavedChanges);
                Assert.True(sm.Require().HasUnsavedChanges);
            }
        }

        [Fact]
        public async Task Audit_record_round_trips_through_save()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "verified-edits-roundtrip-" + Guid.NewGuid().ToString("N") + ".bim");
            try
            {
                string hash;
                var (engine, _) = await OpenAsync(pro: true);
                using (engine)
                {
                    var mref = await FirstMeasureRefAsync(engine);
                    await engine.SetVerifiedModeAsync(true, "human");
                    await engine.SetDaxAsync(mref, "1 + 1", "agent");
                    await engine.UndoAsync("human");                // record survives undo, session is audit-dirty
                    hash = (await engine.ListVerifiedEditsAsync()).Records.Single().Hash;

                    await engine.SaveAsync(path, "bim");            // a checkpoint-resetting save persists the trail...
                    Assert.False((await engine.SessionInfoAsync()).HasUnsavedChanges);   // ...and clears the audit-dirty bit
                }

                // Reopen the saved file with a fresh engine: the record + its chain travelled inside the model file.
                var sm2 = new SessionManager();
                var engine2 = new LocalEngine(sm2, new Fake(true));
                using (engine2)
                {
                    await engine2.OpenAsync(path);
                    var chain = await engine2.ListVerifiedEditsAsync();
                    Assert.Single(chain.Records);
                    Assert.True(chain.ChainIntact);
                    Assert.Equal(hash, chain.Records[0].Hash);   // same record, byte-for-byte through the save
                }
            }
            finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        }

        // --- G2. Tail truncation is detected by the head anchor — link-checking alone can't see it (a prefix of a
        //     valid chain is itself a valid chain), so Load cross-checks the "<count>|<lastHash>" head. ---

        [Fact]
        public async Task Tail_truncation_is_detected()
        {
            var (engine, sm) = await OpenAsync(pro: true);
            using (engine)
            {
                var ms = await engine.ListMeasuresAsync();
                Assert.True(ms.Length >= 3);
                await engine.SetVerifiedModeAsync(true, "human");
                await engine.SetDaxAsync(ms[0].Ref, "1 + 1", "agent");
                await engine.SetDaxAsync(ms[1].Ref, "2 + 2", "agent");
                await engine.SetDaxAsync(ms[2].Ref, "3 + 3", "agent");   // 3 records

                // Drop the LAST record from the chain JSON but leave the head anchor untouched — the internal links of
                // the remaining 2 stay perfectly valid, so ONLY the head cross-check catches the dropped newest record.
                await sm.Require().ReadAsync(m =>
                {
                    var recs = JsonSerializer.Deserialize<System.Collections.Generic.List<VerifiedEditRecord>>(AuditAnnotations.Get(m, VerifiedEditsStore.Annotation));
                    recs.RemoveAt(recs.Count - 1);
                    AuditAnnotations.Set(m, VerifiedEditsStore.Annotation, JsonSerializer.Serialize(recs));   // head annotation left alone
                    return true;
                });

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.False(chain.ChainIntact);
                Assert.Equal(0, chain.FirstBrokenSeq);            // the links are intact — the break is at the tail, not mid-chain
                Assert.Contains("head", chain.Note);
                Assert.Contains("tail", chain.Note);
            }
        }

        [Fact]
        public async Task Deleted_chain_with_surviving_head_is_flagged()
        {
            var (engine, sm) = await OpenAsync(pro: true);
            using (engine)
            {
                var mref = await FirstMeasureRefAsync(engine);
                await engine.SetVerifiedModeAsync(true, "human");
                await engine.SetDaxAsync(mref, "1 + 1", "agent");

                // Delete the CHAIN annotation wholesale but leave the head anchor — a head with no chain is a trail that
                // was deleted, not an empty model. Load must fail LOUD, not read as never-had-history.
                await sm.Require().ReadAsync(m =>
                {
                    AuditAnnotations.Set(m, VerifiedEditsStore.Annotation, null);   // null removes the annotation
                    return true;
                });

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.False(chain.ChainIntact);
                Assert.Contains("deleted", chain.Note);
            }
        }

        // --- G3. The seq-0 sentinel collision is killed — FirstBroken flags by 1-based POSITION (not the stored Seq),
        //     so a tampered record can't set its own Seq to 0 to collide with the "no break" sentinel. ---

        [Fact]
        public async Task Seq_zero_tamper_flags_the_position_not_zero()
        {
            var (engine, sm) = await OpenAsync(pro: true);
            using (engine)
            {
                var ms = await engine.ListMeasuresAsync();
                await engine.SetVerifiedModeAsync(true, "human");
                await engine.SetDaxAsync(ms[0].Ref, "1 + 1", "agent");
                await engine.SetDaxAsync(ms[1].Ref, "2 + 2", "agent");   // 2 records

                // Tamper record #2: change its Summary AND set its Seq to 0 — the pre-fix code returned the Seq as the
                // break signal, so a Seq of 0 would collide with the "no break" sentinel and read as INTACT.
                await sm.Require().ReadAsync(m =>
                {
                    var recs = JsonSerializer.Deserialize<System.Collections.Generic.List<VerifiedEditRecord>>(AuditAnnotations.Get(m, VerifiedEditsStore.Annotation));
                    recs[1].Summary = "edited after the fact";
                    recs[1].Seq = 0;                              // the forged sentinel
                    AuditAnnotations.Set(m, VerifiedEditsStore.Annotation, JsonSerializer.Serialize(recs));
                    return true;
                });

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.False(chain.ChainIntact);
                Assert.Equal(2, chain.FirstBrokenSeq);   // the POSITION, not the forged Seq=0
            }
        }

        // --- G4. Field-smear is detected — the hash basis length-prefixes every field, so content can't shift across a
        //     field boundary undetectably (a bare-\n join let "Summary\nX"|"Y" re-split as "Summary"|"X\nY" identically). ---

        [Fact]
        public async Task Field_smear_across_the_boundary_is_detected()
        {
            var (engine, sm, _, _) = await OpenRedGateModelAsync();
            using (engine)
            {
                // One record carrying BOTH a Summary and an OverrideReason (adjacent hash fields) — the deploy override
                // is the cheapest way to produce it offline (localhost:59999 fails AFTER the record is written).
                try { await engine.DeployLiveAsync("localhost:59999", "db", null, null, null, commit: true, "human", overrideReason: "same value approved by finance"); }
                catch { /* the refused connection is expected — the record was already written pre-push */ }

                // Shift the boundary: steal the reason's first word onto the end of Summary (\n-joined) and drop it from
                // the reason. Under a bare-\n hash this exact move is digest-identical; under length-prefixing it is not.
                await sm.Require().ReadAsync(m =>
                {
                    var recs = JsonSerializer.Deserialize<System.Collections.Generic.List<VerifiedEditRecord>>(AuditAnnotations.Get(m, VerifiedEditsStore.Annotation));
                    var r = recs.Single(x => x.Op == "deploy_live");
                    var words = r.OverrideReason.Split(' ');
                    r.Summary = r.Summary + "\n" + words[0];                      // steal the first word...
                    r.OverrideReason = string.Join(" ", words.Skip(1));          // ...off the front of the reason
                    AuditAnnotations.Set(m, VerifiedEditsStore.Annotation, JsonSerializer.Serialize(recs));
                    return true;
                });

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.False(chain.ChainIntact);   // the length-prefixed basis catches the smear the bare-\n join would have missed
                Assert.Contains("seq", chain.Note); // flagged as a mid-chain hash mismatch
            }
        }

        // --- G5. Non-mutating records carry Revision 0 (deploy outcomes aren't model mutations); a real Verified-Mode
        //     set_dax record carries the actual mutation revision (> 0). ---

        [Fact]
        public async Task Deploy_override_record_carries_revision_zero()
        {
            var (engine, _, _, _) = await OpenRedGateModelAsync();
            using (engine)
            {
                try { await engine.DeployLiveAsync("localhost:59999", "db", null, null, null, commit: true, "human", overrideReason: "ship it — demo"); }
                catch { /* refused connection after the checkpoint — the override record is already written */ }

                var rec = (await engine.ListVerifiedEditsAsync()).Records.Single(r => r.Op == "deploy_live");
                Assert.Equal("overridden", rec.Verdict);
                Assert.Equal(0, rec.Revision);   // a deploy is NOT a model mutation — its record must not weld to a revision
            }
        }

        [Fact]
        public async Task Verified_set_dax_record_carries_the_real_revision()
        {
            var (engine, _) = await OpenAsync(pro: true);
            using (engine)
            {
                var mref = await FirstMeasureRefAsync(engine);
                await engine.SetVerifiedModeAsync(true, "human");
                await engine.SetDaxAsync(mref, "1 + 1", "agent");

                // The mirror of G5: a record that DID back a mutation carries its real revision, so the two are
                // distinguishable in the trail (0 = outcome-only, > 0 = a specific model change).
                Assert.True((await engine.ListVerifiedEditsAsync()).Records.Single().Revision > 0);
            }
        }

        // --- G6. A multi-line override reason cannot forge the markdown export — every caller-supplied line is
        //     flattened, so an embedded "## N." can't inject a fabricated record heading into the auditor report. ---

        [Fact]
        public async Task Multiline_override_reason_cannot_forge_the_markdown_export()
        {
            var (engine, _, _, _) = await OpenRedGateModelAsync();
            using (engine)
            {
                // A reason engineered to look like a fake "record 99" heading if rendered verbatim on its own line.
                try { await engine.DeployLiveAsync("localhost:59999", "db", null, null, null, commit: true, "human", overrideReason: "ship it\n## 99. fake — proven\n"); }
                catch { /* connection refused after the checkpoint — the override record is written */ }

                var md = await engine.ExportVerifiedEditsAsync("md");
                // The reason is flattened to one line (newlines → the ⏎ marker), so NO line starts with the forged heading.
                Assert.DoesNotContain("\n## 99.", md);
                Assert.False(md.StartsWith("## 99."));
                Assert.Contains("⏎", md);   // proof the multi-line reason was folded, not dropped
            }
        }

        // --- G7. Segmentation — the active chain caps at 500 records; on overflow it archives the full segment whole
        //     and opens a fresh chain whose FIRST record is a chain-archived marker that vouches for the frozen one. ---

        [Fact]
        public async Task Chain_archives_a_full_segment_and_opens_a_fresh_one()
        {
            var (engine, sm) = await OpenAsync(pro: true);
            using (engine)
            {
                // Append 501 records directly through the store on the dispatcher thread — no engine op per record, so
                // this stays fast. The 501st tips the 500-cap: the full segment is frozen and a fresh chain opens.
                await sm.Require().ReadAsync(m =>
                {
                    for (var i = 0; i < 501; i++)
                        VerifiedEditsStore.Append(m, new VerifiedEditRecord { Op = "set_dax", Verdict = "validated", Origin = "agent", SessionId = "t", Revision = 1 });
                    return true;
                });

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.True(chain.ChainIntact);                       // the fresh active chain self-checks clean
                Assert.Equal("chain-archived", chain.Records[0].Op);  // its first record vouches for the frozen segment
                Assert.Equal(2, chain.Records.Length);                // the marker + the 501st real record

                // The frozen segment lives under the numbered archive annotation and parses as a full 500-record array.
                var archived = await sm.Require().ReadAsync(m => AuditAnnotations.Get(m, VerifiedEditsStore.ArchivePrefix + "1"));
                Assert.NotNull(archived);
                var segment = JsonSerializer.Deserialize<System.Collections.Generic.List<VerifiedEditRecord>>(archived);
                Assert.Equal(500, segment.Count);
            }
        }

        // ============================================================================================================
        // H. The git-commit anchor (BaseCommit). Each verified-edit record welds to the git HEAD the model sat on —
        //    the durable-time-travel pointer (the chain hashes bodies, never stores them, so REVERT comes from git).
        //    The migration is the whole risk: BaseCommit enters the hash basis ONLY when non-empty (presence-implied),
        //    so every chain already in the wild keeps verifying byte for byte while a stamped commit is tamper-evident.
        // ============================================================================================================

        // A 40-hex commit sha stand-in — the shape HeadCommitAsync returns (git rev-parse HEAD), so the record hashes
        // exactly as a real anchored one would, without needing a live git repo in the test.
        private const string FakeSha = "0123456789abcdef0123456789abcdef01234567";

        // The ORIGINAL 13-field, length-prefixed hash basis, replicated verbatim as it stood BEFORE BaseCommit existed.
        // A record hashed this way is a "known-good pre-existing" record; if the CURRENT Load still validates it, the
        // migration is proven byte-for-byte back-compatible (no field-order or prefixing drift).
        private static string OldFormulaHash(VerifiedEditRecord r)
        {
            string sha(string s) => VerifiedEditsStore.Sha256(s);
            string canon(params string[] fields) => string.Join("\n", fields.Select(f => (f ?? "").Length + ":" + (f ?? "")));
            return sha(canon(
                r.Seq.ToString(), r.When ?? "", r.SessionId ?? "", r.Revision.ToString(), r.Origin ?? "", r.Op ?? "",
                r.ObjectRef ?? "", r.Verdict ?? "", r.Summary ?? "", r.OverrideReason ?? "",
                sha(r.Evidence ?? ""), r.BodyHash ?? "", r.PrevHash ?? ""));
        }

        // Chain + stamp a list of records with the OLD (pre-BaseCommit) formula, exactly as a legacy model on disk
        // would carry them. Seq/PrevHash/Hash are set; When is fixed so the record is fully deterministic.
        private static void StampOldFormula(System.Collections.Generic.List<VerifiedEditRecord> recs)
        {
            var prev = "";
            for (var i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                r.Seq = i + 1;
                r.When ??= "2026-01-01T00:00:00.0000000Z";
                r.PrevHash = prev;
                r.Hash = OldFormulaHash(r);
                prev = r.Hash;
            }
        }

        // Write a fully-stamped chain (+ its matching head anchor) straight into the model annotations, bypassing the
        // store's Append, then Load it back through the CURRENT code. Lets a test present a legacy or tampered chain.
        private static Task<VerifiedEditsChain> LoadRawChainAsync(SessionManager sm, System.Collections.Generic.List<VerifiedEditRecord> recs)
            => sm.Require().ReadAsync(m =>
            {
                AuditAnnotations.Set(m, VerifiedEditsStore.Annotation, JsonSerializer.Serialize(recs));
                AuditAnnotations.Set(m, VerifiedEditsStore.HeadAnnotation, recs.Count == 0 ? "" : recs.Count + "|" + recs[recs.Count - 1].Hash);
                return VerifiedEditsStore.Load(m);
            });

        // H1 — THE POINT OF THE PR: a legacy chain (records with NO BaseCommit, hashed by the pre-migration formula)
        // still verifies under the current code. Proves the presence-implied basis left the short basis untouched.
        [Fact]
        public async Task Legacy_chain_without_base_commit_still_verifies()
        {
            var (engine, sm) = await OpenAsync(pro: true);
            using (engine)
            {
                var recs = new System.Collections.Generic.List<VerifiedEditRecord>
                {
                    new() { SessionId = "s", Revision = 1, Origin = "agent", Op = "set_dax", ObjectRef = "measure:T/A", Verdict = "validated", Summary = "one", BodyHash = "aaa" },
                    new() { SessionId = "s", Revision = 2, Origin = "agent", Op = "set_dax", ObjectRef = "measure:T/B", Verdict = "validated", Summary = "two", BodyHash = "bbb" },
                };
                StampOldFormula(recs);                       // hashed by the ORIGINAL 13-field basis — a known-good pre-existing chain
                var knownGood = recs[1].Hash;

                var chain = await LoadRawChainAsync(sm, recs);
                Assert.True(chain.ChainIntact);              // the current HashOf reproduced the OLD hash → back-compatible
                Assert.Equal(0, chain.FirstBrokenSeq);
                Assert.Equal(knownGood, chain.Records[1].Hash);   // the pre-existing hash validated unchanged
                Assert.All(chain.Records, r => Assert.True(string.IsNullOrEmpty(r.BaseCommit)));
            }
        }

        // H2 — a record WITH a BaseCommit (stamped through the real store path) verifies intact, and the field is
        // carried on the record (so it flows through list_verified_edits / the JSON export for free).
        [Fact]
        public async Task Record_with_base_commit_verifies_and_carries_it()
        {
            var (engine, sm) = await OpenAsync(pro: true);
            using (engine)
            {
                await sm.Require().ReadAsync(m =>
                {
                    VerifiedEditsStore.Append(m, new VerifiedEditRecord
                    {
                        SessionId = "s", Revision = 1, Origin = "agent", Op = "set_dax", ObjectRef = "measure:T/A",
                        Verdict = "validated", Summary = "anchored", BodyHash = "aaa", BaseCommit = FakeSha,
                    });
                    return true;
                });

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.True(chain.ChainIntact);
                Assert.Equal(FakeSha, chain.Records.Single().BaseCommit);   // the anchor rides the record over the door

                // The markdown export surfaces the (short) sha; a JSON export carries the full one.
                var md = await engine.ExportVerifiedEditsAsync("md");
                Assert.Contains("Base commit", md);
                Assert.Contains(FakeSha.Substring(0, 12), md);
                var json = await engine.ExportVerifiedEditsAsync("json");
                Assert.Contains(FakeSha, json);
            }
        }

        // H3 — tamper: mutating a stamped record's BaseCommit breaks the chain at that exact position.
        [Fact]
        public async Task Mutating_base_commit_is_detected()
        {
            var (engine, sm) = await OpenAsync(pro: true);
            using (engine)
            {
                await sm.Require().ReadAsync(m =>
                {
                    VerifiedEditsStore.Append(m, new VerifiedEditRecord { SessionId = "s", Revision = 1, Origin = "agent", Op = "set_dax", ObjectRef = "measure:T/A", Verdict = "validated", BodyHash = "aaa", BaseCommit = FakeSha });
                    VerifiedEditsStore.Append(m, new VerifiedEditRecord { SessionId = "s", Revision = 2, Origin = "agent", Op = "set_dax", ObjectRef = "measure:T/B", Verdict = "validated", BodyHash = "bbb", BaseCommit = FakeSha });
                    return true;
                });

                // Point record #2 at a DIFFERENT commit without recomputing its Hash.
                await sm.Require().ReadAsync(m =>
                {
                    var recs = JsonSerializer.Deserialize<System.Collections.Generic.List<VerifiedEditRecord>>(AuditAnnotations.Get(m, VerifiedEditsStore.Annotation));
                    recs[1].BaseCommit = "ffffffffffffffffffffffffffffffffffffffff";
                    AuditAnnotations.Set(m, VerifiedEditsStore.Annotation, JsonSerializer.Serialize(recs));
                    return true;
                });

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.False(chain.ChainIntact);
                Assert.Equal(2, chain.FirstBrokenSeq);   // the anchor is inside the tamper-evident basis
            }
        }

        // H4 — strip: clearing a stamped record's BaseCommit drops it to the SHORT basis → hash mismatch → detected.
        [Fact]
        public async Task Stripping_base_commit_off_a_stamped_record_is_detected()
        {
            var (engine, sm) = await OpenAsync(pro: true);
            using (engine)
            {
                await sm.Require().ReadAsync(m =>
                {
                    VerifiedEditsStore.Append(m, new VerifiedEditRecord { SessionId = "s", Revision = 1, Origin = "agent", Op = "set_dax", ObjectRef = "measure:T/A", Verdict = "validated", BodyHash = "aaa", BaseCommit = FakeSha });
                    return true;
                });

                await sm.Require().ReadAsync(m =>
                {
                    var recs = JsonSerializer.Deserialize<System.Collections.Generic.List<VerifiedEditRecord>>(AuditAnnotations.Get(m, VerifiedEditsStore.Annotation));
                    recs[0].BaseCommit = "";   // pretend it was never anchored — but the stored Hash was over the LONG basis
                    AuditAnnotations.Set(m, VerifiedEditsStore.Annotation, JsonSerializer.Serialize(recs));
                    return true;
                });

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.False(chain.ChainIntact);
                Assert.Equal(1, chain.FirstBrokenSeq);
            }
        }

        // H5 — forge: adding a BaseCommit onto an UNstamped record lifts it to the LONG basis → mismatch → detected.
        [Fact]
        public async Task Forging_a_base_commit_onto_an_unstamped_record_is_detected()
        {
            var (engine, sm) = await OpenAsync(pro: true);
            using (engine)
            {
                await sm.Require().ReadAsync(m =>
                {
                    VerifiedEditsStore.Append(m, new VerifiedEditRecord { SessionId = "s", Revision = 1, Origin = "agent", Op = "set_dax", ObjectRef = "measure:T/A", Verdict = "validated", BodyHash = "aaa" });   // NO BaseCommit → hashed over the short basis
                    return true;
                });

                await sm.Require().ReadAsync(m =>
                {
                    var recs = JsonSerializer.Deserialize<System.Collections.Generic.List<VerifiedEditRecord>>(AuditAnnotations.Get(m, VerifiedEditsStore.Annotation));
                    recs[0].BaseCommit = FakeSha;   // forge an anchor the stored Hash never covered
                    AuditAnnotations.Set(m, VerifiedEditsStore.Annotation, JsonSerializer.Serialize(recs));
                    return true;
                });

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.False(chain.ChainIntact);
                Assert.Equal(1, chain.FirstBrokenSeq);
            }
        }

        // H6 — a non-git model: a verified edit records BaseCommit == "" and the chain is intact — the op neither
        // requires nor waits on git. The model is created in-process and never saved, so SourcePath is ephemeral
        // (no on-disk path) and TryResolveHeadAsync short-circuits to "" without ever spawning a git process.
        [Fact]
        public async Task Non_git_model_records_empty_base_commit_and_stays_intact()
        {
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, new Fake(true));
            using (engine)
            {
                await engine.CreateModelAsync("NoGit", 1567);
                var t = await engine.CreateTableAsync("Facts", "human");
                var mref = await engine.CreateMeasureAsync(t, "Sales", "1", "human");

                await engine.SetVerifiedModeAsync(true, "human");
                await engine.SetDaxAsync(mref, "1 + 1", "agent");   // a verified edit ⇒ a "validated" record

                var chain = await engine.ListVerifiedEditsAsync();
                Assert.True(chain.ChainIntact);
                var rec = chain.Records.Single();
                Assert.Equal("validated", rec.Verdict);
                Assert.Equal("", rec.BaseCommit);   // no repo, no anchor — and the edit committed regardless
            }
        }

        // ---- H7–H9: the tracked+clean gate against a REAL git repo (review fix — a lying anchor otherwise). ----
        // `git rev-parse HEAD` names the repo commit even when the model is dirty or untracked; stamping it then
        // would point the "durable revert anchor" at a commit that does NOT contain this state. These exercise the
        // status gate end-to-end on a throwaway repo. (Require git on PATH — as the whole source-control feature does.)

        private static (int code, string stdout) RunGit(string dir, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            var outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, outp.Trim());
        }

        // A throwaway repo with a real .bim model copied in. NOTE: the temp dir must NOT carry the "semanticus-" temp
        // prefix, or IsEphemeralAnchor would classify it ephemeral and short-circuit the whole resolve to "".
        private static string InitRepoWithModel(out string modelPath)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitanchor-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            modelPath = System.IO.Path.Combine(dir, "model.bim");
            System.IO.File.Copy(TestModels.FindBim(), modelPath);
            RunGit(dir, "init", "-q");
            RunGit(dir, "config", "user.email", "audit@test.local");
            RunGit(dir, "config", "user.name", "Audit Test");
            RunGit(dir, "config", "commit.gpgsign", "false");
            return dir;
        }

        private static void DeleteTree(string dir)
        {
            try
            {
                foreach (var f in System.IO.Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories))
                    try { System.IO.File.SetAttributes(f, System.IO.FileAttributes.Normal); } catch { /* best effort */ }
                System.IO.Directory.Delete(dir, true);
            }
            catch { /* best-effort temp cleanup — git objects are read-only on Windows */ }
        }

        // H7 — tracked + clean ⇒ the record anchors to the EXACT HEAD the model sat on.
        [Fact]
        public async Task Base_commit_stamped_for_a_tracked_and_clean_model()
        {
            var dir = InitRepoWithModel(out var modelPath);
            try
            {
                RunGit(dir, "add", "model.bim");
                RunGit(dir, "commit", "-q", "-m", "init");
                var (_, head) = RunGit(dir, "rev-parse", "HEAD");

                var sm = new SessionManager();
                var engine = new LocalEngine(sm, new Fake(true));
                using (engine)
                {
                    await engine.OpenAsync(modelPath);
                    var mref = await FirstMeasureRefAsync(engine);
                    await engine.SetVerifiedModeAsync(true, "human");
                    await engine.SetDaxAsync(mref, "1 + 1", "agent");   // in-memory edit; model.bim on disk stays clean vs HEAD

                    var rec = (await engine.ListVerifiedEditsAsync()).Records.Single();
                    Assert.Equal(40, rec.BaseCommit.Length);
                    Assert.Equal(head, rec.BaseCommit);   // the commit the clean model provably sat on
                }
            }
            finally { DeleteTree(dir); }
        }

        // H8 — a dirty working tree (the tracked model file differs from HEAD) ⇒ no anchor. HEAD does not contain
        // this on-disk state, so stamping it would lie.
        [Fact]
        public async Task Base_commit_empty_for_a_dirty_working_tree()
        {
            var dir = InitRepoWithModel(out var modelPath);
            try
            {
                RunGit(dir, "add", "model.bim");
                RunGit(dir, "commit", "-q", "-m", "init");

                var sm = new SessionManager();
                var engine = new LocalEngine(sm, new Fake(true));
                using (engine)
                {
                    await engine.OpenAsync(modelPath);
                    System.IO.File.AppendAllText(modelPath, "\n");   // dirty the tracked file on disk (engine already holds it in memory)
                    var mref = await FirstMeasureRefAsync(engine);
                    await engine.SetVerifiedModeAsync(true, "human");
                    await engine.SetDaxAsync(mref, "1 + 1", "agent");

                    Assert.Equal("", (await engine.ListVerifiedEditsAsync()).Records.Single().BaseCommit);
                }
            }
            finally { DeleteTree(dir); }
        }

        // H9 — an untracked model (repo inited, never added/committed) ⇒ no anchor (also an unborn HEAD).
        [Fact]
        public async Task Base_commit_empty_for_an_untracked_model()
        {
            var dir = InitRepoWithModel(out var modelPath);   // model.bim is NOT added/committed
            try
            {
                var sm = new SessionManager();
                var engine = new LocalEngine(sm, new Fake(true));
                using (engine)
                {
                    await engine.OpenAsync(modelPath);
                    var mref = await FirstMeasureRefAsync(engine);
                    await engine.SetVerifiedModeAsync(true, "human");
                    await engine.SetDaxAsync(mref, "1 + 1", "agent");

                    Assert.Equal("", (await engine.ListVerifiedEditsAsync()).Records.Single().BaseCommit);
                }
            }
            finally { DeleteTree(dir); }
        }
    }
}

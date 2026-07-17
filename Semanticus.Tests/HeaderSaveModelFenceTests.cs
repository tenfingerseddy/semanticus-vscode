using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// CRITICAL 1 (zero-dialog authoring header-save fence): the OPTIONAL <c>expectedSession</c> parameter on
    /// <see cref="IEngine.SetDaxAsync"/> / <see cref="IEngine.RenameObjectAsync"/>. The header-save path reads SessionInfo
    /// once, verifies the editor still belongs to that model, then echoes SessionInfo.SessionId back on the write; if — by
    /// the time the write reaches MutateAsync's single-writer dispatch — an MCP-door model swap has replaced the live
    /// session, its id no longer matches and the mutation is REFUSED before it touches the model or the undo timeline.
    /// Fencing on the SESSION id (not the source path) is what fixed the two holes a source fence left: an UNSAVED model
    /// (source null → the old fence was skipped) is fenced too, and REOPENING the same file (same source, brand-new
    /// session) is caught. Null = the prior behavior, so both doors' other callers and every MCP tool are unaffected — the
    /// extension simply never passes null on the header-save path (it fails closed if the session can't be confirmed).
    /// </summary>
    public sealed class HeaderSaveModelFenceTests
    {
        // A session id that is deliberately not the live one — the extension samples the real SessionInfo.SessionId; only a
        // stale/wrong id (an intervening swap) looks like this to the fence.
        private const string WrongSession = "s-not-the-live-session";

        [Fact]
        public async Task SetDax_expectedSession_mismatch_refuses_before_mutating_and_leaves_model_and_undo_untouched()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.CreateModelAsync("Fence", 1604);
                await engine.CreateTableAsync("T", "agent");
                var mref = await engine.CreateMeasureAsync("table:T", "M", "1", "human");
                var revBefore = (await engine.SessionInfoAsync()).Revision;

                // An UNSAVED model still has a session id (Source is null) — so a non-null expectedSession that does not
                // match the live session's id must be REFUSED, honestly, before any mutation. (The old source-path fence
                // was skipped entirely for unsaved models; the session-id fence closes that hole.)
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetDaxAsync(mref, "2", "human", WrongSession));
                Assert.Contains("model changed before this edit landed", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Nothing was written", ex.Message, StringComparison.OrdinalIgnoreCase);

                // Nothing written: the expression is unchanged and the revision (undo timeline) did NOT advance.
                Assert.Equal("1", (await engine.ListMeasuresAsync()).First(m => m.Name == "M").Expression);
                Assert.Equal(revBefore, (await engine.SessionInfoAsync()).Revision);

                // Undo stack untouched: the only undoable step is the measure CREATE, so one undo removes the measure. If
                // the refused edit had pushed an undo entry, this undo would revert "2"→"1" instead and M would remain.
                await engine.UndoAsync("human");
                Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "M");
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task RenameObject_expectedSession_mismatch_refuses_before_mutating()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.CreateModelAsync("Fence", 1604);
                await engine.CreateTableAsync("T", "agent");
                var mref = await engine.CreateMeasureAsync("table:T", "M", "1", "human");
                var revBefore = (await engine.SessionInfoAsync()).Revision;

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.RenameObjectAsync(mref, "Renamed", "human", WrongSession));
                Assert.Contains("model changed before this edit landed", ex.Message, StringComparison.OrdinalIgnoreCase);

                // The object keeps its name and the timeline did not advance.
                Assert.Contains(await engine.ListMeasuresAsync(), m => m.Name == "M");
                Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "Renamed");
                Assert.Equal(revBefore, (await engine.SessionInfoAsync()).Revision);
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task SetDax_and_rename_with_matching_expectedSession_proceed()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.CreateModelAsync("Fence", 1604);
                await engine.CreateTableAsync("T", "agent");
                var mref = await engine.CreateMeasureAsync("table:T", "M", "1", "human");

                // The live session id is always present (even unsaved); read it back so expectedSession is EXACTLY what the
                // engine will compare against, exactly as the extension samples it from one sessionInfo read.
                var sid = (await engine.SessionInfoAsync()).SessionId;
                Assert.False(string.IsNullOrEmpty(sid));

                // A matching expectedSession proceeds exactly like the unfenced call.
                await engine.SetDaxAsync(mref, "2", "human", sid);
                Assert.Equal("2", (await engine.ListMeasuresAsync()).First(m => m.Name == "M").Expression);

                var renamed = await engine.RenameObjectAsync(mref, "M2", "human", sid);
                Assert.True(renamed.Changed);
                Assert.Contains(await engine.ListMeasuresAsync(), m => m.Name == "M2");
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task SetDax_same_source_but_new_session_refuses_even_though_source_matches()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            var dir = Path.Combine(Path.GetTempPath(), "semanticus-fence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                await engine.CreateModelAsync("Fence", 1604);
                await engine.CreateTableAsync("T", "agent");
                var mref = await engine.CreateMeasureAsync("table:T", "M", "1", "human");
                await engine.SaveAsync(dir, "TMDL");

                var first = await engine.SessionInfoAsync();
                Assert.False(string.IsNullOrEmpty(first.Source));

                // Reopen the SAME file in a NEW session on the same engine (an MCP-door reopen). Save and Open resolve the
                // path identically, so the Source string is UNCHANGED — the exact case a source-path fence would have let
                // through — while the session id is brand new.
                await engine.OpenAsync(dir);
                var reopened = await engine.SessionInfoAsync();
                Assert.Equal(first.Source, reopened.Source);              // SAME source path...
                Assert.NotEqual(first.SessionId, reopened.SessionId);      // ...but a NEW session

                // The STALE (pre-reopen) session id must be refused even though the source still matches.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetDaxAsync(mref, "2", "human", first.SessionId));
                Assert.Contains("model changed before this edit landed", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("1", (await engine.ListMeasuresAsync()).First(m => m.Name == "M").Expression);

                // The CURRENT (reopened) session id proceeds.
                await engine.SetDaxAsync(mref, "3", "human", reopened.SessionId);
                Assert.Equal("3", (await engine.ListMeasuresAsync()).First(m => m.Name == "M").Expression);
            }
            finally
            {
                engine.Dispose();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort temp cleanup */ }
            }
        }

        [Fact]
        public async Task SetDax_with_null_expectedSession_is_unchanged_behavior()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.CreateModelAsync("Fence", 1604);
                await engine.CreateTableAsync("T", "agent");
                var mref = await engine.CreateMeasureAsync("table:T", "M", "1", "human");

                // Null fence stays the PRIOR behavior for every other caller (all MCP tools + the plain-editor path): the
                // write lands no matter the session. The header-save extension path never passes null anymore — it fails
                // closed if it can't confirm the live session id — so this only documents the non-fenced callers.
                await engine.SetDaxAsync(mref, "42", "human", null);
                Assert.Equal("42", (await engine.ListMeasuresAsync()).First(m => m.Name == "M").Expression);
            }
            finally { engine.Dispose(); }
        }
    }

    /// <summary>
    /// CRITICAL (zero-dialog authoring, round 7): the OPTIONAL <c>expectedRevision</c> fence on
    /// <see cref="IEngine.SetDaxAsync"/> / <see cref="IEngine.RenameObjectAsync"/> — a strictly finer check than
    /// <c>expectedSession</c>. The session id fence catches a whole-model SWAP but is BLIND to a same-session, same-name
    /// write landing on the WRONG OBJECT: the header-save recovery path probes old-absent/new-alive over separate read
    /// RPCs, then writes; if between the probes and the write the MCP door deletes the resumed object and RE-CREATES an
    /// unrelated object at the same name, the session id is unchanged so the swap fence passes and the preserved body would
    /// land on the impostor. Every mutation bumps <c>Session.Revision</c>, so a caller that captured the revision it
    /// verified against (the rename's returned revision, or the revision re-read as stable across the recovery probes) can
    /// pass it as <c>expectedRevision</c>; read inside the same single-writer dispatch turn as the mutation, any
    /// interleaving commit makes the revision differ and the write is REFUSED before it touches the model. Null skips the
    /// fence (the product-wide set-by-ref default; only the feature's verified-resumption writes pass a non-null value).
    /// </summary>
    public sealed class HeaderSaveRevisionFenceTests
    {
        [Fact]
        public async Task SetDax_expectedRevision_mismatch_refuses_before_mutating_and_leaves_model_and_undo_untouched()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.CreateModelAsync("Fence", 1604);
                await engine.CreateTableAsync("T", "agent");
                var mref = await engine.CreateMeasureAsync("table:T", "M", "1", "human");
                var revBefore = (await engine.SessionInfoAsync()).Revision;
                var sid = (await engine.SessionInfoAsync()).SessionId;

                // A revision the caller "verified against" that is NOT the live revision (a mutation slipped in since) must be
                // REFUSED before any mutation — even though the session id matches (the swap fence alone cannot see this).
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetDaxAsync(mref, "2", "human", sid, revBefore - 1));
                Assert.Contains("model changed before this edit landed", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Nothing was written", ex.Message, StringComparison.OrdinalIgnoreCase);

                // Nothing written: the expression is unchanged and the revision (undo timeline) did NOT advance.
                Assert.Equal("1", (await engine.ListMeasuresAsync()).First(m => m.Name == "M").Expression);
                Assert.Equal(revBefore, (await engine.SessionInfoAsync()).Revision);

                // Undo stack untouched: the only undoable step is the measure CREATE, so one undo removes the measure.
                await engine.UndoAsync("human");
                Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "M");
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task SetDax_and_rename_with_matching_expectedRevision_proceed()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.CreateModelAsync("Fence", 1604);
                await engine.CreateTableAsync("T", "agent");
                var mref = await engine.CreateMeasureAsync("table:T", "M", "1", "human");

                var info = await engine.SessionInfoAsync();
                // The CURRENT revision (what the caller last observed) proceeds exactly like the unfenced call.
                await engine.SetDaxAsync(mref, "2", "human", info.SessionId, info.Revision);
                Assert.Equal("2", (await engine.ListMeasuresAsync()).First(m => m.Name == "M").Expression);

                // Re-read: the prior write advanced the revision, so the rename must pass the NEW value.
                var info2 = await engine.SessionInfoAsync();
                var renamed = await engine.RenameObjectAsync(mref, "M2", "human", info2.SessionId, info2.Revision);
                Assert.True(renamed.Changed);
                Assert.Contains(await engine.ListMeasuresAsync(), m => m.Name == "M2");
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task RenameObject_expectedRevision_mismatch_refuses_before_mutating()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.CreateModelAsync("Fence", 1604);
                await engine.CreateTableAsync("T", "agent");
                var mref = await engine.CreateMeasureAsync("table:T", "M", "1", "human");
                var info = await engine.SessionInfoAsync();

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.RenameObjectAsync(mref, "Renamed", "human", info.SessionId, info.Revision - 1));
                Assert.Contains("model changed before this edit landed", ex.Message, StringComparison.OrdinalIgnoreCase);

                Assert.Contains(await engine.ListMeasuresAsync(), m => m.Name == "M");
                Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "Renamed");
                Assert.Equal(info.Revision, (await engine.SessionInfoAsync()).Revision);
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task SetDax_with_null_expectedRevision_is_unchanged_behavior()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.CreateModelAsync("Fence", 1604);
                await engine.CreateTableAsync("T", "agent");
                var mref = await engine.CreateMeasureAsync("table:T", "M", "1", "human");
                var sid = (await engine.SessionInfoAsync()).SessionId;

                // Null revision fence stays the PRIOR behavior even when a session id IS supplied: the write lands. Only a
                // NON-null expectedRevision engages the finer check (the feature's verified-resumption writes).
                await engine.SetDaxAsync(mref, "42", "human", sid, null);
                Assert.Equal("42", (await engine.ListMeasuresAsync()).First(m => m.Name == "M").Expression);
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task Recovery_write_refuses_when_object_is_deleted_and_recreated_at_the_same_name_after_the_probes()
        {
            // The reviewer's exact interleaving. The header-save recovery path resolved the resume target (B alive, old
            // absent) and captured the revision as STABLE across its probes, call it R. It is about to write the preserved
            // body to B with expectedRevision=R. Between the probes and that write, the MCP door DELETES B and RE-CREATES an
            // UNRELATED measure at the same name "B" (refs are name-based, so the ref still resolves — to the impostor). The
            // revision has moved to R+2, so the fenced body write must REFUSE and never overwrite the unrelated object.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.CreateModelAsync("Fence", 1604);
                await engine.CreateTableAsync("T", "agent");
                var bref = await engine.CreateMeasureAsync("table:T", "B", "ORIGINAL", "human");
                var info = await engine.SessionInfoAsync();
                var stableRevision = info.Revision;   // what the recovery path re-read as stable across the two liveness probes

                // The interleaving mutations (the MCP door), each bumping the revision.
                await engine.DeleteObjectAsync(bref, "agent");
                var brefRecreated = await engine.CreateMeasureAsync("table:T", "B", "UNRELATED", "agent");
                Assert.Equal(bref, brefRecreated);   // same name → same ref: the write WOULD hit the impostor without the fence
                Assert.NotEqual(stableRevision, (await engine.SessionInfoAsync()).Revision);

                // The recovery write, fenced on the stale (pre-interleaving) stable revision, must refuse.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetDaxAsync(bref, "PRESERVED", "human", info.SessionId, stableRevision));
                Assert.Contains("model changed before this edit landed", ex.Message, StringComparison.OrdinalIgnoreCase);

                // The unrelated recreated B is intact — the preserved body never landed on it.
                Assert.Equal("UNRELATED", (await engine.ListMeasuresAsync()).First(m => m.Name == "B").Expression);
            }
            finally { engine.Dispose(); }
        }
    }
}

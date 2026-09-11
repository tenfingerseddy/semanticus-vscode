using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// T172 (first outcome): the MCP door must be able to say WHICH session a mutation is for, instead of
    /// silently inheriting <see cref="SessionManager.Current"/>. The engine fence already exists
    /// (<c>expectedSession</c> on <see cref="IEngine.SetDaxAsync"/> / <see cref="IEngine.RenameObjectAsync"/>,
    /// enforced by <c>GuardExpectedModel</c> inside the single-writer dispatch turn); the two guarded MCP
    /// mutations simply dropped the id, so an agent holding a stale <c>sessionId</c> could not be refused.
    ///
    /// These drive the REAL MCP route, the same static <c>McpTools</c> methods the server dispatches to,
    /// with the id correct, omitted, blank and mismatched, plus the swap that makes the hazard concrete: a model
    /// change between the read and the write. Omitting the id keeps the old behavior exactly, which is why the
    /// swap case can show what "silent" meant.
    /// </summary>
    public sealed class McpSessionTargetingTests
    {
        // Never a minted id: only a stale or foreign handle looks like this to the fence.
        private const string ForeignSession = "s-not-this-process";

        private const string RefusalText = "model changed before this edit landed";

        [Fact]
        public async Task update_measure_with_the_live_sessionId_proceeds_exactly_like_omitting_it()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var opened = await McpTools.CreateModel(engine, "McpSessionTargeting");
            var table = await McpTools.CreateTable(engine, "T");
            var measure = await McpTools.CreateMeasure(engine, table, "M", "1");

            // The id the client was actually handed: create_model/open_* return it, and model_overview reads it back.
            Assert.False(string.IsNullOrEmpty(opened.SessionId));
            Assert.Equal(opened.SessionId, (await McpTools.ModelOverview(engine)).SessionId);

            var targeted = await McpTools.UpdateMeasure(engine, measure, "2", opened.SessionId);
            Assert.True(targeted.Changed);
            Assert.Equal("2", await McpTools.GetDax(engine, measure));

            // Omitted stays the pre-T172 call shape (an older client never passes the argument at all).
            var untargeted = await McpTools.UpdateMeasure(engine, measure, "3");
            Assert.True(untargeted.Changed);
            Assert.Equal("3", await McpTools.GetDax(engine, measure));
        }

        [Fact]
        public async Task update_measure_with_a_foreign_sessionId_refuses_and_writes_nothing()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await McpTools.CreateModel(engine, "McpSessionTargeting");
            var table = await McpTools.CreateTable(engine, "T");
            var measure = await McpTools.CreateMeasure(engine, table, "M", "1");

            await AssertNoCommitAsync(sessions, async () =>
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.UpdateMeasure(engine, measure, "2", ForeignSession));
                Assert.Contains(RefusalText, ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Nothing was written", ex.Message, StringComparison.OrdinalIgnoreCase);
            });
            Assert.Equal("1", await McpTools.GetDax(engine, measure));

            // The undo timeline is untouched: the only step is the measure CREATE, so one undo removes it. Had the
            // refused write pushed an entry, this undo would have reverted an expression and M would still exist.
            await McpTools.UndoChange(engine);
            Assert.DoesNotContain(await McpTools.ListMeasures(engine), m => m.Name == "M");
        }

        [Fact]
        public async Task rename_object_with_the_live_sessionId_proceeds_and_omitting_it_is_unchanged()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var opened = await McpTools.CreateModel(engine, "McpSessionTargeting");
            var table = await McpTools.CreateTable(engine, "T");
            var measure = await McpTools.CreateMeasure(engine, table, "M", "1");

            var targeted = await McpTools.RenameObject(engine, measure, "M2", opened.SessionId);
            Assert.True(targeted.Changed);
            Assert.Contains(await McpTools.ListMeasures(engine), m => m.Name == "M2");

            var untargeted = await McpTools.RenameObject(engine, targeted.NewRef, "M3");
            Assert.True(untargeted.Changed);
            Assert.Contains(await McpTools.ListMeasures(engine), m => m.Name == "M3");
        }

        [Fact]
        public async Task rename_object_with_a_foreign_sessionId_refuses_and_writes_nothing()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await McpTools.CreateModel(engine, "McpSessionTargeting");
            var table = await McpTools.CreateTable(engine, "T");
            var measure = await McpTools.CreateMeasure(engine, table, "M", "1");

            await AssertNoCommitAsync(sessions, async () =>
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.RenameObject(engine, measure, "Renamed", ForeignSession));
                Assert.Contains(RefusalText, ex.Message, StringComparison.OrdinalIgnoreCase);
            });
            Assert.Contains(await McpTools.ListMeasures(engine), m => m.Name == "M");
            Assert.DoesNotContain(await McpTools.ListMeasures(engine), m => m.Name == "Renamed");
        }

        /// <summary>
        /// THE HAZARD, made concrete. One process, one implicit "current" session: something swaps the model
        /// between the agent's read and its write. Omitted id = the pre-T172 behavior, and the write lands on the
        /// WRONG model without a word. The same write, carrying the id the agent actually read, is refused.
        /// </summary>
        [Fact]
        public async Task a_model_swap_between_read_and_write_is_silent_without_the_id_and_refused_with_it()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);

            // Model A: what the agent read, and what it believes it is editing.
            var modelA = await McpTools.CreateModel(engine, "ModelA");
            var tableA = await McpTools.CreateTable(engine, "T");
            await McpTools.CreateMeasure(engine, tableA, "M", "1");

            // Model B replaces the live session (another conversation on this process, or the human in the UI).
            var modelB = await McpTools.CreateModel(engine, "ModelB", discardUnsaved: true);
            var tableB = await McpTools.CreateTable(engine, "T");
            var measureB = await McpTools.CreateMeasure(engine, tableB, "M", "100");
            Assert.NotEqual(modelA.SessionId, modelB.SessionId);
            Assert.Equal(modelB.SessionId, (await McpTools.ModelOverview(engine)).SessionId);

            // Carrying model A's id, the write is refused before it touches model B, and nothing is committed.
            await AssertNoCommitAsync(sessions, async () =>
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.UpdateMeasure(engine, measureB, "2", modelA.SessionId));
                Assert.Contains(RefusalText, ex.Message, StringComparison.OrdinalIgnoreCase);
            });
            await AssertNoCommitAsync(sessions, async () =>
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.RenameObject(engine, measureB, "RenamedOnTheWrongModel", modelA.SessionId));
                Assert.Contains(RefusalText, ex.Message, StringComparison.OrdinalIgnoreCase);
            });
            Assert.Equal("100", await McpTools.GetDax(engine, measureB));
            Assert.DoesNotContain(await McpTools.ListMeasures(engine), m => m.Name == "RenamedOnTheWrongModel");

            // Omitting the id is still the old behavior, and the old behavior is the hazard: model B is edited
            // silently, with no error and nothing naming the model the agent thought it was on.
            var silent = await McpTools.UpdateMeasure(engine, measureB, "2");
            Assert.True(silent.Changed);
            Assert.Equal("2", await McpTools.GetDax(engine, measureB));
        }

        /// <summary>Only an OMITTED argument is unfenced. A blank string is not a minted session id, so it can never
        /// name the live model: it goes to the same guard as any other supplied value and is refused. The alternative
        /// (quietly reading blank as "not supplied") would be a second, tool-only meaning for a value the client did
        /// choose to send, and the whole point of this task is that targeting stops being implicit.</summary>
        [Fact]
        public async Task a_blank_sessionId_is_a_supplied_value_and_is_refused_on_both_tools()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await McpTools.CreateModel(engine, "McpSessionTargeting");
            var table = await McpTools.CreateTable(engine, "T");
            var measure = await McpTools.CreateMeasure(engine, table, "M", "1");

            await AssertNoCommitAsync(sessions, async () =>
            {
                var set = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.UpdateMeasure(engine, measure, "2", ""));
                Assert.Contains(RefusalText, set.Message, StringComparison.OrdinalIgnoreCase);

                var rename = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.RenameObject(engine, measure, "M2", "   "));
                Assert.Contains(RefusalText, rename.Message, StringComparison.OrdinalIgnoreCase);
            });
            Assert.Equal("1", await McpTools.GetDax(engine, measure));
            Assert.Contains(await McpTools.ListMeasures(engine), m => m.Name == "M");
        }

        /// <summary>
        /// THE STALE HANDLE, made concrete. A client keeps the id it was handed and the engine is restarted
        /// under it (or a second manager takes over): the old owner is gone, a new one opens a different model,
        /// and the objects happen to carry the SAME names. Nothing but the id can tell the two models apart, so
        /// a session id that restarts with its manager would let the old handle pass the fence and edit a model
        /// the client has never seen. Minted ids must therefore be unique across manager instances and process
        /// restarts, not per-manager positions.
        /// </summary>
        [Fact]
        public async Task a_handle_from_a_disposed_manager_cannot_edit_the_new_owners_same_named_model()
        {
            string staleId;
            // The first owner, then fully disposed: what a client's handle outlives when the engine restarts.
            using (var oldSessions = new SessionManager())
            using (var oldEngine = new LocalEngine(oldSessions))
            {
                var first = await McpTools.CreateModel(oldEngine, "McpSessionTargeting");
                var oldTable = await McpTools.CreateTable(oldEngine, "T");
                await McpTools.CreateMeasure(oldEngine, oldTable, "M", "1");
                staleId = first.SessionId;
                Assert.False(string.IsNullOrEmpty(staleId));
            }

            // The new owner. Same model name, same table, same measure name: only the id distinguishes them.
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var fresh = await McpTools.CreateModel(engine, "McpSessionTargeting");
            var table = await McpTools.CreateTable(engine, "T");
            var measure = await McpTools.CreateMeasure(engine, table, "M", "1");

            await AssertNoCommitAsync(sessions, async () =>
            {
                var set = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.UpdateMeasure(engine, measure, "2", staleId));
                Assert.Contains(RefusalText, set.Message, StringComparison.OrdinalIgnoreCase);

                var rename = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.RenameObject(engine, measure, "RenamedByAStaleHandle", staleId));
                Assert.Contains(RefusalText, rename.Message, StringComparison.OrdinalIgnoreCase);
            });
            Assert.Equal("1", await McpTools.GetDax(engine, measure));
            Assert.Contains(await McpTools.ListMeasures(engine), m => m.Name == "M");
            Assert.DoesNotContain(await McpTools.ListMeasures(engine), m => m.Name == "RenamedByAStaleHandle");

            // The cause, asserted after the behavior it explains: the first session of a NEW manager is not
            // the first session of the old one, so a per-manager position could never have been safe here.
            Assert.NotEqual(staleId, fresh.SessionId);

            // The live id of the CURRENT owner still works, so the fence gates the stale handle, not the model.
            var targeted = await McpTools.UpdateMeasure(engine, measure, "2", fresh.SessionId);
            Assert.True(targeted.Changed);
            Assert.Equal("2", await McpTools.GetDax(engine, measure));
        }

        /// <summary>The same proof on the OPEN route. <c>BuildOpenAsync</c> mints its own id, so it needs its own
        /// case: reopening the SAME FILE under a new owner is a different session, and the handle the old owner
        /// handed out cannot edit it. Same file means the same source path and the same object names, so the id is
        /// the only thing that can tell the two sessions apart.</summary>
        [Fact]
        public async Task a_handle_from_a_disposed_manager_cannot_edit_the_same_file_reopened_by_a_new_owner()
        {
            var bim = TestModels.FindBim();

            string staleId;
            using (var oldSessions = new SessionManager())
            using (var oldEngine = new LocalEngine(oldSessions))
                staleId = (await McpTools.OpenModel(oldEngine, bim)).SessionId;

            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var reopened = await McpTools.OpenModel(engine, bim);
            var measure = (await McpTools.ListMeasures(engine)).First();

            await AssertNoCommitAsync(sessions, async () =>
            {
                var set = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.UpdateMeasure(engine, measure.Ref, "0", staleId));
                Assert.Contains(RefusalText, set.Message, StringComparison.OrdinalIgnoreCase);

                var rename = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.RenameObject(engine, measure.Ref, "RenamedByAStaleHandle", staleId));
                Assert.Contains(RefusalText, rename.Message, StringComparison.OrdinalIgnoreCase);
            });
            Assert.Equal(measure.Expression, await McpTools.GetDax(engine, measure.Ref));
            Assert.DoesNotContain(await McpTools.ListMeasures(engine), m => m.Name == "RenamedByAStaleHandle");
            Assert.NotEqual(staleId, reopened.SessionId);

            // The reopened session's OWN id is accepted, so the open route is fenced, not broken.
            var targeted = await McpTools.UpdateMeasure(engine, measure.Ref, measure.Expression + " + 0", reopened.SessionId);
            Assert.True(targeted.Changed);
        }

        private static async Task AssertNoCommitAsync(SessionManager sessions, Func<Task> action)
        {
            var revision = sessions.Current.Revision;
            var seen = new List<ChangeNotification>();
            void Handler(ChangeNotification notification) => seen.Add(notification);
            sessions.Bus.Changed += Handler;
            try { await action(); }
            finally { sessions.Bus.Changed -= Handler; }
            Assert.Equal(revision, sessions.Current.Revision);
            Assert.Empty(seen);
        }
    }
}

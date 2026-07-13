using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Perspectives — the first Studio v2 "Advanced Modelling" engine primitive (create + membership + read).
    /// Proves: a created perspective round-trips through get_perspectives; including/excluding one object is a
    /// single undoable step on the shared timeline; including a TABLE cascades to its children; and the generic
    /// delete_object / rename_object ops work on a perspective (so we add no bespoke delete/rename). Guards cover
    /// the empty/duplicate name, a non-perspective target, a member that can't live in a perspective, and an
    /// unresolved member ref.
    /// </summary>
    public sealed class PerspectiveTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static async Task<LocalEngine> OpenAwAsync(bool pro = false)
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(pro));
            await engine.OpenAsync(TestModels.FindBim());
            return engine;
        }

        private static async Task<PerspectiveInfo> GetAsync(LocalEngine engine, string pRef) =>
            (await engine.GetPerspectivesAsync()).First(p => p.Ref == pRef);

        [Fact]
        public async Task Create_add_member_read_and_undo_round_trip()
        {
            using var engine = await OpenAwAsync();

            var msRef = (await engine.ListMeasuresAsync()).First().Ref;
            var pRef = await engine.CreatePerspectiveAsync("Persp_Test_A", "human");
            Assert.Equal("perspective:Persp_Test_A", pRef);
            Assert.Empty((await GetAsync(engine, pRef)).Members);          // a fresh perspective shows no members

            var r = await engine.SetPerspectiveMemberAsync(pRef, msRef, true, "human");
            Assert.True(r.Changed);
            Assert.Contains(msRef, (await GetAsync(engine, pRef)).Members);

            await engine.UndoAsync("human");                              // membership is one undoable step (shared timeline)
            Assert.DoesNotContain(msRef, (await GetAsync(engine, pRef)).Members);

            // re-add then exclude → not a member; the perspective itself survives
            await engine.SetPerspectiveMemberAsync(pRef, msRef, true, "human");
            await engine.SetPerspectiveMemberAsync(pRef, msRef, false, "human");
            Assert.DoesNotContain(msRef, (await GetAsync(engine, pRef)).Members);
        }

        [Fact]
        public async Task Including_a_table_cascades_to_its_children()
        {
            using var engine = await OpenAwAsync();

            var tbl = (await engine.ListMeasuresAsync()).First().Table;   // a table that actually has children
            var tableRef = "table:" + tbl;
            var pRef = await engine.CreatePerspectiveAsync("Persp_Test_B", "human");

            await engine.SetPerspectiveMemberAsync(pRef, tableRef, true, "human");

            var members = (await GetAsync(engine, pRef)).Members;
            Assert.Contains(tableRef, members);                            // the table is shown…
            Assert.Contains(members, m => m.StartsWith("measure:" + tbl + "/") || m.StartsWith("column:" + tbl + "/"));   // …and its children cascaded in
        }

        [Fact]
        public async Task Delete_and_rename_reuse_the_generic_ops()
        {
            using var engine = await OpenAwAsync();

            var msRef = (await engine.ListMeasuresAsync()).First().Ref;
            var pRef = await engine.CreatePerspectiveAsync("Persp_Rename_Src", "human");
            await engine.SetPerspectiveMemberAsync(pRef, msRef, true, "human");

            // rename via the generic op — the member survives under the new ref
            await engine.RenameObjectAsync(pRef, "Persp_Renamed", "human");
            var renamedRef = "perspective:Persp_Renamed";
            var renamed = await engine.GetPerspectivesAsync();
            Assert.DoesNotContain(renamed, p => p.Ref == pRef);
            Assert.Contains(msRef, renamed.First(p => p.Ref == renamedRef).Members);

            // delete via the generic op — the perspective is gone
            await engine.DeleteObjectAsync(renamedRef, "human");
            Assert.DoesNotContain(await engine.GetPerspectivesAsync(), p => p.Ref == renamedRef);
        }

        [Fact]
        public async Task Description_round_trips_through_the_generic_set_description()
        {
            using var engine = await OpenAwAsync();

            var pRef = await engine.CreatePerspectiveAsync("Persp_Described", "human");
            Assert.True(string.IsNullOrEmpty((await GetAsync(engine, pRef)).Description));

            var r = await engine.SetDescriptionAsync(pRef, "Finance month-end view.", "human");
            Assert.True(r.Changed);
            Assert.Equal("Finance month-end view.", (await GetAsync(engine, pRef)).Description);
        }

        [Fact]
        public async Task Validation_guards()
        {
            using var engine = await OpenAwAsync();

            await Assert.ThrowsAsync<ArgumentException>(() => engine.CreatePerspectiveAsync("   ", "human"));

            await engine.CreatePerspectiveAsync("Persp_Dup", "human");
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreatePerspectiveAsync("Persp_Dup", "human"));   // duplicate name

            var pRef = "perspective:Persp_Dup";
            var msRef = (await engine.ListMeasuresAsync()).First().Ref;

            // the FIRST arg must be a perspective
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SetPerspectiveMemberAsync(msRef, msRef, true, "human"));

            // a member that can't live in a perspective (a role is not an ITabularPerspectiveObject)
            await engine.CreateRoleAsync("Persp_Role", "Read", "human");
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SetPerspectiveMemberAsync(pRef, "role:Persp_Role", true, "human"));

            // an unresolved member ref
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SetPerspectiveMemberAsync(pRef, "measure:NoSuchTable/NoSuch", true, "human"));
        }
    }
}

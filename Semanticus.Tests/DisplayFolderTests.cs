using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Display-folder ops (feat/tree-measure-folders). A display folder has no existence of its own in TOM —
    /// it is just the DisplayFolder string on each measure/column/hierarchy — so the ops are ATOMIC gestures
    /// over that reality:
    ///   • set_display_folder — file/clear MANY objects in ONE MutateAsync (one undo step, all-or-nothing),
    ///   • rename_display_folder — prefix rewrite across a table (nested paths, never a name-prefix collision),
    ///   • create_measure displayFolder — born filed, create + folder = one undo step,
    ///   • the tree fan carries DisplayFolder (flat — the VS Code tree groups client-side).
    /// </summary>
    public sealed class DisplayFolderTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static async Task<LocalEngine> FreshAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(false));
            await engine.CreateModelAsync("Folders", 1604);
            await engine.CreateTableAsync("Sales", "human");
            return engine;
        }

        private static async Task<string> FolderOfAsync(LocalEngine e, string objRef)
            => (await e.GetObjectAsync(objRef)).Properties["displayFolder"] as string ?? "";

        [Fact]
        public async Task Set_display_folder_files_many_objects_as_one_undo_step()
        {
            using var e = await FreshAsync();
            var m1 = await e.CreateMeasureAsync("table:Sales", "Total", "1", "human");
            var m2 = await e.CreateMeasureAsync("table:Sales", "Margin", "2", "human");
            var c1 = await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");

            var r = await e.SetDisplayFolderAsync(new[] { m1, m2, c1 }, "KPIs", "human");
            Assert.True(r.Changed);
            Assert.Equal("KPIs", await FolderOfAsync(e, m1));
            Assert.Equal("KPIs", await FolderOfAsync(e, m2));
            Assert.Equal("KPIs", await FolderOfAsync(e, c1));

            // ONE undo restores every member — the whole point of batching over per-object set_property.
            await e.UndoAsync("human");
            Assert.Equal("", await FolderOfAsync(e, m1));
            Assert.Equal("", await FolderOfAsync(e, m2));
            Assert.Equal("", await FolderOfAsync(e, c1));
        }

        [Fact]
        public async Task Set_display_folder_is_all_or_nothing_and_teaches_on_a_bad_ref()
        {
            using var e = await FreshAsync();
            var m1 = await e.CreateMeasureAsync("table:Sales", "Total", "1", "human");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => e.SetDisplayFolderAsync(new[] { m1, "measure:Sales/Nope" }, "KPIs", "human"));
            Assert.Contains("list_objects", ex.Message);           // the failure names a recovery op (contract §1)
            Assert.Equal("", await FolderOfAsync(e, m1));          // the batch rolled back — m1 untouched

            // A kind without a display folder refuses with the folderable kinds + a discovery op.
            var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(
                () => e.SetDisplayFolderAsync(new[] { "table:Sales" }, "KPIs", "human"));
            Assert.Contains("list_measures", ex2.Message);

            // No refs at all teaches instead of silently succeeding.
            var ex3 = await Assert.ThrowsAsync<InvalidOperationException>(
                () => e.SetDisplayFolderAsync(Array.Empty<string>(), "KPIs", "human"));
            Assert.Contains("list_measures", ex3.Message);
        }

        [Fact]
        public async Task Rename_display_folder_rewrites_nested_paths_but_never_a_name_prefix_collision()
        {
            using var e = await FreshAsync();
            var m1 = await e.CreateMeasureAsync("table:Sales", "Total", "1", "human");
            var m2 = await e.CreateMeasureAsync("table:Sales", "Sub Total", "2", "human");
            var m3 = await e.CreateMeasureAsync("table:Sales", "Fees", "3", "human");
            await e.SetDisplayFolderAsync(new[] { m1 }, "Fin", "human");
            await e.SetDisplayFolderAsync(new[] { m2 }, "Fin\\Sub", "human");
            await e.SetDisplayFolderAsync(new[] { m3 }, "Finance", "human");

            var r = await e.RenameDisplayFolderAsync("table:Sales", "Fin", "Money", "human");
            Assert.Equal(2, r.Members);
            Assert.Equal("Money", await FolderOfAsync(e, m1));
            Assert.Equal("Money\\Sub", await FolderOfAsync(e, m2));
            Assert.Equal("Finance", await FolderOfAsync(e, m3));   // "Fin" must not match "Finance"

            // ONE undo restores the whole rename (both members, nested included).
            await e.UndoAsync("human");
            Assert.Equal("Fin", await FolderOfAsync(e, m1));
            Assert.Equal("Fin\\Sub", await FolderOfAsync(e, m2));
            Assert.Equal("Finance", await FolderOfAsync(e, m3));
        }

        [Fact]
        public async Task Rename_display_folder_to_empty_promotes_members_and_matches_case_insensitively()
        {
            using var e = await FreshAsync();
            var m1 = await e.CreateMeasureAsync("table:Sales", "Total", "1", "human");
            var m2 = await e.CreateMeasureAsync("table:Sales", "Sub Total", "2", "human");
            await e.SetDisplayFolderAsync(new[] { m1 }, "Fin", "human");
            await e.SetDisplayFolderAsync(new[] { m2 }, "Fin\\Sub", "human");

            // AS names are case-insensitive — 'fin' finds 'Fin'; empty toPath removes the folder level.
            var r = await e.RenameDisplayFolderAsync("table:Sales", "fin", "", "human");
            Assert.Equal(2, r.Members);
            Assert.Equal("", await FolderOfAsync(e, m1));
            Assert.Equal("Sub", await FolderOfAsync(e, m2));
        }

        [Fact]
        public async Task Rename_display_folder_with_no_members_teaches_instead_of_silently_succeeding()
        {
            using var e = await FreshAsync();
            await e.CreateMeasureAsync("table:Sales", "Total", "1", "human");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => e.RenameDisplayFolderAsync("table:Sales", "Ghost", "Real", "human"));
            Assert.Contains("list_measures", ex.Message);          // recovery op named
            Assert.Contains("Ghost", ex.Message);                  // and the path it looked for

            var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(
                () => e.RenameDisplayFolderAsync("table:Sales", "", "Real", "human"));
            Assert.Contains("list_measures", ex2.Message);
        }

        [Fact]
        public async Task Rename_display_folder_catches_members_with_stray_separator_paths()
        {
            using var e = await FreshAsync();
            var m1 = await e.CreateMeasureAsync("table:Sales", "Total", "1", "human");
            var m2 = await e.CreateMeasureAsync("table:Sales", "Sub Total", "2", "human");
            await e.SetDisplayFolderAsync(new[] { m1 }, "Fin", "human");
            // Stray leading/trailing separators can land via set_property (it stores the raw string) or older
            // edits. The tree groups such a member INSIDE the folder (normFolder), so the rename must match it
            // with the same normalization — before the fix it was silently missed (or worse: "no members").
            await e.SetObjectPropertyAsync(m2, "DisplayFolder", "\\Fin\\Sub\\", "human");

            var r = await e.RenameDisplayFolderAsync("table:Sales", "Fin", "Money", "human");
            Assert.Equal(2, r.Members);                            // the stray-path member moved WITH its clean sibling
            Assert.Equal("Money", await FolderOfAsync(e, m1));
            Assert.Equal("Money\\Sub", await FolderOfAsync(e, m2));   // rewritten AND canonicalized
        }

        [Fact]
        public async Task Tree_fan_stays_flat_and_carries_each_members_display_folder()
        {
            using var e = await FreshAsync();
            var m1 = await e.CreateMeasureAsync("table:Sales", "Total", "1", "human");
            var c1 = await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateHierarchyAsync("table:Sales", "Geo", new[] { "Amount" }, "human");
            await e.SetDisplayFolderAsync(new[] { m1, c1, "hierarchy:Sales/Geo" }, "KPIs\\Core", "human");

            var kids = await e.ListTreeAsync("table:Sales");
            Assert.Equal("KPIs\\Core", kids.Single(k => k.Ref == m1).DisplayFolder);
            Assert.Equal("KPIs\\Core", kids.Single(k => k.Ref == c1).DisplayFolder);
            Assert.Equal("KPIs\\Core", kids.Single(k => k.Kind == "hierarchy").DisplayFolder);
            // The fan is still FLAT (no folder nodes) — the agents' list_objects contract is unchanged;
            // grouping is the VS Code tree's client-side job.
            Assert.DoesNotContain(kids, k => k.Ref.StartsWith("dfolder:"));
        }

        [Fact]
        public async Task Create_measure_with_display_folder_is_born_filed_in_one_undo_step()
        {
            using var e = await FreshAsync();
            var mRef = await e.CreateMeasureAsync("table:Sales", "Growth", "1", "human", "KPIs\\New");
            Assert.Equal("KPIs\\New", await FolderOfAsync(e, mRef));

            // One undo removes the measure entirely — there is no second "set folder" step left behind.
            await e.UndoAsync("human");
            Assert.DoesNotContain(await e.ListMeasuresAsync(), m => m.Ref == mRef);
        }
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane m11/uat-undo, item U1 (Sol UAT-F-S07a-4-003/-004, F-S07b-3): taking back the removal of a role's table
    /// permission must put the whole permission object back, so the roles list and the plan stay readable.
    /// Class: undo of a removed child object that other objects reference (role permissions, members, OLS columns).
    /// </summary>
    public sealed class UatUndoRoleTests
    {
        private static async Task<(LocalEngine engine, string table)> NewAsync()
        {
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.CreateModelAsync("UatUndo", 1604);
            var table = await engine.CreateTableAsync("Store", "human");
            await engine.CreateColumnAsync(table, "Region", "String", "Region", "human");
            await engine.CreateColumnAsync(table, "City", "String", "City", "human");
            await engine.CreateRoleAsync("Managers", "Read", "human");
            return (engine, table);
        }

        private static async Task AssertReadableAsync(LocalEngine engine)
        {
            await engine.ListRolesAsync();
            await engine.GetModelPlanAsync();
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Undo_of_an_emptied_row_filter_restores_the_filter_and_everything_stays_readable_through_undo_redo_undo()
        {
            var (engine, table) = await NewAsync();
            using (engine)
            {
                await engine.SetTablePermissionAsync("Managers", table, "Store[Region] = \"North\"", "human");
                var r = await engine.SetTablePermissionAsync("Managers", table, "", "human");   // empty = no filter: removes the permission
                Assert.True(r.Changed);
                Assert.Empty(Assert.Single(await engine.ListRolesAsync()).TableFilters);

                await engine.UndoAsync("human");
                await AssertReadableAsync(engine);
                var f = Assert.Single(Assert.Single(await engine.ListRolesAsync()).TableFilters);
                Assert.Equal("Store", f.Table);
                Assert.Equal("Store[Region] = \"North\"", f.FilterExpression);

                await engine.RedoAsync("human");
                await AssertReadableAsync(engine);
                Assert.Empty(Assert.Single(await engine.ListRolesAsync()).TableFilters);

                await engine.UndoAsync("human");
                await AssertReadableAsync(engine);
                Assert.Equal("Store[Region] = \"North\"", Assert.Single(Assert.Single(await engine.ListRolesAsync()).TableFilters).FilterExpression);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Undo_of_a_deleted_role_brings_its_filter_and_members_back_readable()
        {
            var (engine, table) = await NewAsync();
            using (engine)
            {
                await engine.SetTablePermissionAsync("Managers", table, "Store[Region] = \"North\"", "human");
                await engine.SetRoleMemberAsync("Managers", "ann@contoso.com", true, "human");
                await engine.DeleteRoleAsync("Managers", "human");
                Assert.Empty(await engine.ListRolesAsync());

                await engine.UndoAsync("human");
                await AssertReadableAsync(engine);
                var role = Assert.Single(await engine.ListRolesAsync());
                Assert.Equal("Store[Region] = \"North\"", Assert.Single(role.TableFilters).FilterExpression);
                Assert.Contains("ann@contoso.com", role.Members);

                await engine.RedoAsync("human");
                Assert.Empty(await engine.ListRolesAsync());
                await engine.UndoAsync("human");
                await AssertReadableAsync(engine);
                Assert.Equal("Store", Assert.Single(Assert.Single(await engine.ListRolesAsync()).TableFilters).Table);
            }
        }

        [Fact]
        public async Task Undo_of_a_removed_member_brings_the_member_back()
        {
            var (engine, table) = await NewAsync();
            using (engine)
            {
                await engine.SetRoleMemberAsync("Managers", "ann@contoso.com", true, "human");
                await engine.SetRoleMemberAsync("Managers", "ann@contoso.com", false, "human");
                Assert.Empty(Assert.Single(await engine.ListRolesAsync()).Members);
                await engine.UndoAsync("human");
                await AssertReadableAsync(engine);
                Assert.Contains("ann@contoso.com", Assert.Single(await engine.ListRolesAsync()).Members);
                await engine.RedoAsync("human");
                await engine.UndoAsync("human");
                Assert.Contains("ann@contoso.com", Assert.Single(await engine.ListRolesAsync()).Members);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Undo_of_a_removed_table_and_column_object_level_permission_restores_both_and_stays_readable()
        {
            var (engine, table) = await NewAsync();
            using (engine)
            {
                var column = (await engine.ListColumnsAsync()).First(c => c.Name == "City").Ref;
                await engine.SetTableObjectPermissionAsync("Managers", table, "None", "human");
                await engine.SetColumnObjectPermissionAsync("Managers", column, "None", "human");
                await engine.SetTableObjectPermissionAsync("Managers", table, "Default", "human");   // the table still has a column rule: the permission stays
                await engine.SetColumnObjectPermissionAsync("Managers", column, "Default", "human"); // now nothing is left: the permission is removed
                var gone = Assert.Single(await engine.ListRolesAsync());
                Assert.Empty(gone.ObjectPermissions);

                await engine.UndoAsync("human");   // column rule back
                await AssertReadableAsync(engine);
                var back = Assert.Single(Assert.Single(await engine.ListRolesAsync()).ObjectPermissions);
                Assert.Equal("Store", back.Table);
                Assert.Equal("City", Assert.Single(back.Columns).Column);
                Assert.Equal("None", back.Columns[0].MetadataPermission);

                await engine.RedoAsync("human");
                await AssertReadableAsync(engine);
                Assert.Empty(Assert.Single(await engine.ListRolesAsync()).ObjectPermissions);
                await engine.UndoAsync("human");
                await AssertReadableAsync(engine);
                Assert.Equal("None", Assert.Single(Assert.Single(Assert.Single(await engine.ListRolesAsync()).ObjectPermissions).Columns).MetadataPermission);
            }
        }
    }
}

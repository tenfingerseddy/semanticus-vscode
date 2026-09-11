using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Security-role contracts for C5.4: invalid RLS DAX must not save, member identities must be
    /// real identities, a duplicate add / missing delete must not consume a revision, and OLS stays
    /// off calculation groups. Both doors share these LocalEngine methods.
    /// </summary>
    public sealed class RoleSecurityTests
    {
        private static async Task<(LocalEngine engine, SessionManager sessions)> NewModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            await engine.CreateModelAsync("RoleSecurity", 1604);
            return (engine, sessions);
        }

        private static async Task<string> SalesTableAsync(LocalEngine engine)
        {
            var table = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(table, "Region", "String", "Region", "human");
            return table;
        }

        [Fact]
        public async Task Invalid_rls_dax_with_unknown_column_and_dangling_operator_is_refused()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                var table = await SalesTableAsync(engine);
                await engine.CreateRoleAsync("UAT North", "Read", "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetTablePermissionAsync("UAT North", table, "Sales[Nope] =", "human"));
                Assert.Contains("row filter", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("set_table_permission", ex.Message, StringComparison.Ordinal);

                var role = Assert.Single(await engine.ListRolesAsync());
                Assert.Empty(role.TableFilters);
            }
        }

        [Fact]
        public async Task Nonsense_rls_function_is_refused_and_not_saved()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                var table = await SalesTableAsync(engine);
                await engine.CreateRoleAsync("UAT North", "Read", "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetTablePermissionAsync("UAT North", table, "NotAFunction()", "human"));
                Assert.Contains("NotAFunction", ex.Message, StringComparison.OrdinalIgnoreCase);

                var role = Assert.Single(await engine.ListRolesAsync());
                Assert.Empty(role.TableFilters);
            }
        }

        [Fact]
        public async Task Validate_dax_flags_unknown_function_and_incomplete_expression()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                await SalesTableAsync(engine);

                var unknown = await engine.ValidateDaxAsync("NotAFunction()");
                Assert.Contains(unknown.Diagnostics, d => d.Message.IndexOf("NotAFunction", StringComparison.OrdinalIgnoreCase) >= 0);

                var incomplete = await engine.ValidateDaxAsync("Sales[Nope] =");
                Assert.False(incomplete.Valid);
                Assert.NotEmpty(incomplete.Diagnostics);
            }
        }

        [Fact]
        public async Task Textbook_string_equality_rls_filter_saves()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                var table = await SalesTableAsync(engine);
                await engine.CreateRoleAsync("UAT North", "Read", "human");

                // The textbook row filter: a single column compared to a string literal is COMPLETE.
                // Masking the literal to blanks made the tail look like a dangling '=' and both doors refused it.
                var set = await engine.SetTablePermissionAsync("UAT North", table, "[Region] = \"North\"", "human");
                Assert.True(set.Changed);
                Assert.Equal("[Region] = \"North\"",
                    Assert.Single(Assert.Single(await engine.ListRolesAsync()).TableFilters).FilterExpression);

                // The table-qualified form, the not-equal form, a trailing comment and a concatenation all read the
                // same way: the literal is an operand, so nothing trails as an operator.
                foreach (var ok in new[]
                {
                    "Sales[Region] <> \"North\"",
                    "[Region] = \"North\" // west only",
                    "[Region] = \"North\" & \"\"",
                })
                    Assert.True((await engine.SetTablePermissionAsync("UAT North", table, ok, "human")).Changed, ok);

                var v = await engine.ValidateDaxAsync("Sales[Region] = \"North\"");
                Assert.True(v.Valid, string.Join("; ", v.Diagnostics.Select(d => d.Message)));
                Assert.DoesNotContain(v.Diagnostics, d => d.Message.Contains("incomplete", StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public async Task A_broken_string_or_a_keyword_where_the_value_belongs_is_still_refused()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                var table = await SalesTableAsync(engine);
                await engine.CreateRoleAsync("UAT North", "Read", "human");

                // An unterminated quote is not a value, and a keyword sitting where the value belongs still trails.
                foreach (var bad in new[] { "Sales[Region] = \"North", "Sales[Region] = \"North\"IN", "Sales[Region] = \"North\" NOT" })
                {
                    var v = await engine.ValidateDaxAsync(bad);
                    Assert.False(v.Valid, $"expected '{bad}' to stay invalid");
                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.SetTablePermissionAsync("UAT North", table, bad, "human"));
                    Assert.Contains("row filter", ex.Message, StringComparison.OrdinalIgnoreCase);
                }
                Assert.Empty(Assert.Single(await engine.ListRolesAsync()).TableFilters);
            }
        }

        [Fact]
        public async Task Rls_filter_ending_in_a_dangling_operator_is_still_refused()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                var table = await SalesTableAsync(engine);
                await engine.CreateRoleAsync("UAT North", "Read", "human");

                foreach (var bad in new[] { "Sales[Region] =", "[Region] =", "Sales[Region] = ", "[Region] <>", "Sales[Region] +" })
                {
                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.SetTablePermissionAsync("UAT North", table, bad, "human"));
                    Assert.Contains("row filter", ex.Message, StringComparison.OrdinalIgnoreCase);

                    var v = await engine.ValidateDaxAsync(bad);
                    Assert.False(v.Valid, $"expected '{bad}' to stay invalid");
                }
                Assert.Empty(Assert.Single(await engine.ListRolesAsync()).TableFilters);
            }
        }

        [Fact]
        public async Task Valid_rls_dax_still_saves_and_empty_clears()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                var table = await SalesTableAsync(engine);
                await engine.CreateRoleAsync("UAT North", "Read", "human");

                var set = await engine.SetTablePermissionAsync(
                    "UAT North", table, "[Region] = USERPRINCIPALNAME()", "human");
                Assert.True(set.Changed);
                var role = Assert.Single(await engine.ListRolesAsync());
                Assert.Equal("[Region] = USERPRINCIPALNAME()", Assert.Single(role.TableFilters).FilterExpression);

                var clear = await engine.SetTablePermissionAsync("UAT North", table, "", "human");
                Assert.True(clear.Changed);
                Assert.Empty(Assert.Single(await engine.ListRolesAsync()).TableFilters);
            }
        }

        [Fact]
        public async Task Malformed_member_identity_is_refused()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateRoleAsync("UAT Members", "Read", "human");

                var ex = await Assert.ThrowsAsync<ArgumentException>(
                    () => engine.SetRoleMemberAsync("UAT Members", "not an identity !!", true, "human"));
                Assert.Contains("user or group", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("set_role_member", ex.Message, StringComparison.Ordinal);

                Assert.Empty(Assert.Single(await engine.ListRolesAsync()).Members);
            }
        }

        [Fact]
        public async Task Valid_member_identities_are_accepted()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateRoleAsync("UAT Members", "Read", "human");

                Assert.True((await engine.SetRoleMemberAsync("UAT Members", "alex@contoso.com", true, "human")).Changed);
                Assert.True((await engine.SetRoleMemberAsync("UAT Members", "CONTOSO\\SalesLeads", true, "human")).Changed);
                Assert.True((await engine.SetRoleMemberAsync("UAT Members", "Sales Team", true, "human")).Changed);
                Assert.True((await engine.SetRoleMemberAsync("UAT Members", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", true, "human")).Changed);

                var members = Assert.Single(await engine.ListRolesAsync()).Members;
                Assert.Equal(4, members.Length);
            }
        }

        [Fact]
        public async Task Duplicate_member_is_reported_and_does_not_advance_revision()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateRoleAsync("UAT Members", "Read", "human");
                var first = await engine.SetRoleMemberAsync("UAT Members", "alex@contoso.com", true, "human");
                Assert.True(first.Changed);

                var again = await engine.SetRoleMemberAsync("UAT Members", "alex@contoso.com", true, "human");
                Assert.False(again.Changed);
                Assert.Equal(first.Revision, again.Revision);
                Assert.Contains("already", again.Warning, StringComparison.OrdinalIgnoreCase);
                Assert.Single(Assert.Single(await engine.ListRolesAsync()).Members);
            }
        }

        [Fact]
        public async Task Missing_role_delete_does_not_advance_revision()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateRoleAsync("UAT North", "Read", "human");
                var before = sessions.Require().Revision;

                var del = await engine.DeleteRoleAsync("no-such-role-xyz", "human");
                Assert.False(del.Changed);
                Assert.Equal(before, del.Revision);
                Assert.Equal(before, sessions.Require().Revision);
                Assert.Single(await engine.ListRolesAsync());
            }
        }

        [Fact]
        public async Task Ols_on_a_calculation_group_is_refused()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateRoleAsync("UAT North", "Read", "human");
                var cg = await engine.CreateCalculationGroupAsync("UAT Detail", "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetTableObjectPermissionAsync("UAT North", cg, "None", "human"));
                Assert.Contains("calculation group", ex.Message, StringComparison.OrdinalIgnoreCase);

                Assert.Empty(Assert.Single(await engine.ListRolesAsync()).ObjectPermissions);
            }
        }
    }
}

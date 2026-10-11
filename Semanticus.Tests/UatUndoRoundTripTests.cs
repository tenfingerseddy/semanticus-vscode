using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane m11/uat-undo, items U1 and U2 (class): taking back a removal must put the WHOLE object back. The vendored
    /// undo re-creates a removed object from the JSON saved at removal, which names references (a permission's table,
    /// a policy's windows) without pointing at them, so a child that the removal took with it can come back thin or
    /// broken. One model with a compound object of each kind, removed one at a time through the shared delete door,
    /// undone, redone and undone again; the whole model must read as it did before, byte for byte after sorting.
    /// </summary>
    public sealed class UatUndoRoundTripTests
    {
        private static string Write(string dir)
        {
            var db = new TOM.Database("RoundTrip") { CompatibilityLevel = 1604, Model = new TOM.Model { DiscourageImplicitMeasures = true } };
            var sales = new TOM.Table { Name = "Sales", LineageTag = "t-sales" };
            sales.Columns.Add(new TOM.DataColumn { Name = "ProductKey", DataType = TOM.DataType.Int64, SourceColumn = "ProductKey", LineageTag = "c-pk" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Country", DataType = TOM.DataType.String, SourceColumn = "Country", LineageTag = "c-co" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region", LineageTag = "c-re", Description = "where" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-am" });
            sales.Measures.Add(new TOM.Measure { Name = "Total", Expression = "SUM(Sales[Amount])", FormatString = "#,0", Description = "all of it", LineageTag = "m-total" });
            var h = new TOM.Hierarchy { Name = "Geo", LineageTag = "h-geo" };
            h.Levels.Add(new TOM.Level { Name = "Country", Column = sales.Columns["Country"], Ordinal = 0, LineageTag = "l-co" });
            h.Levels.Add(new TOM.Level { Name = "Region", Column = sales.Columns["Region"], Ordinal = 1, LineageTag = "l-re" });
            sales.Hierarchies.Add(h);
            sales.Partitions.Add(new TOM.Partition { Name = "P1", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            sales.Partitions.Add(new TOM.Partition { Name = "P2", Source = new TOM.MPartitionSource { Expression = "let Source = 2 in Source" } });
            db.Model.Tables.Add(sales);

            var product = new TOM.Table { Name = "Product", LineageTag = "t-product" };
            product.Columns.Add(new TOM.DataColumn { Name = "ProductKey", DataType = TOM.DataType.Int64, SourceColumn = "ProductKey", IsKey = true, LineageTag = "c-ppk" });
            product.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-pn" });
            product.Partitions.Add(new TOM.Partition { Name = "Product", Source = new TOM.MPartitionSource { Expression = "let Source = 3 in Source" } });
            db.Model.Tables.Add(product);
            db.Model.Relationships.Add(new TOM.SingleColumnRelationship
            {
                Name = "rel-sales-product", FromColumn = sales.Columns["ProductKey"], ToColumn = product.Columns["ProductKey"],
                FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One,
            });

            var cg = new TOM.Table { Name = "Time Calc", LineageTag = "t-cg", CalculationGroup = new TOM.CalculationGroup { Precedence = 1 } };
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "YTD", Expression = "SELECTEDMEASURE()", Description = "year to date" });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "MTD", Expression = "SELECTEDMEASURE() * 2", FormatStringDefinition = new TOM.FormatStringDefinition { Expression = "\"0.0\"" } });
            cg.Columns.Add(new TOM.DataColumn { Name = "Period", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-cg" });
            cg.Partitions.Add(new TOM.Partition { Name = "Time Calc", Source = new TOM.CalculationGroupSource() });
            db.Model.Tables.Add(cg);

            var persp = new TOM.Perspective { Name = "Everything" };
            var pt = new TOM.PerspectiveTable { Table = sales };
            pt.PerspectiveColumns.Add(new TOM.PerspectiveColumn { Column = sales.Columns["Region"] });
            pt.PerspectiveMeasures.Add(new TOM.PerspectiveMeasure { Measure = sales.Measures["Total"] });
            pt.PerspectiveHierarchies.Add(new TOM.PerspectiveHierarchy { Hierarchy = h });
            persp.PerspectiveTables.Add(pt);
            db.Model.Perspectives.Add(persp);

            var role = new TOM.ModelRole { Name = "Managers", ModelPermission = TOM.ModelPermission.Read };
            var tp = new TOM.TablePermission { Table = sales, FilterExpression = "Sales[Region] = \"North\"", MetadataPermission = TOM.MetadataPermission.Read };
            tp.ColumnPermissions.Add(new TOM.ColumnPermission { Column = sales.Columns["Amount"], MetadataPermission = TOM.MetadataPermission.None });
            tp.Annotations.Add(new TOM.Annotation { Name = "why", Value = "regional managers" });
            role.TablePermissions.Add(tp);
            role.Members.Add(new TOM.ExternalModelRoleMember { MemberName = "ann@contoso.com", IdentityProvider = "AzureAD" });
            db.Model.Roles.Add(role);

            var fact = new TOM.Table { Name = "Fact", LineageTag = "t-fact" };
            fact.Columns.Add(new TOM.DataColumn { Name = "When", DataType = TOM.DataType.DateTime, SourceColumn = "When", LineageTag = "c-when" });
            fact.Partitions.Add(new TOM.Partition { Name = "Fact", Source = new TOM.MPartitionSource { Expression = "let Source = Table.SelectRows(x, each [When] >= RangeStart and [When] < RangeEnd) in Source" } });
            fact.RefreshPolicy = new TOM.BasicRefreshPolicy
            {
                RollingWindowGranularity = TOM.RefreshGranularityType.Year, RollingWindowPeriods = 2,
                IncrementalGranularity = TOM.RefreshGranularityType.Day, IncrementalPeriods = 7, IncrementalPeriodsOffset = -1,
                Mode = TOM.RefreshPolicyMode.Hybrid, PollingExpression = "let x = 1 in x",
                SourceExpression = "let Source = Table.SelectRows(x, each [When] >= RangeStart and [When] < RangeEnd) in Source",
            };
            db.Model.Tables.Add(fact);
            db.Model.Expressions.Add(new TOM.NamedExpression { Name = "RangeStart", Kind = TOM.ExpressionKind.M, Expression = "#datetime(2020, 1, 1, 0, 0, 0) meta [IsParameterQuery=true, Type=\"DateTime\", IsParameterQueryRequired=true]", LineageTag = "e-rs" });
            db.Model.Expressions.Add(new TOM.NamedExpression { Name = "RangeEnd", Kind = TOM.ExpressionKind.M, Expression = "#datetime(2021, 1, 1, 0, 0, 0) meta [IsParameterQuery=true, Type=\"DateTime\", IsParameterQueryRequired=true]", LineageTag = "e-re" });

            var path = Path.Combine(dir, "RoundTrip.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        // The model as TOM writes it, with every list sorted by name so an object that comes back in a different place
        // is not a difference (only a missing or thinner object is), and the clock fields dropped.
        private static async Task<string> SnapshotAsync(SessionManager sessions)
        {
            var json = await sessions.Current.ReadAsync(m => TOM.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase,
                new TOM.SerializeOptions { IgnoreTimestamps = true, IgnoreInferredObjects = true, IgnoreInferredProperties = true, IncludeRestrictedInformation = true }));
            return Normal(JsonNode.Parse(json)).ToJsonString();
        }

        private static JsonNode Normal(JsonNode n)
        {
            switch (n)
            {
                case JsonObject o:
                    var sorted = new JsonObject();
                    foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal).ToList()) sorted[kv.Key] = Normal(kv.Value);
                    return sorted;
                case JsonArray a:
                    var items = a.Select(Normal).ToList();
                    var sortedItems = items.OrderBy(i => (i as JsonObject)?["name"]?.ToString() ?? (i as JsonObject)?["table"]?.ToString() ?? i?.ToJsonString(), StringComparer.Ordinal).ToList();
                    return new JsonArray(sortedItems.Select(i => i == null ? null : JsonNode.Parse(i.ToJsonString())).ToArray());
                default:
                    return n == null ? null : JsonNode.Parse(n.ToJsonString());
            }
        }

        public static IEnumerable<object[]> Removals() => new[]
        {
            "measure:Sales/Total", "column:Sales/Region", "hierarchy:Sales/Geo", "partition:Sales/P2", "perspective:Everything",
            "calcitem:Time Calc/YTD", "calcitem:Time Calc/MTD", "relationship:*", "role:Managers",
            "table:Product", "table:Sales", "table:Time Calc", "table:Fact",
        }.Select(r => new object[] { r });

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Removals))]
        public async Task A_removed_object_comes_back_whole_through_undo_redo_undo(string objRef)
        {
            var dir = Path.Combine(Path.GetTempPath(), "uu-rt-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(Write(dir));
                var before = await SnapshotAsync(sessions);
                if (objRef == "relationship:*")
                    objRef = "relationship:" + await sessions.Current.ReadAsync(m => m.Relationships.First().Name);

                var r = await engine.DeleteObjectAsync(objRef, "human");
                Assert.True(r.Changed, objRef + " was not removed");
                var gone = await SnapshotAsync(sessions);
                Assert.NotEqual(before, gone);

                await engine.UndoAsync("human");
                await engine.ListRolesAsync();                 // every read the Plan and the security page make
                await engine.GetModelPlanAsync();
                Assert.Equal(before, await SnapshotAsync(sessions));

                await engine.RedoAsync("human");
                Assert.Equal(gone, await SnapshotAsync(sessions));
                await engine.UndoAsync("human");
                await engine.ListRolesAsync();
                await engine.GetModelPlanAsync();
                Assert.Equal(before, await SnapshotAsync(sessions));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_removed_incremental_refresh_policy_comes_back_with_every_field()
        {
            var dir = Path.Combine(Path.GetTempPath(), "uu-ir-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(Write(dir));
                var before = await engine.GetIncrementalRefreshPolicyAsync("table:Fact");
                Assert.True(before.Enabled);
                Assert.Equal(2, before.RollingWindowPeriods);

                var r = await engine.RemoveIncrementalRefreshPolicyAsync("table:Fact", "human");
                Assert.True(r.Changed);
                Assert.False((await engine.GetIncrementalRefreshPolicyAsync("table:Fact")).Enabled);

                await engine.UndoAsync("human");
                var back = await engine.GetIncrementalRefreshPolicyAsync("table:Fact");
                Assert.True(back.Enabled);
                Assert.Equal("Year", back.RollingWindowGranularity);
                Assert.Equal(2, back.RollingWindowPeriods);
                Assert.Equal("Day", back.IncrementalGranularity);
                Assert.Equal(7, back.IncrementalPeriods);
                Assert.Equal(-1, back.IncrementalPeriodsOffset);
                Assert.Equal("Hybrid", back.Mode);
                Assert.Equal(before.SourceExpression, back.SourceExpression);
                Assert.Equal("let x = 1 in x", back.PollingExpression);

                await engine.RedoAsync("human");
                Assert.False((await engine.GetIncrementalRefreshPolicyAsync("table:Fact")).Enabled);
                await engine.UndoAsync("human");
                var again = await engine.GetIncrementalRefreshPolicyAsync("table:Fact");
                Assert.Equal(7, again.IncrementalPeriods);
                Assert.Equal(before.SourceExpression, again.SourceExpression);
                Assert.Equal("let x = 1 in x", again.PollingExpression);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public async Task A_policy_switched_off_in_the_properties_grid_comes_back_whole_too()
        {
            // The same take-back from the other door that can switch a policy off (set_property on the table), so the
            // fix is on the property and not only on the one tool that wraps it.
            var dir = Path.Combine(Path.GetTempPath(), "uu-ig-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(Write(dir));
                var before = await engine.GetIncrementalRefreshPolicyAsync("table:Fact");
                await engine.SetObjectPropertyAsync("table:Fact", "EnableRefreshPolicy", "false", "human");
                Assert.False((await engine.GetIncrementalRefreshPolicyAsync("table:Fact")).Enabled);
                await engine.UndoAsync("human");
                var back = await engine.GetIncrementalRefreshPolicyAsync("table:Fact");
                Assert.True(back.Enabled);
                Assert.Equal(before.RollingWindowPeriods, back.RollingWindowPeriods);
                Assert.Equal(before.IncrementalPeriods, back.IncrementalPeriods);
                Assert.Equal(before.SourceExpression, back.SourceExpression);
                Assert.Equal(before.PollingExpression, back.PollingExpression);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}

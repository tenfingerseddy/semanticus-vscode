using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// C4 (health-delta review): the Analysis-side ref formatter (<c>Semanticus.Analysis.Refs</c>) must format
    /// EVERY object kind exactly like the engine's <see cref="ObjectRefs"/> — the health probe intersects
    /// analyzer findings with engine change-deltas by ref, and the scoped BPA overload pre-filters collections
    /// by ref, so any mismatch (hierarchies/levels/perspectives/partitions/functions/roles used to fall to the
    /// "obj:Type/Name" default) silently filtered those objects OUT of the delta. This walks a model carrying
    /// every wrapper kind both formatters can meet and compares them pairwise.
    /// </summary>
    public sealed class ReadinessRefsParityTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        [Fact]
        public async Task Analysis_refs_format_identically_to_engine_object_refs_for_every_object_kind()
        {
            var sm = new SessionManager();
            using var engine = new LocalEngine(sm, new Pro());
            // A high-CL scratch model so every creatable kind (calc groups, functions where supported) is available.
            await engine.CreateModelAsync("RefParity", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            await engine.CreateColumnAsync(t, "Year", "Int64", "Year", "human");
            var dim = await engine.CreateTableAsync("Product", "human");
            await engine.CreateColumnAsync(dim, "ProductKey", "Int64", "ProductKey", "human");
            await engine.CreateColumnAsync(t, "ProductKey", "Int64", "ProductKey", "human");
            await engine.CreateMeasureAsync(t, "Total Amount", "SUM(Sales[Amount])", "human");
            await engine.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, null, "human");
            await engine.CreateHierarchyAsync(t, "Time Drill", new[] { "Year" }, "human");
            await engine.CreatePerspectiveAsync("Reporting", "human");
            await engine.CreateRoleAsync("Readers", "Read", "human");
            await engine.CreateCalculationGroupAsync("Time Intelligence", "human");
            await engine.CreateCalculationItemAsync("table:Time Intelligence", "YTD", "SELECTEDMEASURE()", "human");
            try { await engine.CreateFunctionAsync("MyFn", "(x) => x + 1", "human"); }
            catch { /* UDFs need a newer CL/engine — the Function case is still pinned by any model that has one */ }

            var mismatches = await sm.Current!.ReadAsync(m =>
            {
                var objects = new List<ITabularObject>();
                foreach (var table in m.Tables)
                {
                    objects.Add(table);
                    objects.AddRange(table.Columns.Where(c => c.Type != ColumnType.RowNumber));
                    objects.AddRange(table.Measures);
                    objects.AddRange(table.Hierarchies);
                    foreach (var h in table.Hierarchies) objects.AddRange(h.Levels);
                    objects.AddRange(table.Partitions);
                    if (table is CalculationGroupTable cg) objects.AddRange(cg.CalculationItems);
                }
                objects.AddRange(m.Relationships.OfType<SingleColumnRelationship>());
                objects.AddRange(m.Roles);
                objects.AddRange(m.Perspectives);
                objects.AddRange(m.Functions);

                // Assert we really exercised the kinds C4 fixed (a fixture regression must fail loudly, not
                // silently shrink coverage).
                Assert.Contains(objects, o => o is Hierarchy);
                Assert.Contains(objects, o => o is Level);
                Assert.Contains(objects, o => o is Partition);
                Assert.Contains(objects, o => o is Perspective);
                Assert.Contains(objects, o => o is ModelRole);
                Assert.Contains(objects, o => o is CalculationItem);

                return objects
                    .Select(o => (obj: o, engineRef: ObjectRefs.For(o), analysisRef: Semanticus.Analysis.Refs.For(o)))
                    .Where(x => x.engineRef != x.analysisRef)
                    .Select(x => $"{x.obj.GetType().Name}: engine='{x.engineRef}' analysis='{x.analysisRef}'")
                    .ToArray();
            });

            Assert.True(mismatches.Length == 0, "ref formatter mismatch:\n" + string.Join("\n", mismatches));
        }
    }
}

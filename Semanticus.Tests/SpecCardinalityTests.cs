using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Relationship CARDINALITY on the model spec: the new field round-trips through set_spec→get_spec, and the
    /// spec→model builder honors it — an unspecified relationship still builds the classic many→one (behavior preserved),
    /// while an explicit oneToMany / manyToMany lands the right TOM end cardinalities. Drives the real LocalEngine.</summary>
    public sealed class SpecCardinalityTests
    {
        private sealed class ProEntitlement : IEntitlement   // build_model_from_spec is Pro-gated
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private static LocalEngine NewEngine() => new LocalEngine(new SessionManager(), new ProEntitlement());

        // A minimal fact→dimension spec whose one relationship carries the given cardinality (null = unspecified).
        private static string StarSpec(string? cardinality)
        {
            var rel = new SpecRelationship { FromTable = "Sales", FromColumn = "CustomerKey", ToTable = "Customer", ToColumn = "CustomerKey", Cardinality = cardinality };
            var spec = new ModelSpec
            {
                Name = "CardModel",
                CompatibilityLevel = 1604,
                StorageMode = "import",
                Tables = new[]
                {
                    new SpecTable { Name = "Sales", Role = "fact", Columns = new[] { new SpecColumn { Name = "CustomerKey", DataType = "Int64" } } },
                    new SpecTable { Name = "Customer", Role = "dimension", Columns = new[] { new SpecColumn { Name = "CustomerKey", DataType = "Int64", IsKey = true } } },
                },
                Relationships = new[] { rel },
            };
            return JsonSerializer.Serialize(spec, Camel);
        }

        [Fact]
        public async Task manyToMany_cardinality_survives_setSpec_getSpec()
        {
            using var engine = NewEngine();
            await engine.SetSpecAsync(StarSpec("manyToMany"), "human");
            var view = await engine.GetSpecAsync();
            Assert.Equal("manyToMany", view.Spec.Relationships.Single().Cardinality);
        }

        [Fact]
        public async Task unspecified_cardinality_builds_manyToOne()   // the default MUST preserve today's behavior
        {
            using var engine = NewEngine();
            await engine.CreateModelAsync("CardModel", 1604);
            await engine.SetSpecAsync(StarSpec(null), "human");
            await engine.BuildModelFromSpecAsync("human");

            var rel = (await engine.GetModelGraphAsync()).Relationships.Single();
            Assert.Equal("Many", rel.FromCardinality);
            Assert.Equal("One", rel.ToCardinality);
        }

        [Fact]
        public async Task manyToMany_cardinality_builds_many_to_many()
        {
            using var engine = NewEngine();
            await engine.CreateModelAsync("CardModel", 1604);
            await engine.SetSpecAsync(StarSpec("manyToMany"), "human");
            await engine.BuildModelFromSpecAsync("human");

            var rel = (await engine.GetModelGraphAsync()).Relationships.Single();
            Assert.Equal("Many", rel.FromCardinality);
            Assert.Equal("Many", rel.ToCardinality);
        }

        [Fact]
        public async Task oneToMany_cardinality_builds_one_to_many()
        {
            using var engine = NewEngine();
            await engine.CreateModelAsync("CardModel", 1604);
            await engine.SetSpecAsync(StarSpec("oneToMany"), "human");
            await engine.BuildModelFromSpecAsync("human");

            var rel = (await engine.GetModelGraphAsync()).Relationships.Single();
            Assert.Equal("One", rel.FromCardinality);
            Assert.Equal("Many", rel.ToCardinality);
        }

        // A star with one relationship of EACH shape, so autogenerate has a non-default cardinality on every end to
        // preserve. Sales is the fact; each dim carries a distinct cardinality on its Sales→dim edge.
        private static string MixedCardinalitySpec()
        {
            SpecColumn Key(string n, bool key = false) => new SpecColumn { Name = n, DataType = "Int64", IsKey = key };
            var spec = new ModelSpec
            {
                Name = "RT", CompatibilityLevel = 1604, StorageMode = "import",
                Tables = new[]
                {
                    new SpecTable { Name = "Sales", Role = "fact", Columns = new[] { Key("CustomerKey"), Key("ProductKey"), Key("StoreKey") } },
                    new SpecTable { Name = "Customer", Role = "dimension", Columns = new[] { Key("CustomerKey", true) } },
                    new SpecTable { Name = "Product", Role = "dimension", Columns = new[] { Key("ProductKey", true) } },
                    new SpecTable { Name = "Store", Role = "dimension", Columns = new[] { Key("StoreKey", true) } },
                },
                Relationships = new[]
                {
                    new SpecRelationship { FromTable = "Sales", FromColumn = "CustomerKey", ToTable = "Customer", ToColumn = "CustomerKey" },                          // default many→one
                    new SpecRelationship { FromTable = "Sales", FromColumn = "ProductKey", ToTable = "Product", ToColumn = "ProductKey", Cardinality = "oneToMany" },
                    new SpecRelationship { FromTable = "Sales", FromColumn = "StoreKey", ToTable = "Store", ToColumn = "StoreKey", Cardinality = "oneToOne" },
                },
            };
            return JsonSerializer.Serialize(spec, Camel);
        }

        [Fact]   // the finding: a one-to-many / one-to-one must NOT be downgraded to many-to-one by autogenerate→build.
        public async Task autogenerate_then_build_reproduces_every_cardinality()
        {
            // 1. Build a model that carries a one-to-many and a one-to-one (plus the default many-to-one).
            using var origin = NewEngine();
            await origin.CreateModelAsync("RT", 1604);
            await origin.SetSpecAsync(MixedCardinalitySpec(), "human");
            await origin.BuildModelFromSpecAsync("human");

            // 2. Autogenerate a spec back FROM that model — the round-trip's lossy step under the old end-swap.
            await origin.AutogenerateSpecFromModelAsync("human");
            var regen = (await origin.GetSpecAsync()).Spec;

            // 3. Rebuild the autogenerated spec into a FRESH model.
            using var rebuilt = NewEngine();
            await rebuilt.CreateModelAsync("RT2", 1604);
            await rebuilt.SetSpecAsync(JsonSerializer.Serialize(regen, Camel), "human");
            await rebuilt.BuildModelFromSpecAsync("human");

            // 4. The rebuilt model must carry the SAME per-edge cardinality — oriented to the Sales→dim direction.
            var rels = (await rebuilt.GetModelGraphAsync()).Relationships;
            (string from, string to) Edge(string dim)
            {
                var r = rels.Single(x => x.FromTable == dim || x.ToTable == dim);
                return r.FromTable == "Sales" ? (r.FromCardinality, r.ToCardinality) : (r.ToCardinality, r.FromCardinality);
            }

            Assert.Equal(("Many", "One"), Edge("Customer"));
            Assert.Equal(("One", "Many"), Edge("Product"));
            Assert.Equal(("One", "One"), Edge("Store"));
        }
    }
}

// VERIFIER round 2 (v-coverage-fix): the relationship-by-ends follow must never land on a different relationship.
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class VRelEndsTests
    {
        private readonly ITestOutputHelper _out;
        public VRelEndsTests(ITestOutputHelper o) => _out = o;

        private static async Task<DecisionFixture> ModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Rel", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "ShipCustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            f.UseNull();
            return f;
        }

        private static Task<PlanGroundNow> NowAsync(DecisionFixture f, string r) =>
            f.Sessions.Require().ReadAsync(m => PlanTaskObjects.Now(m, new PlanObjectGround { Ref = r }));

        [Fact]
        public async Task A_direction_change_is_followed_but_a_different_relationship_never_is()
        {
            using var f = await ModelAsync();
            var e = f.Engine;
            var bidi = await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", "BothDirections", true, "human");
            _out.WriteLine("made " + bidi);
            // 1. Same ends, direction changed: followed.
            await e.SetObjectPropertyAsync(bidi, "CrossFilteringBehavior", "OneDirection", "agent");
            var now = await NowAsync(f, bidi);
            _out.WriteLine("after flip: " + now.Current);
            Assert.False(now.Gone);
            Assert.NotEqual(bidi, now.Current);
            Assert.Contains("[CustomerKey]", now.Current);
            Assert.DoesNotContain("ShipCustomerKey", now.Current);
            // 2. Deleted, and a different relationship (other column, same tables) made: gone, not followed.
            await e.DeleteObjectsAsync(new[] { now.Current }, "agent");
            var other = await e.CreateRelationshipAsync("column:Sales/ShipCustomerKey", "column:Customer/CustomerKey", "BothDirections", true, "human");
            _out.WriteLine("other " + other);
            var gone = await NowAsync(f, bidi);
            _out.WriteLine("after delete: " + gone.Current);
            Assert.True(gone.Gone, "followed to a different relationship: " + gone.Current);
            // (Two relationships between the same columns cannot exist: the engine refuses the second, so the ends name one relationship.)
        }
    }
}

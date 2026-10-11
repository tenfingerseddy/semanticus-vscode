// Join 10: the fixes the join itself makes on top of the merged lanes. Each test here failed first on the merge of drafts, shell, psc5 and s10fix.
//
// J2  a declared link turned on beside another that already joins the same two tables (fact_invoice DUE_DT_KEY beside INV_DT_KEY) gets the check a guessed link gets
//     (refused, in the same words) and never leaves the plan "Still working out" for good (check of psc5, item 1).
// J4  (the engine side is in PublishDestinationClearTests: Join10_a_live_model_with_a_linked_destination_publishes_where_connection_context_says)

using System;
using System.Linq;
using Semanticus.Engine;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class Join10DeclaredLinkTests
    {
        private static StructureManifest Keyed() => StructureManifest.Derive(SqlDraftBuildTests.J8Schema(), null, null, "seed", "x", "DW", "import", LinkGuessFacts.NamesOnly());
        private static StartPlanDoc DocOf(StructureManifest m) => new StartPlanDoc { Id = "sp-test", Manifest = m, IdentitySeed = "seed" };
        private static ManifestLink Role(StructureManifest m, string column) =>
            m.Links.Single(l => l.FromTable == "fact_invoice" && l.FromColumns[0] == column && l.ToTable == "dim_date");

        [Theory]
        [InlineData("set_link")]
        [InlineData("keep_line")]
        public void A_declared_second_date_role_is_refused_while_the_first_is_on_and_comes_on_once_the_first_is_off(string op)
        {
            var m = Keyed();
            var doc = DocOf(m);
            var first = Role(m, "INV_DT_KEY");
            var due = Role(m, "DUE_DT_KEY");
            Assert.True(first.Active);
            Assert.False(due.Active);
            Assert.False(due.Guessed);   // declared: this is the path a guessed link already had a check on
            var target = op == "set_link" ? due.Identity : "relationship:" + due.Identity;
            var value = op == "set_link" ? "on" : null;

            var refused = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = op, Target = target, Value = value }, "human");

            Assert.False(refused.Ok);
            Assert.Contains("second path", refused.Refused);
            Assert.Contains("Turn the other link off first", refused.Refused);
            Assert.False(due.Active);
            Assert.Single(m.Links.Where(l => l.FromTable == "fact_invoice" && l.ToTable == "dim_date" && l.Active));   // the plan still builds with one path

            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_link", Target = first.Identity, Value = "off" }, "human").Ok);
            var done = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = op, Target = target, Value = value }, "human");
            Assert.True(done.Ok, done.Refused);
            Assert.True(due.Active);
        }
    }
}

using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Next-release catalog completion batch 5. New detection is executable here; the reconciled relationship rules
    /// keep their violation, clean and dormant cases in ReadinessCatalogBatch1Tests/Batch2Tests/Batch5Tests.
    /// </summary>
    public sealed class ReadinessCatalogCompletionBatch5Tests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static RuleEvaluation Eval(SessionManager sessions, string id) =>
            ReadinessRuleSet.Default().Single(r => r.Id == id).Evaluate(sessions.Current.Model);

        [Fact]
        public async Task Bare_identifier_rule_covers_violation_clean_and_dormant_populations_without_double_counting()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("CatalogCompletionBatch5", 1604);
            var table = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(table, "Customer Name", "String", "Customer Name", "human");

            var dormant = Eval(sessions, "NAME-COLUMN-ID");
            Assert.Equal(0, dormant.Applicable);
            Assert.Empty(dormant.Violations);

            var code = await engine.CreateColumnAsync(table, "Code", "String", "Code", "human");
            var violation = Eval(sessions, "NAME-COLUMN-ID");
            Assert.Equal(1, violation.Applicable);
            Assert.Single(violation.Violations);
            Assert.Equal("Code", violation.Violations[0].ObjectName);

            await engine.RenameObjectAsync(code, "Product Code", "human");
            var clean = Eval(sessions, "NAME-COLUMN-ID");
            Assert.Equal(1, clean.Applicable);
            Assert.Empty(clean.Violations);
            Assert.Equal(1, Eval(sessions, "NAME-COLUMN").Applicable); // Customer Name only; Product Code is partitioned here

            await engine.CreateColumnAsync(table, "ProductID", "String", "ProductID", "human");
            Assert.Empty(Eval(sessions, "NAME-COLUMN-ID").Violations);
            var cryptic = Eval(sessions, "NAME-COLUMN");
            Assert.Equal(2, cryptic.Applicable); // Customer Name + cryptic ProductID; still no double count
            Assert.Contains(cryptic.Violations, f => f.ObjectName == "ProductID");

            var rule = ReadinessRuleSet.Default().Single(r => r.Id == "NAME-COLUMN-ID");
            Assert.Equal(RuleKind.Deterministic, rule.Kind);
            Assert.Equal(FixKind.AiContent, rule.Fix);
        }
    }
}

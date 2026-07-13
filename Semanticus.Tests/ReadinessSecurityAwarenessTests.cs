using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ReadinessSecurityAwarenessTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        [Fact]
        public async Task Ols_restrictions_surface_as_a_score_neutral_answer_coverage_advisory()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro());
            await engine.CreateModelAsync("Governed", 1604);
            var table = await engine.CreateTableAsync("Customer", "human");
            var email = await engine.CreateColumnAsync(table, "Email Address", "String", "Email Address", "human");
            await engine.CreateColumnAsync(table, "Customer Name", "String", "Customer Name", "human");
            await engine.CreateRoleAsync("Restricted Reader", "Read", "human");

            var clean = await engine.AiReadinessScanAsync();
            Assert.DoesNotContain(clean.Findings, f => f.RuleId == "SEC-OLS-AWARENESS");
            var cleanVisibility = clean.Categories.Single(c => c.Category == nameof(ReadinessCategory.Visibility)).Score;

            await engine.SetTableObjectPermissionAsync("Restricted Reader", table, "None", "human");
            var tableRestricted = await engine.AiReadinessScanAsync();
            var tableFinding = Assert.Single(tableRestricted.Findings, f => f.RuleId == "SEC-OLS-AWARENESS");
            Assert.Equal(nameof(FixKind.None), tableFinding.Fix);
            Assert.Contains("1 table assignment(s)", tableFinding.Message);
            Assert.Equal(cleanVisibility, tableRestricted.Categories.Single(c => c.Category == nameof(ReadinessCategory.Visibility)).Score);

            await engine.SetTableObjectPermissionAsync("Restricted Reader", table, "Default", "human");
            await engine.SetColumnObjectPermissionAsync("Restricted Reader", email, "None", "human");
            var columnRestricted = await engine.AiReadinessScanAsync();
            var columnFinding = Assert.Single(columnRestricted.Findings, f => f.RuleId == "SEC-OLS-AWARENESS");
            Assert.Contains("1 column assignment(s)", columnFinding.Message);
            Assert.Equal(cleanVisibility, columnRestricted.Categories.Single(c => c.Category == nameof(ReadinessCategory.Visibility)).Score);
        }
    }
}

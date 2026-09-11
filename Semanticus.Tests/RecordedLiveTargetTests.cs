using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// C0.2: the recorded live double that later publish cards run against. No real endpoint.
    /// Finish line: a commit reaches the fake, and the fake can report drift after the server changed
    /// under a preview.
    /// </summary>
    [Collection("restore-root")]
    public sealed class RecordedLiveTargetTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        [Fact]
        public void Commit_reaches_the_fake()
        {
            var target = RecordedLiveTarget.ContosoDerived();
            var session = target.CloneLive();
            const string next = "SUM ( Sales[Amount] ) + 1";
            session.Model.Tables["Sales"].Measures["Total Sales"].Expression = next;
            var bim = target.WriteBim(session);
            try
            {
                var preview = target.Receive(bim, commit: false);
                Assert.False(preview.Committed);
                Assert.Equal(0, target.SaveChangesCount);
                Assert.Equal("SUM ( Sales[Amount] )",
                    target.Live.Model.Tables["Sales"].Measures["Total Sales"].Expression);

                var rep = target.Receive(bim, commit: true);
                Assert.True(rep.Committed, rep.Error);
                Assert.Equal(1, target.SaveChangesCount);
                Assert.Equal(next, target.Live.Model.Tables["Sales"].Measures["Total Sales"].Expression);
                var call = Assert.Single(target.Calls, c => c.Commit);
                Assert.True(call.Saved);
                Assert.Contains("Total Sales", call.SessionBim, System.StringComparison.Ordinal);
                Assert.Contains("+ 1", call.SessionBim, System.StringComparison.Ordinal);
            }
            finally { File.Delete(bim); }
        }

        [Fact]
        public void Fake_reports_drift_when_the_server_changed_under_preview()
        {
            var target = RecordedLiveTarget.ContosoDerived();
            var session = target.CloneLive();
            session.Model.Tables["Sales"].Measures["Total Sales"].Expression = "SUM ( Sales[Amount] ) + 1";
            var bim = target.WriteBim(session);
            try
            {
                var preview = target.Receive(bim, commit: false);
                Assert.False(preview.Committed);
                Assert.Empty(target.DriftSincePreview());

                target.ChangeUnderPreview(m => m.Tables["Sales"].Measures["Total Sales"].Expression = "99");
                Assert.Equal(1, target.ScriptedChanges);

                var drift = target.DriftSincePreview();
                Assert.Contains(drift, r => r.Contains("Total Sales"));
                Assert.Equal("99", target.Live.Model.Tables["Sales"].Measures["Total Sales"].Expression);

                var commit = target.Receive(bim, commit: true);
                Assert.True(commit.Committed, commit.Error);
                Assert.Equal("SUM ( Sales[Amount] ) + 1",
                    target.Live.Model.Tables["Sales"].Measures["Total Sales"].Expression);
            }
            finally { File.Delete(bim); }
        }

        [Fact]
        public void Fixtures_carry_the_five_table_star_and_the_Contoso_delivery_date()
        {
            var five = RecordedLiveTarget.FiveTable();
            Assert.Equal(new[] { "Date", "Product", "Customer", "Store", "Sales" },
                five.Live.Model.Tables.Cast<TOM.Table>().Select(t => t.Name).ToArray());
            Assert.Equal(4, five.Live.Model.Relationships.Count);
            Assert.All(five.Live.Model.Relationships.Cast<TOM.SingleColumnRelationship>(), r => Assert.True(r.IsActive));

            var contoso = RecordedLiveTarget.ContosoDerived();
            Assert.Equal(5, contoso.Live.Model.Tables.Count);
            Assert.NotNull(contoso.Live.Model.Tables["Sales"].Columns.Find("Order Date"));
            Assert.NotNull(contoso.Live.Model.Tables["Sales"].Columns.Find("Delivery Date"));
            var delivery = contoso.Live.Model.Relationships.Cast<TOM.SingleColumnRelationship>()
                .Single(r => r.Name == "Sales_DeliveryDate");
            Assert.False(delivery.IsActive);
            Assert.Equal("Delivery Date", delivery.FromColumn.Name);
            Assert.Equal("Date", delivery.ToColumn.Name);
        }

        [Fact]
        public void Attach_wires_the_engine_hooks_so_a_push_reaches_the_fake()
        {
            var target = RecordedLiveTarget.FiveTable();
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            target.Attach(engine);
            Assert.NotNull(engine.DeployLiveSyncHook);
            Assert.NotNull(engine.WorkspaceSnapshotHook);
            Assert.NotNull(engine.WorkspacePushHook);

            var session = target.CloneLive();
            session.Model.Tables["Sales"].Measures["Total Sales"].Expression = "1 + 1";
            var bim = target.WriteBim(session);
            try
            {
                var rep = engine.DeployLiveSyncHook(bim, RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, true, null);
                Assert.True(rep.Committed, rep.Error);
                Assert.Equal("1 + 1", target.Live.Model.Tables["Sales"].Measures["Total Sales"].Expression);
                Assert.Equal(1, target.SaveChangesCount);
            }
            finally { File.Delete(bim); }
        }

        [Fact]
        public async Task DeployLiveAsync_commit_reaches_the_attached_fake()
        {
            var target = RecordedLiveTarget.FiveTable();
            var path = target.WriteBim(target.Live);
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            try
            {
                await engine.OpenAsync(path);
                target.Attach(engine);
                await engine.SetObjectPropertyAsync("measure:Sales/Total Sales", "Expression", "1 + 1", "human");
                var preview = await engine.DeployLiveAsync(
                    RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                    "azcli", null, null, commit: false, origin: "human");
                Assert.False(preview.Committed);
                Assert.Equal(0, target.SaveChangesCount);

                var rep = await engine.DeployLiveAsync(
                    RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                    "azcli", null, null, commit: true, origin: "human", overrideReason: "recorded live double",
                    confirmToken: preview.ConfirmToken);
                Assert.True(rep.Committed, rep.Error);
                Assert.Equal("1 + 1", target.Live.Model.Tables["Sales"].Measures["Total Sales"].Expression);
                Assert.True(target.SaveChangesCount >= 1);
            }
            finally { File.Delete(path); }
        }
    }
}

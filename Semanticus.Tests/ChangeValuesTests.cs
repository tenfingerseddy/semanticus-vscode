using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Before and after values on every change (Plan-heart contract 8.3). What TOM's change event carries is rendered,
    /// capped and redacted before it leaves the dispatcher, so the broadcast, the MCP fold and the ledger file all see
    /// the same safe text. A secret is pinned per path: a data source property, a ConnectionString, and a Power Query
    /// expression holding <c>password="x"</c>, which must reach none of the three (Sol 24 Sep). Deletes arrive as
    /// "remove", and a create as "add", because TOM fires no event for an add (see the add test for what it does fire).
    /// </summary>
    public sealed class ChangeValuesTests : IAsyncLifetime
    {
        private SessionManager _sessions = null!;
        private LocalEngine _engine = null!;
        private string _dir = null!;
        private readonly List<ChangeNotification> _seen = new List<ChangeNotification>();

        public async Task InitializeAsync()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sem-values-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _sessions = new SessionManager();
            _engine = new LocalEngine(_sessions, TestEntitlements.Pro);
            await _engine.OpenAsync(LedgerFixture.Write(_dir));
            _sessions.Bus.Changed += _seen.Add;
        }

        public Task DisposeAsync()
        {
            _sessions.Bus.Changed -= _seen.Add;
            _engine.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
            return Task.CompletedTask;
        }

        private ChangeNotification Last => _seen.Last(n => n.Deltas.Length > 0);

        private static ChangeDelta Delta(ChangeNotification n, string reference, string prop) =>
            n.Deltas.Single(d => d.Ref == reference && d.Props != null && d.Props.Contains(prop));

        private EditLedgerEntry LastEntry => _sessions.Ledger.Entries(_sessions.Current.Id).Last();

        [Fact]
        public async Task A_description_carries_its_before_and_after_on_the_broadcast_and_in_the_ledger()
        {
            await _engine.SetDescriptionAsync("measure:Sales/M1", "first", "human");
            await _engine.SetDescriptionAsync("measure:Sales/M1", "second", "human");

            var d = Delta(Last, "measure:Sales/M1", "Description");
            Assert.Equal("update", d.Kind);
            Assert.Equal("first", d.Before);
            Assert.Equal("second", d.After);
            var stored = LastEntry.Deltas.Single(x => x.Ref == "measure:Sales/M1");
            Assert.Equal("first", stored.Before);
            Assert.Equal("second", stored.After);
        }

        [Fact]
        public async Task A_long_DAX_expression_is_cut_at_2000_on_the_wire_and_400_in_the_ledger()
        {
            var longDax = "1" + string.Concat(Enumerable.Repeat(" + 1", 800));   // 3,201 characters
            await _engine.SetDaxAsync("measure:Sales/M2", longDax, "agent");

            var d = Delta(Last, "measure:Sales/M2", "Expression");
            Assert.Equal("SUM(Sales[Amount]) * 2", d.Before);
            Assert.Equal(longDax.Substring(0, 2000) + "(cut)", d.After);
            var stored = LastEntry.Deltas.Single(x => x.Ref == "measure:Sales/M2" && x.Props.Contains("Expression"));
            Assert.Equal(longDax.Substring(0, 400) + "(cut)", stored.After);
        }

        [Fact]
        public async Task Every_data_source_property_and_every_ConnectionString_is_hidden()
        {
            var s = _sessions.Current;
            await s.MutateAsync("human", "add sources", m =>
            {
                var legacy = m.AddDataSource("Legacy");
                legacy.ConnectionString = "Data Source=contoso-sql;Password=first";
                var structured = m.AddStructuredDataSource("Structured");
                structured.Server = "contoso-sql";
            });
            _seen.Clear();
            await s.MutateAsync("human", "change sources", m =>
            {
                ((TabularEditor.TOMWrapper.ProviderDataSource)m.DataSources["Legacy"]).ConnectionString = "Data Source=contoso-sql;Password=second";
                ((TabularEditor.TOMWrapper.StructuredDataSource)m.DataSources["Structured"]).Server = "contoso-sql-2";
                m.DataSources["Structured"].Description = "a plain description, still hidden";
            });

            var n = Last;
            Assert.NotEmpty(n.Deltas);
            foreach (var d in n.Deltas)
            {
                Assert.Equal("(hidden)", d.Before ?? "(hidden)");
                Assert.Equal("(hidden)", d.After);
            }
            var wire = System.Text.Json.JsonSerializer.Serialize(n);
            Assert.DoesNotContain("Password=", wire);
            Assert.DoesNotContain("contoso-sql-2", wire);
            Assert.DoesNotContain("still hidden", wire);
            Assert.DoesNotContain("Password=", System.Text.Json.JsonSerializer.Serialize(LastEntry));
        }

        [Fact]
        public async Task A_password_inside_Power_Query_never_reaches_the_broadcast_the_fold_or_the_ledger_file()
        {
            const string secret = "password=\"x\"";
            var m = "let Source = Sql.Database(\"contoso-sql\", \"Sales\", [" + secret + "]) in Source";
            _sessions.Changes.Register("reader-under-test");
            await _engine.SetPartitionMAsync("partition:Sales/Sales", m, "human");
            await _engine.UpdateNamedExpressionAsync("Server", "\"x\" meta [" + secret + "]", "human");

            var partition = _seen.SelectMany(n => n.Deltas).Single(d => d.Ref == "partition:Sales/Sales" && d.Props.Contains("Expression"));
            Assert.Equal("(Power Query changed, " + m.Length + " characters)", partition.After);
            Assert.StartsWith("(Power Query changed, ", partition.Before);
            foreach (var n in _seen)
                Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(n));

            var fold = _sessions.Changes.Take("reader-under-test", _sessions.Current.Id);
            Assert.NotNull(fold);
            Assert.DoesNotContain(secret, string.Join(" ", fold.Items));

            await _sessions.Ledger.FlushAsync();
            var file = Path.Combine(_dir, ".semanticus", "history.json");
            Assert.True(File.Exists(file), "the ledger was not written to " + file);
            var text = File.ReadAllText(file);
            Assert.Contains("partition:Sales/Sales", text);
            Assert.DoesNotContain("password=", text);
        }

        [Fact]
        public async Task A_delete_is_a_remove_with_no_after()
        {
            await _engine.DeleteObjectAsync("measure:Sales/M3", "human");
            var remove = Last.Deltas.Single(d => d.Kind == "remove");
            Assert.Equal("measure:Sales/M3", remove.Ref);
            Assert.Null(remove.After);
        }

        [Fact]
        public async Task Create_measure_is_an_add_with_no_before()
        {
            // What TOM fires for an add, measured: no ObjectChanged for the new measure itself, only the property
            // writes the create makes afterwards, if any. The add is read from the undo action the commit pushed.
            await _engine.CreateMeasureAsync("table:Sales", "Fresh", "1", "agent");
            var n = Last;
            var add = n.Deltas.First();
            Assert.Equal("add", add.Kind);
            Assert.Equal("measure:Sales/Fresh", add.Ref);
            Assert.Null(add.Before);
            Assert.Single(n.Deltas, d => d.Kind == "add");
        }

        [Fact]
        public async Task A_rehearsal_reports_before_and_after_too()
        {
            var s = _sessions.Current;
            var dry = new DryRunCollector();
            DryRunScope.Current = dry;
            try { await s.MutateAsync("agent", "rehearse", m => m.AllMeasures.First(x => x.Name == "M4").Description = "rehearsed"); }
            finally { DryRunScope.Current = null; }
            var d = dry.Deltas.Single(x => x.Ref == "measure:Sales/M4");
            Assert.True(string.IsNullOrEmpty(d.Before));
            Assert.Equal("rehearsed", d.After);
        }
    }
}

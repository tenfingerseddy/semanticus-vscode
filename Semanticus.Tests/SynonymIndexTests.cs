using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Linguistics;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The parse-once synonym reader (<see cref="SynonymIndex"/>) answers exactly what the donor's per-object reader
    /// (<c>SynonymHelper.GetSynonyms</c>) answers, for every object and every schema shape, including the shapes the
    /// donor stops reading at; and the bulk writer (<see cref="LsdlSynonyms.SetSynonymsMany"/>) writes what the same
    /// single writes would write, with one change to the schema. The readiness scores ride on the reader, so a
    /// difference here would be a wrong number on the Readiness page.
    /// </summary>
    public sealed class SynonymIndexTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("SynonymIndex", 1604);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddDataColumn("Amount");
                sales.AddDataColumn("Region");
                sales.AddMeasure("Total Sales", "SUM(Sales[Amount])");
                var date = m.AddTable("Date");
                var year = date.AddDataColumn("Year");
                var month = date.AddDataColumn("Month");
                date.AddHierarchy("Calendar", null, year, month);
                m.AddTable("Hidden Stuff").IsHidden = true;
                m.Tables["Hidden Stuff"].AddDataColumn("Code");
                m.AddTranslation("en-US");
            });
            return (engine, sessions);
        }

        private static string Lsdl(params string[] entities) =>
            "{\"Version\":\"1.0.0\",\"Language\":\"en-US\",\"Entities\":{" + string.Join(",", entities) + "}}";

        private static string Terms(params string[] words) =>
            "[" + string.Join(",", words.Select(w => "{\"" + w + "\":{}}")) + "]";

        private const string SalesTable = "\"sales\":{\"Binding\":{\"ConceptualEntity\":\"Sales\"},\"Terms\":[{\"revenue\":{}},{\"old\":{\"State\":\"Deleted\"}},{\"maybe\":{\"State\":\"Suggested\"}}]}";
        private const string RegionWrapped = "\"sales.region\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Sales\",\"ConceptualProperty\":\"Region\"}},\"Terms\":[{\"area\":{\"State\":\"Generated\"}},{\"territory\":{\"Weight\":0.9}}]}";
        private const string TotalSales = "\"sales.total\":{\"Binding\":{\"ConceptualEntity\":\"Sales\",\"ConceptualProperty\":\"Total Sales\"},\"Terms\":[{\"turnover\":{}}]}";
        private const string Calendar = "\"date.calendar\":{\"Binding\":{\"ConceptualEntity\":\"Date\",\"Hierarchy\":\"Calendar\"},\"Terms\":[{\"time\":{}}]}";
        private const string MonthLevel = "\"date.calendar.month\":{\"Binding\":{\"ConceptualEntity\":\"Date\",\"Hierarchy\":\"Calendar\",\"HierarchyLevel\":\"Month\"},\"Terms\":[{\"period\":{}}]}";

        public static IEnumerable<object[]> Shapes() => new[]
        {
            new object[] { "ordinary entities, both binding shapes, every object kind", Lsdl(SalesTable, RegionWrapped, TotalSales, Calendar, MonthLevel) },
            new object[] { "the first matching entity wins", Lsdl(TotalSales, "\"dup\":{\"Binding\":{\"ConceptualEntity\":\"Sales\",\"ConceptualProperty\":\"Total Sales\"},\"Terms\":[{\"second\":{}}]}", SalesTable) },
            new object[] { "an entity that is not an object stops the walk", Lsdl(SalesTable, "\"broken\":42", TotalSales, Calendar) },
            new object[] { "a Definition that is not an object stops the walk", Lsdl(RegionWrapped, "\"odd\":{\"Definition\":\"text\"}", SalesTable) },
            new object[] { "a Binding that is not an object falls back to Definition", Lsdl("\"x\":{\"Binding\":\"no\",\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Sales\"}},\"Terms\":" + Terms("shop") + "}", TotalSales) },
            new object[] { "terms that cannot be read read as none", Lsdl("\"sales\":{\"Binding\":{\"ConceptualEntity\":\"Sales\"},\"Terms\":{\"a\":1}}", "\"t\":{\"Binding\":{\"ConceptualEntity\":\"Sales\",\"ConceptualProperty\":\"Amount\"},\"Terms\":[{\"cash\":3}]}", TotalSales) },
            new object[] { "an entity without terms reads as none", Lsdl("\"sales\":{\"Binding\":{\"ConceptualEntity\":\"Sales\"}}", Calendar) },
            new object[] { "a binding with other keys matches nothing", Lsdl("\"y\":{\"Binding\":{\"ConceptualEntity\":\"Sales\",\"Variation\":\"v\"},\"Terms\":" + Terms("nope") + "}", MonthLevel) },
            new object[] { "names match exactly", Lsdl("\"s\":{\"Binding\":{\"ConceptualEntity\":\"sales\"},\"Terms\":" + Terms("lower") + "}", TotalSales) },
            new object[] { "no Entities", "{\"Version\":\"1.0.0\",\"Language\":\"en-US\"}" },
            new object[] { "an empty schema", Lsdl() },
        };

        [Theory]
        [MemberData(nameof(Shapes))]
        public async Task The_index_reads_every_object_as_the_donor_reader_does(string shape, string content)
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await sessions.Require().MutateAsync("human", "inject", m => m.Cultures["en-US"].Content = content);
                await sessions.Require().ReadAsync(m =>
                {
                    var culture = m.Cultures["en-US"];
                    Assert.Equal(content, culture.Content);   // the shape reached the model as written
                    var objects = m.Tables.Cast<TabularNamedObject>()
                        .Concat(m.AllColumns).Concat(m.AllMeasures).Concat(m.AllHierarchies).Concat(m.AllLevels).ToList();
                    var parses = SynonymIndex.Parses;
                    foreach (var o in objects)
                        Assert.True(SynonymHelper.GetSynonyms(o, culture) == SynonymIndex.Terms(o, culture),
                            $"{shape}: {o.GetType().Name} '{o.Name}' read '{SynonymIndex.Terms(o, culture)}', the donor '{SynonymHelper.GetSynonyms(o, culture)}'");
                    Assert.InRange(SynonymIndex.Parses - parses, 0, 1);   // one parse for every object (none when cached)
                    return true;
                });
            }
        }

        [Fact]
        public async Task The_index_reads_the_words_a_write_just_made()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.CreateLinguisticSchemaAsync("en-US", "human");
                Assert.Null(await sessions.Require().ReadAsync(m => SynonymIndex.Terms(m.Tables["Sales"].Columns["Region"], m.Cultures["en-US"])));
                await engine.SetSynonymsAsync("column:Sales/Region", new[] { "area" }, "en-US", "agent");
                Assert.Contains("area", await sessions.Require().ReadAsync(m => SynonymIndex.Terms(m.Tables["Sales"].Columns["Region"], m.Cultures["en-US"])));
                await engine.UndoAsync("human");
                Assert.DoesNotContain("area", await sessions.Require().ReadAsync(m => SynonymIndex.Terms(m.Tables["Sales"].Columns["Region"], m.Cultures["en-US"]) ?? ""));
            }
        }

        internal static int ContentChanges(Model m)
        {
            var handler = (TabularModelHandler)typeof(TabularObject)
                .GetField("Handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(m);
            var stack = (System.Collections.IEnumerable)typeof(TabularEditor.TOMWrapper.Undo.UndoManager)
                .GetField("_UndoStack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(handler.UndoManager);
            // The undo actions are internal to the wrapper, so their names are read by reflection.
            return stack.Cast<object>().Count(a => (a.GetType().GetProperty("ActionName")?.GetValue(a) as string) == "Set Property 'Content'");
        }

        [Fact]
        public async Task Many_writes_in_one_call_leave_what_the_single_writes_leave_with_one_change_to_the_schema()
        {
            const string seed = "{\"Version\":\"1.0.0\",\"Language\":\"en-US\",\"Entities\":{"
                + "\"sales\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Sales\"}},\"State\":\"Generated\",\"Terms\":[{\"sales\":{\"State\":\"Generated\"}}]},"
                + "\"hidden_stuff\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Hidden Stuff\"}},\"State\":\"Generated\",\"Terms\":[]}}}";
            var writes = new (string Ref, string Words)[]
            {
                ("table:Sales", "shop, revenue"), ("column:Sales/Region", "area, territory"),
                ("measure:Sales/Total Sales", "turnover"), ("column:Sales/Region", "area, zone"), ("table:Date", "calendar"),
            };

            // One at a time, each its own commit.
            var (one, oneSessions) = await ModelAsync();
            string single;
            using (one)
            {
                await oneSessions.Require().MutateAsync("human", "inject", m => m.Cultures["en-US"].Content = seed);
                foreach (var (r, words) in writes) await one.SetSynonymsAsync(r, words.Split(", "), "en-US", "agent");
                single = await oneSessions.Require().ReadAsync(m => m.Cultures["en-US"].Content);
            }

            // All at once, in one commit, with a hidden object among them.
            var (many, manySessions) = await ModelAsync();
            using (many)
            {
                var s = manySessions.Require();
                await s.MutateAsync("human", "inject", m => m.Cultures["en-US"].Content = seed);
                var changesBefore = await s.ReadAsync(ContentChanges);
                LsdlSynonyms.SetManyOutcome outcome = null;
                await s.MutateAsync("agent", "write words", m =>
                {
                    var items = writes.Select(w => new LsdlSynonyms.SynonymWrite { Object = (TabularNamedObject)ObjectRefs.Resolve(m, w.Ref), Synonyms = w.Words }).ToList();
                    items.Insert(2, new LsdlSynonyms.SynonymWrite { Object = m.Tables["Hidden Stuff"].Columns["Code"], Synonyms = "secret" });
                    outcome = LsdlSynonyms.SetSynonymsMany(m.Cultures["en-US"], items);
                });
                Assert.Equal(writes.Length, outcome.Written);
                Assert.Equal(new[] { 2 }, outcome.Refused.Keys.ToArray());
                Assert.Contains("hidden", outcome.Refused[2]);
                Assert.Equal(1, outcome.PrunedEntities);   // the hidden table's entity, pruned once for the whole call
                Assert.Equal(single, await s.ReadAsync(m => m.Cultures["en-US"].Content));
                Assert.Equal(changesBefore + 1, await s.ReadAsync(ContentChanges));   // one copy of the schema on the undo stack

                await many.UndoAsync("human");   // and one undo takes every word back
                Assert.Equal(seed, await s.ReadAsync(m => m.Cultures["en-US"].Content));
            }
        }
    }
}

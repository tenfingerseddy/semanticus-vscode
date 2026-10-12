using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class VerifierSealOnceTests : GateFixtureBase
    {
        public VerifierSealOnceTests(RestoreRootFixture fixture) : base(fixture) { }
        private static string Db(Model m) => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase);
        private static string Schema(Model m) => m.Cultures.Select(c => c.Content).FirstOrDefault(c => !string.IsNullOrEmpty(c));

        private static async Task<JsonElement> Call(LocalEngine engine, string method, object[] items)
        {
            var target = typeof(IEngine).GetMethod(method);
            var typed = JsonSerializer.Deserialize(JsonSerializer.Serialize(items), target.GetParameters()[0].ParameterType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var task = (Task)target.Invoke(engine, new[] { typed, "human" });
            await task;
            return JsonSerializer.SerializeToElement(task.GetType().GetProperty("Result").GetValue(task));
        }

        private async Task<(LocalEngine, SessionManager)> Fixture(string tag, bool schema)
        {
            var (engine, sessions, _) = await OpenAsync(tag);
            await sessions.Current.MutateAsync("human", "fixture", m => { for (var i = 0; i < 40; i++) m.Tables["Sales"].AddMeasure("W " + i, "1"); });
            if (schema)
                await engine.WriteWordsAsync(Enumerable.Range(0, 40).Select(i => new WordsWriteItem { ObjRef = "measure:Sales/W " + i, Synonyms = new[] { "first " + i, "word " + i } }).ToArray(), "human");
            return (engine, sessions);
        }

        private static async Task ExactUndoRedo(LocalEngine engine, SessionManager sessions, string method, object[] items, int[] bad)
        {
            var before = await sessions.Current.ReadAsync(Db);
            var r = await Call(engine, method, items);
            Assert.Equal(bad, r.GetProperty("Rejected").EnumerateArray().Select(x => x.GetProperty("Index").GetInt32()).OrderBy(x => x).ToArray());
            var after = await sessions.Current.ReadAsync(Db);
            Assert.NotEqual(before, after);
            await engine.UndoAsync("human");
            Assert.Equal(before, await sessions.Current.ReadAsync(Db));
            await engine.RedoAsync("human");
            Assert.Equal(after, await sessions.Current.ReadAsync(Db));
            await engine.UndoAsync("human");
            Assert.Equal(before, await sessions.Current.ReadAsync(Db));
            await engine.RedoAsync("human");
            Assert.Equal(after, await sessions.Current.ReadAsync(Db));
        }

        [Fact]
        public async Task V_write_words_with_rejects_in_the_middle_is_exact_and_rejected_synonyms_are_not_written()
        {
            var (engine, sessions) = await Fixture("v-ww", true);
            var (twin, twinSessions) = await Fixture("v-ww-twin", true);
            using (engine) using (twin)
            {
                object Good(int i) => new { objRef = "measure:Sales/W " + i, description = "d " + i, synonyms = new[] { "good " + i } };
                var items = Enumerable.Range(0, 30).Select(Good).ToList();
                items[5] = new { objRef = "measure:Sales/Missing", description = "x", synonyms = new[] { "ghost" } };
                // Writes its synonyms slot and its description, then throws on the folder: must leave nothing.
                items[12] = new { objRef = "table:Sales", description = "partial", synonyms = new[] { "partialsyn" }, displayFolder = "F" };
                items[20] = new { objRef = "measure:Sales/W 20", description = "d 20", synonyms = new[] { "good 20" }, displayFolder = "ok" };
                items[21] = new { objRef = "table:Sales", synonyms = new[] { "partialsyn2" }, displayFolder = "F" };
                await ExactUndoRedo(engine, sessions, "WriteWordsAsync", items.ToArray(), new[] { 5, 12, 21 });
                Assert.DoesNotContain("partialsyn", await sessions.Current.ReadAsync(Schema));
                Assert.DoesNotContain("ghost", await sessions.Current.ReadAsync(Schema));
                Assert.NotEqual("partial", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Description));
                var twinItems = items.Where((_, i) => i != 5 && i != 12 && i != 21).ToArray();
                await Call(twin, "WriteWordsAsync", twinItems);
                Assert.Equal(await twinSessions.Current.ReadAsync(Schema), await sessions.Current.ReadAsync(Schema));
            }
        }

        [Fact]
        public async Task V_edit_objects_with_rejects_is_exact()
        {
            var (engine, sessions) = await Fixture("v-eo", false);
            using (engine)
            {
                var items = Enumerable.Range(0, 30).Select(i => (object)new { objRef = "measure:Sales/W " + i, set = new { name = "N " + i, description = "e " + i } }).ToArray();
                items[0] = new { objRef = "measure:Sales/W 0", set = new { description = "rb", summarizeBy = "Sum" } };
                items[14] = new { objRef = "measure:Sales/W 14", set = new { name = "half", summarizeBy = "Sum" } };
                items[29] = new { objRef = "measure:Sales/Nope", set = new { description = "x" } };
                await ExactUndoRedo(engine, sessions, "EditObjectsAsync", items, new[] { 0, 14, 29 });
            }
        }

        [Fact]
        public async Task V_create_measures_with_rejects_is_exact()
        {
            var (engine, sessions) = await Fixture("v-cm", false);
            using (engine)
            {
                var items = Enumerable.Range(0, 20).Select(i => (object)new { tableRef = "table:Sales", name = "C " + i, expression = "1+" + i }).ToArray();
                items[3] = new { tableRef = "table:Nope", name = "C x", expression = "1" };
                items[9] = new { tableRef = "measure:Sales/W 1", name = "C y", expression = "1" };
                var r = await Call(engine, "CreateMeasuresAsync", new object[] { items[3] });
                await ExactUndoRedo(engine, sessions, "CreateMeasuresAsync", items, new[] { 3, 9 });
                var made = await Call(engine, "CreateMeasuresAsync", new object[] { new { tableRef = "table:Sales", name = "After", expression = "2" } });
                Assert.Equal(1, made.GetProperty("Applied").GetInt32());
            }
        }

        [Fact]
        public async Task V_waive_findings_with_rejects_is_exact()
        {
            var (engine, sessions) = await Fixture("v-wf", false);
            using (engine)
            {
                var items = Enumerable.Range(0, 12).Select(i => (object)new { system = "bpa", ruleId = "RULE_" + i, objRef = "measure:Sales/W " + i, reason = "ok " + i }).ToArray();
                items[4] = new { system = "bpa", ruleId = "RULE_4", objRef = "measure:Sales/W 4", reason = "" };
                items[7] = new { system = "nope", ruleId = "RULE_7", objRef = "measure:Sales/W 7", reason = "x" };
                await ExactUndoRedo(engine, sessions, "WaiveFindingsAsync", items, new[] { 4, 7 });
            }
        }

        [Fact]
        public async Task V_thirty_schema_writes_walk_exactly_and_survive_save_and_reopen()
        {
            var (engine, sessions) = await Fixture("v-30", true);
            using (engine)
            {
                var texts = new List<string> { await sessions.Current.ReadAsync(Schema) };
                for (var i = 0; i < 30; i++)
                {
                    await engine.SetSynonymsAsync("measure:Sales/W " + i, new[] { "walk " + i, "ünï " + i }, null, "agent");
                    texts.Add(await sessions.Current.ReadAsync(Schema));
                }
                Assert.Equal(31, texts.Distinct().Count());
                var held = await sessions.Current.ReadAsync(SchemaUndo.Held);
                Assert.Equal(0, held.FullSteps);
                for (var i = 29; i >= 0; i--) { await engine.UndoAsync("human"); Assert.Equal(texts[i], await sessions.Current.ReadAsync(Schema)); }
                for (var i = 1; i <= 30; i++) { await engine.RedoAsync("human"); Assert.Equal(texts[i], await sessions.Current.ReadAsync(Schema)); }
                for (var i = 0; i < 12; i++) await engine.UndoAsync("human");
                var expect = texts[18];
                Assert.Equal(expect, await sessions.Current.ReadAsync(Schema));
                var path = Path.Combine(Root, "v30-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".bim");
                await engine.SaveAsync(path, "bim", false, "human");
                var sessions2 = new SessionManager();
                using var engine2 = new LocalEngine(sessions2, TestEntitlements.Pro);
                await engine2.OpenAsync(path);
                static string N(string j) => JsonSerializer.Serialize(JsonDocument.Parse(j).RootElement);
                Assert.Equal(N(expect), N(await sessions2.Current.ReadAsync(Schema)));
                Assert.NotEqual(N(texts[19]), N(await sessions2.Current.ReadAsync(Schema)));
                // After the save, undo and redo still work on the first engine.
                await engine.RedoAsync("human");
                Assert.Equal(texts[19], await sessions.Current.ReadAsync(Schema));
            }
        }

        [Fact]
        public async Task V_bulk_synonym_call_packs_once_and_snapshots_once()
        {
            var (engine, sessions) = await Fixture("v-once", true);
            using (engine)
            {
                var held = await sessions.Current.ReadAsync(SchemaUndo.Held);
                var captures = await sessions.Current.ReadAsync(m => ModelSeal.For(m).Captures);
                var items = Enumerable.Range(0, 40).Select(i => (object)new { objRef = "measure:Sales/W " + i, synonyms = new[] { "second " + i } }).ToArray();
                items[10] = new { objRef = "measure:Sales/Nope", synonyms = new[] { "x" } };
                items[30] = new { objRef = "measure:Sales/Nope2", synonyms = new[] { "y" } };
                var r = await Call(engine, "WriteWordsAsync", items);
                var after = await sessions.Current.ReadAsync(SchemaUndo.Held);
                var caps = await sessions.Current.ReadAsync(m => ModelSeal.For(m).Captures) - captures;
                Assert.Equal(38, r.GetProperty("Applied").GetInt32());
                Assert.Equal(1, after.PackedSteps - held.PackedSteps);
                Assert.Equal(0, after.FullSteps);
                Assert.Equal(2 + 2, caps);
            }
        }
    }
}

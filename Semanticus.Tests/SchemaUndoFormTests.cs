using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Undo;
using TabularEditor.TOMWrapper.Utils;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>A synonym write kept a full copy of the linguistic schema on the undo stack (807 KB a write on SpaceParts). The
    /// step now holds its two texts packed. Undo must still put back exactly the text it found, whatever wrote the schema since.</summary>
    [Collection("restore-root")]
    public sealed class SchemaUndoFormTests : GateFixtureBase
    {
        public SchemaUndoFormTests(RestoreRootFixture fixture) : base(fixture) { }

        private static string Schema(Model m) => m.Cultures.Select(c => c.Content).FirstOrDefault(c => !string.IsNullOrEmpty(c));

        private static Microsoft.AnalysisServices.Tabular.LinguisticMetadata Tom(Model m) =>
            m.Database.TOMDatabase.Model.Cultures.First(c => c.LinguisticMetadata != null).LinguisticMetadata;

        // Read straight off TE2's stack, so the test does not lean on the code it checks.
        private static (int Steps, long Chars) FullSchemaSteps(Model m)
        {
            var handler = (TabularModelHandler)typeof(TabularObject).GetField("Handler", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(m);
            var stack = (IEnumerable)typeof(UndoManager).GetField("_UndoStack", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handler.UndoManager);
            var steps = 0; long chars = 0;
            foreach (var a in stack)
            {
                if (a.GetType().Name != "UndoPropertyChangedAction") continue;
                var t = a.GetType();
                if (t.GetField("tabularObject", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(a) is not Culture) continue;
                if ((t.GetField("prop", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(a) as PropertyInfo)?.Name != "Content") continue;
                steps++;
                chars += (t.GetField("oldValue", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(a) as string)?.Length ?? 0;
                chars += (t.GetField("newValue", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(a) as string)?.Length ?? 0;
            }
            return (steps, chars);
        }

        private async Task<(LocalEngine engine, SessionManager sessions)> OpenWithSchemaAsync(string tag)
        {
            var (engine, sessions, _) = await OpenAsync(tag);
            await sessions.Current.MutateAsync("human", "fixture", m =>
            {
                for (var i = 0; i < 150; i++) m.Tables["Sales"].AddMeasure("Word " + i, "1");
            });
            await engine.WriteWordsAsync(Enumerable.Range(0, 150).Select(i => new WordsWriteItem
            {
                ObjRef = "measure:Sales/Word " + i,
                Synonyms = new[] { "sales word " + i, "revenue figure " + i, "turnover amount " + i },
            }).ToArray(), "human");
            return (engine, sessions);
        }

        [Fact]
        public async Task A_synonym_write_keeps_its_schema_packed_on_the_undo_stack()
        {
            var (engine, sessions) = await OpenWithSchemaAsync("schema-packed");
            using (engine)
            {
                var size = await sessions.Current.ReadAsync(m => Schema(m).Length);
                Assert.True(size > 20_000, "the fixture schema is too small to show anything: " + size);
                for (var i = 0; i < 5; i++)
                    await engine.SetSynonymsAsync("measure:Sales/Word " + i, new[] { "single write " + i }, null, "agent");
                var full = await sessions.Current.ReadAsync(FullSchemaSteps);
                Assert.Equal(0, full.Steps);
                var held = await sessions.Current.ReadAsync(SchemaUndo.Held);
                Assert.Equal(0, held.FullSteps);
                Assert.True(held.PackedSteps >= 6, "packed steps: " + held.PackedSteps);   // the bulk call's steps and the five single writes
                // Packed, and shared between a step's old text and the step before's new one, the steps hold a small part of their texts.
                Assert.True(held.PackedBytes * 5 < held.TextBytes, $"packed {held.PackedBytes} bytes for {held.TextBytes} bytes of text");
            }
        }

        [Fact]
        public async Task Undo_and_redo_of_packed_schema_steps_restore_every_text_exactly()
        {
            var (engine, sessions) = await OpenWithSchemaAsync("schema-walk");
            using (engine)
            {
                var texts = new List<string> { await sessions.Current.ReadAsync(Schema) };
                for (var i = 0; i < 4; i++)
                {
                    await engine.SetSynonymsAsync("measure:Sales/Word " + (10 + i), new[] { "walk " + i, "näive ünïcode " + i }, null, "agent");
                    texts.Add(await sessions.Current.ReadAsync(Schema));
                }
                Assert.Equal(5, texts.Distinct().Count());
                for (var i = texts.Count - 2; i >= 0; i--)
                {
                    await engine.UndoAsync("human");
                    Assert.Equal(texts[i], await sessions.Current.ReadAsync(Schema));
                }
                for (var i = 1; i < texts.Count; i++)
                {
                    await engine.RedoAsync("human");
                    Assert.Equal(texts[i], await sessions.Current.ReadAsync(Schema));
                }
            }
        }

        [Fact]
        public async Task A_schema_write_outside_undo_never_makes_undo_restore_the_wrong_text()
        {
            var (engine, sessions) = await OpenWithSchemaAsync("schema-outside");
            using (engine)
            {
                var found = await sessions.Current.ReadAsync(Schema);
                await engine.SetSynonymsAsync("measure:Sales/Word 3", new[] { "first write" }, null, "agent");
                var written = await sessions.Current.ReadAsync(Schema);
                // Something writes the schema straight into TOM, with no undo step: changed text, then no schema at all.
                await sessions.Current.ReadAsync(m => { Tom(m).Content = written.Replace("first write", "changed outside"); return true; });
                await engine.UndoAsync("human");
                Assert.Equal(found, await sessions.Current.ReadAsync(Schema));
                await sessions.Current.ReadAsync(m => { Tom(m).Content = "{\"Version\":\"1.0.0\",\"Language\":\"en-US\"}"; return true; });
                await engine.RedoAsync("human");
                Assert.Equal(written, await sessions.Current.ReadAsync(Schema));
                await sessions.Current.ReadAsync(m => { m.Database.TOMDatabase.Model.Cultures.First(c => c.LinguisticMetadata != null).LinguisticMetadata = null; return true; });
                await engine.UndoAsync("human");
                Assert.Equal(found, await sessions.Current.ReadAsync(Schema));
                await engine.RedoAsync("human");
                Assert.Equal(written, await sessions.Current.ReadAsync(Schema));
            }
        }

        // TOM keeps the text on the culture's LinguisticMetadata, TE2's step is on the wrapper's Content: the seal did not match the
        // two and kept its own full copy of both texts in every write's step (the 718 KB a write the packing alone did not remove).
        [Fact]
        public async Task The_undo_seal_keeps_no_second_copy_of_a_synonym_write()
        {
            var (engine, sessions) = await OpenWithSchemaAsync("schema-seal");
            using (engine)
            {
                await sessions.Current.ReadAsync(m => { ModelSeal.For(m).Clear(); ModelSeal.For(m).Detect = true; return 0; });
                await engine.SetSynonymsAsync("measure:Sales/Word 4", new[] { "sealed once" }, null, "agent");
                var kept = await sessions.Current.ReadAsync(m => { ModelSeal.For(m).Detect = false; return ModelSeal.For(m).Sealed; });
                Assert.DoesNotContain(kept, k => k.Contains("Content"));
            }
        }

        [Fact]
        public async Task A_schema_write_outside_undo_inside_the_same_edit_is_still_taken_back()
        {
            var (engine, sessions) = await OpenWithSchemaAsync("schema-inside");
            using (engine)
            {
                var found = await sessions.Current.ReadAsync(Schema);
                await sessions.Current.MutateAsync("agent", "mixed", m =>
                {
                    Tom(m).Content = found.Replace("sales word 7", "outside write");
                    m.Cultures.First(c => !string.IsNullOrEmpty(c.Content)).Content = found.Replace("sales word 7", "tracked write");
                });
                var written = await sessions.Current.ReadAsync(Schema);
                Assert.Contains("tracked write", written);
                await engine.UndoAsync("human");
                Assert.Equal(found, await sessions.Current.ReadAsync(Schema));
                await engine.RedoAsync("human");
                Assert.Equal(written, await sessions.Current.ReadAsync(Schema));
            }
        }

        [Fact]
        public void Packing_gives_back_every_string_exactly()
        {
            var lone = new string(new[] { 'a', (char)0xD800, 'b', (char)0xDFFF, (char)0 });
            foreach (var text in new[] { null, "", "{}", lone, new string('x', 100_000) + "ünïcode" + lone })
                Assert.Equal(text, SchemaUndo.UnpackText(SchemaUndo.PackText(text)));
        }
    }
}

// m11/drafts-sol, Sol's review of the shared draft store (join 10). The two rules, proven against the real engine:
//   R-A  a write a draft produces carries the model identity it was typed for (and, for a batch, the value each object held when it was typed over),
//        and the ENGINE refuses it when either moved. No client check stands in for this one.
//   R-B  is a client rule (the webview tests hold it).
// F1  a documentation write typed for model A is refused while model B is open, and B keeps its section.
// F2  a Desktop identity names the instance (endpoint and database), not only the window stem, so two Sales.pbix are two models.
// F3  a multi-select batch compares EVERY object's own starting value inside the one write, even when the objects differed.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DraftsSolFenceTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-dsol-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        public DraftsSolFenceTests() => Directory.CreateDirectory(_root);
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        private string Copy(string name)
        {
            var p = Path.Combine(_root, name);
            File.Copy(TestModels.FindBim(), p);
            return p;
        }

        // ---- F1 ---------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task F1_a_documentation_write_typed_for_model_A_is_refused_while_model_B_is_open_and_B_keeps_its_section()
        {
            using var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.OpenAsync(Copy("A.bim"), discardUnsaved: true);
            var identityOfA = (await e.SessionInfoAsync()).ModelIdentity;
            await e.OpenAsync(Copy("B.bim"), discardUnsaved: true);   // the person opened B: A's editor now unmounts and writes on its way out
            Assert.NotEqual(identityOfA, (await e.SessionInfoAsync()).ModelIdentity);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                e.SetDocSectionAsync("model", "overview", "Words typed for A", "human", "", identityOfA));

            Assert.Contains("typed for another model", ex.Message);
            Assert.Equal("", await e.GetDocSectionAsync("model", "overview") ?? "");
        }

        [Fact]
        public async Task F1_a_documentation_read_for_another_model_is_refused_too_and_the_matching_model_still_writes()
        {
            using var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.OpenAsync(Copy("A.bim"), discardUnsaved: true);
            var identityOfA = (await e.SessionInfoAsync()).ModelIdentity;
            var ok = await e.SetDocSectionAsync("model", "overview", "Words typed for A", "human", "", identityOfA);
            Assert.True(ok.Changed);
            Assert.Equal("Words typed for A", await e.GetDocSectionAsync("model", "overview", identityOfA));

            await e.OpenAsync(Copy("B.bim"), discardUnsaved: true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.GetDocSectionAsync("model", "overview", identityOfA));
        }

        [Fact]
        public async Task F1_no_identity_means_no_fence_so_the_assistants_own_write_is_unchanged()
        {
            using var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.OpenAsync(Copy("A.bim"), discardUnsaved: true);
            var r = await e.SetDocSectionAsync("model", "overview", "Assistant words", "agent");
            Assert.True(r.Changed);
        }

        // ---- F2 ---------------------------------------------------------------------------------------------------------

        [Fact]
        public void F2_two_Desktop_files_with_one_window_stem_are_two_identities_and_a_restart_of_the_same_instance_is_one()
        {
            string Desk(string stem, string port, string db) => LocalEngine.ModelIdentityOf(new LiveOrigin("localhost:" + port, db, null, localName: stem), null, null, "k");
            // West/Sales.pbix and East/Sales.pbix: one stem, two Desktop instances.
            Assert.NotEqual(Desk("Sales", "51234", "guid-west"), Desk("Sales", "60321", "guid-east"));
            // The same instance (a tab switch, an engine restart while Desktop stays open): one identity, whatever the spelling.
            Assert.Equal(Desk("Sales", "51234", "guid-west"), Desk("SALES", "51234", "GUID-WEST"));
            // A snapshot marker of a Desktop model names the same instance.
            var marker = new LiveModelExport.SnapshotOrigin { Endpoint = "localhost:51234", Database = "guid-west", LocalName = "Sales" };
            Assert.Equal(Desk("Sales", "51234", "guid-west"), LocalEngine.ModelIdentityOf(null, marker, "/b", "k"));
            Assert.StartsWith("desktop:sales", Desk("Sales", "51234", "guid-west"));
        }

        // ---- F3 ---------------------------------------------------------------------------------------------------------

        private static async Task<LocalEngine> TwoMeasuresAsync()
        {
            var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.CreateModelAsync("DraftsSol", 1606);
            await e.CreateTableAsync("T", "agent");
            await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            await e.SetObjectPropertyAsync("measure:T/A", "Description", "First words", "agent");
            await e.SetObjectPropertyAsync("measure:T/B", "Description", "Second words", "agent");
            return e;
        }

        private static async Task<string> Desc(LocalEngine e, string name) => (await e.ListMeasuresAsync()).First(m => m.Name == name).Description ?? "";

        [Fact]
        public async Task F3_a_batch_over_objects_that_differed_refuses_when_ONE_of_them_moved_and_writes_nothing()
        {
            using var e = await TwoMeasuresAsync();
            var refs = new[] { "measure:T/A", "measure:T/B" };
            await e.SetObjectPropertyAsync("measure:T/B", "Description", "The assistant rewrote this one", "agent");   // after the person typed, before Apply

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                e.SetObjectPropertiesAsync(refs, "Description", "Common words", "human", null, new[] { "First words", "Second words" }));

            Assert.Contains("changed in the model after you opened it", ex.Message);
            Assert.Equal("First words", await Desc(e, "A"));   // all or nothing
            Assert.Equal("The assistant rewrote this one", await Desc(e, "B"));
        }

        [Fact]
        public async Task F3_a_batch_over_objects_that_did_not_move_writes_every_one()
        {
            using var e = await TwoMeasuresAsync();
            var r = await e.SetObjectPropertiesAsync(new[] { "measure:T/A", "measure:T/B" }, "Description", "Common words", "human", null, new[] { "First words", "Second words" });
            Assert.True(r.Changed);
            Assert.Equal("Common words", await Desc(e, "A"));
            Assert.Equal("Common words", await Desc(e, "B"));
        }

        [Fact]
        public async Task F3_per_object_starting_values_that_do_not_match_the_object_count_are_refused_not_skipped()
        {
            using var e = await TwoMeasuresAsync();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                e.SetObjectPropertiesAsync(new[] { "measure:T/A", "measure:T/B" }, "Description", "Common words", "human", null, new[] { "First words" }));
            Assert.Contains("one starting value for each object", ex.Message);
            Assert.Equal("First words", await Desc(e, "A"));
        }

        [Fact]
        public async Task R_A_a_property_write_typed_for_another_model_is_refused_inside_the_engine_for_the_single_and_the_batch_door()
        {
            using var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.OpenAsync(Copy("A.bim"), discardUnsaved: true);
            var identityOfA = (await e.SessionInfoAsync()).ModelIdentity;
            await e.OpenAsync(Copy("B.bim"), discardUnsaved: true);
            var any = (await e.ListMeasuresAsync()).Take(2).ToArray();
            Assert.Equal(2, any.Length);
            var before = any.Select(m => m.Description ?? "").ToArray();

            var one = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                e.SetObjectPropertyAsync(any[0].Ref, "Description", "typed for A", "human", null, identityOfA));
            var many = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                e.SetObjectPropertiesAsync(any.Select(m => m.Ref).ToArray(), "Description", "typed for A", "human", null, null, identityOfA));

            Assert.Contains("typed for another model", one.Message);
            Assert.Contains("typed for another model", many.Message);
            Assert.Equal(before, (await e.ListMeasuresAsync()).Take(2).Select(m => m.Description ?? "").ToArray());
        }

        // ---- note from Sol's drafts walk: the other-models line named a TMDL model "definition" -------------------------------------

        [Fact]
        public void The_label_of_a_TMDL_folder_model_is_the_model_folder_not_the_word_definition()
        {
            Assert.Equal("Northstar", LocalEngine.FileModelName("/m/Northstar.SemanticModel/definition"));
            Assert.Equal("Northstar", LocalEngine.FileModelName("/m/Northstar.SemanticModel/definition/"));
            Assert.Equal("Sales", LocalEngine.FileModelName("/m/Sales.bim"));
        }
    }
}

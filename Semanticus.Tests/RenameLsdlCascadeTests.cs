using System;
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
    /// rename_object cascades into the linguistic schema (docs/bug-set_synonyms-duplicate-name-collision.md,
    /// follow-up §): a rename rewrites DAX references but used NOT to touch the culture's LSDL, leaving the renamed
    /// object's entity bound to the OLD name — an orphan the next set_synonyms/enable_qna prunes, silently deleting
    /// authored synonyms one write later. Pins: the cascade follows columns/measures/tables/hierarchies/levels AND
    /// relationship bindings (the whole-document walk); the orphan-then-prune data loss no longer happens; the cascade
    /// rides the shared undo timeline; it is NON-THROWING per culture (a validator-refused culture is left untouched
    /// and warned about — apply_plan's per-item catch must never strand a half-cascaded rename); a refused culture is
    /// RETRIED once at batch end (a later rename in the same batch can remove the very collision that blocked it) and
    /// only survivors warn; the property grid's
    /// Name writes (set_property / set_properties) route through the same seam; plus the two PR-review follow-ups
    /// (ReferencedEntityKeys no longer over-pins on enum values; DescribeCollisions distinguishes the refused exact
    /// tier from the write-through fold tier per member). Driven through LocalEngine — the MCP surface is what's
    /// proven, not just the helper.
    /// </summary>
    public sealed class RenameLsdlCascadeTests
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
            await engine.CreateModelAsync("RenameCascade", 1604);
            var workPack = await engine.CreateTableAsync("Work Pack", "human");
            await engine.CreateColumnAsync(workPack, "Client Name", "String", "Client Name", "human");
            await engine.CreateColumnAsync(workPack, "Region", "String", "Region", "human");
            var task = await engine.CreateTableAsync("Task", "human");
            await engine.CreateColumnAsync(task, "Task Status", "String", "Task Status", "human");
            return (engine, sessions);
        }

        private static Task<string> ContentAsync(SessionManager sessions, string culture = "en-US") =>
            sessions.Require().ReadAsync(m => m.Cultures[culture].Content);

        private static Task<string> SynonymsAsync(SessionManager sessions, string table, string column) =>
            sessions.Require().ReadAsync(m =>
                SynonymHelper.GetSynonyms(m.Tables[table].Columns[column], m.Cultures["en-US"]));

        // Minimal LSDL with hand-placed entities, mirroring SetSynonymsCollisionTests' harness.
        private static string Lsdl(string relationships, params string[] entities) =>
            "{\"Version\":\"1.0.0\",\"Language\":\"en-US\",\"DynamicImprovement\":\"HighConfidence\",\"Entities\":{"
            + string.Join(",", entities) + "},\"SemanticSlots\":{},\"Relationships\":{" + relationships + "}}";

        private static string Entity(string key, string table, string prop) =>
            $"\"{key}\":{{\"Binding\":{{\"ConceptualEntity\":\"{table}\",\"ConceptualProperty\":\"{prop}\"}},\"State\":\"Generated\",\"Terms\":[]}}";

        private static Task InjectAsync(SessionManager sessions, string content, string culture = "en-US") =>
            sessions.Require().MutateAsync("human", "seed linguistic schema", m =>
            {
                var cult = m.Cultures.Contains(culture) ? m.Cultures[culture] : m.AddTranslation(culture);
                cult.Content = content;
            });

        [Fact]
        public async Task Renaming_a_column_keeps_its_synonyms_and_a_later_write_does_not_prune_them()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.EnableQnaAsync(null, "agent");
                await engine.SetSynonymsAsync("column:Work Pack/Client Name", new[] { "client", "account holder" }, null, "agent");

                // The rename: the fixup seam that used to orphan the linguistic entity.
                var rn = await engine.RenameObjectAsync("column:Work Pack/Client Name", "Customer Name", "agent");
                Assert.True(rn.Changed);

                // 1) The synonyms follow the object to its new name (the entity binding was cascaded, not orphaned).
                var afterRename = await SynonymsAsync(sessions, "Work Pack", "Customer Name");
                Assert.Contains("client", afterRename);
                Assert.Contains("account holder", afterRename);

                // 2) The real regression: a SUBSEQUENT set_synonyms on a DIFFERENT object must not prune the renamed
                //    entity as an orphan (pre-fix it was bound to the gone 'Client Name' → dead weight → deleted).
                var write = await engine.SetSynonymsAsync("column:Work Pack/Region", new[] { "area" }, null, "agent");
                Assert.DoesNotContain("Pruned", write.Warning ?? "");

                var survived = await SynonymsAsync(sessions, "Work Pack", "Customer Name");
                Assert.Contains("client", survived);
                Assert.Contains("account holder", survived);
            }
        }

        [Fact]
        public async Task Renaming_a_table_rewrites_the_conceptual_entity_on_child_entities()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.EnableQnaAsync(null, "agent");
                await engine.SetSynonymsAsync("column:Work Pack/Client Name", new[] { "client" }, null, "agent");

                await engine.RenameObjectAsync("table:Work Pack", "Work Package", "agent");

                var content = await ContentAsync(sessions);
                Assert.Contains("\"ConceptualEntity\":\"Work Package\"", content);   // child column entity followed the table
                Assert.DoesNotContain("\"ConceptualEntity\":\"Work Pack\"", content); // no orphan left on the old name

                // And the child column's synonyms are still reachable under the renamed table.
                var syn = await SynonymsAsync(sessions, "Work Package", "Client Name");
                Assert.Contains("client", syn);
            }
        }

        [Fact]
        public async Task Renaming_a_hierarchy_cascades_its_binding()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.CreateHierarchyAsync("table:Work Pack", "Geography", new[] { "Region" }, "human");
                await engine.EnableQnaAsync(null, "agent");
                await engine.SetSynonymsAsync("hierarchy:Work Pack/Geography", new[] { "geo" }, null, "agent");

                await engine.RenameObjectAsync("hierarchy:Work Pack/Geography", "Geo Hierarchy", "agent");

                var content = await ContentAsync(sessions);
                Assert.Contains("\"Hierarchy\":\"Geo Hierarchy\"", content);
                Assert.DoesNotContain("\"Hierarchy\":\"Geography\"", content);

                var syn = await sessions.Require().ReadAsync(m =>
                    SynonymHelper.GetSynonyms(m.Tables["Work Pack"].Hierarchies["Geo Hierarchy"], m.Cultures["en-US"]));
                Assert.Contains("geo", syn);
            }
        }

        [Fact]
        public async Task Undo_after_a_rename_restores_the_original_lsdl()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.EnableQnaAsync(null, "agent");
                await engine.SetSynonymsAsync("column:Work Pack/Client Name", new[] { "client" }, null, "agent");
                var before = await ContentAsync(sessions);

                await engine.RenameObjectAsync("column:Work Pack/Client Name", "Customer Name", "agent");
                Assert.NotEqual(before, await ContentAsync(sessions));   // the cascade is a real LSDL edit

                await engine.UndoAsync("human");
                Assert.Equal(before, await ContentAsync(sessions));      // one undo reverts fixup + cascade together
            }
        }

        [Fact]
        public async Task Apply_plan_rename_also_cascades_the_linguistic_schema()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.EnableQnaAsync(null, "agent");
                await engine.SetSynonymsAsync("column:Work Pack/Client Name", new[] { "client" }, null, "agent");

                // apply_plan's rename item routes through the same Session.Rename seam now. Rename is an opt-in kind,
                // so it lands 'proposed' and needs an explicit approve before apply.
                var view = await engine.AddPlanItemAsync("column:Work Pack/Client Name", "rename", "Customer Name", null, null, null, "agent");
                var id = view.Items.Single(i => i.Kind == "rename").Id;
                await engine.SetPlanItemAsync(id, null, true, "agent");
                await engine.ApplyPlanAsync(null, "agent");

                var syn = await SynonymsAsync(sessions, "Work Pack", "Customer Name");
                Assert.Contains("client", syn);
                var content = await ContentAsync(sessions);
                Assert.DoesNotContain("\"ConceptualProperty\":\"Client Name\"", content);
            }
        }

        [Fact]
        public async Task Over_pinning_fix_enum_value_key_is_prunable_but_a_referenced_entity_is_not()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                // "Generated" is a well-known non-reference property value (State:"Generated"); before the fix it
                // pinned an unrelated entity KEYED "Generated" against pruning. A genuine phrasing reference to
                // "task.real" must still pin. Both entities are bound to DELETED columns → dead but for the pin.
                await InjectAsync(sessions, Lsdl(
                    "\"rel1\":{\"Binding\":{\"ConceptualEntity\":\"Task\"},\"State\":\"Generated\",\"Phrasings\":[{\"Subject\":{\"Entity\":\"task.real\"}}]}",
                    Entity("Generated", "Task", "GhostA"),   // keyed like the enum token → pinned pre-fix, prunable now
                    Entity("task.real", "Task", "GhostB")));  // genuinely referenced by a phrasing → still pinned

                var result = await engine.SetSynonymsAsync("column:Work Pack/Region", new[] { "area" }, null, "agent");
                Assert.Contains("Pruned 1", result.Warning);   // only the enum-keyed orphan went

                var content = await ContentAsync(sessions);
                Assert.DoesNotContain("\"Generated\":{", content);   // enum-value-keyed entity is no longer over-pinned
                Assert.Contains("task.real", content);               // the real Relationships reference stays pinned
            }
        }

        [Fact]
        public async Task Describe_collisions_distinguishes_refused_exact_from_write_through_fold()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.CreateColumnAsync("table:Task", "Client Name", "String", "Client Name", "human");   // exact dup of Work Pack[Client Name]
                await engine.CreateColumnAsync("table:Work Pack", "Cycle Months", "Int64", "Cycle Months", "human");
                await engine.CreateColumnAsync("table:Task", "Cycle Month", "Int64", "Cycle Month", "human");     // fold-only vs Cycle Months
                await InjectAsync(sessions, Lsdl("",
                    Entity("work_pack.client_name", "Work Pack", "Client Name"),
                    Entity("task.client_name", "Task", "Client Name"),
                    Entity("work_pack.cycle_months", "Work Pack", "Cycle Months"),
                    Entity("task.cycle_month", "Task", "Cycle Month")));

                var msg = await sessions.Require().ReadAsync(m =>
                    LsdlSynonyms.DescribeCollisions(m, m.Cultures["en-US"]));

                Assert.NotNull(msg);
                Assert.Contains("client name", msg);
                Assert.Contains("refuses writes until you rename or hide", msg);   // exact tier: hard refusal
                Assert.Contains("After plural folding", msg);                      // fold tier: write proceeds
                Assert.Contains("still writes", msg);
                Assert.Contains("cycle month", msg);
            }
        }

        [Fact]
        public async Task Describe_collisions_mixed_group_names_only_the_exact_pair_as_refused()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                // One FOLDED group of three: an exact pair ('Cycle Months' on two tables) plus a fold-only singular
                // ('Cycle Month'). Only the exact pair is refused; the singular belongs in the warning sentence.
                await engine.CreateTableAsync("Task Action", "human");
                await engine.CreateColumnAsync("table:Work Pack", "Cycle Months", "Int64", "Cycle Months", "human");
                await engine.CreateColumnAsync("table:Task Action", "Cycle Months", "Int64", "Cycle Months", "human");
                await engine.CreateColumnAsync("table:Task", "Cycle Month", "Int64", "Cycle Month", "human");
                await InjectAsync(sessions, Lsdl("",
                    Entity("work_pack.cycle_months", "Work Pack", "Cycle Months"),
                    Entity("task_action.cycle_months", "Task Action", "Cycle Months"),
                    Entity("task.cycle_month", "Task", "Cycle Month")));

                var msg = await sessions.Require().ReadAsync(m =>
                    LsdlSynonyms.DescribeCollisions(m, m.Cultures["en-US"]));

                Assert.NotNull(msg);
                var split = msg.IndexOf("After plural folding", StringComparison.Ordinal);
                Assert.True(split > 0, "expected both an exact and a fold sentence: " + msg);
                var refused = msg.Substring(0, split);
                var folded = msg.Substring(split);
                Assert.Contains("'Work Pack'[Cycle Months]", refused);      // the exact pair is the refused set
                Assert.Contains("'Task Action'[Cycle Months]", refused);
                Assert.DoesNotContain("'Task'[Cycle Month]", refused);      // the singular is NOT claimed refused
                Assert.Contains("'Task'[Cycle Month]", folded);             // it lands in the write-through warning
            }
        }

        [Fact]
        public async Task Cascade_commit_failure_leaves_that_culture_untouched_warns_and_the_rename_succeeds()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.EnableQnaAsync(null, "agent");
                await engine.SetSynonymsAsync("column:Work Pack/Client Name", new[] { "client" }, null, "agent");
                // A second culture proves per-culture isolation: only the faulted one is left untouched.
                await InjectAsync(sessions, Lsdl("", Entity("work_pack.client_name", "Work Pack", "Client Name")), "fr-FR");
                var enBefore = await ContentAsync(sessions);

                // The live AS validator's rejection is unreachable offline — inject it via the internal seam,
                // scoped to THIS model + culture so parallel test classes are untouched.
                var model = await sessions.Require().ReadAsync(m => m);
                LsdlSynonyms.CascadeCommitFault = c =>
                    ReferenceEquals(c.Model, model) && c.Name == "en-US"
                        ? new InvalidOperationException("An item with the same key has already been added. Key: client name.")
                        : null;
                try
                {
                    var rn = await engine.RenameObjectAsync("column:Work Pack/Client Name", "Customer Name", "agent");

                    // The rename itself is legitimate and MUST apply — a cascade failure means that culture's
                    // schema was already uncommittable; refusing the rename would trap the user in the collision.
                    Assert.True(rn.Changed);
                    Assert.NotNull(rn.Warning);
                    Assert.Contains("en-US", rn.Warning);
                    Assert.Contains("Client Name", rn.Warning);        // the honest consequence names the old name

                    Assert.Equal(enBefore, await ContentAsync(sessions));                       // faulted culture untouched
                    var fr = await ContentAsync(sessions, "fr-FR");                             // the other culture cascaded
                    Assert.Contains("\"ConceptualProperty\":\"Customer Name\"", fr);
                    Assert.DoesNotContain("\"ConceptualProperty\":\"Client Name\"", fr);
                }
                finally { LsdlSynonyms.CascadeCommitFault = null; }
            }
        }

        [Fact]
        public async Task Batch_end_retry_recovers_a_transiently_refused_cascade()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.EnableQnaAsync(null, "agent");
                await engine.SetSynonymsAsync("column:Work Pack/Region", new[] { "area" }, null, "agent");
                await engine.SetSynonymsAsync("column:Task/Task Status", new[] { "state" }, null, "agent");

                // Transient-collision sequencing: rename #1's cascade is refused WHILE the collision (#2's old
                // name) still exists; rename #2 then removes it; the batch-end retry recommits #1's culture. The
                // fault models a live validator whose verdict depends on the CURRENT model state.
                var model = await sessions.Require().ReadAsync(m => m);
                LsdlSynonyms.CascadeCommitFault = c =>
                    ReferenceEquals(c.Model, model) && model.Tables["Task"].Columns.Contains("Task Status")
                        ? new InvalidOperationException("An item with the same key has already been added. Key: zone.")
                        : null;
                try
                {
                    var v1 = await engine.AddPlanItemAsync("column:Work Pack/Region", "rename", "Zone", null, null, null, "agent");
                    var id1 = v1.Items.Single(i => i.Kind == "rename").Id;
                    await engine.SetPlanItemAsync(id1, null, true, "agent");
                    var v2 = await engine.AddPlanItemAsync("column:Task/Task Status", "rename", "State", null, null, null, "agent");
                    var id2 = v2.Items.Single(i => i.Kind == "rename" && i.Id != id1).Id;
                    await engine.SetPlanItemAsync(id2, null, true, "agent");

                    var report = await engine.ApplyPlanAsync(null, "agent");
                    Assert.Equal(2, report.AppliedCount);

                    // The retry recovered the refused culture: both entities cascaded and NO warning survives.
                    Assert.All(report.Items, i => Assert.DoesNotContain("Culture", i.Note ?? ""));
                    Assert.Contains("area", await SynonymsAsync(sessions, "Work Pack", "Zone"));
                    Assert.Contains("state", await SynonymsAsync(sessions, "Task", "State"));
                    var content = await ContentAsync(sessions);
                    Assert.DoesNotContain("\"ConceptualProperty\":\"Region\"", content);
                    Assert.DoesNotContain("\"ConceptualProperty\":\"Task Status\"", content);
                }
                finally { LsdlSynonyms.CascadeCommitFault = null; }
            }
        }

        [Fact]
        public async Task Set_property_name_write_cascades_the_lsdl_and_rewrites_dax_like_rename_object()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.EnableQnaAsync(null, "agent");
                await engine.SetSynonymsAsync("column:Work Pack/Client Name", new[] { "client" }, null, "agent");
                await engine.CreateMeasureAsync("table:Work Pack", "Clients", "COUNTROWS(FILTER('Work Pack', 'Work Pack'[Client Name] <> BLANK()))", "agent");

                // The property grid's rename path (set_property "Name") must behave exactly like rename_object.
                await engine.SetObjectPropertyAsync("column:Work Pack/Client Name", "Name", "Customer Name", "human");

                var meas = (await engine.ListMeasuresAsync()).First(x => x.Name == "Clients");
                Assert.Contains("[Customer Name]", meas.Expression);        // FormulaFixup still fired
                Assert.DoesNotContain("[Client Name]", meas.Expression);

                var syn = await SynonymsAsync(sessions, "Work Pack", "Customer Name");
                Assert.Contains("client", syn);                             // and the LSDL cascaded too
                Assert.DoesNotContain("\"ConceptualProperty\":\"Client Name\"", await ContentAsync(sessions));
            }
        }

        [Fact]
        public async Task Set_properties_batch_name_write_cascades_each_object()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.EnableQnaAsync(null, "agent");
                await engine.SetSynonymsAsync("column:Work Pack/Region", new[] { "area" }, null, "agent");
                await engine.SetSynonymsAsync("column:Task/Task Status", new[] { "state" }, null, "agent");

                // The multi-select grid path: one atomic gesture renames both columns (different tables, so the
                // shared new name is legal at the TOM level) — each must cascade its own linguistic entity.
                await engine.SetObjectPropertiesAsync(
                    new[] { "column:Work Pack/Region", "column:Task/Task Status" }, "Name", "Zone", "human");

                Assert.Contains("area", await SynonymsAsync(sessions, "Work Pack", "Zone"));
                Assert.Contains("state", await SynonymsAsync(sessions, "Task", "Zone"));
                var content = await ContentAsync(sessions);
                Assert.DoesNotContain("\"ConceptualProperty\":\"Region\"", content);
                Assert.DoesNotContain("\"ConceptualProperty\":\"Task Status\"", content);
            }
        }

        [Fact]
        public async Task Concurrent_mutations_each_receive_only_their_own_cascade_warning()
        {
            // The MAJOR fix: cascade warnings used to land in a session-global list drained AFTER MutateAsync released
            // its lease, so a second request (B) completing and draining first could STEAL the first request's (A)
            // advisory — and if B applied no rename, B silently discarded it. Each caller now owns a per-call sink
            // filled inside the lease, so attribution is by BATCH, order-independent.
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.EnableQnaAsync(null, "agent");
                await engine.SetSynonymsAsync("column:Work Pack/Client Name", new[] { "client" }, null, "agent");

                // Caller B is an apply_plan with NO rename item (a plain set_description) — the exact case that, under
                // the old global list, found no owner and dropped whatever it drained.
                var view = await engine.AddPlanItemAsync("column:Work Pack/Region", "set_description", "the sales region", null, null, null, "agent");
                var descId = view.Items.Single(i => i.Kind == "set_description").Id;
                await engine.SetPlanItemAsync(descId, null, true, "agent");

                // Fault ONLY caller A's culture so A's rename produces a surviving cascade warning; B touches no culture.
                var model = await sessions.Require().ReadAsync(m => m);
                LsdlSynonyms.CascadeCommitFault = c =>
                    ReferenceEquals(c.Model, model) && c.Name == "en-US"
                        ? new InvalidOperationException("An item with the same key has already been added. Key: client name.")
                        : null;
                try
                {
                    // Both batches in flight together (the engine serializes the mutations, but the post-await drains
                    // race on caller threads — the surface the old global list got wrong).
                    var aTask = engine.RenameObjectAsync("column:Work Pack/Client Name", "Customer Name", "agent");
                    var bTask = engine.ApplyPlanAsync(null, "agent");
                    await Task.WhenAll(aTask, bTask);

                    // A keeps its OWN advisory; B (no rename) never absorbs it.
                    Assert.NotNull(aTask.Result.Warning);
                    Assert.Contains("Client Name", aTask.Result.Warning);
                    Assert.All(bTask.Result.Items, i => Assert.DoesNotContain("Culture", i.Note ?? ""));
                }
                finally { LsdlSynonyms.CascadeCommitFault = null; }
            }
        }

        [Fact]
        public async Task Apply_plan_attributes_a_cascade_warning_to_the_exact_renamed_object_not_the_first_same_named_item()
        {
            // The MINOR fix: apply_plan used to attribute cascade warnings by the item's OLD name (Before), which is
            // ambiguous when two items rename same-named objects — both warnings stuck to the first. Attribution is
            // now by object identity, so the warning lands on the object it actually concerns.
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.CreateColumnAsync("table:Task", "Client Name", "String", "Client Name", "human");   // same name, different table
                await engine.EnableQnaAsync(null, "agent");
                await engine.SetSynonymsAsync("column:Work Pack/Client Name", new[] { "client" }, null, "agent");
                // en-US carries Work Pack's entity; fr-FR carries ONLY Task's. Faulting fr-FR makes ONLY the second
                // rename (Task) warn — and that warning must attach to the Task item, not the first same-named item.
                await InjectAsync(sessions, Lsdl("", Entity("task.client_name", "Task", "Client Name")), "fr-FR");

                var model = await sessions.Require().ReadAsync(m => m);
                LsdlSynonyms.CascadeCommitFault = c =>
                    ReferenceEquals(c.Model, model) && c.Name == "fr-FR"
                        ? new InvalidOperationException("An item with the same key has already been added. Key: customer name.")
                        : null;
                try
                {
                    var v1 = await engine.AddPlanItemAsync("column:Work Pack/Client Name", "rename", "Customer Name", null, null, null, "agent");
                    var id1 = v1.Items.Single(i => i.Kind == "rename").Id;
                    await engine.SetPlanItemAsync(id1, null, true, "agent");
                    var v2 = await engine.AddPlanItemAsync("column:Task/Client Name", "rename", "Customer Name", null, null, null, "agent");
                    var id2 = v2.Items.Single(i => i.Kind == "rename" && i.Id != id1).Id;
                    await engine.SetPlanItemAsync(id2, null, true, "agent");

                    var report = await engine.ApplyPlanAsync(null, "agent");
                    Assert.Equal(2, report.AppliedCount);

                    var workPackItem = report.Items.Single(i => i.Id == id1);
                    var taskItem = report.Items.Single(i => i.Id == id2);
                    Assert.DoesNotContain("Culture", workPackItem.Note ?? "");   // the FIRST same-named item must NOT absorb it
                    Assert.Contains("Culture 'fr-FR'", taskItem.Note);           // the warning lands on the exact renamed object
                    Assert.Contains("'Task'", taskItem.Note);                    // and names the table, so it is unambiguous
                }
                finally { LsdlSynonyms.CascadeCommitFault = null; }
            }
        }

        [Fact]
        public async Task Table_rename_rewrites_relationship_bindings_too()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                // Bindings are not only under Entities: a Relationships phrasing binds its table the same way.
                await InjectAsync(sessions, Lsdl(
                    "\"rel1\":{\"Binding\":{\"ConceptualEntity\":\"Task\"},\"Phrasings\":[{\"Subject\":{\"Entity\":\"task.task_status\"}}]}",
                    Entity("task.task_status", "Task", "Task Status")));

                await engine.RenameObjectAsync("table:Task", "Job", "agent");

                var content = await ContentAsync(sessions);
                Assert.DoesNotContain("\"ConceptualEntity\":\"Task\"", content);   // relationship binding followed too
                Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(content, "\"ConceptualEntity\":\"Job\"").Count);   // entity + relationship
            }
        }
    }
}

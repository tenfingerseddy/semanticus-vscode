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
    /// set_synonyms duplicate-name hardening (docs/bug-set_synonyms-duplicate-name-collision.md): the AS
    /// linguistic validator folds every LSDL entity's bound name into ONE term dictionary, so entities for
    /// hidden shadow columns duplicating visible names made EVERY synonym write fail model-wide. Pins: the
    /// self-heal (prune dead/hidden-bound entities), the pre-flight collision guard with actionable errors,
    /// the hidden-target refusal, the keep-what-you-don't-understand fail-safe, enable_qna's non-fatal
    /// heal-and-warn, and the DAC-QNA-NAME-COLLISIONS readiness rule. Driven through LocalEngine so the
    /// MCP surface is what's proven, not just the helper.
    /// </summary>
    public sealed class SetSynonymsCollisionTests
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
            await engine.CreateModelAsync("SynonymCollisions", 1604);
            var workPack = await engine.CreateTableAsync("Work Pack", "human");
            await engine.CreateColumnAsync(workPack, "Client Name", "String", "Client Name", "human");
            await engine.CreateColumnAsync(workPack, "Region", "String", "Region", "human");
            var task = await engine.CreateTableAsync("Task", "human");
            await engine.CreateColumnAsync(task, "Task Status", "String", "Task Status", "human");
            return (engine, sessions);
        }

        // Minimal LSDL with hand-placed entities (the state a deployed/generated schema arrives in).
        private static string Lsdl(params string[] entities) => LsdlWithRelationships("", entities);

        private static string LsdlWithRelationships(string relationships, params string[] entities) =>
            "{\"Version\":\"1.0.0\",\"Language\":\"en-US\",\"DynamicImprovement\":\"HighConfidence\",\"Entities\":{"
            + string.Join(",", entities) + "},\"SemanticSlots\":{},\"Relationships\":{" + relationships + "}}";

        private static string Entity(string key, string table, string prop) =>
            $"\"{key}\":{{\"Binding\":{{\"ConceptualEntity\":\"{table}\",\"ConceptualProperty\":\"{prop}\"}},\"State\":\"Generated\",\"Terms\":[]}}";

        private static Task InjectAsync(SessionManager sessions, string content) =>
            sessions.Require().MutateAsync("human", "seed linguistic schema", m =>
            {
                var cult = m.Cultures.Contains("en-US") ? m.Cultures["en-US"] : m.AddTranslation("en-US");
                cult.Content = content;
            });

        private static Task<string> ContentAsync(SessionManager sessions) =>
            sessions.Require().ReadAsync(m => m.Cultures["en-US"].Content);

        [Fact]
        public async Task Set_synonyms_round_trips_through_the_donor_reader()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.EnableQnaAsync(null, "agent");
                var result = await engine.SetSynonymsAsync("column:Work Pack/Client Name", new[] { "client", "account holder" }, null, "agent");
                Assert.True(result.Changed);
                Assert.Null(result.Warning);

                var readBack = await sessions.Require().ReadAsync(m =>
                    SynonymHelper.GetSynonyms(m.Tables["Work Pack"].Columns["Client Name"], m.Cultures["en-US"]));
                Assert.Contains("client", readBack);
                Assert.Contains("account holder", readBack);
            }
        }

        [Fact]
        public async Task Dead_entities_are_pruned_and_the_write_succeeds_with_a_warning()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                var task = "table:Task";
                await engine.CreateColumnAsync(task, "Shadow Col", "String", "Shadow Col", "human");
                await engine.SetColumnMetadataAsync("column:Task/Shadow Col", true, null, null, null, "human");   // hidden shadow
                await InjectAsync(sessions, Lsdl(
                    Entity("task.shadow_col", "Task", "Shadow Col"),     // bound to a HIDDEN column → dead weight
                    Entity("task.ghost", "Task", "Ghost")));             // bound to a DELETED column → orphan

                var result = await engine.SetSynonymsAsync("column:Work Pack/Region", new[] { "area" }, null, "agent");
                Assert.True(result.Changed);
                Assert.Contains("Pruned 2", result.Warning);
                Assert.Contains("Q&A ignores hidden objects", result.Warning);

                var content = await ContentAsync(sessions);
                Assert.DoesNotContain("Shadow Col", content);
                Assert.DoesNotContain("Ghost", content);
                Assert.Contains("area", content);   // the target's terms landed
            }
        }

        [Fact]
        public async Task Visible_duplicate_names_are_refused_with_both_tables_named()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.CreateColumnAsync("table:Task", "Client Name", "String", "Client Name", "human");   // visible duplicate
                await InjectAsync(sessions, Lsdl(Entity("work_pack.client_name", "Work Pack", "Client Name")));

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    engine.SetSynonymsAsync("column:Task/Client Name", new[] { "customer" }, null, "agent"));
                Assert.Contains("client name", ex.Message);
                Assert.Contains("Task", ex.Message);
                Assert.Contains("Work Pack", ex.Message);
                Assert.Contains("Rename or hide", ex.Message);
            }
        }

        [Fact]
        public async Task Camel_split_collisions_block_and_plural_fold_collisions_warn()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.CreateColumnAsync("table:Work Pack", "Date Key", "String", "Date Key", "human");
                await engine.CreateColumnAsync("table:Work Pack", "Cycle Months", "Int64", "Cycle Months", "human");
                await engine.CreateColumnAsync("table:Task", "DateKey", "String", "DateKey", "human");
                await engine.CreateColumnAsync("table:Task", "Cycle Month", "Int64", "Cycle Month", "human");
                await InjectAsync(sessions, Lsdl(
                    Entity("work_pack.date_key", "Work Pack", "Date Key"),
                    Entity("work_pack.cycle_months", "Work Pack", "Cycle Months")));

                // camel split is EXACT-tier: DateKey normalises to the existing entity's 'date key' — refused.
                var camel = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    engine.SetSynonymsAsync("column:Task/DateKey", new[] { "when" }, null, "agent"));
                Assert.Contains("date key", camel.Message);

                // the singular fold is only an approximation: Cycle Month vs Cycle Months PROCEEDS with a warning
                // (the commit-time backstop carries the live truth) — a fold false-positive must not block a write.
                var fold = await engine.SetSynonymsAsync("column:Task/Cycle Month", new[] { "cadence" }, null, "agent");
                Assert.True(fold.Changed);
                Assert.Contains("plural folding", fold.Warning);
                Assert.Contains("cycle month", fold.Warning);
            }
        }

        [Fact]
        public async Task Dry_run_set_synonyms_leaves_the_lsdl_byte_identical()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                var task = "table:Task";
                await engine.CreateColumnAsync(task, "Shadow Col", "String", "Shadow Col", "human");
                await engine.SetColumnMetadataAsync("column:Task/Shadow Col", true, null, null, null, "human");
                // A dead entity makes the rehearsal exercise the PRUNE too — the mutation with teeth.
                await InjectAsync(sessions, Lsdl(Entity("task.shadow_col", "Task", "Shadow Col")));
                var before = await ContentAsync(sessions);

                var rpt = await engine.DryRunOpAsync("set_synonyms",
                    "{\"objRef\":\"column:Work Pack/Region\",\"terms\":[\"area\"]}");

                Assert.True(rpt.WouldSucceed);
                Assert.Equal(before, await ContentAsync(sessions));   // byte-identical: prune + terms all rolled back
            }
        }

        [Fact]
        public async Task Undo_restores_the_lsdl_after_set_synonyms()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.EnableQnaAsync(null, "agent");
                var before = await ContentAsync(sessions);

                await engine.SetSynonymsAsync("column:Work Pack/Client Name", new[] { "client" }, null, "agent");
                Assert.NotEqual(before, await ContentAsync(sessions));

                await engine.UndoAsync("human");
                Assert.Equal(before, await ContentAsync(sessions));   // the LSDL write rides the shared undo timeline
            }
        }

        [Fact]
        public async Task Entities_referenced_by_relationships_are_never_pruned()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                var task = "table:Task";
                await engine.CreateColumnAsync(task, "Shadow Col", "String", "Shadow Col", "human");
                await engine.SetColumnMetadataAsync("column:Task/Shadow Col", true, null, null, null, "human");
                await InjectAsync(sessions, LsdlWithRelationships(
                    "\"rel1\":{\"Binding\":{\"ConceptualEntity\":\"Task\"},\"Phrasings\":[{\"Subject\":{\"Entity\":\"task.shadow_col\"}}]}",
                    Entity("task.shadow_col", "Task", "Shadow Col"),   // hidden-bound BUT referenced → pinned
                    Entity("task.ghost", "Task", "Ghost")));           // dead and unreferenced → pruned

                var result = await engine.SetSynonymsAsync("column:Work Pack/Region", new[] { "area" }, null, "agent");
                Assert.Contains("Pruned 1", result.Warning);

                var content = await ContentAsync(sessions);
                Assert.Contains("task.shadow_col", content);   // never orphan a Relationships reference
                Assert.DoesNotContain("task.ghost", content);
            }
        }

        [Fact]
        public async Task Clean_name_key_collisions_suffix_instead_of_overwriting()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                // [AB] and [A,B] both CleanName to the same slug key; exact terms 'ab' vs 'a b' don't collide,
                // so both writes are allowed — the second entity must get a suffixed key, not replace the first.
                await engine.CreateColumnAsync("table:Work Pack", "AB", "String", "AB", "human");
                await engine.CreateColumnAsync("table:Work Pack", "A,B", "String", "A,B", "human");
                await engine.EnableQnaAsync(null, "agent");

                await engine.SetSynonymsAsync("column:Work Pack/AB", new[] { "first" }, null, "agent");
                await engine.SetSynonymsAsync("column:Work Pack/A,B", new[] { "second" }, null, "agent");

                var terms = await sessions.Require().ReadAsync(m => (
                    Ab: SynonymHelper.GetSynonyms(m.Tables["Work Pack"].Columns["AB"], m.Cultures["en-US"]),
                    AcommaB: SynonymHelper.GetSynonyms(m.Tables["Work Pack"].Columns["A,B"], m.Cultures["en-US"])));
                Assert.Contains("first", terms.Ab);        // the first entity survived the second write
                Assert.Contains("second", terms.AcommaB);
            }
        }

        [Fact]
        public async Task Hidden_targets_are_refused_with_an_actionable_message()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.EnableQnaAsync(null, "agent");
                await engine.SetColumnMetadataAsync("column:Task/Task Status", true, null, null, null, "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    engine.SetSynonymsAsync("column:Task/Task Status", new[] { "state" }, null, "agent"));
                Assert.Contains("hidden", ex.Message);
                Assert.Contains("Unhide", ex.Message);
            }
        }

        [Fact]
        public async Task Unrecognised_entity_shapes_are_never_pruned()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await InjectAsync(sessions, Lsdl(
                    "\"mystery\":{\"State\":\"Authored\",\"Terms\":[]}",                                    // no Binding anywhere
                    "\"strange\":{\"Binding\":{\"ConceptualEntity\":\"Task\",\"Future\":\"x\"},\"Terms\":[]}"));   // unknown binding key

                var result = await engine.SetSynonymsAsync("column:Work Pack/Region", new[] { "area" }, null, "agent");
                Assert.True(result.Changed);
                Assert.Null(result.Warning);   // nothing pruned: never delete what you don't understand

                var content = await ContentAsync(sessions);
                Assert.Contains("mystery", content);
                Assert.Contains("strange", content);
            }
        }

        [Fact]
        public async Task Enable_qna_prunes_dead_entities_and_warns_on_surviving_collisions()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.CreateColumnAsync("table:Task", "Client Name", "String", "Client Name", "human");   // visible duplicate
                await InjectAsync(sessions, Lsdl(
                    Entity("task.ghost", "Task", "Ghost"),                            // dead: no such column
                    Entity("work_pack.client_name", "Work Pack", "Client Name"),
                    Entity("task.client_name", "Task", "Client Name")));              // colliding VISIBLE pair survives

                var result = await engine.EnableQnaAsync(null, "agent");
                Assert.True(result.Changed);                                          // the prune is a real edit
                Assert.Contains("Pruned 1", result.Warning);
                Assert.Contains("client name", result.Warning);                       // collision surfaced, not thrown
                Assert.Contains("rename or hide", result.Warning, StringComparison.OrdinalIgnoreCase);

                var content = await ContentAsync(sessions);
                Assert.DoesNotContain("Ghost", content);
                Assert.Contains("work_pack.client_name", content);                    // survivors untouched
                Assert.Contains("task.client_name", content);
            }
        }

        [Fact]
        public async Task Readiness_rule_fires_one_finding_per_collision_group()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                await engine.EnableQnaAsync(null, "agent");
                await engine.CreateColumnAsync("table:Task", "Client Name", "String", "Client Name", "human");   // group 1
                await engine.CreateColumnAsync("table:Work Pack", "Date Key", "String", "Date Key", "human");    // group 2
                await engine.CreateColumnAsync("table:Task", "DateKey", "String", "DateKey", "human");

                var findings = (await engine.AiReadinessScanAsync()).Findings
                    .Where(f => f.RuleId == "DAC-QNA-NAME-COLLISIONS").ToList();
                Assert.Equal(2, findings.Count);
                var clientName = Assert.Single(findings.Where(f => f.ObjectName == "client name"));
                Assert.Contains("Task", clientName.Message);
                Assert.Contains("Work Pack", clientName.Message);
                Assert.Contains("Rename or hide", clientName.Message);
                Assert.Single(findings.Where(f => f.ObjectName == "date key"));

                var ev = await sessions.Require().ReadAsync(m =>
                    ReadinessRuleSet.Default().Single(r => r.Id == "DAC-QNA-NAME-COLLISIONS").Evaluate(m));
                Assert.Equal(2, ev.Applicable);   // Applicable = the collision groups (dormant-or-dock)
            }
        }

        [Fact]
        public async Task Readiness_rule_is_dormant_without_a_linguistic_culture_or_qna_signal()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                // Same duplicate names, but Q&A never engaged: no linguistic culture, no qnaEnabled signal.
                await engine.CreateColumnAsync("table:Task", "Client Name", "String", "Client Name", "human");

                var ev = await sessions.Require().ReadAsync(m =>
                    ReadinessRuleSet.Default().Single(r => r.Id == "DAC-QNA-NAME-COLLISIONS").Evaluate(m));
                Assert.Equal(0, ev.Applicable);
                Assert.Empty(ev.Violations);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, lane m11/uat-undo round 3 (76e8ee34): the round's classes attacked from the sides its own tests do not
    /// stand on. F2 a refused write leaves no trace, tried on the wrapper's other writes made outside its undo (a key
    /// column, a calculation item's order, a role's permission on a table it has none for, a rename that reaches a level,
    /// a filter and a translation). F3 an assistant's path write, tried through a link to the model's folder. J1 an
    /// approval to throw unsaved work away, tried after the person did more work. And the join's two notes, ruled on:
    /// an assistant granting consent to send the model's details out, and an assistant loosening a workflow mandate the
    /// person set on it.
    /// </summary>
    [Collection("restore-root")]
    public sealed class VerifyUatUndoRound3Tests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public VerifyUatUndoRound3Tests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "vu3-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(_root);
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
        }

        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private string WriteModel(string tag)
        {
            var dir = Path.Combine(_root, tag);
            Directory.CreateDirectory(dir);
            var db = new TOM.Database("V3") { CompatibilityLevel = 1604, Model = new TOM.Model { DiscourageImplicitMeasures = true } };
            var sales = new TOM.Table { Name = "Sales", LineageTag = "t-sales" };
            sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region", LineageTag = "c-re" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Code", DataType = TOM.DataType.String, SourceColumn = "Code", LineageTag = "c-co" });
            sales.Columns.Add(new TOM.DataColumn { Name = "ProductId", DataType = TOM.DataType.Int64, SourceColumn = "ProductId", LineageTag = "c-pi" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-am" });
            sales.Measures.Add(new TOM.Measure { Name = "Total", Expression = "SUM(Sales[Amount])", LineageTag = "m-total" });
            sales.Measures.Add(new TOM.Measure { Name = "Margin", Expression = "1", LineageTag = "m-margin" });
            sales.Hierarchies.Add(new TOM.Hierarchy { Name = "Geo", LineageTag = "h-geo", Levels = { new TOM.Level { Name = "Region", Column = sales.Columns["Region"], Ordinal = 0, LineageTag = "l-re" }, new TOM.Level { Name = "Code", Column = sales.Columns["Code"], Ordinal = 1, LineageTag = "l-co" } } });
            sales.Partitions.Add(new TOM.Partition { Name = "P1", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            db.Model.Tables.Add(sales);
            var product = new TOM.Table { Name = "Product", LineageTag = "t-product" };
            product.Columns.Add(new TOM.DataColumn { Name = "ProductId", DataType = TOM.DataType.Int64, SourceColumn = "ProductId", LineageTag = "c-ppi", IsKey = true });
            product.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-pn" });
            product.Partitions.Add(new TOM.Partition { Name = "P2", Source = new TOM.MPartitionSource { Expression = "let Source = 2 in Source" } });
            db.Model.Tables.Add(product);
            db.Model.Relationships.Add(new TOM.SingleColumnRelationship { Name = "r1", FromColumn = sales.Columns["ProductId"], ToColumn = product.Columns["ProductId"], FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One });
            // A calculation group whose items carry no order of their own (-1), as TE2 itself leaves them (CalculationGroupTable.cs:147).
            var cg = new TOM.Table { Name = "Scenario", LineageTag = "t-cg", CalculationGroup = new TOM.CalculationGroup { Precedence = 0 } };
            cg.Columns.Add(new TOM.DataColumn { Name = "Scenario", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-cgn" });
            cg.Partitions.Add(new TOM.Partition { Name = "Scenario", Source = new TOM.CalculationGroupSource() });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Actual", Expression = "SELECTEDMEASURE()", Ordinal = -1 });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Plan", Expression = "SELECTEDMEASURE() * 2", Ordinal = -1 });
            db.Model.Tables.Add(cg);
            var role = new TOM.ModelRole { Name = "Managers", ModelPermission = TOM.ModelPermission.Read };
            role.TablePermissions.Add(new TOM.TablePermission { Table = sales, FilterExpression = "[Region] = \"A\"" });
            db.Model.Roles.Add(role);
            db.Model.Perspectives.Add(new TOM.Perspective { Name = "View" });
            var culture = new TOM.Culture { Name = "fr-FR" };
            culture.ObjectTranslations.SetTranslation(sales.Columns["Region"], TOM.TranslatedProperty.Caption, "Zone");
            db.Model.Cultures.Add(culture);
            var path = Path.Combine(dir, "model.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        // Everything a trace can show: the model as TOM writes it, both undo stacks, the unsaved flag, History length, revision.
        private static async Task<string> TraceAsync(SessionManager sessions)
        {
            var s = sessions.Current;
            var json = await s.ReadAsync(m => TOM.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase, new TOM.SerializeOptions { IgnoreTimestamps = true }));
            var undo = await s.ReadAsync(_ => s.UndoStateNow());
            return json + "\n" + JsonSerializer.Serialize(undo) + "\nunsaved=" + s.HasUnsavedEdits
                + "\nhistory=" + sessions.Ledger.ForModel(s.Id).Count + "\nrevision=" + s.Revision;
        }

        private static string FirstDifference(string a, string b)
        {
            var at = Enumerable.Range(0, Math.Min(a.Length, b.Length)).FirstOrDefault(i => a[i] != b[i]);
            string Cut(string x) => x.Substring(Math.Max(0, at - 80), Math.Min(220, x.Length - Math.Max(0, at - 80))).Replace("\n", " ");
            return "was [" + Cut(a) + "] now [" + Cut(b) + "]";
        }

        // ---- F2: a refused write leaves no trace, on the wrapper's other writes made outside its undo ---------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("key column: set IsKey on Product[Name] (TE2 clears ProductId's key straight in TOM)")]
        [InlineData("calculation item order: move Plan first (TE2 numbers -1 items straight in TOM)")]
        [InlineData("column OLS for a table the role has no permission on")]
        [InlineData("table OLS for a table the role has no permission on")]
        [InlineData("row filter for a table the role has no permission on")]
        [InlineData("rename a column a level, a row filter and a translation use")]
        [InlineData("rename a table a role filters")]
        [InlineData("delete a column a relationship uses")]
        [InlineData("delete a calculation item")]
        [InlineData("add a calculation item")]
        [InlineData("make a hierarchy")]
        public async Task Under_Locked_a_refused_write_on_another_cascade_family_leaves_the_model_exactly_as_it_was(string family)
        {
            var path = WriteModel("f2-" + Math.Abs(family.GetHashCode()).ToString("x"));
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(path);
            await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's words", "human");   // unsaved, with a redo behind it
            await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's later words", "human");
            await engine.UndoAsync("human");
            AgentPolicyStore.SetPreset("locked", "human");
            var before = await TraceAsync(sessions);
            Func<Task> write = family.Split(':')[0] switch
            {
                "key column" => () => engine.SetObjectPropertyAsync("column:Product/Name", "IsKey", "true", "agent"),
                "calculation item order" => () => engine.SetObjectPropertyAsync("calcitem:Scenario/Plan", "Ordinal", "0", "agent"),
                "column OLS for a table the role has no permission on" => () => engine.SetColumnObjectPermissionAsync("Managers", "column:Product/Name", "None", "agent"),
                "table OLS for a table the role has no permission on" => () => engine.SetTableObjectPermissionAsync("Managers", "table:Product", "None", "agent"),
                "row filter for a table the role has no permission on" => () => engine.SetTablePermissionAsync("Managers", "table:Product", "[Name] = \"x\"", "agent"),
                "rename a column a level, a row filter and a translation use" => () => engine.RenameObjectAsync("column:Sales/Region", "Area", "agent"),
                "rename a table a role filters" => () => engine.RenameObjectAsync("table:Sales", "Orders", "agent"),
                "delete a column a relationship uses" => () => engine.DeleteObjectsAsync(new[] { "column:Sales/ProductId" }, "agent"),
                "delete a calculation item" => () => engine.DeleteObjectsAsync(new[] { "calcitem:Scenario/Plan" }, "agent"),
                "add a calculation item" => () => engine.CreateCalculationItemAsync("table:Scenario", "Forecast", "SELECTEDMEASURE() * 3", "agent"),
                "make a hierarchy" => () => engine.CreateHierarchyAsync("table:Sales", "Geo2", new[] { "Code", "Region" }, "agent"),
                _ => throw new InvalidOperationException("no such family"),
            };
            var refused = await Assert.ThrowsAnyAsync<Exception>(write);
            Assert.True(refused.Message.Contains("approval", StringComparison.OrdinalIgnoreCase), "the write did not reach the question: " + refused.Message);
            Assert.Single(await engine.ListPendingApprovalsAsync());
            var after = await TraceAsync(sessions);
            Assert.True(before == after, family + ": the refused write left a trace: " + FirstDifference(before, after));
        }

        // ---- F3: a link to the model's folder is the model's folder ----------------------------------------------------

        [Fact]
        public async Task Under_Locked_an_assistants_new_file_put_inside_the_TMDL_model_folder_through_a_link_asks_first()
        {
            var bim = WriteModel("f3-src");
            var real = Path.Combine(_root, "f3-real");
            {
                var sessions0 = new SessionManager();
                using var e0 = new LocalEngine(sessions0, TestEntitlements.Pro);
                await e0.OpenAsync(bim);
                await e0.SaveAsync(real, "TMDL", true, "human");
            }
            var link = Path.Combine(_root, "f3-link");
            Directory.CreateSymbolicLink(link, real);
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(real);
            AgentPolicyStore.SetPreset("locked", "human");
            var straight = Path.Combine(real, "tables", "Straight.vpax");
            var aliased = Path.Combine(link, "tables", "Aliased.vpax");
            // Named by its own path, a new file inside the model's folder asks (the builder's rule: "own" covers new files too).
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExportVpaxAsync(straight, "agent"));
            Assert.False(File.Exists(straight));
            // The same folder reached through a link is the same folder.
            try { await engine.ExportVpaxAsync(aliased, "agent"); } catch (InvalidOperationException) { }
            Assert.Empty(Directory.GetFiles(Path.Combine(real, "tables"), "Aliased*"));
            Assert.Empty(Directory.GetFiles(Path.Combine(real, "tables"), "Straight*"));
            Assert.Equal(2, (await engine.ListPendingApprovalsAsync()).Length);   // both were asked about, not refused for some other reason
        }

        /// <summary>The same family through the person's own undo: making Product[Name] the key and taking it back must leave
        /// ProductId the key again. TE2 clears the other columns' key straight in TOM (Column.cs:296), outside its undo.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Making_another_column_the_key_and_undoing_it_puts_the_old_key_back()
        {
            var path = WriteModel("iskey-undo");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(path);
            await engine.SetObjectPropertyAsync("column:Product/Name", "IsKey", "true", "human");
            await engine.UndoAsync("human");
            Assert.True(await sessions.Current.ReadAsync(m => m.Tables["Product"].Columns["ProductId"].IsKey), "undo left the table with no key: ProductId lost its key for good");
        }

        // ---- J1: an approval to throw unsaved work away covers the work the person saw ----------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_an_approval_to_discard_unsaved_work_is_not_spent_on_work_the_person_did_after_approving()
        {
            var path = WriteModel("j1");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(path);
            await engine.SetDescriptionAsync("measure:Sales/Total", "a small unsaved edit", "human");
            AgentPolicyStore.SetPreset("locked", "human");
            await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.OpenModel(engine, path, discardUnsaved: true));
            await engine.ApproveAgentActionAsync(Assert.Single(await engine.ListPendingApprovalsAsync()).Id, "human");
            // The person said yes to losing that one edit, then went on working before the assistant came back.
            await engine.SetDescriptionAsync("measure:Sales/Margin", "an hour of the person's later work", "human");
            try { await McpTools.OpenModel(engine, path, discardUnsaved: true); } catch (InvalidOperationException) { }
            Assert.Equal("an hour of the person's later work", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Margin"].Description));
        }

        // ---- Join notes (1) and (2), ruled on: governance of the assistant itself ---------------------------------------

        /// <summary>1.4 chatyes (Kane, 10 Oct 2026), reversing the earlier JOIN ruling: the person's yes in chat lets the assistant turn
        /// Jev on for a model, even under Locked. The grant is recorded as the assistant's, and the person turns it off again.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task CHATYES_an_assistant_can_turn_Jev_on_on_the_persons_word_and_the_person_can_turn_it_off()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync();
            await f.ConsentAsync();
            await f.Engine.WithdrawConsentAsync("human");
            Assert.False((await f.Engine.GetConsentAsync()).Granted);
            AgentPolicyStore.SetPreset("locked", "human");
            var done = await McpTools.SetConsent(f.Engine, Semanticus.Decisions.DecisionRegistry.DeclaredFields.ToArray(), Semanticus.Decisions.DecisionRegistry.ConsentSchemaVersion);
            Assert.True(done.Granted);
            Assert.Equal("agent", done.GrantedBy);
            await f.Engine.WithdrawConsentAsync("human");
            Assert.False((await f.Engine.GetConsentAsync()).Granted);
        }

        /// <summary>JOIN item, pre-existing (not one of the three tools round 3 wired): cicd_generate with write=true, as the
        /// assistant under Locked, writes a CI workflow, a deploy script and parameter.yml into the person's repository with
        /// no question. None of those paths is named by the request (it names "github"), so the round's rule ("under Locked
        /// every assistant file write that is not to a new path the request names is held") does not hold for it. The file
        /// sweep never drove it: it leaves out every tool that takes a workspaceId.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task JOIN_under_Locked_an_assistants_CI_scaffold_is_not_written_into_the_repository_unasked()
        {
            var path = WriteModel("cicd");
            var dir = Path.GetDirectoryName(path)!;
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(path);
            AgentPolicyStore.SetPreset("locked", "human");
            try { await McpTools.CicdGenerate(engine, "github", null, "PROD", write: true); } catch (Exception) { }
            Assert.False(File.Exists(Path.Combine(dir, ".github", "workflows", "fabric-cicd.yml")), "a CI workflow was written into the person's repository unasked");
            Assert.False(File.Exists(Path.Combine(dir, "parameter.yml")));
        }

        /// <summary>JOIN item, pre-existing (not this lane's): set_workflow_binding is an assistant tool, and the binding is a
        /// control over what the assistant may do (Plan-heart rule: such controls are person-only, or a named exception).
        /// The person makes create_measure go through a reviewed workflow; the assistant takes the mandate off and is free.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task JOIN_an_assistant_cannot_take_off_a_workflow_mandate_the_person_set_on_it()
        {
            var ws = Path.Combine(_root, "wfbind");
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "bind-vehicle.md"),
                "---\nname: bind-vehicle\ntitle: Binding vehicle\nwhenToUse: \"Author a measure the reviewed way.\"\n---\n## Step 1: Author the measure\nCreate the measure at this step.\n```yaml gate\nops: [create_measure]\n```\n## Step 2: Review\nReview what you made.\n");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro, ws);
            await engine.CreateModelAsync("BindTest", 1701);
            var t = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            await engine.SetWorkflowBindingAsync("create_measure", new[] { "bind-vehicle" }, "hard", "human");
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateMeasureAsync("table:Facts", "Bare", "SUM(Facts[Amount])", "agent"));
            try { await McpTools.SetWorkflowBinding(engine, "create_measure", Array.Empty<string>(), "off"); } catch (Exception) { }
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateMeasureAsync("table:Facts", "Bare", "SUM(Facts[Amount])", "agent"));
        }
    }
}

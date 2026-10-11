using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    /// <summary>
    /// Plan statement check 5, the fixes that live in the engine (the page's link count is N1, in startplan-model.ts; psc5-link-count.test.mjs holds it).
    /// P2: a source that declares its keys gets no role link guessed between two of its tables (ONBRD_DT_KEY, HIRE_DT_KEY, START_DT_KEY, END_DT_KEY to dim_date). They doubled the best practice
    /// warnings of the build (inactive links, snowflake) and the short answers behind the release line. The facts' date roles and the keyless shape are unchanged.
    /// P3: an assistant's start whose sample was refused (the QueryData gate) on a source with no keys at all.
    /// </summary>
    public sealed class PlanStatementCheck5LinkTests
    {
        private static string Key(ManifestLink l) => l.FromTable + "." + l.FromColumns[0] + ">" + l.ToTable + "." + l.ToColumns[0];

        private static readonly HashSet<string> Declared = new HashSet<string>(SqlDraftBuildTests.J8Schema().ForeignKeys.Select(f => f.FromTable + "." + f.FromColumn + ">" + f.ToTable + "." + f.ToColumn));

        // What the J8 rows show: each dimension's own id is unique; a fact's links repeat.
        private static LinkGuessFacts Sample() => LinkGuessFacts.NamesOnly()
            .Measured("dbo.dim_date", new Dictionary<string, bool> { ["DATE_KEY"] = true, ["CAL_DT"] = true, ["FY_NO"] = false })
            .Measured("dbo.dim_client", new Dictionary<string, bool> { ["CLNT_ID"] = true, ["CLNT_NM"] = true, ["ONBRD_DT_KEY"] = false })
            .Measured("dbo.dim_staff", new Dictionary<string, bool> { ["EMP_NO"] = true, ["EMP_NM"] = true, ["HIRE_DT_KEY"] = false })
            .Measured("dbo.dim_project", new Dictionary<string, bool> { ["PRJ_ID"] = true, ["CLNT_ID"] = false, ["START_DT_KEY"] = false, ["END_DT_KEY"] = false })
            .Measured("dbo.fact_timesheet", new Dictionary<string, bool> { ["TS_LINE_ID"] = true, ["EMP_NO"] = false, ["PRJ_ID"] = false, ["WORK_DT_KEY"] = false })
            .Measured("dbo.fact_invoice", new Dictionary<string, bool> { ["INV_NO"] = true, ["PRJ_ID"] = false, ["CLNT_ID"] = false, ["INV_DT_KEY"] = false, ["DUE_DT_KEY"] = false, ["PAID_DT_KEY"] = false });

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_source_that_declares_its_keys_gets_no_role_link_guessed_between_its_dimensions(bool withSample)
        {
            var m = StructureManifest.Derive(SqlDraftBuildTests.J8Schema(), null, null, "seed", "x", "DW", "import", withSample ? Sample() : LinkGuessFacts.NamesOnly());
            Assert.DoesNotContain(m.Links, l => l.Guessed);
            Assert.Equal(Declared.Count, m.Links.Count);
            Assert.All(m.Links, l => Assert.Contains(Key(l), Declared));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_source_that_declares_some_keys_still_guesses_the_facts_date_roles_it_leaves_undeclared(bool withSample)
        {
            var s = SqlDraftBuildTests.J8Schema();
            s.ForeignKeys = s.ForeignKeys.Where(f => !(f.FromTable == "fact_timesheet" && f.FromColumn == "WORK_DT_KEY")).ToArray();
            var m = StructureManifest.Derive(s, null, null, "seed", "x", "DW", "import", withSample ? Sample() : LinkGuessFacts.NamesOnly());
            var guessed = m.Links.Where(l => l.Guessed).ToList();
            Assert.Equal(new[] { "fact_timesheet.WORK_DT_KEY>dim_date.DATE_KEY" }, guessed.Select(Key).ToArray());   // the fact's date role, and no dimension's
            Assert.True(guessed.Single().Active);
        }

        private static FabricSchema NoKeysAtAll()
        {
            var s = SqlDraftBuildTests.J8Schema();
            s.ForeignKeys = new FabricForeignKey[0];
            foreach (var t in s.Tables) t.KeyColumns = new string[0];
            return s;
        }

        [Fact]
        public void With_no_key_and_no_rows_a_role_link_is_on_only_for_a_table_named_like_a_fact_and_the_rest_say_why_they_are_off()
        {
            var m = StructureManifest.Derive(NoKeysAtAll(), null, null, "seed", "x", "DW", "import", LinkGuessFacts.NamesOnly());
            Assert.Equal(new[] { "fact_invoice.INV_DT_KEY>dim_date.DATE_KEY", "fact_timesheet.WORK_DT_KEY>dim_date.DATE_KEY" }, m.Links.Where(l => l.Active).Select(Key).OrderBy(k => k).ToArray());
            foreach (var key in new[] { "dim_client.ONBRD_DT_KEY", "dim_staff.HIRE_DT_KEY", "dim_project.START_DT_KEY" })
            {
                var l = m.Links.Single(x => Key(x).StartsWith(key + ">"));
                Assert.False(l.Active, key + " is a guess with no rows and no key behind it");
                Assert.StartsWith("Off because no rows were read", l.Note);
                Assert.Contains(l.FromTable, l.Note);
            }
            // The facts keep their date links, so the date table is found and the time measures are planned, as before.
            Assert.True(m.ToSpec().DateTable.MarkAsDate);
            Assert.NotEmpty(m.TimeMeasures());
        }

        /// <summary>Q2 (check 5 fix round 2): Kane's rule is a fact_ naming convention. A "fact" word followed by an underscore, a space or a capital letter is one; Factory, Factor, Factual and Facts are not.</summary>
        [Theory]
        [InlineData("fact_invoice", true)]
        [InlineData("FACT_INVOICE", true)]
        [InlineData("Fact_Sales", true)]
        [InlineData("Fact Sales", true)]
        [InlineData("fact sales", true)]
        [InlineData("FactInternetSales", true)]
        [InlineData("factSales", true)]
        [InlineData("FACT_Sales", true)]
        [InlineData("Factory", false)]
        [InlineData("factory", false)]
        [InlineData("FACTORY", false)]
        [InlineData("FactoryOutlet", false)]
        [InlineData("Factor", false)]
        [InlineData("Factors", false)]
        [InlineData("Factual", false)]
        [InlineData("Facts", false)]
        [InlineData("Faction", false)]
        [InlineData("factsales", false)]
        [InlineData("Fact", false)]
        [InlineData("dim_fact", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void A_table_is_named_like_a_fact_only_by_the_fact_naming_convention(string name, bool expected) =>
            Assert.Equal(expected, LinkGuesser.NamedLikeAFact(name));

        [Fact]
        public void With_no_key_and_no_rows_a_table_named_Factory_has_its_date_link_off_and_one_named_FactTimesheet_has_it_on()
        {
            var s = NoKeysAtAll();
            s.Tables.Single(t => t.Name == "fact_invoice").Name = "Factory";
            s.Tables.Single(t => t.Name == "fact_timesheet").Name = "FactTimesheet";
            var m = StructureManifest.Derive(s, null, null, "seed", "x", "DW", "import", LinkGuessFacts.NamesOnly());
            Assert.Equal(new[] { "FactTimesheet.WORK_DT_KEY>dim_date.DATE_KEY" }, m.Links.Where(l => l.Active).Select(Key).OrderBy(k => k).ToArray());
            var factory = m.Links.Single(l => l.FromTable == "Factory" && l.FromColumns[0] == "INV_DT_KEY");
            Assert.False(factory.Active, "Factory is not named like a fact, and no rows say it is one");
            Assert.StartsWith("Off because no rows were read", factory.Note);
        }

        [Fact]
        public void The_same_source_with_its_tables_not_named_like_facts_has_no_role_link_on_and_no_time_measure_on_an_off_link()
        {
            var s = NoKeysAtAll();
            foreach (var t in s.Tables) t.Name = t.Name.Replace("dim_", "").Replace("fact_", "");
            var m = StructureManifest.Derive(s, null, null, "seed", "x", "DW", "import", LinkGuessFacts.NamesOnly());
            Assert.NotEmpty(m.Links);
            Assert.DoesNotContain(m.Links, l => l.Active);
            Assert.All(m.Links, l => Assert.StartsWith("Off because", l.Note));
            // A time measure reads the date table through a link that is ON: with every date link off, no time measure is planned for a table whose date link is off.
            Assert.Empty(m.TimeMeasures());
        }

        [Fact]
        public void The_rows_landing_later_bring_a_proven_role_link_on_and_leave_the_rest_off()
        {
            var doc = new StartPlanDoc { Schema = NoKeysAtAll(), IdentitySeed = "seed" };
            doc.Manifest = StructureManifest.Derive(doc.Schema, null, null, "seed", "x", "DW", "import", LinkGuessFacts.NamesOnly());
            Assert.Contains(doc.Manifest.Links, l => !l.Active && l.Note.StartsWith("Off because no rows were read"));
            LinkGuesser.Reconsider(doc, Sample());
            // The rows show the dimension ids unique, so the facts now link to them, those tables are dimensions, and their own date roles are no guess at all.
            Assert.DoesNotContain(doc.Manifest.Links, l => !l.Active && l.Note != null && l.Note.StartsWith("Off because no rows were read"));
            Assert.DoesNotContain(doc.Manifest.Links, l => l.FromTable.StartsWith("dim_") && l.ToTable == "dim_date");
            Assert.All(doc.Manifest.Links.Where(l => l.FromTable.StartsWith("fact_") && l.FromColumns[0] == "WORK_DT_KEY"), l => Assert.True(l.Active));
        }

        [Fact]
        public void The_dimension_date_roles_are_dropped_on_the_sample_landing_too_when_the_source_declares_its_keys()
        {
            // Reconsider runs when the rows are read after the plan was made; it must not bring the dimension roles back.
            var doc = new StartPlanDoc { Schema = SqlDraftBuildTests.J8Schema(), IdentitySeed = "seed" };
            doc.Manifest = StructureManifest.Derive(doc.Schema, null, null, "seed", "x", "DW", "import", LinkGuessFacts.NamesOnly());
            LinkGuesser.Reconsider(doc, Sample());
            Assert.DoesNotContain(doc.Manifest.Links, l => l.Guessed);
        }
    }
    /// <summary>P3: an assistant's start on a source with no keys at all, whose sample the QueryData gate refused. The default preset asks the person before a row is read (an unlabelled target counts as
    /// production), so an assistant's start meets this on the first call, through the real MCP door logic (McpTools.StartModelFromSql, origin "agent").</summary>
    [Collection("restore-root")]
    public sealed class PlanStatementCheck5NoSampleTests : GateFixtureBase
    {
        private const string Server = "wh-j8.datawarehouse.fabric.microsoft.com", Db = "ContosoConsulting";
        private readonly ITestOutputHelper _out;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public PlanStatementCheck5NoSampleTests(RestoreRootFixture fixture, ITestOutputHelper output) : base(fixture) { _out = output; }

        public override void Dispose() { foreach (var o in _owned) { try { o.Dispose(); } catch { } } base.Dispose(); }

        private sealed class CountingSource
        {
            public int Queries;
            private readonly Func<string, string, string, int, Task<ResultSet>> _inner;
            public CountingSource(FabricSchema schema)
            {
                var t = typeof(StartPlanEndToEndTests).GetNestedType("J8Source", BindingFlags.NonPublic);
                var src = Activator.CreateInstance(t, schema);
                _inner = (Func<string, string, string, int, Task<ResultSet>>)Delegate.CreateDelegate(typeof(Func<string, string, string, int, Task<ResultSet>>), src, t.GetMethod("Execute"));
            }
            public Task<ResultSet> Execute(string server, string db, string sql, int max) { Queries++; return _inner(server, db, sql, max); }
        }

        private static FabricSchema NoKeys(bool keepPrimaryKeys)
        {
            var s = SqlDraftBuildTests.J8Schema();
            s.ForeignKeys = new FabricForeignKey[0];
            if (!keepPrimaryKeys) foreach (var t in s.Tables) t.KeyColumns = new string[0];
            return s;
        }

        private async Task<(LocalEngine Engine, SessionManager Sessions, StartPlanView View, CountingSource Source)> AssistantStartAsync(FabricSchema schema, string preset)
        {
            AgentPolicyStore.SetPreset(preset, "human");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Free, Path.Combine(Root, "ws"));
            _owned.Add(engine); _owned.Add(sessions);
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(schema);
            var source = new CountingSource(schema);
            engine.SourceSampleExecutorForTest = source.Execute;
            var view = await McpTools.StartModelFromSql(engine, Server, Db, Path.Combine(Root, "model"), "Uplift");
            await engine.StartSampleSettledForTest(); await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            return (engine, sessions, view, source);
        }

        private static List<StartLine> LinkLines(StartPlanView v) =>
            v.Sections.Single(s => s.Id == StartSectionId.Links).Groups.SelectMany(g => g.Lines).Where(l => l.Item?.Kind == "create_relationship").ToList();

        private static bool IsFactName(string table) => table.StartsWith("fact_", StringComparison.OrdinalIgnoreCase);

        [Theory]
        [InlineData("standard")]   // the default: an unlabelled target counts as production, so the person is asked first and the sample is not read
        [InlineData("client")]     // production is a wall: the sample is refused outright
        public async Task An_assistants_start_on_a_keyless_source_with_the_sample_refused_builds_no_role_link_that_nothing_supports(string preset)
        {
            var (engine, sessions, view, source) = await AssistantStartAsync(NoKeys(keepPrimaryKeys: false), preset);
            Assert.Equal(0, source.Queries);                              // the gate held: not one row was asked for
            Assert.Equal(StartSampleStatus.Refused, view.Sample.State);
            var lines = LinkLines(view);
            Assert.NotEmpty(lines);
            // Before the fix: ONBRD, HIRE and START on three dimensions were ON. Now only the tables named like facts have a link on, and each other link says why it is off.
            Assert.All(lines.Where(l => l.Kept), l => Assert.True(IsFactName(TableOf(l)), "a link from a table that is not named like a fact is on: " + l.Item.Title));
            foreach (var l in lines.Where(l => !l.Kept && !IsFactName(TableOf(l))))
                Assert.Contains("Off because no rows were read", l.Why);
            // The page reads a guessed link's two tables out of this title to count and draw it (startplan-model.ts GUESSED_LINK): the title must keep that shape, the tables named as the model names them.
            var pageReads = new System.Text.RegularExpressions.Regex(@"^Linked (.+?)\[[^\]]*\] to (.+?)\[[^\]]*\]: ");
            Assert.All(lines, l => Assert.True(pageReads.IsMatch(l.Item.Title), "the page cannot read this link title: " + l.Item.Title));
            Assert.Contains(lines, l => pageReads.Match(l.Item.Title) is var m && m.Groups[1].Value == "fact_invoice" && m.Groups[2].Value == "dim_date");
            Assert.Contains(lines, l => l.Kept && l.Item.Title.Contains("fact_timesheet[WORK_DT_KEY]"));
            Assert.Contains(lines, l => !l.Kept && l.Item.Title.Contains("dim_client[ONBRD_DT_KEY]"));
            // And the plan says plainly that the links came from the names alone.
            var need = view.NeedsYou.Single(n => n.Kind == "sample_refused");
            Assert.Contains("guessed from the names alone", need.Text);
            Assert.Contains("Check each one before you build", need.Text);
            // The model it builds has only those links on.
            var approved = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await McpTools.BuildStartPlan(engine, approved.Digest);
            Assert.True(built.Built, built.Refused);
            var active = await sessions.Current.ReadAsync(m => m.Relationships.OfType<SingleColumnRelationship>().Where(r => r.IsActive).Select(r => r.FromTable.Name).Distinct().OrderBy(n => n).ToArray());
            Assert.Equal(new[] { "fact_invoice", "fact_timesheet" }, active);
        }

        private static string TableOf(StartLine l)
        {
            // "Linked dim_client[ONBRD_DT_KEY] to dim_date[DATE_KEY]: ..." : the table the link starts from.
            var t = l.Item.Title; var from = t.IndexOf("Linked ", StringComparison.Ordinal) + "Linked ".Length;
            return t.Substring(from, t.IndexOf('[', from) - from);
        }

        [Fact]
        public async Task The_same_start_with_the_primary_keys_declared_is_unchanged_the_facts_link_to_their_dimensions_and_no_dimension_has_a_date_link()
        {
            var (engine, sessions, view, source) = await AssistantStartAsync(NoKeys(keepPrimaryKeys: true), "standard");
            Assert.Equal(StartSampleStatus.Refused, view.Sample.State);
            var lines = LinkLines(view);
            Assert.DoesNotContain(lines, l => TableOf(l).StartsWith("dim_") && l.Item.Title.Contains("to dim_date["));
            Assert.Contains(lines, l => l.Kept && l.Item.Title.Contains("fact_timesheet[EMP_NO] to dim_staff[EMP_NO]"));
            Assert.Contains(lines, l => l.Kept && l.Item.Title.Contains("fact_invoice[INV_DT_KEY] to dim_date[DATE_KEY]"));
        }
    }

    /// <summary>The build, not only the plan: what the review lists is what the build writes (N1), and what the Pro build leaves open in best practice on the J8 source with its declared keys (N2: 12 warnings
    /// with four dimension date roles guessed beside the declared keys, 6 without).</summary>
    [Collection("restore-root")]
    public sealed class PlanStatementCheck5BuildTests : GateFixtureBase
    {
        private const string Server = "wh-j8.datawarehouse.fabric.microsoft.com", Db = "ContosoConsulting";
        private readonly ITestOutputHelper _out;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public PlanStatementCheck5BuildTests(RestoreRootFixture fixture, ITestOutputHelper output) : base(fixture) { _out = output; ConnectionRegistry.RootOverride = Path.Combine(Root, "user"); AgentPolicyStore.SetPreset("open", "human"); }

        public override void Dispose() { foreach (var o in _owned) { try { o.Dispose(); } catch { } } base.Dispose(); }

        private static string Humanise(string r)
        {
            var name = (r ?? "").Substring((r ?? "").LastIndexOf('/') + 1);
            var colon = name.IndexOf(':'); if (colon >= 0) name = name.Substring(colon + 1);
            return string.Join(" ", name.Split('_', ' ').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1).ToLowerInvariant()));
        }

        private static StartWord[] WordsFor(StartWordsPage page) => page.Items.Select(i => new StartWord
        {
            Ref = i.Ref, Kind = i.Kind,
            Text = i.Kind == "synonyms" ? "alias one, alias two"
                 : i.Kind == "display_name" ? (string.Equals(Humanise(i.Ref), (i.Ref ?? "").Substring((i.Ref ?? "").LastIndexOf('/') + 1), StringComparison.OrdinalIgnoreCase) ? Humanise(i.Ref) + " Value" : Humanise(i.Ref))
                 : i.Kind == "instructions" ? "This model answers questions about consulting work: hours, projects, clients and invoices."
                 : "What " + Humanise(i.Ref) + " means for the business, in plain words.",
        }).ToArray();

        private static FabricSchema Source(bool keyless)
        {
            var s = SqlDraftBuildTests.J8Schema();
            if (keyless) s.ForeignKeys = new FabricForeignKey[0];
            return s;
        }

        /// <returns>The review's link lines (all, and the kept ones), and what the built model holds: the relationships (all, and the active ones), and its open best practice warnings by rule.</returns>
        private async Task<(int Listed, int ListedOn, int Built, int BuiltOn, Dictionary<string, int> Warnings, int Errors, List<StartLinkEdge> Shown, List<string> BuiltKeys)> BuildAsync(bool pro, bool keyless, string door = "ui", bool dropOneAndLeaveOneOut = false)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(Root, "ws"));
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(Source(keyless));
            if (pro) engine.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(new StartPlanDecideTests.PolicyOracle()));
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = Path.Combine(Root, "model"), Name = "Uplift" }, "human");
            Assert.True(view.Exists);
            if (pro)
            {
                await engine.SetConsentAsync(Semanticus.Decisions.DecisionRegistry.DeclaredFields.ToArray(), Semanticus.Decisions.DecisionRegistry.ConsentSchemaVersion, "human");
                view = await engine.GrantStartDisclosureAsync(view.Digest, "human");
                await engine.StartDecideSettledForTest();
            }
            for (var pass = 0; pass < 60; pass++)
            {
                var page = await engine.GetStartWordsAsync(40, 0);
                if (page.Items.Count == 0) break;
                var wrote = await McpTools.WriteStartWords(engine, WordsFor(page));
                var keeps = wrote.Refused.Where(r => r.Contains("keep_name")).Select(r => r.Substring(0, r.IndexOf(": ", StringComparison.Ordinal))).Distinct().ToArray();
                var kept = keeps.Length == 0 ? 0 : (await McpTools.WriteStartWords(engine, keeps.Select(k => new StartWord { Ref = k, Kind = "keep_name" }).ToArray())).Written;
                if (wrote.Written + kept == 0) break;
            }
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            var fy = view.NeedsYou.SingleOrDefault(x => x.Kind == "fiscal_year");
            if (fy != null) view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = "fiscal_year", Value = fy.Recommended }, "human");
            await engine.StartPreviewSettledForTest();
            view = await engine.GetStartPlanAsync();
            if (dropOneAndLeaveOneOut)
            {
                // The person drops one guessed link (or a declared one, on a keyed source) and leaves one table out: the review must then list exactly what the build writes.
                var one = view.Sections.Single(s => s.Id == StartSectionId.Links).Groups.SelectMany(g => g.Lines)
                    .Single(l => l.Item?.Kind == "create_relationship" && (l.Item.Title ?? "").Contains("fact_timesheet") && (l.Item.Title ?? "").Contains("EMP_NO") && (l.Item.Title ?? "").Contains("dim_staff"));
                view = door == "mcp" ? await McpTools.EditStartPlan(engine, "drop_line", one.Item.ObjectRef, null) : await engine.EditStartPlanAsync(new StartPlanEdit { Op = "drop_line", Target = one.Item.ObjectRef }, "human");
                view = door == "mcp" ? await McpTools.EditStartPlan(engine, "include_table", "dim_client", "false") : await engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "dim_client", Value = "false" }, "human");
                await engine.StartPreviewSettledForTest();
                view = door == "mcp" ? await McpTools.GetStartPlan(engine) : await engine.GetStartPlanAsync();
            }
            view = await engine.ApproveStartPlanAsync(view.Digest);
            Assert.True(view.CanBuild, view.BlockedBecause);
            var lines = view.Sections.Single(s => s.Id == StartSectionId.Links).Groups.SelectMany(g => g.Lines).Where(l => l.Item?.Kind == "create_relationship").ToList();
            var shown = view.Links;
            var built = door == "mcp" ? await McpTools.BuildStartPlan(engine, view.Digest) : await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            return await sessions.Current.ReadAsync(m =>
            {
                var rels = m.Relationships.OfType<SingleColumnRelationship>().ToList();
                var open = BpaAnalyzer.Analyze(m, BpaRuleSet.ForModel(m)).Violations;
                var warnings = open.Where(v => v.Severity == 2).GroupBy(v => v.RuleId).ToDictionary(g => g.Key, g => g.Count());
                var keys = rels.Select(r => r.FromTable.Name + ">" + r.ToTable.Name + (r.IsActive ? "" : " (off)")).OrderBy(x => x, StringComparer.Ordinal).ToList();
                return (lines.Count, lines.Count(l => l.Kept), rels.Count, rels.Count(r => r.IsActive), warnings, open.Count(v => v.Severity >= 3), shown, keys);
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task What_the_review_lists_is_what_the_Pro_build_writes_on_both_key_shapes(bool keyless)
        {
            var r = await BuildAsync(true, keyless);
            Assert.Equal(r.Listed, r.Built);        // every link the review lists is written (a link that is off is written off)
            Assert.Equal(r.ListedOn, r.BuiltOn);    // and the links it shows on are the links that are on
            Assert.Equal(9, r.Built);               // the 9 the source declares (keyed), or the 9 its names give (keyless): no dimension date role guessed beside them
            Assert.Equal(6, r.BuiltOn);
        }

        /// <summary>Check 5 fix round 2 (Q1): a guessed link the person dropped, and every link to a table the person left out, are lines in the review but are not built. The page's count and drawing are
        /// the engine's own list of the links the build writes (StartPlanView.Links), so after a drop and a leave-out the shown links, the links on, and which tables they join equal the built model, on both doors
        /// and both key shapes (a keyless source's links are guesses, a keyed source's are declared).</summary>
        [Theory]
        [InlineData(false, "ui")]
        [InlineData(false, "mcp")]
        [InlineData(true, "ui")]
        [InlineData(true, "mcp")]
        public async Task After_a_drop_and_a_leave_out_the_links_the_review_shows_are_the_links_the_build_writes(bool keyless, string door)
        {
            var r = await BuildAsync(false, keyless, door, dropOneAndLeaveOneOut: true);
            Assert.True(r.Listed > r.Built, $"the review still lists the dropped link and the links to the left-out table as lines ({r.Listed} lines, {r.Built} built)");
            Assert.Equal(r.Built, r.Shown.Count);
            Assert.Equal(r.BuiltOn, r.Shown.Count(l => l.Active));
            Assert.DoesNotContain(r.Shown, l => l.From == "dim_client" || l.To == "dim_client");
            // Dropping a guessed link takes it out of the build. Dropping a link the source declares only switches it off: it is still written, off, so the drawing keeps it as a link that is off.
            var dropped = r.Shown.Where(l => l.From == "fact_timesheet" && l.To == "dim_staff").ToList();
            if (keyless) Assert.Empty(dropped);
            else Assert.False(Assert.Single(dropped).Active);
            // The model names its tables as the person reads them (Fact Invoice); the plan keeps the source's names (fact_invoice). The two lists are the same links.
            Assert.Equal(r.BuiltKeys.Select(k => { var off = k.EndsWith(" (off)"); var ends = (off ? k.Substring(0, k.Length - 6) : k).Split('>'); return Humanise(ends[0]) + ">" + Humanise(ends[1]) + (off ? " (off)" : ""); }).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                r.Shown.Select(l => Humanise(l.From) + ">" + Humanise(l.To) + (l.Active ? "" : " (off)")).OrderBy(x => x, StringComparer.Ordinal).ToList());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task With_nothing_dropped_the_shown_links_are_the_review_lines_and_the_built_links(bool keyless)
        {
            var r = await BuildAsync(false, keyless);
            Assert.Equal(r.Built, r.Shown.Count);
            Assert.Equal(r.Listed, r.Shown.Count);
            Assert.Equal(r.BuiltOn, r.Shown.Count(l => l.Active));
        }

        [Fact]
        public async Task The_Pro_build_on_the_keyed_J8_source_carries_the_links_the_source_declares_and_no_guessed_dimension_date_roles()
        {
            // Before the fix: 13 links (4 guessed dimension date roles), 7 inactive-link warnings and 3 snowflake warnings with the test oracle's words (measured 5 Oct on omarchy).
            var r = await BuildAsync(true, keyless: false);
            Assert.Equal(3, r.Warnings.GetValueOrDefault("INACTIVE_RELATIONSHIPS_THAT_ARE_NEVER_ACTIVATED"));   // the invoice's DUE and PAID dates, and the project to client hop
            Assert.Equal(1, r.Warnings.GetValueOrDefault("SNOWFLAKE_SCHEMA_ARCHITECTURE"));                      // project to client
        }
    }
}

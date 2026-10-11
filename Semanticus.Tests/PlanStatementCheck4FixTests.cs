using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Plan statement check 4, the fixes that are not the rename (P3 here; P4 and P5 have their own files). A source that declares no keys (most Fabric warehouses) lost every date
    /// link: WORK_DT_KEY did not match DATE_KEY, because the name rule read the letters and DT is not DATE. So no date table was marked and no time measure was planned.
    /// </summary>
    public sealed class PlanStatementCheck4LinkTests
    {
        private static string Key(ManifestLink l) => l.FromTable + "." + l.FromColumns[0] + ">" + l.ToTable + "." + l.ToColumns[0];

        private static FabricSchema Keyless(bool keepPrimaryKeys)
        {
            var s = SqlDraftBuildTests.J8Schema();
            s.ForeignKeys = new FabricForeignKey[0];
            if (!keepPrimaryKeys) foreach (var t in s.Tables) t.KeyColumns = new string[0];
            return s;
        }

        // What the J8 rows show: each dimension's own id is unique; a fact's links repeat.
        private static LinkGuessFacts Sample() => LinkGuessFacts.NamesOnly()
            .Measured("dbo.dim_date", new Dictionary<string, bool> { ["DATE_KEY"] = true, ["CAL_DT"] = true, ["FY_NO"] = false })
            .Measured("dbo.dim_client", new Dictionary<string, bool> { ["CLNT_ID"] = true, ["CLNT_NM"] = true, ["ONBRD_DT_KEY"] = false })
            .Measured("dbo.dim_staff", new Dictionary<string, bool> { ["EMP_NO"] = true, ["EMP_NM"] = true, ["HIRE_DT_KEY"] = false })
            .Measured("dbo.dim_project", new Dictionary<string, bool> { ["PRJ_ID"] = true, ["CLNT_ID"] = false, ["START_DT_KEY"] = false, ["END_DT_KEY"] = false })
            .Measured("dbo.fact_timesheet", new Dictionary<string, bool> { ["TS_LINE_ID"] = true, ["EMP_NO"] = false, ["PRJ_ID"] = false, ["WORK_DT_KEY"] = false })
            .Measured("dbo.fact_invoice", new Dictionary<string, bool> { ["INV_NO"] = true, ["PRJ_ID"] = false, ["CLNT_ID"] = false, ["INV_DT_KEY"] = false, ["DUE_DT_KEY"] = false, ["PAID_DT_KEY"] = false });

        [Theory]
        [InlineData("WORK_DT_KEY", "DATE_KEY", "dim_date")]
        [InlineData("INV_DT_KEY", "DATE_KEY", "dim_date")]
        [InlineData("INV_DTE_KEY", "DATE_KEY", "dim_date")]
        [InlineData("WorkDtKey", "DateKey", "Date")]
        [InlineData("OrderDteKey", "DateKey", "Date")]
        [InlineData("WORK_DATE_KEY", "DT_KEY", "dim_date")]
        public void A_role_date_column_with_DT_or_DTE_for_DATE_matches_the_date_key(string column, string key, string table) =>
            Assert.Equal(LinkGuesser.EndsWith, LinkGuesser.MatchKind(column, key, table));

        [Theory]
        [InlineData("WORKDT_KEY", "DATE_KEY", "dim_date")]      // DT is not a word of its own here
        [InlineData("EDT_KEY", "DATE_KEY", "dim_date")]
        [InlineData("WORK_DTX_KEY", "DATE_KEY", "dim_date")]
        [InlineData("WORK_DT_KEY", "DATE_NAME", "dim_date")]    // the target must say it identifies something
        [InlineData("WORK_DT", "DATE_KEY", "dim_date")]         // the column must say it identifies something too (no key, id, code ending: it is a date, not a link)
        public void The_abbreviation_is_read_only_as_a_word_of_its_own_and_the_other_rules_still_hold(string column, string key, string table) =>
            Assert.Null(LinkGuesser.MatchKind(column, key, table));

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        [InlineData(false, true)]
        public void The_keyless_J8_shape_finds_its_date_links_marks_a_date_table_and_plans_time_measures(bool keepPrimaryKeys, bool withSample)
        {
            var m = StructureManifest.Derive(Keyless(keepPrimaryKeys), null, null, "seed", "x", "DW", "import", withSample ? Sample() : LinkGuessFacts.NamesOnly());
            var toDate = m.Links.Where(l => l.ToTable == "dim_date").ToDictionary(Key, l => l);

            // The two single-role facts keep their date link on; the invoice's first role stays on and the other two are off with the reason (the role-playing rule).
            Assert.True(toDate["fact_timesheet.WORK_DT_KEY>dim_date.DATE_KEY"].Active);
            Assert.True(toDate["fact_invoice.INV_DT_KEY>dim_date.DATE_KEY"].Active);
            foreach (var role in new[] { "DUE_DT_KEY", "PAID_DT_KEY" })
            {
                var l = toDate["fact_invoice." + role + ">dim_date.DATE_KEY"];
                Assert.False(l.Active);
                Assert.False(string.IsNullOrWhiteSpace(l.Note));
                Assert.Contains("dim_date", l.Note);
            }

            // Every one of those four is a link the source's own keys declare; the date links add nothing the keys would not have declared on the fact tables.
            var declared = new HashSet<string>(SqlDraftBuildTests.J8Schema().ForeignKeys.Select(f => f.FromTable + "." + f.FromColumn + ">" + f.ToTable + "." + f.ToColumn));
            Assert.All(toDate.Keys.Where(k => k.StartsWith("fact_")), k => Assert.Contains(k, declared));

            // So the date table is found and marked, and the time measures are planned.
            var spec = m.ToSpec();
            Assert.True(spec.DateTable.MarkAsDate, spec.DateTable.Note);
            Assert.Equal("dim_date", spec.DateTable.Name);
            Assert.Equal("CAL_DT", spec.DateTable.DateColumn);
            Assert.NotEmpty(m.TimeMeasures());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_dimensions_own_date_column_is_not_made_a_second_path_to_the_date_table(bool withSample)
        {
            // HIRE_DT_KEY on the staff table and START_DT_KEY on the project would, once DT reads as DATE, point at the date table too. A fact that reaches the date table
            // directly and again through that dimension has two paths to it, and the source declares neither, so they are not guessed (the declared J8 shape has no such link).
            var m = StructureManifest.Derive(Keyless(true), null, null, "seed", "x", "DW", "import", withSample ? Sample() : LinkGuessFacts.NamesOnly());
            Assert.DoesNotContain(m.Links, l => l.ToTable == "dim_date" && !l.FromTable.StartsWith("fact_"));
            Assert.Equal(4, m.Links.Count(l => l.ToTable == "dim_date"));   // the four date roles of the two facts, and no more
        }
    }

    /// <summary>P4: the start form's refusal when it cannot sign in or read the source, in plain words (docs/product-copy-style.md). It used to print the engine's own text.</summary>
    [Collection("restore-root")]
    public sealed class PlanStatementCheck4CopyTests : IDisposable
    {
        private readonly RestoreRootFixture _fixture;
        private readonly string _root;

        public PlanStatementCheck4CopyTests(RestoreRootFixture fixture)
        {
            _fixture = fixture;
            _root = Path.Combine(Path.GetTempPath(), "psc4c-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home"));
            ConnectionRegistry.RootOverride = Path.Combine(_root, "user");
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _fixture.Root;
            try { Directory.Delete(_root, true); } catch { }
        }

        // What an engine says and a person should never have to read.
        private static readonly string[] EngineWords = { "introspection", "authMode", "tenant=", "tenantId", "token", "az default", "MCP", "parameter", "azcli", "AADSTS", "Azure.Identity", "Exception", "RootActivityId" };

        private static void AssertPlainWords(string message)
        {
            foreach (var w in EngineWords) Assert.DoesNotContain(w, message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\u2014", message);   // no em dash
            Assert.DoesNotContain("\u2013", message);   // no en dash
            // What failed, and what to do.
            Assert.Contains("Nothing was created.", message);
            Assert.Contains("az login", message);
            Assert.Contains("Microsoft account", message);
            Assert.Contains("choose Sign in in a browser for this source in Connections", message);   // the SQL source form's own name for it (R6)
            Assert.DoesNotContain("account sign-in", message);
            // Short sentences, one idea each (ASD-STE100: 25 words or fewer in a description). The database's own sentence is the one thing quoted, so it is cut off first.
            var ours = message.Contains(" The database said:") ? message.Substring(0, message.IndexOf(" The database said:", StringComparison.Ordinal)) : message;
            foreach (var sentence in Regex.Split(ours, @"(?<=[.!?])\s+").Where(x => x.Length > 0))
                Assert.True(sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 25, "too long for one sentence: " + sentence);
        }

        [Fact]
        public void The_three_refusals_are_plain_words_that_say_what_failed_and_what_to_do()
        {
            AssertPlainWords(StartSourceCopy.NoSignIn());
            AssertPlainWords(StartSourceCopy.SignInRefused());
            var cannotRead = StartSourceCopy.CannotRead("A network-related error occurred while establishing a connection to the server.");
            Assert.Contains("The database said: A network-related error", cannotRead);
            Assert.Contains("Check the endpoint and the database name", cannotRead);
            Assert.Contains("Nothing was created.", cannotRead);
        }

        [Fact]
        public async Task A_start_that_cannot_sign_in_shows_the_plain_words_and_creates_nothing()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Free, Path.Combine(_root, "ws"));
            var folder = Path.Combine(_root, "model");
            // No token is given, so the sign-in cannot be made. This is the same refusal a laptop with no az login gets.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartModelFromSqlAsync(
                new StartModelRequest { Server = "wh.datawarehouse.fabric.microsoft.com", Database = "Contoso", Folder = folder, Name = "Contoso", AuthMode = "token" }, "human"));
            Assert.Equal(StartSourceCopy.NoSignIn(), ex.Message);
            AssertPlainWords(ex.Message);
            Assert.NotNull(ex.InnerException);   // the reason is kept for the log, not shown
            Assert.False(Directory.Exists(folder));
            Assert.Null(sessions.Current);
        }

        [Theory]
        [InlineData("Login failed for user '<token-identified principal>'.")]
        [InlineData("AADSTS50020: User account does not exist in tenant.")]
        public async Task A_source_that_refuses_the_sign_in_says_so_in_plain_words(string reason)
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Free, Path.Combine(_root, "ws"));
            engine.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(new FabricSchema { Error = reason });
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartModelFromSqlAsync(
                new StartModelRequest { Server = "wh.datawarehouse.fabric.microsoft.com", Database = "Contoso", Folder = Path.Combine(_root, "m"), Name = "Contoso" }, "human"));
            Assert.Equal(StartSourceCopy.SignInRefused(), ex.Message);
            AssertPlainWords(ex.Message);
        }
    }
}

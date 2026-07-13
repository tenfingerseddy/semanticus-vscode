using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Find &amp; Replace — the detailed-search modes/surface (Phase 1) and the MatchClass-routed safe replace
    /// (Phase 2). These drive the same public <see cref="IEngine"/> both doors use, so they pin the engine-level
    /// guarantees: the DAX MatchClass classifier, the reference HARD-BLOCK, and the rename-driven replace that
    /// rewrites references via FormulaFixup. Each test builds its own uniquely-named objects (fresh engine per test).
    /// </summary>
    public sealed class SearchReplaceTests : IAsyncLifetime
    {
        private SessionManager _sessions = null!;
        private LocalEngine _engine = null!;
        private string _table = null!;
        private string _tq = null!;   // the table name quoted for DAX

        public async Task InitializeAsync()
        {
            _sessions = new SessionManager();
            _engine = new LocalEngine(_sessions);
            await _engine.OpenAsync(TestModels.FindBim());
            _table = (await _engine.ListMeasuresAsync()).First().Table;
            _tq = "'" + _table.Replace("'", "''") + "'";
        }

        public Task DisposeAsync() { _engine.Dispose(); return Task.CompletedTask; }

        private Task<SearchResult> Search(SearchOptions o) => _engine.SearchModelAsync(o);
        private async Task<string> MeasureExpr(string name) => (await _engine.ListMeasuresAsync()).First(m => m.Name == name).Expression;

        [Fact]
        public async Task Case_sensitive_and_whole_word_modes_filter_matches()
        {
            await _engine.CreateMeasureAsync("table:" + _table, "SR_CaseWord", "1", "agent");

            // case-insensitive (default) finds it; case-sensitive with wrong case does not.
            Assert.Contains((await Search(new SearchOptions { Query = "sr_caseword" })).Hits, h => h.Name == "SR_CaseWord");
            Assert.DoesNotContain((await Search(new SearchOptions { Query = "sr_caseword", CaseSensitive = true })).Hits, h => h.Name == "SR_CaseWord");

            // whole-word: a strict substring of the name must NOT match; the full word must.
            Assert.DoesNotContain((await Search(new SearchOptions { Query = "CaseWor", WholeWord = true })).Hits, h => h.Name == "SR_CaseWord");
            Assert.Contains((await Search(new SearchOptions { Query = "SR_CaseWord", WholeWord = true })).Hits, h => h.Name == "SR_CaseWord");
        }

        [Fact]
        public async Task Regex_mode_matches_and_an_invalid_pattern_is_reported_not_thrown()
        {
            await _engine.CreateMeasureAsync("table:" + _table, "SR_Rx_2024", "1", "agent");
            var ok = await Search(new SearchOptions { Query = @"SR_Rx_\d{4}", Regex = true });
            Assert.Contains(ok.Hits, h => h.Name == "SR_Rx_2024");

            var bad = await Search(new SearchOptions { Query = "[unterminated", Regex = true });
            Assert.NotNull(bad.Error);              // fail-soft — the door never throws on a bad pattern
            Assert.Empty(bad.Hits);
        }

        [Fact]
        public async Task Fields_filter_widens_beyond_the_default_surface()
        {
            var mref = await _engine.CreateMeasureAsync("table:" + _table, "SR_FolderProbe", "1", "agent");
            await _engine.SetObjectPropertyAsync(mref, "DisplayFolder", "SR_UniqueFolder", "agent");

            // The default surface (name+description+expression) does NOT index display folders.
            Assert.Empty((await Search(new SearchOptions { Query = "SR_UniqueFolder" })).Hits);
            // Asking for the displayFolder field surfaces it, tagged PlainText + replaceable.
            var wide = await Search(new SearchOptions { Query = "SR_UniqueFolder", Fields = new[] { "displayFolder" } });
            var hit = Assert.Single(wide.Hits, h => h.Ref == mref);
            Assert.Equal("displayFolder", hit.Field);
            Assert.Equal("PlainText", hit.MatchClass);
            Assert.True(hit.Replaceable);
        }

        [Fact]
        public async Task Dax_matches_are_classified_into_reference_literal_and_comment()
        {
            await _engine.CreateCalculatedColumnAsync("table:" + _table, "SR_Col", "1", "agent");
            var expr = $"VAR y = \"SR_Col literal\" // SR_Col comment\nRETURN SUM ( {_tq}[SR_Col] ) + LEN ( y )";
            var mref = await _engine.CreateMeasureAsync("table:" + _table, "SR_Classify", expr, "agent");

            var r = await Search(new SearchOptions
            {
                Query = "SR_Col", CaseSensitive = true, Fields = new[] { "expression" },
                Kinds = new[] { "measure" }, Scope = "table:" + _table,
            });
            var mine = r.Hits.Where(h => h.Ref == mref).ToList();

            // The [SR_Col] token is a REFERENCE — read-only, steered to rename.
            var refHit = Assert.Single(mine, h => h.MatchClass == "DaxReference");
            Assert.False(refHit.Replaceable);
            Assert.Contains("rename", refHit.ReplaceHint, StringComparison.OrdinalIgnoreCase);

            // The string literal and the comment are replaceable text.
            Assert.True(Assert.Single(mine, h => h.MatchClass == "DaxLiteral").Replaceable);
            Assert.True(Assert.Single(mine, h => h.MatchClass == "DaxComment").Replaceable);
        }

        [Fact]
        public async Task Replace_on_a_dax_reference_is_hard_blocked_in_the_engine()
        {
            await _engine.CreateCalculatedColumnAsync("table:" + _table, "SR_Col", "1", "agent");
            var expr = $"VAR y = \"SR_Col literal\" // SR_Col comment\nRETURN SUM ( {_tq}[SR_Col] ) + LEN ( y )";
            var mref = await _engine.CreateMeasureAsync("table:" + _table, "SR_Block", expr, "agent");

            // A replace-all spanning a reference must be refused (some matches are identifiers) — the model is untouched.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _engine.ReplaceInObjectAsync(new ReplaceRequest { Ref = mref, Field = "expression", Find = "SR_Col", Replace = "ZZZ" }, "agent"));
            Assert.Contains("rename", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[SR_Col]", await MeasureExpr("SR_Block"));   // nothing changed
        }

        [Fact]
        public async Task Replace_targeting_a_string_literal_span_edits_only_that_literal()
        {
            await _engine.CreateCalculatedColumnAsync("table:" + _table, "SR_Col", "1", "agent");
            var expr = $"VAR y = \"SR_Col literal\" // SR_Col comment\nRETURN SUM ( {_tq}[SR_Col] ) + LEN ( y )";
            var mref = await _engine.CreateMeasureAsync("table:" + _table, "SR_Lit", expr, "agent");

            var r = await Search(new SearchOptions { Query = "SR_Col", CaseSensitive = true, Fields = new[] { "expression" }, Kinds = new[] { "measure" }, Scope = "table:" + _table });
            var lit = r.Hits.First(h => h.Ref == mref && h.MatchClass == "DaxLiteral");
            var span = lit.Spans[0];

            var res = await _engine.ReplaceInObjectAsync(new ReplaceRequest
            {
                Ref = mref, Field = "expression", Find = "SR_Col", Replace = "ZZZ",
                CaseSensitive = true, Span = new SearchSpan { Start = span.Start, Len = span.Len },
            }, "agent");

            Assert.True(res.Changed);
            var after = await MeasureExpr("SR_Lit");
            Assert.Contains("ZZZ literal", after);       // the literal changed
            Assert.Contains("[SR_Col]", after);          // the reference is untouched
            Assert.Contains("// SR_Col comment", after); // the comment is untouched (we scoped to one span)
        }

        [Fact]
        public async Task Replace_in_a_name_renames_and_FormulaFixup_rewrites_the_reference()
        {
            var colRef = await _engine.CreateCalculatedColumnAsync("table:" + _table, "SR_RenMe", "1", "agent");
            await _engine.CreateMeasureAsync("table:" + _table, "SR_RenRef", $"SUM ( {_tq}[SR_RenMe] ) // SR_RenMe note", "agent");

            var res = await _engine.ReplaceInObjectAsync(new ReplaceRequest
            {
                Ref = colRef, Field = "name", Find = "SR_RenMe", Replace = "SR_Renamed",
            }, "agent");

            Assert.True(res.Changed);
            Assert.Equal("ObjectName", res.MatchClass);
            Assert.Equal("column:" + _table + "/SR_Renamed", res.Ref);   // the new ref

            var expr = await MeasureExpr("SR_RenRef");
            Assert.Contains("[SR_Renamed]", expr);       // the reference was rewritten by fixup
            Assert.DoesNotContain("[SR_RenMe]", expr);   // the old reference is gone
            Assert.Contains("SR_RenMe note", expr);      // fixup is DAX-only: the comment text is (correctly) left alone
        }

        [Fact]
        public async Task Replace_in_a_name_refuses_a_sibling_collision()
        {
            await _engine.CreateMeasureAsync("table:" + _table, "SR_CollA", "1", "agent");
            var b = await _engine.CreateMeasureAsync("table:" + _table, "SR_CollB", "2", "agent");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _engine.ReplaceInObjectAsync(new ReplaceRequest { Ref = b, Field = "name", Find = "SR_CollB", Replace = "SR_CollA" }, "agent"));
            Assert.Contains("already exists", ex.Message);
        }

        [Fact]
        public async Task Rename_warns_when_an_M_expression_mentions_the_old_name()
        {
            // M partitions need CL≥1400, so this leg runs on a purpose-built fixture (AdventureWorks is CL 1200).
            // The fixture has a measure 'SR_MWarnToken' and a table whose M partition mentions that token — a rename
            // must WARN (FormulaFixup is DAX-only and never rewrites M).
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "semanticus-mwarn-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim");
            System.IO.File.WriteAllText(path, MWarnFixtureBim);
            var fxSessions = new SessionManager();
            try
            {
                var fx = new LocalEngine(fxSessions);
                await fx.OpenAsync(path);
                var mref = (await fx.ListMeasuresAsync()).First(m => m.Name == "SR_MWarnToken").Ref;

                var res = await fx.ReplaceInObjectAsync(new ReplaceRequest
                {
                    Ref = mref, Field = "name", Find = "SR_MWarnToken", Replace = "SR_MWarnRenamed",
                }, "agent");

                Assert.True(res.Changed);
                Assert.NotEmpty(res.Warnings);   // FormulaFixup is DAX-only — the M reference is flagged, not silently missed
                Assert.Contains(res.Warnings, w => w.Contains("Facts") && w.Contains("SR_MWarnToken"));
            }
            finally { fxSessions.Dispose(); try { System.IO.File.Delete(path); } catch { } }
        }

        // A minimal PBI-V3 model (CL 1500 so M partitions are allowed) with a measure whose name a table's M partition
        // mentions (in a comment) — the fixture for the M-breakage rename warning.
        private const string MWarnFixtureBim = """{"name":"MWarnModel","compatibilityLevel":1500,"model":{"defaultPowerBIDataSourceVersion":"powerBI_V3","tables":[{"name":"Facts","columns":[{"name":"Id","dataType":"int64","sourceColumn":"Id"}],"partitions":[{"name":"Facts","mode":"import","source":{"type":"m","expression":"let Source = #table(type table [Id = Int64.Type], {}) /* references SR_MWarnToken here */ in Source"}}],"measures":[{"name":"SR_MWarnToken","expression":"1"}]}]}}""";

        [Fact]
        public async Task Replace_in_a_description_is_a_single_undoable_plain_text_edit()
        {
            var mref = await _engine.CreateMeasureAsync("table:" + _table, "SR_Desc", "1", "agent");
            await _engine.SetDescriptionAsync(mref, "the quick brown fox", "agent");

            var res = await _engine.ReplaceInObjectAsync(new ReplaceRequest
            {
                Ref = mref, Field = "description", Find = "quick", Replace = "slow",
            }, "agent");
            Assert.True(res.Changed);
            Assert.Equal("PlainText", res.MatchClass);
            Assert.Equal("the slow brown fox", res.After);

            await _engine.UndoAsync("human");   // one edit, undoable on the shared timeline
            var back = (await _engine.GetObjectPropertiesAsync(mref)).First(p => p.Name == "Description").Value;
            Assert.Equal("the quick brown fox", back);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// [T215] The guard over the ratified operation taxonomy (option A, "the tree", Kane 2026-08-01,
    /// no renames). The decision page is the source of truth; this test holds OpTaxonomy.cs to it:
    /// every op on the real tool surface has exactly one home, every question and shelf name is the
    /// ratified string verbatim, every shelf holds exactly the ratified number of ops, and the 27
    /// refiled ops sit where the page's small print filed them. A new tool fails here BY NAME until
    /// its placement is added — and a placement addition is a product decision to flag for Kane,
    /// not a refactor.
    /// </summary>
    public sealed class OpTaxonomyTests
    {
        // The page's top-to-bottom question order — Dictionary enumeration order is not a contract,
        // so the ORDER guard reads this array, never Ratified.Keys.
        private static readonly string[] RatifiedOrder =
        {
            "Open a model and connect", "See what is in the model", "Change the model", "Make it better",
            "Prove it is right", "Track versions and compare", "Ship it out", "Teach the AI about this model",
            "Set the rules of the work", "Look things up",
        };

        // T215 placements plus the workflow document, preview, layout and upgrade operations.
        // 25+41+93+15+30+14+36+32+36+6 = 328 (the Tests shelf took get_test_run, record_test_run and get_unmatched_rows;
        // the Reports shelf took list_report_scope, set_report_scope and check_reports with the one report scope;
        // Connections took list_sql_source_usage, the shared free projection the Pro Tests feature made necessary).
        private static readonly Dictionary<string, Dictionary<string, int>> Ratified = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal)
        {
            ["Open a model and connect"] = new Dictionary<string, int> { ["Connections and targets"] = 25 },
            ["See what is in the model"] = new Dictionary<string, int>
            {
                ["Measures, DAX and queries"] = 7, ["Reports and what uses this model"] = 10, ["Speed and size"] = 7,
                ["Tables, columns and hierarchies"] = 5, ["The model as a whole"] = 5,
                ["Relationships and the diagram"] = 4, ["Tests, numbers and evidence"] = 3,
            },
            ["Change the model"] = new Dictionary<string, int>
            {
                ["Measures, DAX and queries"] = 19, ["Where the data comes from"] = 14, ["The model as a whole"] = 14,
                ["Tables, columns and hierarchies"] = 14, ["How the model looks and reads"] = 12,
                ["The model spec"] = 8, ["Dates and time"] = 8, ["Relationships and the diagram"] = 4,
            },
            ["Make it better"] = new Dictionary<string, int> { ["Findings and fixes"] = 15 },
            ["Prove it is right"] = new Dictionary<string, int> { ["Tests, numbers and evidence"] = 30 },
            ["Track versions and compare"] = new Dictionary<string, int> { ["Versions and history"] = 14 },
            ["Ship it out"] = new Dictionary<string, int>
            {
                ["Publishing and deployment"] = 24, ["Who can see what"] = 8, ["The model write-up"] = 4,
            },
            ["Teach the AI about this model"] = new Dictionary<string, int> { ["Teach the AI"] = 32 },
            ["Set the rules of the work"] = new Dictionary<string, int> { ["Workflows and rules"] = 36 },
            ["Look things up"] = new Dictionary<string, int> { ["Reference lists and instruction sheets"] = 6 },
        };

        // The page's small print: the 27 operations the merge refiled, pinned to their exact homes.
        // These are the placements most likely to "drift back" to where the flat notes had them.
        private static readonly (string Op, string Question, string Shelf)[] Refiled =
        {
            ("connect_local", "Open a model and connect", "Connections and targets"),
            ("connect_xmla", "Open a model and connect", "Connections and targets"),
            ("connection_status", "Open a model and connect", "Connections and targets"),
            ("create_model", "Open a model and connect", "Connections and targets"),
            ("disconnect", "Open a model and connect", "Connections and targets"),
            ("list_local_instances", "Open a model and connect", "Connections and targets"),
            ("open_live", "Open a model and connect", "Connections and targets"),
            ("open_local", "Open a model and connect", "Connections and targets"),
            ("open_model", "Open a model and connect", "Connections and targets"),
            ("create_role", "Ship it out", "Who can see what"),
            ("git_push", "Ship it out", "Publishing and deployment"),
            ("apply_model_diff", "Ship it out", "Publishing and deployment"),
            ("refresh_partition", "Ship it out", "Publishing and deployment"),
            ("publish_data_agent", "Ship it out", "Publishing and deployment"),
            ("preview_table", "See what is in the model", "Tables, columns and hierarchies"),
            ("get_model_fingerprint", "See what is in the model", "The model as a whole"),
            ("remove_safe_objects", "Change the model", "Tables, columns and hierarchies"),
            ("dry_run", "Change the model", "The model as a whole"),
            ("create_data_agent", "Teach the AI about this model", "Teach the AI"),
            ("harness_report", "Set the rules of the work", "Workflows and rules"),
            ("get_reference_tree", "Track versions and compare", "Versions and history"),
            ("probe_measure", "Prove it is right", "Tests, numbers and evidence"),
            ("verify_dax_equivalence", "Prove it is right", "Tests, numbers and evidence"),
            ("get_verified_mode", "Prove it is right", "Tests, numbers and evidence"),
            ("set_verified_mode", "Prove it is right", "Tests, numbers and evidence"),
            ("lint_dax", "Make it better", "Findings and fixes"),
            ("optimize_measure", "Make it better", "Findings and fixes"),
        };

        [Fact]
        public void Every_op_on_the_real_tool_surface_has_exactly_one_ratified_home()
        {
            var catalog = LocalEngine.OpSurface.Infos;
            var surface = catalog.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
            var filed = OpTaxonomy.Ops.ToHashSet(StringComparer.Ordinal);

            // Both directions, BY NAME: a new tool with no ratified placement, and a filed op that
            // no longer exists on the surface, each fail with the exact list to act on.
            Assert.True(surface.SetEquals(filed),
                "The tool surface and the ratified taxonomy disagree. " +
                $"On the surface but not filed (add to OpTaxonomy.cs and flag the placement for ratification): [{string.Join(", ", surface.Except(filed).OrderBy(x => x, StringComparer.Ordinal))}]. " +
                $"Filed but no longer on the surface (remove from OpTaxonomy.cs): [{string.Join(", ", filed.Except(surface).OrderBy(x => x, StringComparer.Ordinal))}].");

            // Exactly one home each: the catalog stamp is present and agrees with the single source.
            Assert.All(catalog, o =>
            {
                Assert.True(OpTaxonomy.TryGet(o.Name, out var q, out var s), o.Name);
                Assert.Equal(q, o.Question);
                Assert.Equal(s, o.Shelf);
                Assert.False(string.IsNullOrWhiteSpace(o.Question), o.Name);
                Assert.False(string.IsNullOrWhiteSpace(o.Shelf), o.Name);
            });
            Assert.Equal(328, catalog.Length);
            Assert.Equal(328, OpTaxonomy.Count);
        }

        [Fact]
        public void Question_and_shelf_names_and_counts_include_the_authoring_operations()
        {
            var grouped = OpTaxonomy.Ops
                .Select(op => { OpTaxonomy.TryGet(op, out var q, out var s); return (op, q, s); })
                .GroupBy(x => x.q, StringComparer.Ordinal)
                .ToDictionary(g => g.Key,
                              g => g.GroupBy(x => x.s, StringComparer.Ordinal)
                                    .ToDictionary(sg => sg.Key, sg => sg.Count(), StringComparer.Ordinal),
                              StringComparer.Ordinal);

            Assert.True(new HashSet<string>(Ratified.Keys, StringComparer.Ordinal).SetEquals(grouped.Keys),
                $"Questions differ from the ratified ten. Got: [{string.Join(" | ", grouped.Keys.OrderBy(x => x, StringComparer.Ordinal))}]");
            foreach (var (question, shelves) in Ratified)
            {
                Assert.True(new HashSet<string>(shelves.Keys, StringComparer.Ordinal).SetEquals(grouped[question].Keys),
                    $"Shelves under '{question}' differ from the ratified set. Got: [{string.Join(" | ", grouped[question].Keys.OrderBy(x => x, StringComparer.Ordinal))}]");
                foreach (var (shelf, count) in shelves)
                    Assert.True(count == grouped[question][shelf],
                        $"'{question}' > '{shelf}' holds {grouped[question][shelf]} ops; expected {count}.");
            }

            // The ratified questions carry the ratified ORDER too (the picker's top level).
            Assert.Equal(RatifiedOrder, OpTaxonomy.Questions);
            Assert.True(new HashSet<string>(RatifiedOrder, StringComparer.Ordinal).SetEquals(Ratified.Keys));
        }

        // The page's top-to-bottom SHELF order under each question. Order is part of what Kane
        // ratified: "See" opens on "Measures, DAX and queries", not on whichever shelf owns the
        // first alphabetical op (PR #311 comment 3693594789 caught the picker doing exactly that).
        private static readonly Dictionary<string, string[]> RatifiedShelfOrder = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Open a model and connect"] = new[] { "Connections and targets" },
            ["See what is in the model"] = new[]
            {
                "Measures, DAX and queries", "Reports and what uses this model", "Speed and size",
                "Tables, columns and hierarchies", "The model as a whole",
                "Relationships and the diagram", "Tests, numbers and evidence",
            },
            ["Change the model"] = new[]
            {
                "Measures, DAX and queries", "Where the data comes from", "The model as a whole",
                "Tables, columns and hierarchies", "How the model looks and reads",
                "The model spec", "Dates and time", "Relationships and the diagram",
            },
            ["Make it better"] = new[] { "Findings and fixes" },
            ["Prove it is right"] = new[] { "Tests, numbers and evidence" },
            ["Track versions and compare"] = new[] { "Versions and history" },
            ["Ship it out"] = new[] { "Publishing and deployment", "Who can see what", "The model write-up" },
            ["Teach the AI about this model"] = new[] { "Teach the AI" },
            ["Set the rules of the work"] = new[] { "Workflows and rules" },
            ["Look things up"] = new[] { "Reference lists and instruction sheets" },
        };

        [Fact]
        public void Shelf_order_within_each_question_matches_the_page_verbatim()
        {
            foreach (var q in RatifiedOrder)
                Assert.Equal(RatifiedShelfOrder[q], OpTaxonomy.ShelvesOf(q).ToArray());
            Assert.Empty(OpTaxonomy.ShelvesOf("no-such-question"));
            Assert.Empty(OpTaxonomy.ShelvesOf(null));
        }

        /// <summary>The catalog TRANSMITS the ratified order: entries arrive sorted by (question page
        /// order, shelf page order, name), so a consumer that groups in received order — the picker —
        /// renders the page without re-deriving it. One home for the order, same as for the mapping.</summary>
        [Fact]
        public void Get_op_catalog_is_ordered_by_the_ratified_tree()
        {
            var catalog = LocalEngine.OpSurface.Infos;
            var expected = catalog
                .OrderBy(o => OpTaxonomy.OrderOf(o.Name).QuestionIndex)
                .ThenBy(o => OpTaxonomy.OrderOf(o.Name).ShelfIndex)
                .ThenBy(o => o.Name, StringComparer.Ordinal)
                .Select(o => o.Name).ToArray();
            Assert.Equal(expected, catalog.Select(o => o.Name).ToArray());

            // And the transmitted order really is the page: shelves appear in ratified order as the
            // catalog is walked front to back within each question.
            foreach (var q in RatifiedOrder)
            {
                var shelvesInArrival = catalog.Where(o => o.Question == q).Select(o => o.Shelf).Distinct().ToArray();
                Assert.Equal(RatifiedShelfOrder[q], shelvesInArrival);
            }
        }

        [Fact]
        public void The_27_refiled_ops_sit_exactly_where_the_pages_small_print_filed_them()
        {
            Assert.Equal(27, Refiled.Length);
            Assert.All(Refiled, r =>
            {
                Assert.True(OpTaxonomy.TryGet(r.Op, out var q, out var s), r.Op);
                Assert.True(r.Question == q && r.Shelf == s,
                    $"{r.Op} is filed under '{q}' > '{s}'; the page files it under '{r.Question}' > '{r.Shelf}'.");
            });
        }

        [Fact]
        public void No_name_violates_the_copy_rules()
        {
            // docs/product-copy-style.md: no em dashes anywhere user-facing; these names are UI copy.
            // Also: plain words only — no underscores, no code casing, nothing that reads as engine jargon.
            var names = OpTaxonomy.Questions
                .Concat(OpTaxonomy.Ops.Select(op => { OpTaxonomy.TryGet(op, out _, out var s); return s; }))
                .Distinct(StringComparer.Ordinal).ToArray();
            Assert.All(names, n =>
            {
                Assert.DoesNotContain('—', n);            // em dash
                Assert.DoesNotContain('–', n);            // en dash
                Assert.DoesNotContain('_', n);                 // snake_case = engine speak
                Assert.False(n.Contains("MCP") || n.Contains("TOM") || n.Contains("XMLA") || n.Contains("TMDL"),
                    $"'{n}' reads as engine jargon on a user-facing shelf.");
                Assert.Equal(n.Trim(), n);
            });
            // "DAX" and "AI" appear on the page itself and are ratified spellings, so they pass by
            // being part of the verbatim set — the jargon list above stays engine-side terms only.
        }

        /// <summary>Dual-drive: the same stamped catalog is what get_op_catalog serves, so the MCP
        /// door and the Studio picker read one taxonomy. This walks the ENGINE api, not the static.</summary>
        [Fact]
        public async Task Get_op_catalog_serves_the_taxonomy_through_the_engine_door()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-optax-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            try
            {
                var e = new LocalEngine(new SessionManager(), new FreeTier(), ws);
                var cat = await e.GetOpCatalogAsync();
                Assert.Equal(328, cat.Length);
                Assert.All(cat, o => Assert.False(string.IsNullOrWhiteSpace(o.Question) || string.IsNullOrWhiteSpace(o.Shelf), o.Name));
                var wf = cat.Single(o => o.Name == "start_workflow");
                Assert.Equal("Set the rules of the work", wf.Question);
                Assert.Equal("Workflows and rules", wf.Shelf);
            }
            finally { Directory.Delete(ws, true); }
        }

        private sealed class FreeTier : IEntitlement { public bool IsPro => false; public EntitlementInfo Info => new EntitlementInfo { Tier = "free" }; }
    }
}

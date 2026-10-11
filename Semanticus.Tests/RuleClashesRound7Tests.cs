using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// m11/rule-clashes round 7. The promise, unchanged: an accepted finding is never carried out, by any rule or any path, an
    /// acceptance is never invisible while it declines a change, and a finding nobody accepted is never read as accepted.
    /// Round 6's design stands; these cover what the round 6 verifier found: every plan row that writes asks the declined
    /// set (F1), the table never over-declines (F2, F3, and a check that each row's property is one its rule reads), a
    /// reopened ask brings back its own words (F4), a rule-level acceptance always has a row (F5), and the residues (a
    /// model-level acceptance survives a rename of the model, the merge lookup ignores case).
    /// </summary>
    public sealed class RuleClashesRound7Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public RuleClashesRound7Tests(ModelTemplates templates) => _templates = templates;

        private const string Why = "Declined on purpose; reports depend on it as it is.";

        // ---- the property-check test: no row names a property its own rule does not read ------------------------------------

        private static IEnumerable<BpaRule> AllModelQualityRules() =>
            BpaRuleSet.Standard().Concat(BpaDefaultRules.Rules).GroupBy(r => r.ID, StringComparer.OrdinalIgnoreCase).Select(g => g.First());

        private static IReadOnlyList<ReadinessRule> AllReadinessRules() =>
            ReadinessRuleSet.Default().Concat(ReadinessRuleSet.LiveRules(new ReadinessLiveStats())).ToList();

        private static readonly Regex Identifier = new Regex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

        /// <summary>The identifiers a Model quality condition mentions, outside string literals.</summary>
        private static HashSet<string> ConditionIdentifiers(BpaRule r)
        {
            var text = Regex.Replace(r.Expression ?? "", "\"(?:[^\"\\\\]|\\\\.|\"\")*\"", " ");
            return new HashSet<string>(Identifier.Matches(text).Select(m => m.Value), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Model quality rows whose condition does not name the property directly, with the reason it still reads it.
        /// Everything else must name it, and the test fails on a row that is neither.</summary>
        private static readonly Dictionary<string, string> ConditionReadsIndirectly = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DAX_COLUMNS_FULLY_QUALIFIED"] = "reads the references DependsOn lists, which are the expression's own references",
            ["DAX_MEASURES_UNQUALIFIED"] = "reads the references DependsOn lists, which are the expression's own references",
        };


        [Fact]
        public void Every_model_quality_row_names_a_property_its_own_condition_reads_on_an_object_kind_it_checks()
        {
            var problems = new List<string>();
            var rows = new List<string>();
            foreach (var r in AllModelQualityRules())
            {
                var about = RuleAboutTable.ForBpa(r);
                if (about == null || !about.DeclinesSomething) continue;
                var idents = ConditionIdentifiers(r);
                var read = idents.Contains(about.Property) || (r.TokenCheck != null && about.Property == "expression");
                var why = ConditionReadsIndirectly.TryGetValue(r.ID, out var w) ? w : null;
                rows.Add(r.ID + " | " + about.Property + " | " + (read ? "read by the condition" : "listed: " + (why ?? "NOT READ")));
                if (!read && why == null) problems.Add(r.ID + ": the row names '" + about.Property + "' but the condition never reads it");
                // The row's kinds must be kinds the rule is scoped to, or a rule-level acceptance over-reaches to objects it never checks.
                var scopeKinds = RuleAboutTable.KindsOfScope(r.Scope).Split(',', StringSplitOptions.RemoveEmptyEntries);
                // A finding on the model whose write is on an object of the row's kinds ("no date table": the finding is the model's, the
                // write is a table's data category) names those kinds on purpose.
                foreach (var k in about.Kinds.Where(k => !scopeKinds.Contains(k) && !about.FromModel))
                    problems.Add(r.ID + ": the row names kind '" + k + "' but the rule is scoped to " + r.Scope);
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
            Assert.True(rows.Count >= 40, "the check must see the bundled rules (saw " + rows.Count + ")");
        }

        /// <summary>The C# identifiers a readiness row's property is read as (a check reads its own object's property by one
        /// of these). A row whose check does not mention any of them is listed below with the reason.</summary>
        private static readonly Dictionary<string, string[]> ReadAs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["isHidden"] = new[] { "IsHidden" },
            ["name"] = new[] { "Name" },
            ["description"] = new[] { "Description" },
            ["formatString"] = new[] { "FormatString" },
            ["isDefaultLabel"] = new[] { "IsDefaultLabel", "DimensionRowLabels" },   // DIM-PRIMARY-NAME asks for the row label (Kane 27 Sep)
            ["summarizeBy"] = new[] { "SummarizeBy", "AutoAggregates" },   // FMT-SUMMARIZE asks ReadinessRuleSet.AutoAggregates, which reads it
            ["dataCategory"] = new[] { "DataCategory" },
            ["dataType"] = new[] { "DataType" },
            ["sortByColumn"] = new[] { "SortByColumn" },
            ["crossFilteringBehavior"] = new[] { "CrossFilteringBehavior" },
            ["cardinality"] = new[] { "FromCardinality", "ToCardinality" },
            ["isActive"] = new[] { "IsActive" },
            ["isKey"] = new[] { "IsKey" },
            ["expression"] = new[] { "Expression" },
            ["synonyms"] = new[] { "Synonyms", "Synonym" },
            ["linguisticSchema"] = new[] { "Cultures", "LinguisticMetadata", "Linguistic", "Lsdl", "PrepForAi", "NeedsLinguisticSchema" },   // SYN-SCHEMA asks NeedsLinguisticSchema, which reads Cultures
            ["aiInstructions"] = new[] { "Instructions", "PrepForAi", "AiInstructions" },
        };

        /// <summary>Readiness rows whose check text does not name the property, each with the reason it still reads it. A
        /// row that is neither here nor found by the scan fails the test, so a new row cannot be added unread.</summary>
        private static readonly Dictionary<string, string> CheckReadsIndirectly = new(StringComparer.OrdinalIgnoreCase)
        {
            // The DAX lint rules lint the object's expression text through DaxBestPracticeRule (a class, not a lambda).
            ["BP-DAX-IFERROR"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-IFERROR-DIV"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-IFERROR-SEARCH"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-SUMMARIZE-EXT"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-BARE-TABLE-FILTER"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-VAR-ALIAS"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-ISBLANK-EQ"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-DIV-GUARD"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-ERROR-FN"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-MEASURE-PREDICATE"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-EARLIER"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-VAR-UNUSED"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-SELECTEDVALUE"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-DISTINCTCOUNT"] = "DaxBestPracticeRule lints the object's expression text",
            ["BP-DAX-REMOVEFILTERS"] = "DaxBestPracticeRule lints the object's expression text",
            // Its check measures the expression's length through the DaxObjects helper, which yields (object, name, expression).
            ["LIMIT-DAX-LENGTH"] = "measures the expression text it gets from the DaxObjects helper",
        };

        /// <summary>The text of a rule's definition with string literals and comments blanked, from its id literal to the
        /// closing parenthesis of the call that builds it; null when the id is not built in this file.</summary>
        private static string? RuleDefinition(string source, string id)
        {
            var key = "\"" + id + "\"";
            var from = 0;
            while (true)
            {
                var at = source.IndexOf(key, from, StringComparison.Ordinal);
                if (at < 0) return null;
                from = at + key.Length;
                var head = source.Substring(Math.Max(0, at - 60), Math.Min(60, at));
                if (!Regex.IsMatch(head, @"(new\s+(ObjectRule<[^>]*>|ModelRule)|DaxRule|DaxAdvisory)\s*\(\s*$")) continue;
                var open = source.LastIndexOf('(', at);
                return Balanced(source, open);
            }
        }

        /// <summary>From an open parenthesis to its match, with string literals and comments blanked out.</summary>
        private static string Balanced(string s, int open)
        {
            var sb = new StringBuilder();
            var depth = 0;
            for (var i = open; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { i += 2; while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++; i++; continue; }
                if (c == '"')
                {
                    var verbatim = i > 0 && (s[i - 1] == '@' || (i > 1 && s[i - 1] == '$' && s[i - 2] == '@') || (i > 1 && s[i - 1] == '@' && s[i - 2] == '$'));
                    i++;
                    while (i < s.Length)
                    {
                        if (verbatim && s[i] == '"' && i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                        if (!verbatim && s[i] == '\\') { i += 2; continue; }
                        if (s[i] == '"') break;
                        i++;
                    }
                    sb.Append(' ');
                    continue;
                }
                if (c == '\'' && i + 2 < s.Length) { var close = s[i + 1] == '\\' ? i + 3 : i + 2; if (close < s.Length && s[close] == '\'') { i = close; sb.Append(' '); continue; } }
                if (c == '(') depth++;
                if (c == ')') { depth--; if (depth == 0) { sb.Append(c); return sb.ToString(); } }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string? RepoFile(string relative)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, relative))) d = d.Parent;
            return d == null ? null : Path.Combine(d.FullName, relative);
        }

        [Fact]
        public void Every_readiness_row_names_a_property_its_own_check_reads_or_is_listed_with_the_reason()
        {
            var path = RepoFile(Path.Combine("Semanticus.Analysis", "ReadinessRules.cs"));
            Assert.NotNull(path);
            var source = File.ReadAllText(path!);
            var problems = new List<string>();
            var read = 0;
            foreach (var r in AllReadinessRules())
            {
                var about = RuleAboutTable.ForReadiness(r.Id);
                if (about == null || !about.DeclinesSomething) continue;
                if (!ReadAs.TryGetValue(about.Property, out var names)) { problems.Add(r.Id + ": '" + about.Property + "' has no reading in this test; add it"); continue; }
                if (CheckReadsIndirectly.ContainsKey(r.Id)) continue;
                var def = RuleDefinition(source, r.Id);
                if (def == null) { problems.Add(r.Id + ": its definition was not found in ReadinessRules.cs and it is not listed"); continue; }
                var idents = new HashSet<string>(Identifier.Matches(def).Select(m => m.Value), StringComparer.Ordinal);
                if (names.Any(idents.Contains)) read++;
                else problems.Add(r.Id + ": the row names '" + about.Property + "' but the check never reads " + string.Join(" or ", names));
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
            Assert.True(read >= 20, "the check must see the rules' definitions (matched " + read + ")");
        }

        // ---- F2 and F3: the table never over-declines -----------------------------------------------------------------------

        [Fact]
        public void An_expression_rule_asks_its_own_rewrite_so_accepting_one_declines_no_other()
        {
            var offenders = new List<string>();
            foreach (var r in AllModelQualityRules())
            {
                var a = RuleAboutTable.ForBpa(r);
                if (a != null && string.Equals(a.Property, "expression", StringComparison.OrdinalIgnoreCase) && !a.OwnAsk) offenders.Add("bpa " + r.ID);
            }
            foreach (var r in AllReadinessRules())
            {
                var a = RuleAboutTable.ForReadiness(r.Id);
                if (a != null && string.Equals(a.Property, "expression", StringComparison.OrdinalIgnoreCase) && !a.OwnAsk) offenders.Add("air " + r.Id);
            }
            Assert.True(offenders.Count == 0, "keep-what-is-there on an expression: " + string.Join(", ", offenders));
        }

        [Fact]
        public void A_many_to_many_row_is_about_cardinality_and_not_about_the_direction_of_filtering()
        {
            var about = RuleAboutTable.ForReadiness("REL-M2M");
            Assert.NotEqual("crossFilteringBehavior", about.Property);
            Assert.True(!about.DeclinesSomething || about.Property == "cardinality");
        }

        // ---- the model ------------------------------------------------------------------------------------------------

        private async Task<DecisionFixture> StarAsync(bool free = false, string? save = null)
        {
            // The build runs once per class; every test opens a copy of the saved model (ModelTemplates, lean suite).
            var f = new DecisionFixture(free ? TestEntitlements.Free : TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "star", BuildStarAsync, (save ?? "star") + ((save ?? "star").EndsWith(".bim", StringComparison.Ordinal) ? "" : ".bim"));
            if (save != null) f.UseNull();
            return f;
        }

        private static async Task BuildStarAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Round7", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Order Date", "DateTime", null, "human");
            await e.CreateColumnAsync("table:Sales", "Country", "String", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Product", "human");
            await e.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Product", "ProductName", "String", null, "human");
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "Date", "DateTime", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/Order Date", "column:Date/Date", null, true, "human");
            await e.SetColumnMetadataAsync("column:Sales/ProductKey", null, "Sum", null, null, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Sales YTD", "TOTALYTD ( [Total Sales], 'Date'[Date] )", "human");
        }

        private static Task<SetResult> WaiveAsync(DecisionFixture f, string door, string system, string ruleId, string? objRef) => door == "studio"
            ? new EngineRpcTarget(f.Engine).waiveFinding(system, ruleId, objRef!, Why)
            : McpTools.WaiveFinding(f.Engine, system, ruleId, objRef!, Why);

        private static Task<SetResult> UnwaiveAsync(DecisionFixture f, string door, string system, string ruleId, string objRef) => door == "studio"
            ? new EngineRpcTarget(f.Engine).unwaiveFinding(system, ruleId, objRef)
            : McpTools.UnwaiveFinding(f.Engine, system, ruleId, objRef);

        private static Task<ChangePlanView> ProposeAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).proposePlan()
            : McpTools.ProposePlan(f.Engine);

        private static Task<T> Read<T>(DecisionFixture f, Func<Model, T> read) => f.Sessions.Require().ReadAsync(read);

        /// <summary>Run the plan (Pro): press until nothing Semanticus can do is ready. Returns what each press said.</summary>
        private static async Task<List<ApplyPlanItemsResult>> PressAsync(DecisionFixture f, string door)
        {
            var results = new List<ApplyPlanItemsResult>();
            for (var press = 0; press < 6; press++)
            {
                var plan = await f.Engine.GetModelPlanAsync();
                foreach (var r in plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.ChangeSet?.Stale == true))
                    await f.Engine.ModelPlanActionAsync(r.Id, "propose", "human");
                plan = await f.Engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)) break;
                var result = door == "studio"
                    ? await new EngineRpcTarget(f.Engine).applyPlanItems(null, "human", plan.ReadyDigest)
                    : DoorResult.AsItems(await McpTools.ApplyPlanItems(f.Engine, null, plan.ReadyDigest));
                results.Add(result);
                if (!result.Performed && result.Skipped.Length == 0) break;
                if (results.Count(r => !r.Performed) >= 2) break;
            }
            return results;
        }

        /// <summary>The rows that ran.</summary>
        private static async Task<string[]> RunThePlanAsync(DecisionFixture f, string door) =>
            (await PressAsync(f, door)).Where(r => r.Performed).SelectMany(r => r.ItemIds ?? Array.Empty<string>()).ToArray();

        // ---- F1: every seeded row asks --------------------------------------------------------------------------------------

        /// <summary>Every row Semanticus can do is worked out, and every line it writes carries the rule id it stands for and,
        /// where that rule declines a property, the property its write is about. A line with no rule id is never asked, so
        /// one that could write would slip past every acceptance.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_line_a_run_the_plan_row_can_write_carries_its_rule_and_the_property_its_write_is_about()
        {
            using var f = await StarAsync(save: "R7Walk.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var rows = plan.Items.Where(i => i.Doer == PlanDoers.Semanticus).Select(i => i.Id).ToList();
            Assert.Contains("datetable/model", rows);   // not vacuous: the date row is one of them
            Assert.Contains(rows, r => r.StartsWith("safefix/", StringComparison.Ordinal));
            foreach (var id in rows) await f.Engine.ModelPlanActionAsync(id, "propose", "human");
            var lines = (await f.Engine.GetPlanAsync()).Items.Where(i => i.Source is "deterministic" or "bpa" or "ai").ToList();   // the rows' own lines
            await f.Engine.ProposePlanAsync(null, true, 40, "human");   // and the change plan's seeds, which replace them
            lines.AddRange((await f.Engine.GetPlanAsync()).Items.Where(i => i.Source is "deterministic" or "bpa" or "ai"));
            Assert.True(lines.Count >= 5, "expected the rows' lines, saw " + lines.Count);
            var problems = new List<string>();
            var bpaRules = AllModelQualityRules().ToDictionary(r => r.ID, StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines)
            {
                if (string.IsNullOrWhiteSpace(l.RuleId)) { problems.Add(l.Kind + " on " + l.ObjectRef + ": no rule id"); continue; }
                var about = l.Source == "bpa"
                    ? (bpaRules.TryGetValue(l.RuleId, out var br) ? RuleAboutTable.ForBpa(br) : null)
                    : RuleAboutTable.ForReadiness(l.RuleId);
                if (about != null && about.DeclinesSomething && !string.Equals(about.Property, l.Target, StringComparison.OrdinalIgnoreCase))
                    problems.Add(l.Kind + " on " + l.ObjectRef + ": " + l.RuleId + " is about '" + about.Property + "' but the line's write is about '" + l.Target + "'");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
            Assert.Contains(lines, l => l.Kind == "mark_date_table" && l.RuleId == "DATE-MARK" && l.Target == "dataCategory");
        }

        /// <summary>Each line the Auto rows write is declined, one at a time on a fresh model, by an acceptance of the rule the
        /// line stands for (rule-level, so nothing depends on which object), and Run the plan leaves what it writes alone.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("DATE-MARK", "air")]
        [InlineData("VIS-FK", "air")]
        [InlineData("FMT-SUMMARIZE", "air")]
        [InlineData("CAT-GEO", "air")]
        public async Task Run_the_plan_leaves_alone_what_the_accepted_rule_of_each_row_writes(string ruleId, string system)
        {
            using var f = await StarAsync(save: "R7Run-" + ruleId + ".bim");
            await WaiveAsync(f, "mcp", system, ruleId, null);
            var before = await Read(f, m => (m.Tables["Date"].DataCategory, m.Tables["Date"].Columns["Date"].IsKey, m.Tables["Sales"].Columns["CustomerKey"].IsHidden,
                m.Tables["Sales"].Columns["ProductKey"].SummarizeBy, m.Tables["Sales"].Columns["Country"].DataCategory));

            await RunThePlanAsync(f, "studio");

            var after = await Read(f, m => (m.Tables["Date"].DataCategory, m.Tables["Date"].Columns["Date"].IsKey, m.Tables["Sales"].Columns["CustomerKey"].IsHidden,
                m.Tables["Sales"].Columns["ProductKey"].SummarizeBy, m.Tables["Sales"].Columns["Country"].DataCategory));
            switch (ruleId)
            {
                // Join, part 2: the mark is the data category. A key on the Date column can now be set by the best-practice row's own
                // MARK_PRIMARY_KEYS fix (m11/plan-rows puts Model quality's fixes in Run the plan), a different finding the person did
                // not accept, so only what DATE-MARK declines is asserted here.
                case "DATE-MARK": Assert.Equal(before.Item1, after.Item1); break;
                case "VIS-FK": Assert.Equal(before.Item3, after.Item3); break;
                case "FMT-SUMMARIZE": Assert.Equal(before.Item4, after.Item4); break;
                case "CAT-GEO": Assert.Equal(before.Item5, after.Item5); break;
            }
        }

        /// <summary>The model-level finding "no marked date table" accepted for the model: the row is not offered, nothing is marked,
        /// and the Model quality finding for the same change reads as accepted under it and says which acceptance.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task An_accepted_no_marked_date_table_finding_keeps_the_date_row_and_the_mark_away_and_covers_its_twin(string door)
        {
            using var f = await StarAsync(save: "R7DateOwn-" + door + ".bim");
            var dm = (await f.Engine.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "DATE-MARK");
            await WaiveAsync(f, door, "air", "DATE-MARK", dm.ObjectRef);

            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "datetable/model");
            await RunThePlanAsync(f, door);
            Assert.NotEqual("Time", await Read(f, m => m.Tables["Date"].DataCategory));

            // The Model quality twin is a deploy-gate blocker, so it stays fully open beside the acceptance: a related finding never lifts the gate.
            var twin = (await f.Engine.BpaScanAsync()).Violations.Single(v => v.RuleId == "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE" && v.ObjectRef == "table:Date");
            Assert.False(twin.Waived);
            Assert.Contains("marked date table", twin.CoveredBy, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(DeclinedChanges.TwinReason, twin.WaiverReason);
        }

        /// <summary>The default-tier witness of the theory above (lean suite): one row, the hardest door. The full form runs in the sweep.</summary>
        [Fact]
        public Task An_accepted_no_marked_date_table_finding_keeps_the_date_row_and_the_mark_away_and_covers_its_twin_sample() => An_accepted_no_marked_date_table_finding_keeps_the_date_row_and_the_mark_away_and_covers_its_twin("mcp");

        /// <summary>The Model quality finding accepted on the table: the row is still offered, Run the plan takes it, the line is
        /// left out at apply and named, nothing is marked, and no receipt says the row was applied.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Run_the_plan_names_the_date_mark_an_acceptance_left_out_and_does_not_record_the_row_as_applied(string door)
        {
            using var f = await StarAsync(save: "R7DateNamed-" + door + ".bim");
            await WaiveAsync(f, door, "bpa", "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE", "table:Date");
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Contains(plan.Items, i => i.Id == "datetable/model" && i.Doer == PlanDoers.Semanticus);   // not vacuous: the row is offered

            var results = await PressAsync(f, door);

            var skipped = results.SelectMany(r => r.Skipped).ToList();
            Assert.True(skipped.Any(x => x.ItemId == "datetable/model" && x.What != null), System.Text.Json.JsonSerializer.Serialize(results.Select(r => new { r.Skipped, r.Sentence, r.Performed, r.ItemIds })));
            Assert.All(skipped.Where(x => x.ItemId == "datetable/model" && x.What != null),
                x => Assert.Equal("Left out because you accepted this change under another rule.", x.Reason));
            Assert.Contains(results, r => r.Sentence.Contains("left out because you accepted"));
            Assert.NotEqual("Time", await Read(f, m => m.Tables["Date"].DataCategory));
            var row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "datetable/model");
            Assert.NotEqual(ModelPlanChangeStates.Applied, row.ChangeSet?.State);
        }

        /// <summary>A finding sits on a kind of object its rule's row names (or, for a finding about the model, the row says the write is
        /// on the kinds it names), so a rule-level acceptance never reaches an object the rule does not check.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_finding_only_sits_on_an_object_kind_its_rows_names()
        {
            using var f = await StarAsync();
            var problems = new List<string>();
            var bpaRules = AllModelQualityRules().ToDictionary(r => r.ID, StringComparer.OrdinalIgnoreCase);
            foreach (var x in (await f.Engine.AiReadinessScanAsync()).Findings.Where(x => !x.WholeRuleRow))
            {
                var about = RuleAboutTable.ForReadiness(x.RuleId);
                if (about == null || !about.DeclinesSomething || about.FromModel) continue;
                var kind = x.ObjectRef.Substring(0, x.ObjectRef.IndexOf(':'));
                if (!about.Kinds.Contains(kind)) problems.Add("air " + x.RuleId + " fires on " + x.ObjectRef + " but its row names " + string.Join(",", about.Kinds));
            }
            foreach (var v in (await f.Engine.BpaScanAsync()).Violations.Where(v => !v.WholeRuleRow))
            {
                if (!bpaRules.TryGetValue(v.RuleId, out var rule)) continue;
                var about = RuleAboutTable.ForBpa(rule);
                if (about == null || !about.DeclinesSomething || about.FromModel) continue;
                var kind = v.ObjectRef.Substring(0, v.ObjectRef.IndexOf(':'));
                if (!about.Kinds.Contains(kind)) problems.Add("bpa " + v.RuleId + " fires on " + v.ObjectRef + " but its row names " + string.Join(",", about.Kinds));
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        // ---- F4: a reopened ask brings back its own words -------------------------------------------------------------------

        /// <summary>A line that carries two findings' asks, where the person accepted one of the two: the note says "you accepted this
        /// finding", not "under another rule", because one of the line's own findings is the acceptance.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_merged_line_whose_own_finding_is_accepted_says_so()
        {
            using var f = await StarAsync();
            const string col = "column:Product/ProductName";
            var planned = (await ProposeAsync(f, "mcp")).Items.Single(i => i.ObjectRef == col && i.Target == "name" && !string.IsNullOrEmpty(i.RuleId));
            Assert.Equal("approved", planned.Status == "needs_content" ? "approved" : planned.Status);

            await WaiveAsync(f, "studio", "air", "NAME-COLUMN", col);

            var line = (await f.Engine.GetPlanAsync()).Items.Single(i => i.ObjectRef == col && i.Target == "name" && !string.IsNullOrEmpty(i.RuleId));
            Assert.Equal(("skipped", "Left out because you accepted this finding after it was planned."), (line.Status, line.Note));
        }

        // ---- F5: a rule-level acceptance always has a row -------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", "bpa", "HIDE_FOREIGN_KEYS")]
        [InlineData("mcp", "air", "VIS-FK")]
        [InlineData("mcp", "air", "VIS-KEY")]
        [InlineData("studio", "bpa", "MARK_PRIMARY_KEYS")]
        public async Task A_rule_level_acceptance_has_a_row_on_its_page_that_reopens_it_whether_or_not_the_rule_fires(string door, string system, string rule)
        {
            using var f = await StarAsync();
            await WaiveAsync(f, door, system, rule, null);
            foreach (var key in new[] { "column:Sales/CustomerKey", "column:Sales/ProductKey", "column:Sales/Order Date", "column:Customer/CustomerKey", "column:Product/ProductKey" })
                await f.Engine.SetColumnMetadataAsync(key, true, null, null, null, "human");   // every key hidden by hand: the rule fires nowhere
            await f.Sessions.Require().MutateAsync("human", "setup: keys marked, so the mark rule fires nowhere", m =>
            {
                foreach (var c in m.AllColumns.Where(c => c.Name.EndsWith("Key") || c.Name == "Date")) c.IsKey = true;
            });
            async Task<(bool Any, int Rows, bool Counted)> Rows()
            {
                var bpa = await f.Engine.BpaScanAsync();
                var air = await f.Engine.AiReadinessScanAsync();
                var count = system == "bpa"
                    ? bpa.Violations.Count(v => v.RuleId == rule && v.Waived && v.WaiverRuleLevel)
                    : air.Findings.Count(x => x.RuleId == rule && x.Waived && x.WaiverRuleLevel);
                return (count > 0, count, system == "bpa" ? bpa.WaivedCount >= count : air.WaivedCount >= count);
            }
            // Not vacuous: the rule fires nowhere, so the only row it could have is the one that stands for the acceptance.
            var scanNow = system == "bpa"
                ? (await f.Engine.BpaScanAsync()).Violations.Where(v => v.RuleId == rule && !v.WholeRuleRow).Select(v => v.ObjectRef).ToList()
                : (await f.Engine.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == rule && !x.WholeRuleRow).Select(x => x.ObjectRef).ToList();
            Assert.True(scanNow.Count == 0, rule + " still fires on " + string.Join(", ", scanNow));
            var shown = await Rows();
            Assert.True(shown.Any, "the rule-level acceptance of " + rule + " has no row on its page");
            Assert.True(shown.Counted);

            // Reopening from that row removes the acceptance, and the row goes with it.
            await UnwaiveAsync(f, door, system, rule, "*");
            Assert.False((await Rows()).Any);
        }

        /// <summary>The row says what the acceptance reaches. A rule that declines a property reaches every object of its kinds,
        /// not only the ones it flags, and the row says so; it is never scored and never counted by the deploy gate.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_whole_model_row_says_what_it_reaches_and_is_not_a_finding()
        {
            using var f = await StarAsync();
            await WaiveAsync(f, "mcp", "air", "VIS-KEY", null);
            var before = await f.Engine.AiReadinessScanAsync();
            var row = before.Findings.Single(x => x.RuleId == "VIS-KEY" && x.WaiverRuleLevel && x.WholeRuleRow);
            Assert.Contains("whole model", row.ObjectName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("every column", row.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(row.Waived);

            await WaiveAsync(f, "mcp", "bpa", "HIDE_FOREIGN_KEYS", null);
            var gate = await f.Engine.DeployGateAsync(null, "human");
            var bpa = await f.Engine.BpaScanAsync();
            var gateRows = bpa.Violations.Count(v => v.Severity >= 2 && !v.CanAutoFix && v.Waived && !v.WholeRuleRow && !v.NoLongerFires);
            Assert.Equal(gateRows, gate.BpaWaivedBlocking);   // the whole-model row is not counted as a finding the gate looked at
            Assert.Contains(bpa.Violations, v => v.RuleId == "HIDE_FOREIGN_KEYS" && v.WholeRuleRow || v.RuleId == "HIDE_FOREIGN_KEYS" && v.WaiverRuleLevel);
        }

        // ---- F5 and the twin rows: the reason names the covering acceptance --------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_twin_row_names_the_acceptance_that_covers_it()
        {
            using var f = await StarAsync();
            await WaiveAsync(f, "mcp", "air", "VIS-FK", "column:Sales/CustomerKey");
            var bpa = await f.Engine.BpaScanAsync();
            var twin = bpa.Violations.Single(v => v.RuleId == "HIDE_FOREIGN_KEYS" && v.ObjectRef == "column:Sales/CustomerKey");
            Assert.True(twin.WaivedAsTwin);
            Assert.Contains("AI readiness", twin.CoveredBy);   // the page the covering acceptance is on
            Assert.Contains("visible", twin.CoveredBy, StringComparison.OrdinalIgnoreCase);   // and the rule's own name
            Assert.Equal(Why, twin.CoveredReason);
        }

        // ---- the residues ---------------------------------------------------------------------------------------------------

        /// <summary>A model-level acceptance is stored under a key that does not carry the model's name, so renaming the model
        /// does not orphan it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_model_level_acceptance_survives_a_rename_of_the_model()
        {
            using var f = await StarAsync();
            var card = await f.Engine.AiReadinessScanAsync();
            var modelFinding = card.Findings.First(x => x.ObjectRef.StartsWith("obj:Model", StringComparison.Ordinal) && !x.Waived);
            await WaiveAsync(f, "mcp", "air", modelFinding.RuleId, modelFinding.ObjectRef);
            var stored = await Read(f, m => WaiverStore.Load(m).Single(w => w.RuleId == modelFinding.RuleId).ObjectRef);
            Assert.DoesNotContain("Round7", stored);

            await f.Sessions.Require().MutateAsync("human", "rename the model", m => m.Name = "Renamed model");

            var after = await f.Engine.AiReadinessScanAsync();
            Assert.Contains(after.Findings, x => x.RuleId == modelFinding.RuleId && x.ObjectRef.StartsWith("obj:Model", StringComparison.Ordinal) && x.Waived && !x.NoLongerFires);
            Assert.Empty(after.OrphanedWaivers);
            // and Reopen, which the page sends with the finding's current ref, still finds it
            var unwaived = await UnwaiveAsync(f, "studio", "air", modelFinding.RuleId, after.Findings.First(x => x.RuleId == modelFinding.RuleId).ObjectRef);
            Assert.True(unwaived.Changed);
            Assert.Contains((await f.Engine.AiReadinessScanAsync()).Findings, x => x.RuleId == modelFinding.RuleId && !x.Waived);
        }

        /// <summary>The plan merges a second finding's ask onto a line that asks for the same property of the same object, and
        /// the lookup ignores case as the de-duplication does: Model quality lower-cases its targets ("formatstring").</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_plan_merges_an_ai_ask_onto_a_model_quality_line_for_the_same_property_whatever_the_case()
        {
            using var f = await StarAsync();
            var card = await f.Engine.AiReadinessScanAsync();
            var fmt = card.Findings.Single(x => x.RuleId == "FMT-MEASURE" && x.ObjectRef == "measure:Sales/Order Count");
            var bpa = await f.Engine.BpaScanAsync();
            Assert.Contains(bpa.Violations, v => v.RuleId == "INTEGER_FORMATTING" && v.ObjectRef == "measure:Sales/Order Count" && v.CanAutoFix);   // not vacuous: both fire

            var plan = await f.Engine.ProposePlanAsync(null, true, 40, "human");

            var line = plan.Items.Single(i => i.ObjectRef == "measure:Sales/Order Count" && string.Equals(i.Target, "formatString", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(fmt.Message, line.Rationale ?? "");
        }

        // ---- the lows -------------------------------------------------------------------------------------------------------

        [Fact]
        public void The_format_flag_rule_asks_the_format_string_its_description_says_and_names_rows_only_for_kinds_it_checks()
        {
            var flag = AllModelQualityRules().Single(r => r.ID == "FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS");
            var about = RuleAboutTable.ForBpa(flag);
            Assert.True(about.DeclinesSomething);
            Assert.Equal("formatString", about.Property, ignoreCase: true);
            Assert.Equal(new[] { "column" }, about.Kinds);
            // the readiness name rules that fire on columns only list only columns
            Assert.Equal(new[] { "column" }, RuleAboutTable.ForReadiness("NAME-DUP").Kinds);
            Assert.DoesNotContain("hierarchy", RuleAboutTable.ForReadiness("NAME-INVALID-CHARS").Kinds);
        }
    }
}

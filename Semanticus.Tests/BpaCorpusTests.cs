using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The shipped Best Practice Analyzer corpus: that it loads at all, that it is the pinned shape, and that
    /// every expression in it actually compiles and runs.
    ///
    /// Why this exists: <c>BpaRuleSet.Standard()</c> locates the corpus by resource-name SUFFIX and swallows any
    /// load failure, returning an empty set; <c>LocalEngine.GetBpaRules</c> then quietly serves the 9-rule
    /// hardcoded fallback. So a renamed file or malformed JSON degrades to a much smaller rule set with no
    /// exception and no failing test anywhere. These tests are the tripwire for that, and they are the only
    /// place the shipped corpus is asserted against the file rather than assumed.
    ///
    /// They assert the rule id SET, not just the count. The count alone was the whole check until 2026-07-30 and
    /// it cannot distinguish one 74-rule corpus from another, so a swap, a rename or a drop-one-and-add-one was
    /// invisible. Both directions of the set comparison are reported by name when they fail.
    /// </summary>
    public sealed class BpaCorpusTests
    {
        // THE CORPUS IS PINNED BY IDENTITY, NOT BY COUNT. Until 2026-07-30 these tests asserted only
        // `rules.Count == 74` and `rules.Count - authored.Count == 69`, so the five SEM_ rules were pinned by id
        // while the 69 Microsoft rules were not pinned at all: swapping one rule for another, renaming one, or
        // dropping one and adding one all preserve both counts and passed every assertion here. A count is not a
        // set. The ids below are the set, read out of the shipped file, and every one of them was confirmed to
        // exist in the pinned upstream (microsoft/Analysis-Services at commit
        // 50e8ce5028dd7046e73974ec69cb79af225e41b0, BestPracticeRules/BPARules.json, 71 rules).
        //
        // We ship 69 of Microsoft's 71: two declare ONLY scopes our evaluator does not support
        // (TablePermission, ProviderDataSource/StructuredDataSource) and would sit in the corpus looking like
        // checks while never evaluating anything. Both omissions, two scope narrowings and the five rules we
        // authored ourselves are enumerated in Rules/BPARules-PowerBI.PROVENANCE.md.
        //
        // Sorted with the ordinal comparer, which is how the assertion reports a diff. Keep it sorted.
        private static readonly string[] MicrosoftRuleIds =
        {
            "ADD_DATA_CATEGORY_FOR_COLUMNS",
            "AVOID_BI-DIRECTIONAL_RELATIONSHIPS_AGAINST_HIGH-CARDINALITY_COLUMNS",
            "AVOID_DUPLICATE_MEASURES",
            "AVOID_EXCESSIVE_BI-DIRECTIONAL_OR_MANY-TO-MANY_RELATIONSHIPS",
            "AVOID_FLOATING_POINT_DATA_TYPES",
            "AVOID_INVALID_DESCRIPTION_CHARACTERS",
            "AVOID_INVALID_NAME_CHARACTERS",
            "AVOID_STRUCTURED_DATA_SOURCES_WITH_PROVIDER_PARTITIONS",
            "AVOID_THE_USERELATIONSHIP_FUNCTION_AND_RLS_AGAINST_THE_SAME_TABLE",
            "AVOID_USING_'1-(X/Y)'_SYNTAX",
            "AVOID_USING_MANY-TO-MANY_RELATIONSHIPS_ON_TABLES_USED_FOR_DYNAMIC_ROW_LEVEL_SECURITY",
            "AVOID_USING_THE_IFERROR_FUNCTION",
            "CALCULATION_GROUPS_WITH_NO_CALCULATION_ITEMS",
            "CHECK_IF_BI-DIRECTIONAL_AND_MANY-TO-MANY_RELATIONSHIPS_ARE_VALID",
            "DATA_COLUMNS_MUST_HAVE_A_SOURCE_COLUMN",
            "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE",
            "DATECOLUMN_FORMATSTRING",
            "DAX_COLUMNS_FULLY_QUALIFIED",
            "DAX_MEASURES_UNQUALIFIED",
            "ENSURE_TABLES_HAVE_RELATIONSHIPS",
            "EVALUATEANDLOG_SHOULD_NOT_BE_USED_IN_PRODUCTION_MODELS",
            "EXPRESSION_RELIANT_OBJECTS_MUST_HAVE_AN_EXPRESSION",
            "FILTER_COLUMN_VALUES",
            "FILTER_MEASURE_VALUES_BY_COLUMNS",
            "FIRST_LETTER_OF_OBJECTS_MUST_BE_CAPITALIZED",
            "FIX_REFERENTIAL_INTEGRITY_VIOLATIONS",
            "FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS",
            "HIDE_FACT_TABLE_COLUMNS",
            "HIDE_FOREIGN_KEYS",
            "INACTIVE_RELATIONSHIPS_THAT_ARE_NEVER_ACTIVATED",
            "INTEGER_FORMATTING",
            "ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS",
            "LARGE_TABLES_SHOULD_BE_PARTITIONED",
            "LIMIT_ROW_LEVEL_SECURITY_(RLS)_LOGIC",
            "MANY-TO-MANY_RELATIONSHIPS_SHOULD_BE_SINGLE-DIRECTION",
            "MARK_PRIMARY_KEYS",
            "MEASURES_SHOULD_NOT_BE_DIRECT_REFERENCES_OF_OTHER_MEASURES",
            "MEASURES_USING_TIME_INTELLIGENCE_AND_MODEL_IS_USING_DIRECT_QUERY",
            "MINIMIZE_POWER_QUERY_TRANSFORMATIONS",
            "MODEL_SHOULD_HAVE_A_DATE_TABLE",
            "MODEL_USING_DIRECT_QUERY_AND_NO_AGGREGATIONS",
            "MONTHCOLUMN_FORMATSTRING",
            "MONTH_(AS_A_STRING)_MUST_BE_SORTED",
            "NUMERIC_COLUMN_SUMMARIZE_BY",
            "OBJECTS_SHOULD_NOT_START_OR_END_WITH_A_SPACE",
            "OBJECTS_WITH_NO_DESCRIPTION",
            "PARTITION_NAME_SHOULD_MATCH_TABLE_NAME_FOR_SINGLE_PARTITION_TABLES",
            "PERCENTAGE_FORMATTING",
            "PERSPECTIVES_WITH_NO_OBJECTS",
            "PROVIDE_FORMAT_STRING_FOR_MEASURES",
            "REDUCE_NUMBER_OF_CALCULATED_COLUMNS",
            "REDUCE_USAGE_OF_CALCULATED_COLUMNS_THAT_USE_THE_RELATED_FUNCTION",
            "REDUCE_USAGE_OF_CALCULATED_TABLES",
            "REDUCE_USAGE_OF_LONG-LENGTH_COLUMNS_WITH_HIGH_CARDINALITY",
            "RELATIONSHIP_COLUMNS_SAME_DATA_TYPE",
            "RELATIONSHIP_COLUMNS_SHOULD_BE_OF_INTEGER_DATA_TYPE",
            "REMOVE_AUTO-DATE_TABLE",
            "REMOVE_REDUNDANT_COLUMNS_IN_RELATED_TABLES",
            "REMOVE_ROLES_WITH_NO_MEMBERS",
            "SET_ISAVAILABLEINMDX_TO_TRUE_ON_NECESSARY_COLUMNS",
            "SNOWFLAKE_SCHEMA_ARCHITECTURE",
            "SPECIAL_CHARS_IN_OBJECT_NAMES",
            "SPLIT_DATE_AND_TIME",
            "TRIM_OBJECT_NAMES",
            "UNNECESSARY_COLUMNS",
            "UNNECESSARY_MEASURES",
            "UNPIVOT_PIVOTED_(MONTH)_DATA",
            "USE_THE_DIVIDE_FUNCTION_FOR_DIVISION",
            "USE_THE_TREATAS_FUNCTION_INSTEAD_OF_INTERSECT",
        };

        private static readonly string[] AuthoredRuleIds =
        {
            "SEM_COLUMN_NEEDS_FORMAT_STRING",
            "SEM_RELATIONSHIP_KEY_NAMES_SHOULD_MATCH",
            "SEM_UNFINISHED_MARKER_IN_EXPRESSION",
            "SEM_MEASURES_NEED_DISPLAY_FOLDERS",
            "SEM_COLUMNS_NEED_DISPLAY_FOLDERS",
        };

        // Derived from the pinned sets on purpose. As hand-written constants these could disagree with the id
        // lists, which would put the "is it the right corpus" question back into a number nobody re-counts.
        private static int MicrosoftRuleCount => MicrosoftRuleIds.Length;   // 69
        private static int AuthoredRuleCount => AuthoredRuleIds.Length;     // 5
        private static int TotalRuleCount => MicrosoftRuleCount + AuthoredRuleCount;   // 74

        /// <summary>
        /// Assert that a shipped id collection IS the pinned set, reporting both directions by name. Set equality
        /// is what catches the mutations a count cannot see: a swap, a rename, or a drop-one-and-add-one.
        /// </summary>
        private static void AssertIsPinnedIdSet(IEnumerable<string> actualIds, string where)
        {
            var expected = MicrosoftRuleIds.Concat(AuthoredRuleIds).ToHashSet(StringComparer.Ordinal);
            var actual = actualIds.ToHashSet(StringComparer.Ordinal);

            var missing = expected.Except(actual).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var unexpected = actual.Except(expected).OrderBy(x => x, StringComparer.Ordinal).ToList();

            Assert.True(missing.Count == 0 && unexpected.Count == 0,
                where + " is not the pinned corpus." + Environment.NewLine
                + "pinned but NOT shipped: " + (missing.Count == 0 ? "none" : string.Join(", ", missing))
                + Environment.NewLine
                + "shipped but NOT pinned: " + (unexpected.Count == 0 ? "none" : string.Join(", ", unexpected))
                + Environment.NewLine
                + "If this change is intended, update the id lists in BpaCorpusTests and say in the commit "
                + "message which upstream commit the new ids came from.");
        }

        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        /// <summary>The corpus as shipped, read straight off the embedded resource.</summary>
        private static string RawCorpusJson()
        {
            var asm = typeof(BpaRuleSet).Assembly;
            var name = asm.GetManifestResourceNames()
                          .SingleOrDefault(n => n.EndsWith("BPARules-PowerBI.json", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(name);   // the suffix match BpaRuleSet itself relies on
            using var s = asm.GetManifestResourceStream(name!);
            using var r = new StreamReader(s!);
            return r.ReadToEnd();
        }

        private static async Task<LocalEngine> FreshAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake());
            await engine.CreateModelAsync("BpaCorpus", 1601);
            var t = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            await engine.CreateMeasureAsync(t, "Total", "SUM(Facts[Amount])", "human");
            return engine;
        }

        [Fact]
        public void Standard_corpus_is_the_pinned_rule_set()
        {
            var rules = BpaRuleSet.Standard();

            // Guards the silent-degradation path: an empty or short set means the resource did not load and the
            // 9-rule hardcoded fallback is being served instead.
            Assert.Equal(TotalRuleCount, rules.Count);

            // The count above cannot tell one corpus from another. This can: it pins WHICH 74 rules ship.
            AssertIsPinnedIdSet(rules.Select(r => r.ID), "BpaRuleSet.Standard()");

            var authored = rules.Where(r => r.ID.StartsWith("SEM_", StringComparison.Ordinal)).ToList();
            Assert.Equal(AuthoredRuleCount, authored.Count);
            Assert.Equal(MicrosoftRuleCount, rules.Count - authored.Count);
        }

        [Fact]
        public void Rule_ids_are_unique_and_safe_as_composite_keys()
        {
            var ids = BpaRuleSet.Standard().Select(r => r.ID).ToList();

            // Parse() de-dups by id case-insensitively, LAST WINS, silently. A duplicate would delete a rule
            // with no error, so assert uniqueness on the same comparison Parse uses.
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // Finding identity is composed as RuleId + "|" + ObjectRef in the workflow lanes, unescaped, so an
            // id containing the separator would collide two different findings into one key.
            Assert.DoesNotContain(ids, i => i.Contains('|'));

            Assert.DoesNotContain(ids, string.IsNullOrWhiteSpace);
        }

        [Fact]
        public void Every_authored_rule_is_present_and_carries_its_own_copy()
        {
            var byId = BpaRuleSet.Standard().ToDictionary(r => r.ID, StringComparer.OrdinalIgnoreCase);

            foreach (var id in AuthoredRuleIds)
            {
                Assert.True(byId.ContainsKey(id), $"authored rule {id} is missing from the shipped corpus");
                var r = byId[id];
                Assert.False(string.IsNullOrWhiteSpace(r.Name));
                Assert.False(string.IsNullOrWhiteSpace(r.Description));
                Assert.False(string.IsNullOrWhiteSpace(r.Expression));
                Assert.False(string.IsNullOrWhiteSpace(r.Category));
            }
        }

        /// <summary>
        /// The real proof that the corpus works: run every shipped rule through the same authoring validator
        /// that backs validate_rule, which compiles each expression against its declared scope and test-runs it.
        /// A rule that does not compile is reported by name, because "74 rules load" means nothing if half of
        /// them throw at scan time.
        /// </summary>
        [Fact]
        public async Task Every_shipped_rule_compiles_and_runs()
        {
            var engine = await FreshAsync();

            var result = await engine.ValidateRuleAsync("bpa", RawCorpusJson());

            Assert.Equal(TotalRuleCount, result.Rules.Length);
            // Pinned by identity here too. This reads the EMBEDDED RESOURCE rather than BpaRuleSet.Standard(), so
            // it is the check that would catch the shipped file and the loaded set drifting apart.
            AssertIsPinnedIdSet(result.Rules.Select(r => r.Id), "the embedded BPARules-PowerBI.json resource");

            var broken = result.Rules
                .Where(r => !r.Valid)
                .Select(r => $"{r.Id}: {string.Join("; ", r.Errors ?? Array.Empty<string>())}")
                .ToList();

            Assert.True(broken.Count == 0,
                "these shipped rules do not compile:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
            Assert.True(result.AllValid);
        }

        /// <summary>
        /// A scan over a real model completes and attributes every violation to a rule that is actually in the
        /// corpus. Catches a rule that compiles in isolation but throws once it walks a live object graph.
        /// </summary>
        [Fact]
        public async Task Scan_completes_and_every_violation_maps_to_a_live_rule()
        {
            var engine = await FreshAsync();

            var scan = await engine.BpaScanAsync();

            var live = BpaRuleSet.Standard().Select(r => r.ID).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var orphans = scan.Violations.Select(v => v.RuleId).Where(id => !live.Contains(id)).Distinct().ToList();
            Assert.True(orphans.Count == 0, "violations reported against unknown rule ids: " + string.Join(", ", orphans));
        }

        // ---- quoted FixExpression: semicolons are data, unicode decodes once, preview equals apply ------------
        // PERCENTAGE_FORMATTING's advertised fix is a semicolon-separated format string. Eligibility used to
        // treat any ';' as a second statement, Coerce stripped quotes without decoding \u003B, and PreviewFix
        // took the last '.' segment, so the plan preview and the written value could not agree.

        private const string CanonicalPercentage = "#,0.0%;-#,0.0%;#,0.0%";
        private const string QuotedPercentageFix = "FormatString = \"#,0.0%;-#,0.0%;#,0.0%\"";
        // The six-character sequence backslash-u-003B, which JSON leaves in the shipped corpus today.
        private const string UnicodePercentageFix = "FormatString = \"#,0.0%\\u003B-#,0.0%\\u003B#,0.0%\"";

        private static BpaRule PercentageRule(string fix) => new BpaRule
        {
            ID = "PERCENTAGE_FORMATTING",
            Name = "percentages",
            Category = "Formatting",
            Severity = 2,
            Scope = "Measure",
            Expression = "FormatString.Contains(\"%\")",
            FixExpression = fix,
        };

        private static async Task<(LocalEngine engine, SessionManager sessions)> PercentageModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("PctFix", 1604);
            var t = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            var m = await engine.CreateMeasureAsync(t, "Margin", "1", "human");
            await engine.SetMeasureFormatAsync(m, "0%", "human");
            return (engine, sessions);
        }

        [Fact]
        public void Quoted_semicolons_in_a_fix_assignment_are_data()
        {
            Assert.True(BpaAnalyzer.CanAutoFix(QuotedPercentageFix));
            Assert.False(BpaAnalyzer.CanAutoFix("IsHidden = true; FormatString = \"x\""));
        }

        [Fact]
        public void Unicode_escapes_in_a_quoted_fix_are_auto_fixable()
        {
            Assert.True(BpaAnalyzer.CanAutoFix(UnicodePercentageFix));
        }

        [Fact]
        public void Shipped_percentage_formatting_fix_is_auto_fixable()
        {
            var rule = BpaRuleSet.Standard().Single(r => r.ID == "PERCENTAGE_FORMATTING");
            Assert.True(BpaAnalyzer.CanAutoFix(rule.FixExpression));
        }

        [Fact]
        public async Task Percentage_preview_equals_the_value_written_by_direct_apply()
        {
            var (engine, sessions) = await PercentageModelAsync();
            using (engine)
            {
                var rule = PercentageRule(QuotedPercentageFix);
                var preview = await sessions.Require().ReadAsync(m =>
                {
                    var obj = m.AllMeasures.Single(x => x.Name == "Margin");
                    return BpaAnalyzer.PreviewFix(rule, obj);
                });
                Assert.Equal("FormatString", preview.prop);
                Assert.Equal(CanonicalPercentage, preview.after);

                await sessions.Require().MutateAsync("human", "percentage fix", m =>
                {
                    var obj = m.AllMeasures.Single(x => x.Name == "Margin");
                    Assert.True(BpaAnalyzer.ApplyFix(m, rule, obj));
                });

                var written = await sessions.Require().ReadAsync(m =>
                    m.AllMeasures.Single(x => x.Name == "Margin").FormatString);
                Assert.Equal(CanonicalPercentage, written);
                Assert.Equal(preview.after, written);
            }
        }

        [Fact]
        public async Task Unicode_percentage_fix_decodes_once_to_the_same_written_value()
        {
            var (engine, sessions) = await PercentageModelAsync();
            using (engine)
            {
                var rule = PercentageRule(UnicodePercentageFix);
                var preview = await sessions.Require().ReadAsync(m =>
                {
                    var obj = m.AllMeasures.Single(x => x.Name == "Margin");
                    return BpaAnalyzer.PreviewFix(rule, obj);
                });
                Assert.Equal(CanonicalPercentage, preview.after);

                await sessions.Require().MutateAsync("human", "unicode percentage fix", m =>
                {
                    var obj = m.AllMeasures.Single(x => x.Name == "Margin");
                    Assert.True(BpaAnalyzer.ApplyFix(m, rule, obj));
                });

                var written = await sessions.Require().ReadAsync(m =>
                    m.AllMeasures.Single(x => x.Name == "Margin").FormatString);
                Assert.Equal(CanonicalPercentage, written);
                Assert.Equal(preview.after, written);
            }
        }

        [Fact]
        public async Task Bulk_percentage_fix_writes_the_same_value_as_preview()
        {
            var (engine, sessions) = await PercentageModelAsync();
            using (engine)
            {
                var ruleJson =
                    "[{\"ID\":\"PERCENTAGE_FORMATTING\",\"Name\":\"percentages\",\"Category\":\"Formatting\"," +
                    "\"Severity\":2,\"Scope\":\"Measure\",\"Expression\":\"FormatString.Contains(\\\"%\\\")\"," +
                    "\"FixExpression\":\"FormatString = \\\"#,0.0%;-#,0.0%;#,0.0%\\\"\"}]";
                await engine.LoadBpaRulesAsync(ruleJson, replace: true, "human");

                var preview = await sessions.Require().ReadAsync(m =>
                {
                    var rule = BpaRuleSet.Parse(ruleJson).Single();
                    var obj = m.AllMeasures.Single(x => x.Name == "Margin");
                    return BpaAnalyzer.PreviewFix(rule, obj);
                });
                Assert.Equal(CanonicalPercentage, preview.after);

                var bulk = await engine.BpaFixAllAsync("human");
                Assert.True(bulk.Applied >= 1);

                var written = await sessions.Require().ReadAsync(m =>
                    m.AllMeasures.Single(x => x.Name == "Margin").FormatString);
                Assert.Equal(CanonicalPercentage, written);
                Assert.Equal(preview.after, written);
            }
        }

        // ---- object-aware fix eligibility: an invalid literal is not "auto-fixable" ---------------------
        // A custom rule (load_bpa_rules) is the only path that can introduce an arbitrary FixExpression, and
        // eligibility used to be pure SYNTAX: "IsHidden = yes" and an unknown enum member both parse as
        // deterministic assignments, so the scan advertised a fix that cannot exist on the object. Two things
        // broke downstream. propose_plan seeds BPA fixes through PreviewFix, whose conversion THREW on the first
        // such rule, so one bad custom rule aborted the whole proposal instead of omitting one item. And the
        // deploy gate blocks on Severity >= 2 && !CanAutoFix (LocalEngine.DeployGateAsync), so the same
        // syntax-only "true" quietly cleared a blocking violation nobody could actually fix.

        private const string InvalidFixRulesJson =
            "[{\"ID\":\"TEST_BAD_BOOL_FIX\",\"Name\":\"Invalid boolean fix literal\",\"Category\":\"Formatting\",\"Severity\":2,\"Scope\":\"Measure\",\"Expression\":\"FormatString.Contains(\\\"%\\\")\",\"FixExpression\":\"IsHidden = yes\"},{\"ID\":\"TEST_BAD_ENUM_FIX\",\"Name\":\"Unknown enum member fix\",\"Category\":\"Formatting\",\"Severity\":3,\"Scope\":\"Column\",\"Expression\":\"Name == \\\"Amount\\\"\",\"FixExpression\":\"SummarizeBy = AggregateFunction.Bogus\"}]";

        [Fact]
        public async Task An_invalid_fix_literal_is_not_auto_fixable_and_still_blocks_the_deploy_gate()
        {
            var (engine, sessions) = await PercentageModelAsync();
            using (engine)
            {
                await engine.LoadBpaRulesAsync(InvalidFixRulesJson, replace: true, "human");

                var scan = await engine.BpaScanAsync();
                var badBool = scan.Violations.Where(v => v.RuleId == "TEST_BAD_BOOL_FIX").ToArray();
                var badEnum = scan.Violations.Where(v => v.RuleId == "TEST_BAD_ENUM_FIX").ToArray();
                Assert.NotEmpty(badBool);
                Assert.NotEmpty(badEnum);
                Assert.All(badBool, v => Assert.False(v.CanAutoFix, "'yes' is not a Boolean, so this fix cannot apply"));
                Assert.All(badEnum, v => Assert.False(v.CanAutoFix, "'Bogus' is not an AggregateFunction member"));

                // Severity 2 and 3, unwaived, not really fixable: both must reach the gate as blockers.
                var gate = await engine.DeployGateAsync(null, "human");
                Assert.True(gate.BpaBlocking >= badBool.Length + badEnum.Length,
                    $"expected at least {badBool.Length + badEnum.Length} blocking violations, got {gate.BpaBlocking}");
                Assert.Contains(gate.Blockers, b => b != null && b.Contains("Invalid boolean fix literal", StringComparison.Ordinal));
                Assert.Contains(gate.Blockers, b => b != null && b.Contains("Unknown enum member fix", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task Proposing_a_plan_omits_an_invalid_custom_fix_and_keeps_the_valid_items()
        {
            var (engine, sessions) = await PercentageModelAsync();
            using (engine)
            {
                await engine.LoadBpaRulesAsync(InvalidFixRulesJson, replace: true, "human");

                var plan = await engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, origin: "test");

                Assert.DoesNotContain(plan.Items, i => i.RuleId == "TEST_BAD_BOOL_FIX" || i.RuleId == "TEST_BAD_ENUM_FIX");
                // The proposal still carries the shipped rule's real fix — one bad rule omits one item, it does not
                // cost the user every other seed in the plan.
                var pct = Assert.Single(plan.Items.Where(i => i.Kind == "bpa_fix" && i.RuleId == "PERCENTAGE_FORMATTING"));
                Assert.Equal(CanonicalPercentage, pct.After);
            }
        }

        [Fact]
        public async Task Applying_an_invalid_fix_directly_refuses_loudly_instead_of_mutating()
        {
            var (engine, sessions) = await PercentageModelAsync();
            using (engine)
            {
                var rule = PercentageRule("IsHidden = yes");
                var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                    sessions.Require().MutateAsync("human", "invalid fix", m =>
                    {
                        var obj = m.AllMeasures.Single(x => x.Name == "Margin");
                        BpaAnalyzer.ApplyFix(m, rule, obj);
                    }));
                Assert.Contains("yes", ex.Message, StringComparison.Ordinal);

                var stillVisible = await sessions.Require().ReadAsync(m =>
                    m.AllMeasures.Single(x => x.Name == "Margin").IsHidden);
                Assert.False(stillVisible);
            }
        }

        // ---- quoted-literal escapes: the parser and the conversion must agree on ONE decoding -----------
        // The eligibility scan already treated a backslash inside a quoted literal as an escape (that is how a
        // semicolon-bearing format string with an embedded quote stays ONE statement), but the conversion only
        // stripped the outer quotes and decoded \uXXXX. So the escaping characters themselves were written into
        // the model. One left-to-right pass now decodes \" , \\ and \uXXXX, and an emitted character is never
        // re-scanned, so a doubled backslash before a u-escape stays literal text.

        [Theory]
        [InlineData("FormatString = \"a\\\"b;c\"", "a\"b;c")]
        [InlineData("FormatString = \"a\\\\b;c\"", "a\\b;c")]
        [InlineData("FormatString = \"\\\\u0041;x\"", "\\u0041;x")]
        [InlineData("FormatString = \"\\u0041;x\"", "A;x")]
        public async Task Quoted_literal_escapes_decode_once_and_preview_equals_apply(string fix, string expected)
        {
            Assert.True(BpaAnalyzer.CanAutoFix(fix), "an escaped quote or backslash keeps the literal a single statement");

            var (engine, sessions) = await PercentageModelAsync();
            using (engine)
            {
                var rule = PercentageRule(fix);
                var preview = await sessions.Require().ReadAsync(m =>
                {
                    var obj = m.AllMeasures.Single(x => x.Name == "Margin");
                    return BpaAnalyzer.PreviewFix(rule, obj);
                });
                Assert.Equal("FormatString", preview.prop);
                Assert.Equal(expected, preview.after);

                await sessions.Require().MutateAsync("human", "escaped literal fix", m =>
                {
                    var obj = m.AllMeasures.Single(x => x.Name == "Margin");
                    Assert.True(BpaAnalyzer.ApplyFix(m, rule, obj));
                });

                var written = await sessions.Require().ReadAsync(m =>
                    m.AllMeasures.Single(x => x.Name == "Margin").FormatString);
                Assert.Equal(expected, written);
                Assert.Equal(preview.after, written);
            }
        }

        [Fact]
        public void A_second_statement_after_a_quoted_literal_is_still_refused()
        {
            // The closing quote ends the data; the ';' after it is a second statement, not a format separator.
            Assert.False(BpaAnalyzer.CanAutoFix("FormatString = \"a;b\"; IsHidden = true"));
        }
    }
}

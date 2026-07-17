using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;

namespace Semanticus.AirSmoke
{
    /// <summary>
    /// P-AIR1 proof: the AI-readiness engine end-to-end (headless, the same path the user's Claude
    /// drives over MCP): scan -> A-F grade + findings -> apply safe deterministic fixes -> re-score
    /// improves -> grounding for a measure -> set an AI description -> coverage improves.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static async Task<int> Main()
        {
            var sessions = new SessionManager();
            // Smoke harness runs as Pro so it exercises the BULK functionality (apply_safe_fixes etc.); the Pro GATE
            // itself is covered by Semanticus.Tests/EntitlementGateTests.
            var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            try
            {
                var bim = FindTestBim();
                await engine.OpenAsync(bim);
                Console.WriteLine($"[i] opened {Path.GetFileName(bim)}");

                // ---- SCAN -----------------------------------------------------------------------
                var c1 = await engine.AiReadinessScanAsync();
                Console.WriteLine($"[i] grade {c1.Grade} ({c1.Overall}/100, raw {c1.RawOverall}) | findings {c1.Findings.Length} | safe-fixes {c1.SafeFixCount}");
                foreach (var cat in c1.Categories.Where(c => c.HasRules))
                    Console.WriteLine($"      {cat.Category,-16} {cat.Score,5:0.0}  (viol {cat.Violations}/{cat.Applicable})");
                if (c1.GatedBy.Length > 0) Console.WriteLine("      gated by: " + string.Join("; ", c1.GatedBy));
                // Catalog batch 2 precision diagnostic: dump any firings of the new rules on AdventureWorks — every
                // firing must be a GENUINE finding (false positives get tuned out before ship, per the batch-1 lesson).
                var batch2Ids = new[] { "LIMIT-QNA-INDEX", "LIMIT-DATAAGENT-TABLES", "DATE-AMBIGUOUS",
                    "REL-HIERARCHY-SINGLE-LEVEL", "NAME-HIERARCHY", "DAC-PERSPECTIVE-NOT-SCOPE", "DAC-FIELD-PARAM-COMPLEXITY",
                    "NAME-COLUMN-ID" };
                foreach (var id in batch2Ids)
                    foreach (var f in c1.Findings.Where(x => x.RuleId == id))
                        Console.WriteLine($"[batch2] {id} -> {f.ObjectName}: {f.Message.Substring(0, Math.Min(100, f.Message.Length))}");
                // Batch-2 true-positive + precision assertions on AdventureWorks (a real model):
                bool AwFiresOn(string id, string obj) => c1.Findings.Any(f => f.RuleId == id && f.ObjectName == obj);
                Check("DATE-AMBIGUOUS: fires on a fact with role-playing event dates (Internet Sales — Order/Due/Ship)", AwFiresOn("DATE-AMBIGUOUS", "Internet Sales"));
                Check("DATE-AMBIGUOUS: does NOT fire on a Start/End validity-range table (Product) — range boundaries stripped", !AwFiresOn("DATE-AMBIGUOUS", "Product"));
                Check("DATE-AMBIGUOUS: does NOT fire on a Start/End validity-range table (Promotion)", !AwFiresOn("DATE-AMBIGUOUS", "Promotion"));
                Check("DAC-PERSPECTIVE-NOT-SCOPE: advisory fires on a model with perspectives (AdventureWorks has 3)", c1.Findings.Any(f => f.RuleId == "DAC-PERSPECTIVE-NOT-SCOPE"));
                Check("LIMIT-QNA-INDEX: dormant on AdventureWorks (< 1,000 indexable entities)", !c1.Findings.Any(f => f.RuleId == "LIMIT-QNA-INDEX"));
                Check("LIMIT-DATAAGENT-TABLES: dormant on AdventureWorks (<= 25 visible tables)", !c1.Findings.Any(f => f.RuleId == "LIMIT-DATAAGENT-TABLES"));
                Check("NAME-HIERARCHY / REL-HIERARCHY-SINGLE-LEVEL: dormant on AdventureWorks (clean multi-level hierarchies)",
                    !c1.Findings.Any(f => f.RuleId == "NAME-HIERARCHY" || f.RuleId == "REL-HIERARCHY-SINGLE-LEVEL"));
                var batch5Ids = new[] { "NAME-TABLE", "NAME-COLUMN-ID", "REL-BIDI", "REL-M2M", "REL-SNOWFLAKE",
                    "REL-DISCONNECTED", "REL-INACTIVE", "REL-HIERARCHY-MISSING", "REL-HIERARCHY-SINGLE-LEVEL" };
                var registered = ReadinessRuleSet.Default().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
                Check("catalog batch 5: all nine naming/relationship rules are registered", batch5Ids.All(registered.Contains));
                var batch6OfflineIds = new[] { "LIMIT-SCALE", "REL-DISCONNECTED", "DAC-FIELD-PARAM-COMPLEXITY",
                    "MEAS-DUP-EXPR", "FMT-SUMMARIZE", "BP-DAX-IFERROR", "BP-DAX-SUMMARIZE-EXT",
                    "BP-DAX-BARE-TABLE-FILTER", "BP-DAX-VAR-ALIAS" };
                var batch6LiveIds = ReadinessRuleSet.LiveRules(new ReadinessLiveStats()).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
                Check("catalog batch 6: all ten performance/complexity rules are registered",
                    batch6OfflineIds.All(registered.Contains) && batch6LiveIds.Contains("SCALE-HICARD-COLUMN"));
                Check("scan: grade is assigned", !string.IsNullOrEmpty(c1.Grade));
                Check("scan: findings produced", c1.Findings.Length > 0);
                Check("scan: category scores in 0..100", c1.Categories.All(c => c.Score >= 0 && c.Score <= 100));

                // ---- BPA (general-purpose best practices) SPIKE ---------------------------------
                var bpa = await engine.BpaScanAsync();
                Console.WriteLine($"[i] BPA: {bpa.RuleCount} rules, {bpa.ViolationCount} violations, {bpa.AutoFixable} auto-fixable");
                foreach (var re in bpa.RuleErrors.Take(8)) Console.WriteLine("      RULE ERROR: " + re);
                foreach (var grp in bpa.Violations.GroupBy(v => v.RuleName)) Console.WriteLine($"      {grp.Count(),4}  {grp.Key}");
                Check("bpa: rules evaluated without expression errors", bpa.RuleErrors.Length == 0);
                Check("bpa: produced violations on AdventureWorks", bpa.ViolationCount > 0);

                // Take a real auto-fixable violation from the standard ruleset, apply its deterministic fix, and
                // confirm it clears (rule-agnostic — survives ruleset changes; the standard set yields literal
                // FixExpressions like SummarizeBy=None and DataType=Decimal).
                var av = bpa.Violations.FirstOrDefault(v => v.CanAutoFix);
                Check("bpa: the standard ruleset yields auto-fixable violations", av != null);
                if (av != null)
                {
                    var fr = await engine.BpaFixAsync(av.RuleId, av.ObjectRef, "agent");
                    Check("bpa_fix: deterministic fix applied", fr.Changed);
                    var b3 = await engine.BpaScanAsync();
                    Check("bpa_fix: violation cleared after fix",
                        !b3.Violations.Any(v => v.RuleId == av.RuleId && v.ObjectRef == av.ObjectRef));
                    Console.WriteLine($"[i] bpa_fix cleared {av.RuleId} on {av.ObjectRef}");
                }

                // Pin the SummarizeBy=None enum-coercion fix specifically (standard META_SUMMARIZE_NONE), so the
                // rule-agnostic pick above doesn't quietly stop covering it.
                var sv = bpa.Violations.FirstOrDefault(v => v.RuleId == "META_SUMMARIZE_NONE" && v.CanAutoFix);
                if (sv != null)
                {
                    await engine.BpaFixAsync(sv.RuleId, sv.ObjectRef, "agent");
                    var b4 = await engine.BpaScanAsync();
                    Check("bpa_fix: META_SUMMARIZE_NONE (SummarizeBy=None enum coercion) clears the violation",
                        !b4.Violations.Any(v => v.RuleId == "META_SUMMARIZE_NONE" && v.ObjectRef == sv.ObjectRef));
                }

                // bpa_fix_all runs without error; AI fix-prompt routes a content violation to Claude.
                var bpaPre = await engine.BpaScanAsync();   // pre-fix count, to prove ONE undo reverts the whole bulk (#22)
                var fixAll = await engine.BpaFixAllAsync("agent");
                Check("bpa_fix_all: ran (applied >= 0, returns scorecard)", fixAll.Scorecard != null && fixAll.Applied >= 0);
                Console.WriteLine($"[i] bpa_fix_all applied {fixAll.Applied}; remaining violations {fixAll.Scorecard.ViolationCount}");
                // SINGLE-UNDO ATOMICITY (#22): bpa_fix_all applies every auto-fix inside ONE mutate batch, so a single
                // undo must restore EVERY fixed object in one timeline step. Only meaningful when the pass changed something.
                if (fixAll.Applied > 0)
                {
                    Check("bpa_fix_all: the bulk pass reduced the violation count (so undo has something to revert)",
                        fixAll.Scorecard.ViolationCount < bpaPre.ViolationCount);
                    await engine.UndoAsync("agent");
                    var afterUndoAll = await engine.BpaScanAsync();
                    Check("bpa_fix_all: ONE undo atomically reverts ALL bulk fixes (count returns to the exact pre-fix value)",
                        afterUndoAll.ViolationCount == bpaPre.ViolationCount);
                    await engine.BpaFixAllAsync("agent");   // re-apply so the rest of the smoke runs against the fixed model
                }
                // A non-auto-fixable violation (e.g. a format-string or naming rule with no literal FixExpression)
                // routes to Claude via a fix prompt.
                var nonFixV = bpa.Violations.FirstOrDefault(v => !v.CanAutoFix);
                Check("bpa: the standard ruleset yields non-auto-fixable (agent-routed) violations", nonFixV != null);
                if (nonFixV != null)
                {
                    var fp = await engine.BpaGetFixPromptAsync(nonFixV.RuleId, nonFixV.ObjectRef);
                    Check("bpa_get_fix_prompt: names the object + rule",
                        fp != null && fp.ObjectRef == nonFixV.ObjectRef && fp.RuleId == nonFixV.RuleId
                        && !string.IsNullOrEmpty(fp.Prompt) && fp.Prompt.Contains("Best Practice"));
                }

                // ---- SAFE FIXES -----------------------------------------------------------------
                var fix = await engine.ApplySafeFixesAsync("agent");
                Console.WriteLine($"[i] applied {fix.Applied.Length} safe fix(es); new grade {fix.Scorecard.Grade} ({fix.Scorecard.Overall}/100)");
                foreach (var a in fix.Applied.Take(6)) Console.WriteLine("      - " + a);
                var c2 = fix.Scorecard;
                if (c1.SafeFixCount > 0)
                {
                    Check("safe-fixes: at least one applied", fix.Applied.Length > 0);
                    Check("safe-fixes: remaining safe-fix findings decreased", c2.SafeFixCount < c1.SafeFixCount);
                    Check("safe-fixes: overall score did not regress", c2.Overall >= c1.Overall - 0.05);
                }
                else
                {
                    Console.WriteLine("[i] (model had no safe-fix findings to apply)");
                }

                // ---- GROUNDING + AI DESCRIPTION -------------------------------------------------
                var undescribed = c1.Findings.FirstOrDefault(f => f.RuleId == "DESC-MEASURE");
                if (undescribed != null)
                {
                    var g = await engine.GetGroundingAsync(undescribed.ObjectRef);
                    Console.WriteLine($"[i] grounding {g.ObjectRef}: expr={(string.IsNullOrEmpty(g.Expression) ? "(none)" : g.Expression.Substring(0, Math.Min(40, g.Expression.Length)) + "…")}, siblings={g.SiblingNames.Length}");
                    Check("grounding: measure has a DAX expression", !string.IsNullOrWhiteSpace(g.Expression));

                    var before = await engine.AiReadinessScanAsync();
                    var covBefore = before.Coverage.TryGetValue("measuresWithDescription", out var cb) ? cb : 0;
                    await engine.SetDescriptionAsync(undescribed.ObjectRef, "Test business description authored for AI readiness.", "agent");
                    var after = await engine.AiReadinessScanAsync();
                    var covAfter = after.Coverage.TryGetValue("measuresWithDescription", out var ca) ? ca : 0;
                    Console.WriteLine($"[i] measuresWithDescription coverage {covBefore}% -> {covAfter}%");
                    Check("set_description: described-measure coverage increased", covAfter > covBefore);
                    Check("set_description: that measure no longer flagged", !after.Findings.Any(f => f.RuleId == "DESC-MEASURE" && f.ObjectRef == undescribed.ObjectRef));
                }
                else
                {
                    Console.WriteLine("[i] (all visible measures already described)");
                }

                // ---- NEW RULES: true-positive verification (induce a violation → assert it fires) ----
                // DESC-LONG-OBJECT: a visible column with a >200-char description.
                var aCol = (await engine.ListColumnsAsync()).FirstOrDefault(c => !c.IsHidden);
                if (aCol != null)
                {
                    await engine.SetDescriptionAsync(aCol.Ref, new string('x', 250), "agent");
                    var sc = await engine.AiReadinessScanAsync();
                    Check("DESC-LONG-OBJECT fires on a column with a >200-char description",
                        sc.Findings.Any(f => f.RuleId == "DESC-LONG-OBJECT" && f.ObjectRef == aCol.Ref));
                }
                // MEAS-DUP-EXPR: two measures on the SAME table set to identical DAX (the same-table fix).
                var dupTable = (await engine.ListMeasuresAsync()).GroupBy(r => r.Table).FirstOrDefault(g => g.Count() >= 2);
                if (dupTable != null)
                {
                    var two = dupTable.Take(2).ToList();
                    await engine.SetDaxAsync(two[0].Ref, "1 + 1", "agent");
                    await engine.SetDaxAsync(two[1].Ref, "1 + 1", "agent");
                    var sc = await engine.AiReadinessScanAsync();
                    Check("MEAS-DUP-EXPR fires on two same-table measures with identical DAX",
                        two.All(r => sc.Findings.Any(f => f.RuleId == "MEAS-DUP-EXPR" && f.ObjectRef == r.Ref)));
                }
                // NAME-INVALID-CHARS: rename a measure to embed an emoji (a surrogate-pair char), built by codepoint
                // to keep the source ASCII-clean.
                var renMeas = (await engine.ListMeasuresAsync()).FirstOrDefault();
                if (renMeas != null)
                {
                    var badName = renMeas.Name + " " + char.ConvertFromUtf32(0x1F525);  // 🔥
                    await engine.RenameObjectAsync(renMeas.Ref, badName, "agent");
                    var sc = await engine.AiReadinessScanAsync();
                    Check("NAME-INVALID-CHARS fires on a measure name with an emoji",
                        sc.Findings.Any(f => f.RuleId == "NAME-INVALID-CHARS" && f.ObjectName == badName));
                }

                // SUMMARIZE-DIMENSION: a VISIBLE numeric column whose name reads as a non-additive identifier but that
                // auto-aggregates is flagged; the safe fix (SummarizeBy=None) clears it; and it does NOT double-count
                // with DAC-IMPLICIT-MEASURE (the partition). Pick a visible numeric non-key/non-endpoint column via TOM
                // (full access to relationships/keys), rename it to a non-additive name, force Sum — then restore.
                var sdPick = await sessions.Current.ReadAsync<(string Ref, string Table, string Name, string Sum)>(m =>
                {
                    var endpoints = m.Relationships.OfType<SingleColumnRelationship>()
                        .SelectMany(r => new[] { r.FromColumn, r.ToColumn }).Where(c => c != null).ToHashSet();
                    var c = m.AllColumns.FirstOrDefault(col => !col.IsHidden && col.Type != ColumnType.RowNumber
                        && (col.DataType == DataType.Int64 || col.DataType == DataType.Double || col.DataType == DataType.Decimal)
                        && !col.IsKey && !endpoints.Contains(col) && !ReadinessRuleSet.IsNonAdditiveDimensionName(col.Name));
                    return c == null ? (null, null, null, null)
                                     : ($"column:{c.Table?.Name}/{c.Name}", c.Table?.Name, c.Name, c.SummarizeBy.ToString());
                });
                if (sdPick.Ref != null)
                {
                    const string sdName = "Probe Fiscal Year";
                    await engine.RenameObjectAsync(sdPick.Ref, sdName, "agent");
                    var sdRef = $"column:{sdPick.Table}/{sdName}";
                    await engine.SetColumnMetadataAsync(sdRef, false, "Sum", null, null, "agent");
                    var sd1 = await engine.AiReadinessScanAsync();
                    Check("SUMMARIZE-DIMENSION: a non-additive numeric column that auto-aggregates is flagged",
                        sd1.Findings.Any(f => f.RuleId == "SUMMARIZE-DIMENSION" && f.ObjectRef == sdRef));
                    Check("SUMMARIZE-DIMENSION: partitions DAC-IMPLICIT-MEASURE (the same column is not double-counted)",
                        !sd1.Findings.Any(f => f.RuleId == "DAC-IMPLICIT-MEASURE" && f.ObjectRef == sdRef));
                    var sdFix = await engine.ApplyFixAsync("SUMMARIZE-DIMENSION", sdRef, "agent");
                    Check("SUMMARIZE-DIMENSION: apply_fix sets SummarizeBy=None", sdFix.Changed);
                    var sd2 = await engine.AiReadinessScanAsync();
                    Check("SUMMARIZE-DIMENSION: cleared after the safe fix",
                        !sd2.Findings.Any(f => f.RuleId == "SUMMARIZE-DIMENSION" && f.ObjectRef == sdRef));
                    // The BULK apply_safe_fixes pass ALSO covers the SUMMARIZE-DIMENSION population (not just apply_fix) —
                    // re-induce the violation, run the bulk pass, assert it cleared (guards SafeFixes.Apply population drift).
                    await engine.SetColumnMetadataAsync(sdRef, false, "Sum", null, null, "agent");
                    await engine.ApplySafeFixesAsync("agent");
                    Check("SUMMARIZE-DIMENSION: the bulk apply_safe_fixes pass also clears it (population parity with the rule)",
                        !(await engine.AiReadinessScanAsync()).Findings.Any(f => f.RuleId == "SUMMARIZE-DIMENSION" && f.ObjectRef == sdRef));
                    await engine.SetColumnMetadataAsync(sdRef, false, sdPick.Sum, null, null, "agent"); // restore aggregation
                    await engine.RenameObjectAsync(sdRef, sdPick.Name, "agent");                        // restore name
                }

                // Regex precision guards for the auto-applied SUMMARIZE-DIMENSION SafeFix (battery-tested; lock in the
                // review fixes): additive aggregates are NOT flagged (no silent grand-total suppression), the no-space
                // camelCase identifier convention IS (else DAC-IMPLICIT-MEASURE emits a nonsensical 'Total ProductID').
                Check("non-additive: 'Total Number' (additive) is NOT flagged", !ReadinessRuleSet.IsNonAdditiveDimensionName("Total Number"));
                Check("non-additive: '3 Year Return' (metric, not a year id) is NOT flagged", !ReadinessRuleSet.IsNonAdditiveDimensionName("3 Year Return"));
                Check("non-additive: 'Fiscal Year' IS flagged", ReadinessRuleSet.IsNonAdditiveDimensionName("Fiscal Year"));
                Check("non-additive: camelCase 'ProductID' IS flagged (warehouse surrogate convention)", ReadinessRuleSet.IsNonAdditiveDimensionName("ProductID"));
                Check("non-additive: 'Account Number' IS flagged (the 'count' lookbehind is word-anchored)", ReadinessRuleSet.IsNonAdditiveDimensionName("Account Number"));

                // NAME-TECH-PREFIX: a visible table with a capitalised ETL/modelling prefix ("Fact …") is flagged; the
                // clean rename clears it. Pick a clean (non-cryptic, non-date, un-prefixed) business table, then restore.
                var ntpPick = await sessions.Current.ReadAsync<(string Ref, string Name)>(m =>
                {
                    var t = m.Tables.FirstOrDefault(x => !x.IsHidden && !(x is CalculationGroupTable)
                        && !string.Equals(x.DataCategory, "Time", StringComparison.OrdinalIgnoreCase)
                        && !ReadinessRuleSet.IsCrypticName(x.Name)
                        && !System.Text.RegularExpressions.Regex.IsMatch(x.Name ?? "", @"(?i)^(fact|dim|dimension|stg|staging)[\s_\-]"));
                    return t == null ? (null, null) : ($"table:{t.Name}", t.Name);
                });
                if (ntpPick.Ref != null)
                {
                    await engine.RenameObjectAsync(ntpPick.Ref, "Fact Probe Wave", "agent");
                    var ntp1 = await engine.AiReadinessScanAsync();
                    Check("NAME-TECH-PREFIX: a capitalised ETL prefix ('Fact Probe Wave') is flagged",
                        ntp1.Findings.Any(f => f.RuleId == "NAME-TECH-PREFIX" && f.ObjectName == "Fact Probe Wave"));
                    await engine.RenameObjectAsync("table:Fact Probe Wave", ntpPick.Name, "agent"); // restore
                    var ntp2 = await engine.AiReadinessScanAsync();
                    Check("NAME-TECH-PREFIX: dropping the prefix clears the finding",
                        !ntp2.Findings.Any(f => f.RuleId == "NAME-TECH-PREFIX" && f.ObjectName == ntpPick.Name));
                    // Denylist (review nit): a legitimate business noun after the prefix is NOT flagged ("Fact Sheet").
                    await engine.RenameObjectAsync(ntpPick.Ref, "Fact Sheet", "agent");
                    Check("NAME-TECH-PREFIX: 'Fact Sheet' (a real business noun) is NOT flagged (denylist)",
                        !(await engine.AiReadinessScanAsync()).Findings.Any(f => f.RuleId == "NAME-TECH-PREFIX" && f.ObjectName == "Fact Sheet"));
                    await engine.RenameObjectAsync("table:Fact Sheet", ntpPick.Name, "agent"); // restore
                }

                // ---- CUSTOM READINESS RULES (user-authored, model-embedded, the BPA expression language) ----
                // validate_rule previews honestly -> load runs the rule with real Applicable/AppliesTo scoring +
                // provenance -> waivers apply -> a dormant rule never moves the score -> reset restores everything.
                {
                    var crPick = await sessions.Current.ReadAsync(m =>
                        m.AllMeasures.Where(x => !x.IsHidden).Take(2)
                         .Select(x => (Ref: $"measure:{x.Table?.Name}/{x.Name}", Folder: x.DisplayFolder ?? "")).ToList());
                    if (crPick.Count == 2)
                    {
                        foreach (var p in crPick) await engine.SetObjectPropertyAsync(p.Ref, "DisplayFolder", "Smoke KPI", "agent");
                        const string crRule = "[{\"ID\":\"ORG-SMOKE-KPI\",\"Name\":\"Smoke KPI probe\",\"Category\":\"Descriptions\",\"Severity\":\"Medium\"," +
                            "\"Scope\":\"Measure\",\"AppliesTo\":\"DisplayFolder = \\\"Smoke KPI\\\"\",\"Expression\":\"Name <> null\"," +
                            "\"Message\":\"%object% flagged by the smoke rule\",\"FixKind\":\"AiContent\"}]";

                        var crBefore = await engine.AiReadinessScanAsync();
                        var crPreview = await engine.ValidateRuleAsync("readiness", crRule);
                        Check("validate_rule: compiles + test-runs (Applicable = the AppliesTo population, both flagged)",
                            crPreview.AllValid && crPreview.Rules[0].Applicable == 2 && crPreview.Rules[0].Violations == 2);
                        Check("validate_rule: previewing saves NOTHING (no finding in a scan)",
                            !(await engine.AiReadinessScanAsync()).Findings.Any(f => f.RuleId == "ORG-SMOKE-KPI"));

                        await engine.LoadReadinessRulesAsync(crRule, false, "agent");
                        var crAfter = await engine.AiReadinessScanAsync();
                        var crFindings = crAfter.Findings.Where(f => f.RuleId == "ORG-SMOKE-KPI").ToList();
                        Check("custom rule fires on exactly the AppliesTo population (2 findings, custom provenance)",
                            crFindings.Count == 2 && crFindings.All(f => f.Custom));
                        var crDescB = crBefore.Categories.Single(c => c.Category == "Descriptions");
                        var crDescA = crAfter.Categories.Single(c => c.Category == "Descriptions");
                        Check("custom rule scoring: Descriptions Applicable +2 / Violations +2 (population-honest, not all measures)",
                            crDescA.Applicable == crDescB.Applicable + 2 && crDescA.Violations == crDescB.Violations + 2);
                        Check("custom rule scan reports no rule errors", crAfter.RuleErrors.Length == 0);

                        // Waivers work on custom findings like any finding (per-instance, reason required).
                        await engine.WaiveFindingAsync("air", "ORG-SMOKE-KPI", crFindings[0].ObjectRef, "smoke: accepted", "agent");
                        var crWaived = await engine.AiReadinessScanAsync();
                        Check("waive_finding applies to a custom finding (surfaced, excluded from the count)",
                            crWaived.Findings.Count(f => f.RuleId == "ORG-SMOKE-KPI" && f.Waived) == 1
                            && crWaived.Categories.Single(c => c.Category == "Descriptions").Violations == crDescA.Violations - 1);
                        await engine.UnwaiveFindingAsync("air", "ORG-SMOKE-KPI", crFindings[0].ObjectRef, "agent");

                        // A dormant rule (empty AppliesTo population) never moves any category or the overall.
                        var dormantRule = crRule.Replace("ORG-SMOKE-KPI", "ORG-SMOKE-DORMANT").Replace("Smoke KPI\\\"", "No Such Folder X9\\\"");
                        await engine.LoadReadinessRulesAsync(dormantRule, false, "agent");
                        var crDormant = await engine.AiReadinessScanAsync();
                        Check("a dormant custom rule (empty population) never inflates a category or the overall",
                            crDormant.Overall == crAfter.Overall
                            && !crDormant.Findings.Any(f => f.RuleId == "ORG-SMOKE-DORMANT")
                            && crDormant.Categories.Single(c => c.Category == "Descriptions").Applicable == crDescA.Applicable);

                        // A built-in rule id can never be overridden or weakened.
                        var crCollided = false;
                        try { await engine.LoadReadinessRulesAsync(crRule.Replace("ORG-SMOKE-KPI", "DESC-MEASURE"), false, "agent"); }
                        catch (InvalidOperationException) { crCollided = true; }
                        Check("a built-in readiness rule id collision is refused with teaching", crCollided);

                        var crReset = await engine.ResetReadinessRulesAsync("agent");
                        Check("reset_readiness_rules clears the custom rules (scan back to built-ins only)",
                            crReset.ModelRules == 0 && !(await engine.AiReadinessScanAsync()).Findings.Any(f => f.RuleId.StartsWith("ORG-SMOKE")));
                        foreach (var p in crPick) await engine.SetObjectPropertyAsync(p.Ref, "DisplayFolder", p.Folder, "agent");   // restore
                    }
                    else Console.WriteLine("[i] (custom-rule leg skipped: fewer than 2 visible measures)");
                }

                // ---- MAKE MODEL AI-READY (orchestrator work-list) -------------------------------
                var plan = await engine.MakeAiReadyAsync("agent", 10);
                Console.WriteLine($"[i] make_model_ai_ready: applied {plan.Applied.Length} safe fix(es), AI-content queue {plan.AiQueue.Length}, grade {plan.Scorecard.Grade}");
                Check("make_ai_ready: returns an AI-content work queue", plan.AiQueue.Length > 0);
                Check("make_ai_ready: every queue item carries a finding + grounding", plan.AiQueue.All(w => w.Finding != null && w.Grounding != null));

                // ---- SET MEASURE FORMAT ---------------------------------------------------------
                var noFmt = (await engine.AiReadinessScanAsync()).Findings.FirstOrDefault(f => f.RuleId == "FMT-MEASURE");
                if (noFmt != null)
                {
                    await engine.SetMeasureFormatAsync(noFmt.ObjectRef, "#,0", "agent");
                    var afterFmt = await engine.AiReadinessScanAsync();
                    Check("set_measure_format: that measure no longer flagged", !afterFmt.Findings.Any(f => f.RuleId == "FMT-MEASURE" && f.ObjectRef == noFmt.ObjectRef));
                }

                // ---- MARK DATE TABLE ------------------------------------------------------------
                if ((await engine.AiReadinessScanAsync()).Findings.Any(f => f.RuleId == "DATE-MARK"))
                {
                    var tables = await engine.ListTreeAsync(null);
                    var dateTable = tables.FirstOrDefault(t => t.Name.IndexOf("date", StringComparison.OrdinalIgnoreCase) >= 0 || t.Name.IndexOf("calendar", StringComparison.OrdinalIgnoreCase) >= 0)
                                    ?? tables.FirstOrDefault();
                    if (dateTable != null)
                    {
                        await engine.MarkDateTableAsync(dateTable.Ref, null, "agent");
                        var afterDate = await engine.AiReadinessScanAsync();
                        Check("mark_date_table: 'no date table' finding cleared", !afterDate.Findings.Any(f => f.RuleId == "DATE-MARK"));
                    }
                }

                // ---- SYNONYMS / Q&A LINGUISTIC SCHEMA (requires compatibility level >= 1465) -----
                var preSyn = await engine.AiReadinessScanAsync();
                var synApplicable = preSyn.Categories.Any(c => c.Category == "Synonyms" && c.HasRules);
                if (!synApplicable)
                {
                    Console.WriteLine("[i] synonyms: not applicable (model compatibility level < 1465) — gated correctly, skipping");
                }
                else
                {
                    await engine.EnableQnaAsync(null, "agent");
                    var afterQna = await engine.AiReadinessScanAsync();
                    Check("enable_qna: 'no linguistic schema' finding cleared", !afterQna.Findings.Any(f => f.RuleId == "SYN-SCHEMA"));
                    var synTarget = afterQna.Findings.FirstOrDefault(f => f.RuleId == "SYN-FIELD" && f.ObjectRef.StartsWith("measure:"))?.ObjectRef;
                    if (synTarget != null)
                    {
                        await engine.SetSynonymsAsync(synTarget, new[] { "revenue", "turnover" }, null, "agent");
                        var afterSyn = await engine.AiReadinessScanAsync();
                        Console.WriteLine($"[i] set synonyms on {synTarget}; fieldsWithSynonyms now {(afterSyn.Coverage.TryGetValue("fieldsWithSynonyms", out var fs) ? fs : 0)}%");
                        Check("set_synonyms: that field no longer flagged", !afterSyn.Findings.Any(f => f.RuleId == "SYN-FIELD" && f.ObjectRef == synTarget));
                    }
                    var tableSynTarget = afterQna.Findings.FirstOrDefault(f => f.RuleId == "SYN-TABLE")?.ObjectRef;
                    if (tableSynTarget != null)
                    {
                        await engine.SetSynonymsAsync(tableSynTarget, new[] { "business records" }, null, "agent");
                        var afterTableSyn = await engine.AiReadinessScanAsync();
                        Check("set_synonyms: that table no longer flagged", !afterTableSyn.Findings.Any(f => f.RuleId == "SYN-TABLE" && f.ObjectRef == tableSynTarget));
                    }
                }

                // ---- SYNONYMS round-trip on a MODERN model (CL >= 1465) -------------------------
                var modern = FindTestData("AllProperties.bim");
                if (modern != null)
                {
                    await engine.OpenAsync(modern);
                    var mc = await engine.AiReadinessScanAsync();
                    Console.WriteLine($"[i] opened {Path.GetFileName(modern)} (modern); synonyms applicable={mc.Categories.Any(c => c.Category == "Synonyms" && c.HasRules)}");

                    // ---- NO-OP must not silently seed a schema (regression for the "no-op enables Q&A" bug) ----
                    // While the model still has NO linguistic schema (SYN-SCHEMA fires), a no-op LSDL write must not
                    // create one (which would enable Q&A) as a side effect.
                    if (mc.Findings.Any(f => f.RuleId == "SYN-SCHEMA"))
                    {
                        var clr0 = await engine.SetAiInstructionsAsync("", null, "agent");
                        var ns1 = await engine.AiReadinessScanAsync();
                        Check("set_ai_instructions: clearing on a schema-less model is a true no-op (does NOT seed/enable Q&A)",
                            !clr0.Changed && ns1.Findings.Any(f => f.RuleId == "SYN-SCHEMA"));
                        var freshTbl = (await engine.ListTreeAsync(null)).FirstOrDefault(x => x.Kind == "table");
                        var freshField = freshTbl == null ? null : (await engine.ListTreeAsync(freshTbl.Ref)).FirstOrDefault(x => x.Kind == "measure" || x.Kind == "column")?.Ref;
                        if (freshField != null)
                        {
                            var inc0 = await engine.SetAiDataSchemaAsync(freshField, true, null, "agent");
                            var ns2 = await engine.AiReadinessScanAsync();
                            Check("set_ai_data_schema: including on a schema-less model is a true no-op (does NOT seed)",
                                !inc0.Changed && ns2.Findings.Any(f => f.RuleId == "SYN-SCHEMA"));
                        }
                    }

                    if (mc.Categories.Any(c => c.Category == "Synonyms" && c.HasRules))
                    {
                        await engine.EnableQnaAsync(null, "agent");
                        var aq = await engine.AiReadinessScanAsync();
                        Check("modern: enable_qna clears the no-linguistic-schema finding", !aq.Findings.Any(f => f.RuleId == "SYN-SCHEMA"));
                        var t = aq.Findings.FirstOrDefault(f => f.RuleId == "SYN-FIELD")?.ObjectRef;
                        if (t != null)
                        {
                            await engine.SetSynonymsAsync(t, new[] { "alpha", "beta" }, null, "agent");
                            var asn = await engine.AiReadinessScanAsync();
                            Console.WriteLine($"[i] modern: set synonyms on {t}");
                            Check("modern: set_synonyms clears that field's finding", !asn.Findings.Any(f => f.RuleId == "SYN-FIELD" && f.ObjectRef == t));
                        }
                        var tableTarget = aq.Findings.FirstOrDefault(f => f.RuleId == "SYN-TABLE")?.ObjectRef;
                        Check("modern: SYN-TABLE finds a visible business table without synonyms", tableTarget != null);
                        if (tableTarget != null)
                        {
                            await engine.SetSynonymsAsync(tableTarget, new[] { "business records" }, null, "agent");
                            var tableScan = await engine.AiReadinessScanAsync();
                            Check("modern: set_synonyms clears that table's finding", !tableScan.Findings.Any(f => f.RuleId == "SYN-TABLE" && f.ObjectRef == tableTarget));
                        }

                        // ---- AI DATA SCHEMA toggle round-trip (LSDL per-entity Visibility) ----
                        var dsTbl = (await engine.ListTreeAsync(null)).FirstOrDefault(x => x.Kind == "table");
                        var dsField = dsTbl == null ? null
                            : (await engine.ListTreeAsync(dsTbl.Ref)).FirstOrDefault(x => x.Kind == "measure" || x.Kind == "column")?.Ref;
                        if (dsField != null)
                        {
                            var ex1 = await engine.SetAiDataSchemaAsync(dsField, false, null, "agent");
                            Check("set_ai_data_schema: excluding a visible field is applied", ex1.Changed);
                            var ex2 = await engine.SetAiDataSchemaAsync(dsField, false, null, "agent");
                            Check("set_ai_data_schema: re-excluding is an idempotent no-op (exclusion persisted in the LSDL)", !ex2.Changed);
                            var inc1 = await engine.SetAiDataSchemaAsync(dsField, true, null, "agent");
                            Check("set_ai_data_schema: re-including toggles it back", inc1.Changed);
                            var inc2 = await engine.SetAiDataSchemaAsync(dsField, true, null, "agent");
                            Check("set_ai_data_schema: re-including again is a no-op (absence == included)", !inc2.Changed);

                            // Undo reverts the immediately-preceding real change. Exclude (real), undo → included again,
                            // then excluding once more must register as a change (proving the undo reverted it).
                            var exC = await engine.SetAiDataSchemaAsync(dsField, false, null, "agent");
                            await engine.UndoAsync("agent");
                            var exD = await engine.SetAiDataSchemaAsync(dsField, false, null, "agent");
                            Check("set_ai_data_schema: a single undo reverts the toggle", exC.Changed && exD.Changed);

                            // Disk round-trip: the exclusion — carried by an entity the writer SYNTHESIZED (NewEntity,
                            // since the seeded LSDL started with no entities) — must survive a TMDL save + reopen. If the
                            // synthesized entity were dropped on reload, re-excluding would register as a change.
                            var tmpDs = Path.Combine(Path.GetTempPath(), "semanticus_ds_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                            await engine.SaveAsync(tmpDs, "TMDL");
                            await engine.OpenAsync(tmpDs);
                            var reEx = await engine.SetAiDataSchemaAsync(dsField, false, null, "agent");
                            Check("set_ai_data_schema: exclusion survives TMDL save + reopen (re-excluding is a no-op)", !reEx.Changed);
                        }

                        // ---- AI INSTRUCTIONS round-trip (Prep-for-AI CustomInstructions in the LSDL) ----
                        // After enable_qna the model has a linguistic schema, so DAC-AI-INSTRUCTIONS is applicable + fires.
                        var preInstr = await engine.AiReadinessScanAsync();
                        if (preInstr.Findings.Any(f => f.RuleId == "DAC-AI-INSTRUCTIONS"))
                        {
                            var instr = "Revenue means recognised revenue; always use [Total Sales]. The fiscal year starts in July.";
                            var set = await engine.SetAiInstructionsAsync(instr, null, "agent");
                            Check("set_ai_instructions: applied + reports length", set.Changed && set.Length == instr.Length);
                            Check("set_ai_instructions: result carries the LSDL service-refresh caveat", !string.IsNullOrEmpty(set.Note) && set.Note.Contains("refresh"));
                            var afterInstr = await engine.AiReadinessScanAsync();
                            Check("set_ai_instructions: 'no AI instructions' finding cleared (write seen on re-scan — cache invalidation)",
                                !afterInstr.Findings.Any(f => f.RuleId == "DAC-AI-INSTRUCTIONS"));

                            // Shared undo timeline reverts it — the finding must return.
                            await engine.UndoAsync("agent");
                            var afterUndo = await engine.AiReadinessScanAsync();
                            Check("set_ai_instructions: a single undo reverts it (finding returns)",
                                afterUndo.Findings.Any(f => f.RuleId == "DAC-AI-INSTRUCTIONS"));

                            // The documented 10,000-char cap is enforced (rejected, not silently truncated).
                            var rejected = false;
                            try { await engine.SetAiInstructionsAsync(new string('x', 10001), null, "agent"); }
                            catch { rejected = true; }
                            Check("set_ai_instructions: >10000 chars is rejected", rejected);

                            // Clear path: setting empty REMOVES the instructions (Changed=true) and the finding returns.
                            await engine.SetAiInstructionsAsync("Temporary instructions to be cleared.", null, "agent");
                            var cleared = await engine.SetAiInstructionsAsync("", null, "agent");
                            var afterClear = await engine.AiReadinessScanAsync();
                            Check("set_ai_instructions: clearing existing instructions removes them (Changed + finding returns)",
                                cleared.Changed && afterClear.Findings.Any(f => f.RuleId == "DAC-AI-INSTRUCTIONS"));

                            // Disk round-trip: the instruction must survive a TMDL save + reopen. Proof: after reload,
                            // re-setting the identical value is a no-op (Changed=false) — only possible if it persisted.
                            var instr2 = "Persisted AI instructions for the TMDL round-trip test.";
                            await engine.SetAiInstructionsAsync(instr2, null, "agent");
                            var tmpAi = Path.Combine(Path.GetTempPath(), "semanticus_ai_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                            await engine.SaveAsync(tmpAi, "TMDL");
                            await engine.OpenAsync(tmpAi);
                            var reSet = await engine.SetAiInstructionsAsync(instr2, null, "agent");
                            Check("set_ai_instructions: survives TMDL save + reopen (re-setting the same value is a no-op)", !reSet.Changed);
                        }
                    }
                }

                // ---- CHANGE-PLAN INTEGRATION of the Prep-for-AI writers ----------------------------
                // The flagship "PR for your model" now covers AI instructions (seeded → authored → applied in the
                // batch, undoable) and AI-data-schema curation (object-scoped add_plan_item).
                var planModel = FindTestData("AllProperties.bim");
                if (planModel != null)
                {
                    await engine.OpenAsync(planModel);
                    await engine.EnableQnaAsync(null, "agent"); // linguistic schema → DAC-AI-INSTRUCTIONS becomes applicable
                    var prePlan = await engine.AiReadinessScanAsync();
                    if (prePlan.Findings.Any(f => f.RuleId == "DAC-AI-INSTRUCTIONS"))
                    {
                        // Budget-starvation regression: the model-level item is seeded OUTSIDE the per-object maxAi
                        // throttle, so it must still appear even when the budget is exhausted by per-object findings.
                        // (Done first: each ProposePlanAsync StartNew's a fresh plan, so capture aiItem from the LAST propose.)
                        var tinyPlan = await engine.ProposePlanAsync(null, true, 1, "agent");
                        Check("plan: model-level set_ai_instructions survives a tiny maxAi=1 budget (seeded unbudgeted)",
                            tinyPlan.Items.Any(i => i.Kind == "set_ai_instructions"));

                        var aiPlan = await engine.ProposePlanAsync(null, true, 40, "agent");
                        var aiItem = aiPlan.Items.FirstOrDefault(i => i.Kind == "set_ai_instructions");
                        Check("plan: propose seeds a model-level set_ai_instructions item (needs_content, grounded, honest Before)",
                            aiItem != null && aiItem.Status == "needs_content" && aiItem.Grounding != null
                            && aiItem.Grounding.Kind == "model" && aiItem.Before == "(none)"); // no instructions yet ⇒ Before="(none)"
                        if (aiItem != null)
                        {
                            await engine.SetPlanItemAsync(aiItem.Id, "Use [Total Sales] for revenue; fiscal year starts in July.", null, "agent");
                            await engine.ApplyPlanAsync(new[] { aiItem.Id }, "agent");
                            var appliedScan = await engine.AiReadinessScanAsync();
                            Check("plan: applying the AI-instructions item clears DAC-AI-INSTRUCTIONS",
                                !appliedScan.Findings.Any(f => f.RuleId == "DAC-AI-INSTRUCTIONS"));
                            await engine.UndoAsync("agent");
                            var revertedScan = await engine.AiReadinessScanAsync();
                            Check("plan: a single undo reverts the applied AI-instructions item",
                                revertedScan.Findings.Any(f => f.RuleId == "DAC-AI-INSTRUCTIONS"));
                        }
                        await engine.ClearPlanAsync("agent");
                    }

                    var pTbl = (await engine.ListTreeAsync(null)).FirstOrDefault(x => x.Kind == "table");
                    var pField = pTbl == null ? null : (await engine.ListTreeAsync(pTbl.Ref)).FirstOrDefault(x => x.Kind == "measure" || x.Kind == "column")?.Ref;
                    if (pField != null)
                    {
                        await engine.AddPlanItemAsync(pField, "set_ai_data_schema", "Excluded", null, null, null, "agent");
                        var dsItem = (await engine.GetPlanAsync()).Items.FirstOrDefault(i => i.Kind == "set_ai_data_schema");
                        Check("plan: add_plan_item(set_ai_data_schema,'Excluded') is approved with Included→Excluded",
                            dsItem != null && dsItem.Status == "approved" && dsItem.After == "Excluded" && dsItem.Before == "Included");
                        if (dsItem != null)
                        {
                            await engine.ApplyPlanAsync(new[] { dsItem.Id }, "agent");
                            var reEx = await engine.SetAiDataSchemaAsync(pField, false, null, "agent");
                            Check("plan: applying set_ai_data_schema excluded the field (re-exclude is a no-op)", !reEx.Changed);
                        }
                        await engine.ClearPlanAsync("agent");
                    }

                    // get_grounding contract: the model-level fallback must be gated to genuine "obj:Model/..." refs.
                    // A typo'd / stale object ref must still surface "Object not found" — NOT a fabricated whole-model bundle.
                    var badRefThrew = false;
                    try { await engine.GetGroundingAsync("measure:NoSuchTable/NoSuchMeasure"); }
                    catch { badRefThrew = true; }
                    Check("grounding: a bogus object ref throws (not a fabricated model bundle)", badRefThrew);
                    if (pField != null)
                    {
                        var realG = await engine.GetGroundingAsync(pField);
                        Check("grounding: a real object ref still resolves (kind != model)", realG != null && realG.Kind != "model");
                    }
                }

                // ---- NAMING: time-intelligence abbreviations are NOT cryptic (regression for the YoY/MoM bug) ----
                // The mixed-case period-over-period suffixes look like a camelCase hump to the cryptic pre-filter;
                // they must be exempted (these names are standard + AI-legible), while genuinely cryptic names and
                // identifier code-smells must still fire.
                Check("naming: 'YoY' is not cryptic", !Semanticus.Analysis.ReadinessRuleSet.IsCrypticName("YoY"));
                Check("naming: 'MoM' is not cryptic", !Semanticus.Analysis.ReadinessRuleSet.IsCrypticName("MoM"));
                Check("naming: 'Sales YoY %' is not cryptic", !Semanticus.Analysis.ReadinessRuleSet.IsCrypticName("Sales YoY %"));
                Check("naming: 'Revenue QoQ' is not cryptic", !Semanticus.Analysis.ReadinessRuleSet.IsCrypticName("Revenue QoQ"));
                Check("naming: 'Orders WoW' is not cryptic", !Semanticus.Analysis.ReadinessRuleSet.IsCrypticName("Orders WoW"));
                Check("naming: all-caps 'Sales YOY' is not cryptic (known acronym)", !Semanticus.Analysis.ReadinessRuleSet.IsCrypticName("Sales YOY"));
                Check("naming: 'Sales YTG' is not cryptic (added time-intel acronym)", !Semanticus.Analysis.ReadinessRuleSet.IsCrypticName("Sales YTG"));
                Check("naming: a camelCase identifier 'custKey' is STILL cryptic", Semanticus.Analysis.ReadinessRuleSet.IsCrypticName("custKey"));
                Check("naming: an unknown all-caps code 'Sales XYZ' is STILL cryptic", Semanticus.Analysis.ReadinessRuleSet.IsCrypticName("Sales XYZ"));
                Check("naming: a name embedding a token without a boundary 'YoYGrowth' is STILL cryptic", Semanticus.Analysis.ReadinessRuleSet.IsCrypticName("YoYGrowth"));

                // ---- LLM-JUDGMENT RULE WAVE: NAME-TABLE / DESC-CALCGROUP-ITEMS --------------------
                // Deterministic detection (regex / whole-token item-name scan), AI-authored fix. Each verified on BOTH
                // the positive (fires) and negative (cleared) path. AllProperties.bim has a calc group + CL>=1465.
                var waveModel = FindTestData("AllProperties.bim");
                if (waveModel != null)
                {
                    // NAME-TABLE: a cryptic table name fires; a clean rename clears it (isolated by ObjectName).
                    await engine.OpenAsync(waveModel);
                    var aTable = (await engine.ListTreeAsync(null)).FirstOrDefault(x => x.Kind == "table" && !x.Ref.Contains("CalculationGroup"));
                    if (aTable != null)
                    {
                        await engine.RenameObjectAsync(aTable.Ref, "tbl_RuleWave", "agent");
                        var n1 = await engine.AiReadinessScanAsync();
                        Check("NAME-TABLE: a cryptic table name (tbl_RuleWave) is flagged",
                            n1.Findings.Any(f => f.RuleId == "NAME-TABLE" && f.ObjectName == "tbl_RuleWave"));
                        await engine.RenameObjectAsync("table:tbl_RuleWave", "Rule Wave Sales", "agent");
                        var n2 = await engine.AiReadinessScanAsync();
                        Check("NAME-TABLE: a clean, human table name clears the finding",
                            !n2.Findings.Any(f => f.RuleId == "NAME-TABLE" && f.ObjectName == "Rule Wave Sales"));
                    }

                    // DESC-CALCGROUP-ITEMS: a documented calc group whose description omits an item name fires; once the
                    // description lists every item it clears. Also proves no double-count with DAC-CALC-GROUP.
                    await engine.OpenAsync(waveModel); // fresh (the rename above mutated the in-memory model)
                    const string cgRef = "table:CalculationGroup";
                    var preCg = await engine.AiReadinessScanAsync();
                    if (preCg.Findings.Any(f => f.RuleId == "DAC-CALC-GROUP")) // confirm the fixture calc group is undocumented
                    {
                        await engine.SetDescriptionAsync(cgRef, "Time-intelligence calculation group for the model.", "agent");
                        var g1 = await engine.AiReadinessScanAsync();
                        Check("DESC-CALCGROUP-ITEMS: documented calc group omitting its item name (CalcItem) is flagged",
                            g1.Findings.Any(f => f.RuleId == "DESC-CALCGROUP-ITEMS"));
                        Check("DESC-CALCGROUP-ITEMS: once documented, DAC-CALC-GROUP no longer fires (mutually exclusive, no double-count)",
                            !g1.Findings.Any(f => f.RuleId == "DAC-CALC-GROUP"));
                        await engine.SetDescriptionAsync(cgRef, "Time-intelligence group. Calculation items: CalcItem.", "agent");
                        var g2 = await engine.AiReadinessScanAsync();
                        Check("DESC-CALCGROUP-ITEMS: listing every calc item name clears the finding",
                            !g2.Findings.Any(f => f.RuleId == "DESC-CALCGROUP-ITEMS"));

                        // Whole-token match: the item name embedded inside a larger word ("CalcItemized") must NOT count
                        // as listed — otherwise a short item name would falsely clear (the substring false-negative).
                        await engine.SetDescriptionAsync(cgRef, "Covers CalcItemized period reporting.", "agent");
                        var g3 = await engine.AiReadinessScanAsync();
                        Check("DESC-CALCGROUP-ITEMS: an item name embedded in a larger word does NOT count as listed (whole-token)",
                            g3.Findings.Any(f => f.RuleId == "DESC-CALCGROUP-ITEMS"));

                        // Whitespace-normalised: extra/odd internal spacing around a listed item still clears (no false positive).
                        await engine.SetDescriptionAsync(cgRef, "Items:\n   CalcItem  \t (time intel).", "agent");
                        var g4 = await engine.AiReadinessScanAsync();
                        Check("DESC-CALCGROUP-ITEMS: a listed item with odd surrounding whitespace still clears (normalised)",
                            !g4.Findings.Any(f => f.RuleId == "DESC-CALCGROUP-ITEMS"));
                    }
                    // DAC-GLOSSARY-GAP: a code in a VISIBLE field name that the AI instructions don't define is flagged;
                    // defining it clears it; the rule is dormant when the model has no instructions at all.
                    await engine.OpenAsync(waveModel);
                    await engine.EnableQnaAsync(null, "agent");
                    var gp0 = await engine.AiReadinessScanAsync();
                    var catalogBatch3 = new[] { "SYN-COLLIDE", "SYN-LSDL-XML", "FMT-COLUMN" };
                    var shippedRuleIds = ReadinessRuleSet.Default().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
                    Check("catalog batch 3: all new deterministic rules are registered", catalogBatch3.All(shippedRuleIds.Contains));
                    Check("SYN-COLLIDE: generated/suggested recurring terms in the real fixture do not false-fire",
                        !gp0.Findings.Any(f => f.RuleId == "SYN-COLLIDE"));
                    Check("SYN-LSDL-XML: a JSON linguistic schema is not reported as legacy XML",
                        !gp0.Findings.Any(f => f.RuleId == "SYN-LSDL-XML"));
                    Check("DAC-GLOSSARY-GAP: dormant when the model has no AI instructions (Applicable=0)",
                        !gp0.Findings.Any(f => f.RuleId == "DAC-GLOSSARY-GAP"));
                    var gTbl = (await engine.ListTreeAsync(null)).FirstOrDefault(x => x.Kind == "table" && !x.Ref.Contains("CalculationGroup"));
                    if (gTbl != null)
                    {
                        await engine.RenameObjectAsync(gTbl.Ref, "ZZQ Report", "agent"); // ZZQ = an undefined code in a visible name
                        await engine.SetAiInstructionsAsync("Revenue is total sales for the period.", null, "agent");
                        var gp1 = await engine.AiReadinessScanAsync();
                        Check("DAC-GLOSSARY-GAP: a visible field-name code absent from the AI instructions is flagged",
                            gp1.Findings.Any(f => f.RuleId == "DAC-GLOSSARY-GAP" && f.Message.Contains("ZZQ")));
                        await engine.SetAiInstructionsAsync("Revenue is total sales. ZZQ means the ZZ quarter metric.", null, "agent");
                        var gp2 = await engine.AiReadinessScanAsync();
                        Check("DAC-GLOSSARY-GAP: defining the code in the AI instructions clears it",
                            !gp2.Findings.Any(f => f.RuleId == "DAC-GLOSSARY-GAP" && f.Message.Contains("ZZQ")));

                        // Roman numerals (II) and universal acronyms (IT) are NOT codes needing a glossary line.
                        await engine.RenameObjectAsync("table:ZZQ Report", "Phase II IT Costs", "agent");
                        var gp3 = await engine.AiReadinessScanAsync();
                        var gg = gp3.Findings.FirstOrDefault(f => f.RuleId == "DAC-GLOSSARY-GAP");
                        Check("DAC-GLOSSARY-GAP: a Roman numeral (II) and a universal acronym (IT) are not flagged",
                            gg == null || (!gg.Message.Contains("II") && !gg.Message.Contains("IT")));
                    }
                }

                // ---- VIS-TECH-COLUMN: a visible technical/audit/ETL column fires; hiding it clears -------------
                // (Precise: 0 on the curated Finance + AdventureWorks models per probe, so prove firing synthetically.)
                var techModel = FindTestData("AdventureWorks.bim");
                if (techModel != null)
                {
                    await engine.OpenAsync(techModel);
                    string victim = null;
                    foreach (var t in (await engine.ListTreeAsync(null)).Where(x => x.Kind == "table"))
                    {
                        victim = (await engine.ListTreeAsync(t.Ref)).FirstOrDefault(c => c.Kind == "column"
                            && !c.Name.EndsWith("Key", StringComparison.OrdinalIgnoreCase)
                            && !c.Name.EndsWith("ID", StringComparison.OrdinalIgnoreCase))?.Ref;
                        if (victim != null) break;
                    }
                    if (victim != null && victim.StartsWith("column:") && victim.Contains('/'))
                    {
                        var tablePart = victim.Substring("column:".Length, victim.IndexOf('/') - "column:".Length);
                        await engine.SetColumnMetadataAsync(victim, false, null, null, null, "agent"); // ensure visible
                        // Underscore-joined name — the dominant real-warehouse ETL form; guards the \b-vs-underscore fix.
                        await engine.RenameObjectAsync(victim, "ETL_Batch_ID", "agent");
                        var techRef = "column:" + tablePart + "/ETL_Batch_ID";
                        var vt1 = await engine.AiReadinessScanAsync();
                        Check("VIS-TECH-COLUMN: a visible underscore-joined ETL column is flagged (ETL_Batch_ID)",
                            vt1.Findings.Any(f => f.RuleId == "VIS-TECH-COLUMN" && f.ObjectName == "ETL_Batch_ID"));
                        await engine.SetColumnMetadataAsync(techRef, true, null, null, null, "agent"); // hide it
                        var vt2 = await engine.AiReadinessScanAsync();
                        Check("VIS-TECH-COLUMN: hiding the column clears it",
                            !vt2.Findings.Any(f => f.RuleId == "VIS-TECH-COLUMN" && f.ObjectName == "ETL_Batch_ID"));
                    }
                }

                // ---- DESC-ECHO placeholder: a LONG placeholder description (past the <12-char test) fires; real clears
                var phModel = FindTestData("AdventureWorks.bim");
                if (phModel != null)
                {
                    await engine.OpenAsync(phModel);
                    string phMeas = null;
                    foreach (var t in (await engine.ListTreeAsync(null)).Where(x => x.Kind == "table"))
                    {
                        phMeas = (await engine.ListTreeAsync(t.Ref)).FirstOrDefault(x => x.Kind == "measure")?.Ref;
                        if (phMeas != null) break;
                    }
                    if (phMeas != null)
                    {
                        await engine.SetDescriptionAsync(phMeas, "TODO: write a proper business description for this measure.", "agent");
                        var ph1 = await engine.AiReadinessScanAsync();
                        Check("DESC-ECHO: a long placeholder description ('TODO: …') is flagged",
                            ph1.Findings.Any(f => f.RuleId == "DESC-ECHO" && f.ObjectRef == phMeas));
                        await engine.SetDescriptionAsync(phMeas, "Total sales revenue recognised across reseller channels for the selected period.", "agent");
                        var ph2 = await engine.AiReadinessScanAsync();
                        Check("DESC-ECHO: a real business description clears the placeholder finding",
                            !ph2.Findings.Any(f => f.RuleId == "DESC-ECHO" && f.ObjectRef == phMeas));
                        // FP guard: a real finance description that BEGINS with the ambiguous acronym 'TBA' must NOT fire.
                        await engine.SetDescriptionAsync(phMeas, "TBA mortgage-backed security notional pending settlement.", "agent");
                        var ph3 = await engine.AiReadinessScanAsync();
                        Check("DESC-ECHO: a real 'TBA …' finance description is NOT flagged (acronym only matches standalone)",
                            !ph3.Findings.Any(f => f.RuleId == "DESC-ECHO" && f.ObjectRef == phMeas));
                    }
                    // DESC-PLACEHOLDER: the same placeholder check for a TABLE (DESC-ECHO covers measures; this covers
                    // tables/columns). A placeholder table description fires; a real one clears.
                    var phTbl2 = (await engine.ListTreeAsync(null)).FirstOrDefault(x => x.Kind == "table" && !x.Ref.Contains("CalculationGroup"));
                    if (phTbl2 != null)
                    {
                        await engine.SetDescriptionAsync(phTbl2.Ref, "TODO: describe this table for Copilot.", "agent");
                        var pt1 = await engine.AiReadinessScanAsync();
                        Check("DESC-PLACEHOLDER: a placeholder TABLE description is flagged",
                            pt1.Findings.Any(f => f.RuleId == "DESC-PLACEHOLDER" && f.ObjectRef == phTbl2.Ref));
                        await engine.SetDescriptionAsync(phTbl2.Ref, "Customer dimension: one row per reseller customer with geography and segment attributes.", "agent");
                        var pt2 = await engine.AiReadinessScanAsync();
                        Check("DESC-PLACEHOLDER: a real table description clears it",
                            !pt2.Findings.Any(f => f.RuleId == "DESC-PLACEHOLDER" && f.ObjectRef == phTbl2.Ref));
                    }
                }

                // ---- DISK-BACKED Prep-for-AI rules (definition.pbism qnaEnabled) -------------------
                // These only fire for an on-disk PBIP (not a .bim), so synthesize one: save the model to TMDL, drop a
                // definition.pbism beside it with qnaEnabled=false, reopen, and confirm ReadDiskArtifacts + the rule read it.
                {
                    var pbipRoot = Path.Combine(Path.GetTempPath(), "semanticus_pbip_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    await engine.SaveAsync(pbipRoot, "TMDL");
                    System.IO.File.WriteAllText(Path.Combine(pbipRoot, "definition.pbism"), "{\"settings\":{\"qnaEnabled\":false}}");
                    await engine.OpenAsync(pbipRoot);
                    var diskScan = await engine.AiReadinessScanAsync();
                    Check("disk: definition.pbism qnaEnabled=false is read from disk → DAC-QNA-DISABLED fires",
                        diskScan.Findings.Any(f => f.RuleId == "DAC-QNA-DISABLED"));
                }

                // ---- NEW Copilot Tooling Format (GA ~May 2026) — FAIL-LOUD, not silent mis-score -------------------
                // A model migrated to the new format stores Copilot grounding in a `copilot/` folder, NOT the legacy
                // linguistic schema (LSDL). Detect its presence so (a) DAC-AI-INSTRUCTIONS stands down (no false
                // "missing" on the stale LSDL signal), and (b) an explicit DAC-COPILOT-TOOLING-FORMAT advisory fires.
                // Setup: enable_qna seeds an LSDL (so DAC-AI-INSTRUCTIONS would normally fire), then drop a copilot/ folder.
                {
                    var ctfRoot = Path.Combine(Path.GetTempPath(), "semanticus_ctf_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    await engine.SetCompatibilityLevelAsync(1604, "agent");   // Q&A/synonyms need CL >= 1465
                    await engine.EnableQnaAsync(null, "agent");      // seed the linguistic schema → DAC-AI-INSTRUCTIONS becomes applicable
                    await engine.SaveAsync(ctfRoot, "TMDL");
                    System.IO.File.WriteAllText(Path.Combine(ctfRoot, "definition.pbism"), "{\"settings\":{\"qnaEnabled\":true}}");
                    var copilotDir = Path.Combine(ctfRoot, "copilot");
                    System.IO.Directory.CreateDirectory(copilotDir);
                    System.IO.File.WriteAllText(Path.Combine(copilotDir, "synonyms.json"), "{}");
                    await engine.OpenAsync(ctfRoot);
                    var ctfScan = await engine.AiReadinessScanAsync();
                    Check("copilot-tooling-format: the new copilot/ folder is detected → DAC-COPILOT-TOOLING-FORMAT advisory fires (fail-loud)",
                        ctfScan.Findings.Any(f => f.RuleId == "DAC-COPILOT-TOOLING-FORMAT"));
                    Check("copilot-tooling-format: DAC-AI-INSTRUCTIONS stands down on a migrated model (no false 'missing' on the stale LSDL)",
                        !ctfScan.Findings.Any(f => f.RuleId == "DAC-AI-INSTRUCTIONS"));
                }

                // ---- PBIP OPEN: resolve a modern nested TMDL PBIP from any natural entry point ---
                // A real Power BI PBIP nests the model under <name>.SemanticModel/definition/; the vendored TE2 can't
                // reach that root from the .pbip file, the project folder, or the .SemanticModel folder (legacy
                // model.bim assumption / parent-walk → split-model). ModelPathResolver normalises all three to the
                // inner definition root. Build the layout in a temp dir (the engine saves a flat TMDL root INTO
                // .../definition) + a sibling definition.pbism + a .pbip, open it three ways, and confirm the model
                // loads AND Prep-for-AI reads definition.pbism through the nest (qnaEnabled=false → DAC-QNA-DISABLED).
                {
                    var proj = Path.Combine(Path.GetTempPath(), "semx_pbip_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    var semModel = Path.Combine(proj, "Probe.SemanticModel");
                    var defDir = Path.Combine(semModel, "definition");
                    try
                    {
                        await engine.OpenAsync(FindTestBim());          // fresh AdventureWorks
                        Directory.CreateDirectory(defDir);
                        await engine.SaveAsync(defDir, "TMDL");          // engine writes a flat TMDL root INTO .../definition
                        File.WriteAllText(Path.Combine(semModel, "definition.pbism"), "{\"settings\":{\"qnaEnabled\":false}}");
                        File.WriteAllText(Path.Combine(proj, "Probe.pbip"), "{\"version\":\"1.0\",\"artifacts\":[]}");

                        foreach (var (label, entry) in new[] {
                            ("the .pbip file", Path.Combine(proj, "Probe.pbip")),
                            ("the project folder", proj),
                            ("the .SemanticModel folder", semModel),
                        })
                        {
                            await engine.OpenAsync(entry);
                            var tableCount = (await engine.ListTreeAsync(null)).Count(x => x.Kind == "table");
                            Check($"PBIP open: a nested TMDL PBIP opens from {label} (tables loaded)", tableCount > 0);
                            var pbScan = await engine.AiReadinessScanAsync();
                            Check($"PBIP open: Prep-for-AI reads definition.pbism through the nest ({label})",
                                pbScan.Findings.Any(f => f.RuleId == "DAC-QNA-DISABLED"));
                        }

                        // ---- SAVE-BACK: in-place edit+save preserves every sibling + persists into definition/ ------
                        // Seed a couple of realistic sibling files so the test proves they survive an in-place save untouched.
                        Directory.CreateDirectory(Path.Combine(semModel, "VerifiedAnswers"));
                        File.WriteAllText(Path.Combine(semModel, "VerifiedAnswers", "version.json"), "{\"version\":\"1.0.0\"}");
                        File.WriteAllText(Path.Combine(semModel, "diagramLayout.json"), "{\"version\":\"1.1.0\",\"diagrams\":[]}");
                        string Sha(string f) { using var sha = System.Security.Cryptography.SHA256.Create(); using var fs = File.OpenRead(f); return Convert.ToHexString(sha.ComputeHash(fs)); }
                        System.Collections.Generic.Dictionary<string, string> Snap()
                        {
                            var d = new System.Collections.Generic.Dictionary<string, string>();
                            foreach (var f in Directory.EnumerateFiles(semModel, "*", SearchOption.AllDirectories))
                            {
                                var rel = Path.GetRelativePath(semModel, f).Replace('\\', '/');
                                if (rel.StartsWith("definition/") || rel.StartsWith(".semanticus/")) continue; // engine-owned, not a sibling
                                d[rel] = Sha(f);
                            }
                            return d;
                        }
                        bool Matches(System.Collections.Generic.Dictionary<string, string> a, System.Collections.Generic.Dictionary<string, string> b)
                            => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var h) && h == kv.Value);

                        await engine.OpenAsync(Path.Combine(proj, "Probe.pbip"));
                        var sbMeasure = (await engine.ListMeasuresAsync()).First();
                        var sbBefore = Snap();
                        var stamp = "saveback " + Guid.NewGuid().ToString("N").Substring(0, 8);
                        await engine.SetDescriptionAsync(sbMeasure.Ref, stamp, "agent");
                        await engine.SaveAsync(null, null);   // in-place, default format (TMDL)
                        Check("PBIP save-back: every .SemanticModel sibling preserved byte-for-byte (none missing/added/changed)",
                            Matches(sbBefore, Snap()));
                        Check("PBIP save-back: no flat TMDL littered into the .SemanticModel root",
                            !File.Exists(Path.Combine(semModel, "database.tmdl")) && !File.Exists(Path.Combine(semModel, "model.tmdl")));
                        await engine.OpenAsync(Path.Combine(proj, "Probe.pbip"));
                        Check("PBIP save-back: the edit persisted inside definition/",
                            (await engine.ListMeasuresAsync()).First(x => x.Ref == sbMeasure.Ref).Description == stamp);

                        // Format-coercion guard: a folder/JSON save into a PBIP definition/ is coerced to TMDL (no clobber).
                        await engine.SetDescriptionAsync(sbMeasure.Ref, stamp + "-2", "agent");
                        await engine.SaveAsync(null, "folder");
                        Check("PBIP save-back: format='folder' is coerced to TMDL — no JSON written into definition/, siblings intact",
                            !Directory.EnumerateFiles(defDir, "*.json", SearchOption.AllDirectories).Any()
                            && Directory.EnumerateFiles(defDir, "*.tmdl", SearchOption.AllDirectories).Any()
                            && Matches(sbBefore, Snap()));

                        // Sidecar location: the engine layout sidecar anchors in .SemanticModel/, NOT the publishable definition/ tree.
                        var sidecarDir = LayoutStore.DirFor(defDir);
                        Check("PBIP save-back: the layout sidecar anchors in .SemanticModel/, not inside definition/",
                            sidecarDir != null
                            && sidecarDir.StartsWith(semModel, StringComparison.OrdinalIgnoreCase)
                            && !sidecarDir.StartsWith(defDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

                        // Review fix (DL-folder-save-parent): a folder-save aimed at the .SemanticModel PARENT is
                        // resolved into definition/ and coerced to TMDL — no parallel split-model (database.json/tables/)
                        // littered into the publishable item folder; siblings intact.
                        await engine.SetDescriptionAsync(sbMeasure.Ref, stamp + "-3", "agent");
                        await engine.SaveAsync(semModel, "folder");
                        Check("PBIP save-back: a folder-save targeting the .SemanticModel parent is redirected to definition/+TMDL (no split-model litter)",
                            !File.Exists(Path.Combine(semModel, "database.json")) && !Directory.Exists(Path.Combine(semModel, "tables"))
                            && Matches(sbBefore, Snap()));

                        // Review fix (resolver-saveguard-pbism-asymmetry, MAJOR): a pbism-LESS definition/ (a TMDL-only
                        // export) is STILL protected — the guard keys on IsTmdlRoot (structure), not definition.pbism,
                        // so a folder-save is coerced to TMDL and never mixes JSON into the TMDL tree. (Done LAST: deletes pbism.)
                        File.Delete(Path.Combine(semModel, "definition.pbism"));
                        await engine.SaveAsync(defDir, "folder");
                        Check("PBIP save-back: a pbism-LESS definition/ is still coerced to TMDL (structural guard, never lags the open-resolver)",
                            !Directory.EnumerateFiles(defDir, "*.json", SearchOption.AllDirectories).Any()
                            && Directory.EnumerateFiles(defDir, "*.tmdl", SearchOption.AllDirectories).Any());
                    }
                    finally { try { Directory.Delete(proj, true); } catch { } await engine.OpenAsync(FindTestBim()); }
                }

                // ---- PBIP DIAGRAM: seed the Semanticus diagram from Power BI Desktop's native diagramLayout.json ----
                // On opening a PBIP the diagram should match Desktop: get_layout reads diagramLayout.json (read-only base,
                // keyed by nodeLineageTag/nodeIndex) and the engine sidecar overlays it (a Semanticus reposition wins).
                {
                    var proj = Path.Combine(Path.GetTempPath(), "semx_diag_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    var semModel = Path.Combine(proj, "Probe.SemanticModel");
                    var defDir = Path.Combine(semModel, "definition");
                    try
                    {
                        await engine.OpenAsync(FindTestBim());
                        Directory.CreateDirectory(defDir);
                        await engine.SaveAsync(defDir, "TMDL");
                        File.WriteAllText(Path.Combine(semModel, "definition.pbism"), "{\"settings\":{\"qnaEnabled\":true}}");
                        File.WriteAllText(Path.Combine(proj, "Probe.pbip"), "{\"version\":\"1.0\",\"artifacts\":[]}");
                        var tbl = await sessions.Current.ReadAsync<(string Name, string Tag)>(m =>
                        {
                            var t = m.Tables.FirstOrDefault(x => !x.IsHidden && !(x is CalculationGroupTable));
                            return t == null ? (null, null) : (t.Name, t.LineageTag);
                        });
                        // Synthesize Desktop's native layout: one node at a DISTINCTIVE position, keyed by the table's
                        // lineage tag (and nodeIndex). Invariant formatting so the JSON is valid under any locale.
                        var ci = System.Globalization.CultureInfo.InvariantCulture;
                        double nx = 1234.5, ny = 6789.25;
                        var tagJson = string.IsNullOrEmpty(tbl.Tag) ? "" : $"\"nodeLineageTag\":\"{tbl.Tag}\",";
                        File.WriteAllText(Path.Combine(semModel, "diagramLayout.json"),
                            "{\"version\":\"1.1.0\",\"diagrams\":[{\"ordinal\":0,\"nodes\":[{\"location\":{\"x\":"
                            + nx.ToString(ci) + ",\"y\":" + ny.ToString(ci) + "},\"nodeIndex\":\"" + tbl.Name + "\","
                            + tagJson + "\"size\":{\"width\":240,\"height\":120}}]}]}");

                        await engine.OpenAsync(Path.Combine(proj, "Probe.pbip"));
                        var node = (await engine.GetLayoutAsync()).Tables.FirstOrDefault(t => t.Name == tbl.Name);
                        Check("PBIP diagram: get_layout seeds a table position from the native diagramLayout.json (matches Desktop)",
                            node != null && Math.Abs(node.X - nx) < 0.01 && Math.Abs(node.Y - ny) < 0.01);
                        // Sidecar overlay wins: reposition in Semanticus, then get_layout returns the sidecar position.
                        await engine.SaveLayoutAsync(new[] { new LayoutNode { Ref = $"table:{tbl.Name}", Name = tbl.Name, X = 11, Y = 22, Width = 240, Height = 120 } }, "agent");
                        var node2 = (await engine.GetLayoutAsync()).Tables.FirstOrDefault(t => t.Name == tbl.Name);
                        Check("PBIP diagram: a Semanticus reposition (sidecar) overrides the native base layer",
                            node2 != null && Math.Abs(node2.X - 11) < 0.01 && Math.Abs(node2.Y - 22) < 0.01);
                        Check("PBIP diagram: the sidecar override persisted in .SemanticModel/.semanticus/ (not definition/, native file untouched)",
                            File.Exists(Path.Combine(semModel, ".semanticus", "layout.json"))
                            && !Directory.Exists(Path.Combine(defDir, ".semanticus")));
                    }
                    finally { try { Directory.Delete(proj, true); } catch { } await engine.OpenAsync(FindTestBim()); }
                }

                // Diagram native-read hardening (adversarial review): a STRING "ordinal" must NOT nuke the whole native
                // layer (TryGetInt32 throws on a non-Number element), and a TAGLESS native node (Desktop emits these for
                // e.g. a measures table) must still match a tagged live table by the Name fallback.
                {
                    var proj = Path.Combine(Path.GetTempPath(), "semx_diag2_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    var semModel = Path.Combine(proj, "Probe.SemanticModel");
                    var defDir = Path.Combine(semModel, "definition");
                    try
                    {
                        await engine.OpenAsync(FindTestBim());
                        Directory.CreateDirectory(defDir);
                        await engine.SaveAsync(defDir, "TMDL");
                        File.WriteAllText(Path.Combine(semModel, "definition.pbism"), "{\"settings\":{\"qnaEnabled\":true}}");
                        File.WriteAllText(Path.Combine(proj, "Probe.pbip"), "{\"version\":\"1.0\",\"artifacts\":[]}");
                        var name = await sessions.Current.ReadAsync(m => m.Tables.First(x => !x.IsHidden && !(x is CalculationGroupTable)).Name);
                        // STRING ordinal "0" (would throw → drop all) + a node with nodeIndex but NO nodeLineageTag.
                        File.WriteAllText(Path.Combine(semModel, "diagramLayout.json"),
                            "{\"version\":\"1.1.0\",\"defaultDiagram\":\"All tables\",\"diagrams\":[{\"ordinal\":\"0\",\"name\":\"All tables\",\"nodes\":[{\"location\":{\"x\":314,\"y\":159},\"nodeIndex\":\""
                            + name + "\",\"size\":{\"width\":240,\"height\":120}}]}]}");
                        await engine.OpenAsync(Path.Combine(proj, "Probe.pbip"));
                        var node = (await engine.GetLayoutAsync()).Tables.FirstOrDefault(t => t.Name == name);
                        Check("PBIP diagram: a string 'ordinal' doesn't nuke the native layer, and a tagless node still matches by name",
                            node != null && Math.Abs(node.X - 314) < 0.01 && Math.Abs(node.Y - 159) < 0.01);
                    }
                    finally { try { Directory.Delete(proj, true); } catch { } await engine.OpenAsync(FindTestBim()); }
                }

                // ---- RENAME-WITH-FIXUP (the Tabular Editor crown jewel) -------------------------
                var rtables = await engine.ListTreeAsync(null);
                var rt = rtables.FirstOrDefault(t => t.Kind == "table");
                if (rt != null)
                {
                    var aRef = await engine.CreateMeasureAsync(rt.Ref, "Semanticus_A", "1", "agent");
                    var bRef = await engine.CreateMeasureAsync(rt.Ref, "Semanticus_B", "[Semanticus_A] + 1", "agent");
                    Check("create_measure: A and B created", !string.IsNullOrEmpty(aRef) && !string.IsNullOrEmpty(bRef));
                    var ren = await engine.RenameObjectAsync(aRef, "Semanticus_A2", "agent");
                    Check("rename_object: applied", ren.Changed);
                    var bExpr = await engine.GetDaxAsync(bRef);
                    Console.WriteLine($"[i] after renaming A→A2, B = \"{bExpr}\"");
                    Check("rename-with-fixup: B's reference auto-updated to the new name",
                        bExpr != null && bExpr.Contains("[Semanticus_A2]") && !bExpr.Contains("[Semanticus_A]"));
                }

                // ---- UDF (DAX Function) editor SPIKE -------------------------------------------
                // Functions need a high compatibility level; wrap defensively so a TOM rejection
                // reports cleanly instead of failing the whole run.
                try
                {
                    // DAX UDFs need CL >= 1702 — raise it (one-way upgrade) before creating the function.
                    var clr = await engine.SetCompatibilityLevelAsync(1702, "agent");
                    Console.WriteLine($"[i] raised compatibility level to 1702 (changed={clr.Changed})");
                    var fname = "Semanticus_Double";
                    var fref = await engine.CreateFunctionAsync(fname, "(x: INT64) => x * 2", "agent");
                    var fexpr = await engine.GetDaxAsync(fref);
                    Console.WriteLine($"[i] created function {fref}: {fexpr}");
                    Check("create_function: created with expression", !string.IsNullOrEmpty(fref) && fexpr != null && fexpr.Contains("x * 2"));

                    var funcs = await engine.ListFunctionsAsync();
                    Check("list_functions: the new function is listed", funcs.Any(f => f.Ref == fref));

                    await engine.SetDaxAsync(fref, "(x: INT64) => x * 3", "agent");
                    Check("set function expression: updated", (await engine.GetDaxAsync(fref)).Contains("x * 3"));

                    var rf = await engine.RenameObjectAsync(fref, "Semanticus_Triple", "agent");
                    Check("rename function: applied", rf.Changed);
                    Console.WriteLine($"[i] renamed function → {rf.NewRef}");

                    // Round-trip the function through TMDL.
                    var tmp = Path.Combine(Path.GetTempPath(), "semanticus_udf_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    await engine.SaveAsync(tmp, "TMDL");
                    await engine.OpenAsync(tmp);
                    var afterFuncs = await engine.ListFunctionsAsync();
                    Check("udf TMDL round-trip: function survives save+reload", afterFuncs.Any(f => f.Name == "Semanticus_Triple"));
                    Console.WriteLine($"[i] functions after round-trip: [{string.Join(", ", afterFuncs.Select(f => f.Name))}]");
                    try { Directory.Delete(tmp, true); } catch { }
                }
                catch (Exception fex)
                {
                    Console.WriteLine("[i] UDF spike: not supported on this model/CL — " + fex.GetType().Name + ": " + fex.Message);
                }

                // ---- CALENDAR / TIME-INTELLIGENCE GENERATORS (offline authoring) ----------------
                {
                    var dtName = "Semanticus_Calendar";
                    var dt = await engine.GenerateDateTableAsync(dtName, null, null, true, "agent");
                    Check("generate_date_table: created a table", dt.Created.Length == 1);
                    Check("generate_date_table: it's a calculated date table", dt.Created.Length == 1 && dt.Created[0].Expression.Contains("CALENDAR"));
                    var dtExpr = dt.Created.Length == 1 ? dt.Created[0].Expression : "";
                    Check("generate_date_table: integer sort-key + quarter/half-year columns present",
                        dtExpr.Contains("Year Month Sort") && dtExpr.Contains("Half Year") && dtExpr.Contains("Quarter Number"));
                    Check("generate_date_table: relative/today-anchored columns present",
                        dtExpr.Contains("Is Current Month") && dtExpr.Contains("Day Offset") && dtExpr.Contains("Is Today"));
                    var tablesNow = await engine.ListTreeAsync(null);
                    var dtNode = tablesNow.FirstOrDefault(t => t.Name == dtName);
                    Check("generate_date_table: table appears in the tree", dtNode != null);
                    if (dtNode != null)
                    {
                        var info = await engine.GetObjectAsync(dtNode.Ref);
                        Console.WriteLine($"[i] date table '{dtName}' created (kind={info.Kind})");
                    }

                    // Time-intelligence suite over a base measure (created on a real table).
                    var anyTable = (await engine.ListTreeAsync(null)).FirstOrDefault(t => t.Kind == "table" && t.Name != dtName);
                    if (anyTable != null)
                    {
                        var baseRef = await engine.CreateMeasureAsync(anyTable.Ref, "Semanticus_Base", "1", "agent");
                        var ti = await engine.GenerateTimeIntelligenceAsync(baseRef, "'" + dtName + "'[Date]", null, null, "agent");
                        Console.WriteLine($"[i] time-intelligence: created {ti.Created.Length} measures: {string.Join(", ", ti.Created.Select(c => c.Name))}");
                        Check("generate_time_intelligence: created the full suite (6)", ti.Created.Length == 6);
                        Check("generate_time_intelligence: YTD uses TOTALYTD", ti.Created.Any(c => c.Name.EndsWith("YTD") && c.Expression.Contains("TOTALYTD")));
                        Check("generate_time_intelligence: YoY % uses DIVIDE", ti.Created.Any(c => c.Name.Contains("YoY %") && c.Expression.Contains("DIVIDE")));
                        var ytd = ti.Created.FirstOrDefault(c => c.Name.EndsWith("YTD"));
                        if (ytd != null)
                        {
                            var ytdDax = await engine.GetDaxAsync(ytd.Ref);
                            Check("generate_time_intelligence: generated measure resolves + has DAX", !string.IsNullOrWhiteSpace(ytdDax) && ytdDax.Contains("[Semanticus_Base]"));
                            // get_dependencies: the YTD measure DEPENDS ON its base measure (the inverse of get_dependents).
                            var ytdDeps = await engine.GetDependenciesAsync(ytd.Ref);
                            Check("get_dependencies: the YTD measure lists its base measure as a dependency",
                                ytdDeps.Any(d => d.Name == "Semanticus_Base"));
                        }
                        // Idempotency: a second run skips the existing measures.
                        var ti2 = await engine.GenerateTimeIntelligenceAsync(baseRef, "'" + dtName + "'[Date]", null, null, "agent");
                        Check("generate_time_intelligence: re-run skips existing (no duplicates)", ti2.Created.Length == 0 && ti2.Skipped.Length == 6);

                        // New opt-in variants (not in the default 6): rolling windows, SPLY, prior-year-YTD, MoM%.
                        var tiX = await engine.GenerateTimeIntelligenceAsync(baseRef, "'" + dtName + "'[Date]", new[] { "ROLL12", "SPLY", "PYTD", "MOMPCT", "PM" }, "Extra TI", "agent");
                        Console.WriteLine($"[i] extra TI variants: {string.Join(", ", tiX.Created.Select(c => c.Name))}");
                        Check("generate_time_intelligence: 5 new opt-in variants created", tiX.Created.Length == 5);
                        Check("generate_time_intelligence: Rolling 12M uses DATESINPERIOD", tiX.Created.Any(c => c.Name.Contains("Rolling 12M") && c.Expression.Contains("DATESINPERIOD")));
                        Check("generate_time_intelligence: SPLY uses SAMEPERIODLASTYEAR", tiX.Created.Any(c => c.Name.EndsWith("SPLY") && c.Expression.Contains("SAMEPERIODLASTYEAR")));
                        Check("generate_time_intelligence: PY YTD wraps TOTALYTD in a prior-year DATEADD", tiX.Created.Any(c => c.Name.Contains("PY YTD") && c.Expression.Contains("TOTALYTD") && c.Expression.Contains("DATEADD")));
                    }
                }

                // ---- CONNECTIVITY (non-live: error paths + connection-string factory) -----------
                var notConn = await engine.ConnectionStatusAsync();
                Check("connectivity: reports not-connected initially", !notConn.Connected);
                var dax = await engine.RunDaxAsync("EVALUATE ROW(\"x\", 1)", 10);
                Check("connectivity: run_dax w/o connection returns error envelope (no throw)", !string.IsNullOrEmpty(dax.Error));
                var xcs = LiveConnection.XmlaConnectionString("powerbi://api.powerbi.com/v1.0/myorg/WS", "DB", "TKN");
                Check("connectivity: XMLA connection string is well-formed (token as Password)",
                    xcs.Contains("Data Source=powerbi://") && xcs.Contains("Initial Catalog=DB") && xcs.Contains("Password=TKN"));
                var locals = await engine.ListLocalInstancesAsync();
                Check("connectivity: local-instance discovery runs (no throw)", locals != null);
                Console.WriteLine($"[i] local Power BI Desktop instances discovered: {locals.Length}");
                if (locals.Length > 0)
                {
                    Console.WriteLine("[i] opportunistic LIVE verification against a local Power BI Desktop instance (read-only):");
                    try
                    {
                        var st = await engine.ConnectLocalAsync(null, null);
                        Console.WriteLine($"[i]   connected={st.Connected} ({st.DataSource})");
                        var r = await engine.RunDaxAsync("EVALUATE ROW(\"answer\", 21 * 2)", 10);
                        Console.WriteLine(string.IsNullOrEmpty(r.Error)
                            ? $"[i]   live DAX OK: {r.RowCount} row, {r.Columns.Length} col, {r.ElapsedMs}ms, value={r.Rows.FirstOrDefault()?.LastOrDefault()}"
                            : $"[i]   live DAX error: {r.Error}");
                        var dmv = await engine.RunDmvAsync("SELECT [CATALOG_NAME] FROM $SYSTEM.DBSCHEMA_CATALOGS", 10);
                        Console.WriteLine(string.IsNullOrEmpty(dmv.Error)
                            ? $"[i]   live DMV OK: catalogs=[{string.Join(", ", dmv.Rows.Select(x => x.FirstOrDefault()))}]"
                            : $"[i]   live DMV error: {dmv.Error}");
                        var vp = await engine.VertiPaqScanAsync(5);
                        Check("vertipaq: live scan executed without error", string.IsNullOrEmpty(vp.Error));
                        if (string.IsNullOrEmpty(vp.Error))
                        {
                            Console.WriteLine($"[i]   VertiPaq: model {vp.ModelSize / 1024.0 / 1024.0:0.00} MB · {vp.ColumnCount} columns · {vp.Tables.Length} tables");
                            foreach (var c in vp.TopColumns.Take(5))
                                Console.WriteLine($"[i]     {c.Table}[{c.Column}]  {c.TotalSize / 1024.0:0} KB  ({c.PctOfModel}%)  {c.Encoding}");
                        }
                        else Console.WriteLine("[i]   VertiPaq error: " + vp.Error);

                        // ---- AI-NATIVE DAX OPTIMIZE/VERIFY LOOP (schema-agnostic, live) ----------
                        var bench = await engine.BenchmarkDaxAsync("EVALUATE TOPN(500, CALENDAR(DATE(2000,1,1), DATE(2001,12,31)))", 4);
                        Check("benchmark: ran without error", string.IsNullOrEmpty(bench.Error));
                        Check("benchmark: produced per-run timings", bench.RunsMs != null && bench.RunsMs.Length == 4);
                        Console.WriteLine($"[i]   DAX benchmark first={bench.FirstMs}ms warmMin={bench.WarmMinMs}ms median={bench.WarmMedianMs}ms runs=[{string.Join(",", bench.RunsMs ?? System.Array.Empty<long>())}]");

                        var same = await engine.VerifyEquivalenceAsync("1+1", "2", System.Array.Empty<string>(), null, 1000);
                        Check("verify: equivalent expressions report allMatch", string.IsNullOrEmpty(same.Error) && same.AllMatch);
                        var diff = await engine.VerifyEquivalenceAsync("1", "2", System.Array.Empty<string>(), null, 1000);
                        Check("verify: differing expressions detected as mismatch", string.IsNullOrEmpty(diff.Error) && !diff.AllMatch && diff.MismatchCount > 0);
                        Console.WriteLine($"[i]   DAX verify: equal-case allMatch={same.AllMatch}; diff-case mismatches={diff.MismatchCount} (e.g. {(diff.Mismatches.Length > 0 ? diff.Mismatches[0].ValueA + " vs " + diff.Mismatches[0].ValueB : "n/a")})");

                        // ---- TABLE DATA PREVIEW (live) ------------------------------------------
                        var tnames = await engine.RunDmvAsync("SELECT [Name] FROM $SYSTEM.TMSCHEMA_TABLES", 1000);
                        var liveTable = tnames.Rows
                            .Select(r => r.FirstOrDefault()?.ToString())
                            .FirstOrDefault(nm => !string.IsNullOrEmpty(nm) && !nm.StartsWith("LocalDateTable_") && !nm.StartsWith("DateTableTemplate_"));
                        if (liveTable != null)
                        {
                            var prev = await engine.PreviewTableAsync(liveTable, 20);
                            Check("preview_table: returned rows without error", string.IsNullOrEmpty(prev.Error) && prev.Columns.Length > 0);
                            Console.WriteLine($"[i]   preview '{liveTable}': {prev.RowCount} rows × {prev.Columns.Length} cols in {prev.ElapsedMs}ms");

                            // ---- PIVOT / MEASURE TESTING (live) --------------------------------
                            var safeName = liveTable.Replace("'", "''");
                            var pivot = await engine.PivotMeasureAsync($"COUNTROWS('{safeName}')", System.Array.Empty<string>(), null, null, 100);
                            Check("pivot_measure: executed and returned a value", string.IsNullOrEmpty(pivot.Error) && pivot.RowCount >= 1 && pivot.Columns.Length >= 1);
                            Console.WriteLine($"[i]   pivot COUNTROWS('{liveTable}') = {pivot.Rows.FirstOrDefault()?.LastOrDefault()}");

                            // ---- SERVER TIMINGS / DAX TRACE (live) ----------------------------
                            var timings = await engine.ProfileDaxAsync($"EVALUATE TOPN(2000, '{safeName}')");
                            Console.WriteLine($"[i]   server timings: total={timings.TotalMs}ms FE={timings.FeMs}ms SE={timings.SeMs}ms seQ={timings.SeQueries} par={timings.SeParallelism} traceAvail={timings.TraceAvailable}");
                            if (!string.IsNullOrEmpty(timings.Note)) Console.WriteLine("[i]   timings note: " + timings.Note);
                            if (!string.IsNullOrEmpty(timings.Error)) Console.WriteLine("[i]   timings error: " + timings.Error);
                            Check("profile_dax: ran without error", string.IsNullOrEmpty(timings.Error));
                            // The AMO server-timings Trace (FE/SE split) needs an admin XMLA endpoint / local PBI Desktop with
                            // XEvents; it degrades gracefully where unavailable (doc 03 §2.4). Assert only when it IS available —
                            // otherwise a live-but-non-admin connection (or a clean CI runner) would fail a capability that's meant to degrade.
                            if (timings.TraceAvailable) Check("profile_dax: trace captured server timings", timings.TotalMs >= 0);
                            else Console.WriteLine("[i]   profile_dax: server-timings trace unavailable here (needs admin XMLA / XEvents) — skipping the timings assertion (graceful degrade)");

                            // ---- EVALUATEANDLOG DEBUGGING (live spike) ------------------------
                            var ev = await engine.EvaluateAndLogAsync($"EVALUATE ROW(\"v\", EVALUATEANDLOG(COUNTROWS('{safeName}'), \"row count\"))", 100);
                            Console.WriteLine($"[i]   evaluateandlog: traceAvail={ev.TraceAvailable} entries={ev.Entries.Length} resultRows={ev.ResultRowCount}");
                            foreach (var le in ev.Entries.Take(3))
                                Console.WriteLine($"[i]     log '{le.Label}' expr=\"{le.Expression}\" rows={le.RowCount} cols=[{string.Join(",", le.Columns)}] firstVal={le.Rows.FirstOrDefault()?.LastOrDefault()}");
                            if (!string.IsNullOrEmpty(ev.Note)) Console.WriteLine("[i]   evallog note: " + ev.Note);
                            Check("evaluate_and_log: ran without error", string.IsNullOrEmpty(ev.Error));
                            // Same trace dependency as profile_dax: assert the captured log only when the trace is available.
                            if (ev.TraceAvailable) Check("evaluate_and_log: captured a labelled log entry with a value", ev.Entries.Length >= 1 && ev.Entries[0].Label == "row count" && ev.Entries[0].RowCount >= 1);
                            else Console.WriteLine("[i]   evaluate_and_log: trace unavailable here — skipping the log-capture assertion (graceful degrade)");

                            // Iterated case: EVALUATEANDLOG inside an iterator logs one row per iteration.
                            var evi = await engine.EvaluateAndLogAsync($"EVALUATE ROW(\"v\", SUMX(TOPN(3, '{safeName}'), EVALUATEANDLOG(1, \"per row\")))", 100);
                            var iter = evi.Entries.FirstOrDefault(x => x.Label == "per row");
                            Console.WriteLine($"[i]   iterated log: entries={evi.Entries.Length} perRowRows={(iter?.RowCount ?? 0)}");
                            if (evi.TraceAvailable) Check("evaluate_and_log: iterated log captured multiple rows", iter != null && iter.RowCount >= 1);
                            else Console.WriteLine("[i]   evaluate_and_log iterated: trace unavailable here — skipping (graceful degrade)");
                        }

                        // ---- CHANGE-PLAN verify-gating (live): a DAX rewrite must be PROVEN before it applies ----
                        try
                        {
                            var planTable = (await engine.ListTreeAsync(null)).FirstOrDefault(t => t.Kind == "table");
                            if (planTable != null)
                            {
                                await engine.ClearPlanAsync("human");
                                var vmRef = await engine.CreateMeasureAsync(planTable.Ref, "Plan_VerifyTarget", "1", "agent");
                                // An EMPTY verify matrix ([]) proves only the grand total — it is now opt-in
                                // ("proposed"), so the engine must require an explicit approve, and a grand-total
                                // match is applied but labelled UNVERIFIED (not over-claimed as "verified").
                                await engine.AddPlanItemAsync(vmRef, "set_dax", "2", "rewrite (changes results)", System.Array.Empty<string>(), null, "agent");
                                await engine.AddPlanItemAsync(vmRef, "set_dax", "1", "rewrite (equivalent)", System.Array.Empty<string>(), null, "agent");
                                var pv = await engine.GetPlanAsync();
                                var ids = pv.Items.Where(i => i.Kind == "set_dax").Select(i => i.Id).ToArray();
                                Check("plan verify-gate: an empty-matrix set_dax is opt-in (proposed, not auto-approved)", pv.Items.Where(i => i.Kind == "set_dax").All(i => i.Status == "proposed"));
                                foreach (var id in ids) await engine.SetPlanItemAsync(id, null, true, "human");   // explicit opt-in approve
                                var rep = await engine.ApplyPlanAsync(ids, "human");
                                var bad = rep.Items.First(i => i.Title.Contains("changes results"));
                                var good = rep.Items.First(i => i.Title.Contains("equivalent"));
                                Console.WriteLine($"[i]   verify-gate: changing rewrite → {bad.Status}/{bad.VerifyState}; grand-total match → {good.Status}/{good.VerifyState}");
                                Check("plan verify-gate: a results-changing rewrite is SKIPPED even at the grand total", bad.Status == "skipped" && bad.VerifyState == "failed");
                                Check("plan verify-gate: a grand-total-only match applies but is labelled unverified (not 'verified')", good.Status == "applied" && good.VerifyState == "unverified");
                                await engine.ClearPlanAsync("human");
                            }
                        }
                        catch (Exception pex) { Console.WriteLine("[i]   change-plan verify-gate skipped: " + pex.Message); }

                        await engine.DisconnectAsync();
                    }
                    catch (Exception ex) { Console.WriteLine("[i]   live verification skipped: " + ex.Message); }
                }
                Console.WriteLine("[i] connectivity scaffolding ready; live DAX/DMV needs an XMLA endpoint or local PBI Desktop (verify in-app).");

                // ---- MODEL GRAPH (ER diagram data) + PER-FINDING FIX + FIX PROMPT ---------------
                // Re-open AdventureWorks fresh so foreign-key columns are still visible (the earlier
                // ApplySafeFixes run hid them). This isolates the new visual/AI-depth surface.
                await engine.OpenAsync(bim);
                var graph = await engine.GetModelGraphAsync();
                Console.WriteLine($"[i] model graph: {graph.Tables.Length} tables, {graph.Relationships.Length} relationships");
                Check("graph: tables returned", graph.Tables.Length > 0);
                Check("graph: relationships returned", graph.Relationships.Length > 0);
                Check("graph: every relationship has both endpoints", graph.Relationships.All(r => !string.IsNullOrEmpty(r.FromTable) && !string.IsNullOrEmpty(r.ToTable)));
                Check("graph: cardinality strings populated", graph.Relationships.All(r => !string.IsNullOrEmpty(r.FromCardinality) && !string.IsNullOrEmpty(r.ToCardinality)));
                Check("graph: table refs round-trip with ObjectRefs", graph.Tables.All(t => t.Ref.StartsWith("table:")));
                var withRel = graph.Relationships.FirstOrDefault();
                if (withRel != null)
                    Console.WriteLine($"[i]   e.g. {withRel.FromTable}[{withRel.FromColumn}] {withRel.FromCardinality}->{withRel.ToCardinality} {withRel.ToTable}[{withRel.ToColumn}] xfilter={withRel.CrossFilter} active={withRel.IsActive}");

                var fresh = await engine.AiReadinessScanAsync();
                var fkFinding = fresh.Findings.FirstOrDefault(f => f.RuleId == "VIS-FK");
                if (fkFinding != null)
                {
                    var r = await engine.ApplyFixAsync("VIS-FK", fkFinding.ObjectRef, "human");
                    Check("apply_fix VIS-FK: applied", r.Changed);
                    var afterFix = await engine.AiReadinessScanAsync();
                    Check("apply_fix VIS-FK: that finding cleared", !afterFix.Findings.Any(f => f.RuleId == "VIS-FK" && f.ObjectRef == fkFinding.ObjectRef));
                    Console.WriteLine($"[i] apply_fix hid {fkFinding.ObjectRef}");
                }
                var geoFinding = fresh.Findings.FirstOrDefault(f => f.RuleId == "CAT-GEO");
                if (geoFinding != null)
                {
                    var r = await engine.ApplyFixAsync("CAT-GEO", geoFinding.ObjectRef, "human");
                    Check("apply_fix CAT-GEO: applied", r.Changed);
                }

                var descFinding = fresh.Findings.FirstOrDefault(f => f.RuleId == "DESC-MEASURE");
                if (descFinding != null)
                {
                    var fp = await engine.GetFixPromptAsync("DESC-MEASURE", descFinding.ObjectRef);
                    Console.WriteLine($"[i] fix prompt tool={fp.Tool}: {fp.Prompt.Substring(0, Math.Min(90, fp.Prompt.Length))}…");
                    Check("get_fix_prompt: routes DESC-MEASURE to set_description", fp.Tool == "set_description");
                    Check("get_fix_prompt: prompt names the object ref", fp.Prompt.Contains(descFinding.ObjectRef));
                }
                Check("apply_fix: non-deterministic rule is rejected (not silently applied)",
                    await Throws(() => engine.ApplyFixAsync("DESC-MEASURE", descFinding?.ObjectRef ?? "measure:x/y", "human")));

                // ---- CHANGE-PLAN ENGINE (analyse → review → fix-all in ONE undoable transaction) -------
                await engine.OpenAsync(bim);   // fresh AdventureWorks so FK columns are visible again
                {
                    var view = await engine.ProposePlanAsync(null, true, 40, "human");
                    Console.WriteLine($"[i] change-plan: {view.Summary.Total} items ({view.Summary.Deterministic} det · {view.Summary.Bpa} bpa · {view.Summary.Ai} ai · {view.Summary.NeedsContent} need content)");
                    Check("plan: propose produced items", view.Summary.Total > 0);
                    Check("plan: produced deterministic/bpa fixes", view.Summary.Deterministic + view.Summary.Bpa > 0);
                    Check("plan: every item has id/kind/status", view.Items.All(i => !string.IsNullOrEmpty(i.Id) && !string.IsNullOrEmpty(i.Kind) && !string.IsNullOrEmpty(i.Status)));
                    Check("plan: deterministic items are auto-approved with a before→after", view.Items.Where(i => i.Source != "ai").All(i => i.Status == "approved" && i.After != null));
                    Check("plan: AI items carry grounding and await content", view.Items.Where(i => i.Source == "ai" && i.Status == "needs_content").All(i => i.Grounding != null && i.After == null));
                    Check("plan: nothing applied yet (no item is 'applied')", view.Items.All(i => i.Status != "applied"));

                    var got = await engine.GetPlanAsync();
                    Check("plan: get_plan mirrors the live plan", got.PlanId == view.PlanId && got.Items.Length == view.Items.Length);

                    // Author one AI description item (Claude filling content → approved).
                    var aiDesc = view.Items.FirstOrDefault(i => i.Kind == "set_description" && i.Status == "needs_content");
                    if (aiDesc != null)
                    {
                        var filled = await engine.SetPlanItemAsync(aiDesc.Id, "Authored business description for the change-plan smoke.", null, "agent");
                        var fi = filled.Items.First(i => i.Id == aiDesc.Id);
                        Check("plan: authoring an AI item sets After + Generated + approves it", fi.After != null && fi.Generated && fi.Status == "approved");
                    }

                    // Reject one deterministic item — it must be excluded from the apply batch.
                    var reject = view.Items.FirstOrDefault(i => i.Status == "approved" && i.Source != "ai");
                    var rejectedId = reject?.Id;
                    if (reject != null) await engine.SetPlanItemAsync(reject.Id, null, false, "human");

                    // Apply ALL currently-approved items as one transaction.
                    var report = await engine.ApplyPlanAsync(null, "human");
                    Console.WriteLine($"[i] apply_plan: {report.Note}");
                    Check("plan: apply applied at least one change", report.AppliedCount > 0);
                    Check("plan: apply failed nothing", report.FailedCount == 0);
                    Check("plan: applied-count matches items marked applied", report.Items.Count(i => i.Status == "applied") == report.AppliedCount);
                    Check("plan: rejected item was excluded from the batch", rejectedId == null || report.Items.All(i => i.Id != rejectedId));
                    Check("plan: applying the plan reduced BPA violations", report.BpaViolationsAfter <= report.BpaViolationsBefore);
                    Console.WriteLine($"[i]   BPA {report.BpaViolationsBefore}→{report.BpaViolationsAfter} · grade {report.GradeBefore}→{report.GradeAfter} · overall {report.OverallBefore:0.0}→{report.OverallAfter:0.0}");

                    // ONE undo reverts the entire batch.
                    await engine.UndoAsync("human");
                    var afterUndo = await engine.BpaScanAsync();
                    Check("plan: a single undo reverts the whole apply batch", afterUndo.ViolationCount == report.BpaViolationsBefore);

                    // Explicit-id apply must still honour rejection: passing a rejected item's id does NOT apply it.
                    var detPlan = await engine.ProposePlanAsync(null, false, 0, "human");   // deterministic-only plan
                    Check("plan: a deterministic-only plan (includeAi=false) still seeds fixes with no AI items", detPlan.Summary.Total > 0 && detPlan.Summary.Ai == 0);
                    var det = detPlan.Items.Where(i => i.Status == "approved").ToList();
                    if (det.Count >= 1)
                    {
                        var rj = det[0];
                        await engine.SetPlanItemAsync(rj.Id, null, false, "human");          // reject it
                        var allIds = det.Select(i => i.Id).ToArray();                        // include the rejected id explicitly
                        var rep2 = await engine.ApplyPlanAsync(allIds, "human");
                        Check("plan: explicit-id apply excludes a rejected item (no approval bypass)", rep2.Items.All(i => !(i.Id == rj.Id && i.Status == "applied")));
                        await engine.UndoAsync("human");
                    }

                    var cleared = await engine.ClearPlanAsync("human");
                    Check("plan: clear empties the plan", cleared.Items.Length == 0);
                    Check("plan: get_plan after clear is empty", (await engine.GetPlanAsync()).Items.Length == 0);

                    // Scope: a table-scoped plan only contains items on that table.
                    var firstTable = (await engine.ListTreeAsync(null)).FirstOrDefault(t => t.Kind == "table");
                    if (firstTable != null)
                    {
                        var scoped = await engine.ProposePlanAsync(firstTable.Ref, true, 40, "human");
                        var tn = firstTable.Name;
                        Check("plan: scoped plan only touches the chosen table", scoped.Items.All(i =>
                            i.ObjectRef.StartsWith("table:" + tn) ||
                            i.ObjectRef.StartsWith("measure:" + tn + "/") ||
                            i.ObjectRef.StartsWith("column:" + tn + "/")));
                        Console.WriteLine($"[i]   scoped to '{tn}': {scoped.Summary.Total} items");
                        await engine.ClearPlanAsync("human");
                    }

                    // Multi-rename batch: renaming a table AND a child column in ONE apply must not invalidate
                    // the child's ref — child renames apply before the table rename.
                    var ctRef = (await engine.GenerateDateTableAsync("Plan_RenameTbl", null, null, false, "human")).Created.FirstOrDefault()?.Ref;
                    if (ctRef != null)
                    {
                        await engine.CreateMeasureAsync(ctRef, "Plan_RenameMeas", "1", "human");
                        await engine.ClearPlanAsync("human");
                        // Add the PARENT-TABLE rename FIRST: with naive insertion order it would run before the
                        // child and break it. The child-renames-first ordering must reorder them regardless.
                        await engine.AddPlanItemAsync("table:Plan_RenameTbl", "rename", "Plan_RenameTbl2", "rename parent table", null, null, "human");
                        await engine.AddPlanItemAsync("measure:Plan_RenameTbl/Plan_RenameMeas", "rename", "Plan_RenameMeas2", "rename child measure", null, null, "human");
                        var rids = (await engine.GetPlanAsync()).Items.Select(i => i.Id).ToArray();
                        foreach (var id in rids) await engine.SetPlanItemAsync(id, null, true, "human");   // renames are opt-in
                        var rr = await engine.ApplyPlanAsync(rids, "human");
                        Console.WriteLine($"[i]   multi-rename batch: {rr.Note}");
                        Check("plan: a table + child-column rename in one batch both apply (no stale-ref failure)",
                            rr.AppliedCount == 2 && rr.FailedCount == 0);
                        var renamed = await engine.ListTreeAsync(null);
                        Check("plan: the renamed table exists under its new name", renamed.Any(t => t.Name == "Plan_RenameTbl2"));
                        await engine.ClearPlanAsync("human");
                    }

                    // ---- PARTIAL-FAILURE ATOMICITY (#5, flagship safety) --------------------------------
                    // A multi-item batch where exactly ONE item fails at apply time: the good items must commit,
                    // the bad one lands Status=="failed" (with a note), and -- because every item mutates inside
                    // the single MutateAsync in ApplyPlanAsync -- the whole partial batch is ONE undo step that a
                    // single undo reverts. We force exactly one apply-time failure WITHOUT failing add-time: a
                    // set_summarize_by whose object (a real column) resolves fine at add time, but whose After is
                    // an invalid enum value -> Enum.Parse<AggregateFunction> throws inside ApplyOneItem -> the
                    // per-item catch marks it failed while the surrounding good items still commit.
                    await engine.OpenAsync(bim);   // fresh AdventureWorks (undo timeline reset, no prior plan state)
                    await engine.ClearPlanAsync("human");
                    {
                        var pfMeasures = new System.Collections.Generic.List<string>();
                        string pfColumn = null;
                        foreach (var t in (await engine.ListTreeAsync(null)).Where(n => n.Kind == "table"))
                        {
                            var kids = await engine.ListTreeAsync(t.Ref);
                            foreach (var meas in kids.Where(k => k.Kind == "measure"))
                                if (pfMeasures.Count < 2) pfMeasures.Add(meas.Ref);
                            if (pfColumn == null) pfColumn = kids.FirstOrDefault(k => k.Kind == "column")?.Ref;
                            if (pfMeasures.Count >= 2 && pfColumn != null) break;
                        }
                        Check("plan(partial): found two measures + a column to build a mixed-outcome batch",
                            pfMeasures.Count == 2 && pfColumn != null);
                        if (pfMeasures.Count == 2 && pfColumn != null)
                        {
                            var beforeDescs = new string[2];
                            for (int i = 0; i < 2; i++)
                            {
                                var oi = await engine.GetObjectAsync(pfMeasures[i]);
                                beforeDescs[i] = oi.Properties.TryGetValue("description", out var d) ? d as string : null;
                            }

                            await engine.AddPlanItemAsync(pfMeasures[0], "set_description",
                                "PARTIAL-batch good item A.", "good desc A", null, null, "human");
                            await engine.AddPlanItemAsync(pfColumn, "set_summarize_by",
                                "NotAnAggregate", "invalid summarize (forces apply-time failure)", null, null, "human");
                            await engine.AddPlanItemAsync(pfMeasures[1], "set_description",
                                "PARTIAL-batch good item B.", "good desc B", null, null, "human");

                            var planView = await engine.GetPlanAsync();
                            var badId = planView.Items.First(i => i.Kind == "set_summarize_by").Id;
                            Check("plan(partial): all three items are approved before apply (none fails add-time)",
                                planView.Items.Length == 3 && planView.Items.All(i => i.Status == "approved"));

                            var prep = await engine.ApplyPlanAsync(null, "human");   // Pro => a >1 batch is allowed
                            Console.WriteLine($"[i] apply_plan (partial): {prep.Note}");

                            Check("plan(partial): exactly one item failed",  prep.FailedCount == 1);
                            Check("plan(partial): the two good items still committed (not aborted by the failure)",
                                prep.AppliedCount == 2);
                            Check("plan(partial): the bad item is Status=failed with a diagnostic note",
                                prep.Items.Any(i => i.Id == badId && i.Status == "failed" && !string.IsNullOrEmpty(i.Note)));
                            Check("plan(partial): both good set_description items are Status=applied",
                                prep.Items.Where(i => i.Kind == "set_description").All(i => i.Status == "applied"));

                            var afterA = await engine.GetObjectAsync(pfMeasures[0]);
                            var afterB = await engine.GetObjectAsync(pfMeasures[1]);
                            Check("plan(partial): good edits visible in the model after the partial apply",
                                (afterA.Properties.TryGetValue("description", out var da) ? da as string : null) == "PARTIAL-batch good item A."
                                && (afterB.Properties.TryGetValue("description", out var db) ? db as string : null) == "PARTIAL-batch good item B.");

                            await engine.UndoAsync("human");
                            var undoA = await engine.GetObjectAsync(pfMeasures[0]);
                            var undoB = await engine.GetObjectAsync(pfMeasures[1]);
                            var ua = undoA.Properties.TryGetValue("description", out var ra) ? ra as string : null;
                            var ub = undoB.Properties.TryGetValue("description", out var rb) ? rb as string : null;
                            Check("plan(partial): ONE undo reverts the entire partial batch (both good commits restored)",
                                ua == beforeDescs[0] && ub == beforeDescs[1]);

                            await engine.ClearPlanAsync("human");
                        }
                    }
                }

                // ---- Wave-2 DMV rules (live per-column cardinality) — synthetic stats + parser fixture, no live conn ----
                {
                    await engine.OpenAsync(FindTestBim());   // fresh AdventureWorks
                    var offline = await engine.AiReadinessScanAsync();
                    Check("dmv rules are dormant offline (no SCALE-* findings without live stats)",
                        !offline.Findings.Any(f => f.RuleId.StartsWith("SCALE-")));

                    // Synthesize cardinality on VISIBLE TEXT columns (the indexed population): all small, ONE spiked over
                    // the per-column high-card threshold (which also pushes the text total over the 5M Q&A ceiling).
                    var stats = await sessions.Current.ReadAsync(m =>
                    {
                        var st = new ReadinessLiveStats();
                        var cols = m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber
                            && !(c.Table is CalculationGroupTable) && c.DataType == DataType.String).ToList();
                        foreach (var c in cols) st.Set(c.Table?.Name, c.Name, 100);
                        st.Set(cols.First().Table?.Name, cols.First().Name, 6_000_000);
                        return st;
                    });
                    var liveCard = await sessions.Current.ReadAsync(m => new ReadinessAnalyzer().Analyze(m, stats));
                    Check("dmv SCALE-HICARD-COLUMN fires on a >1M visible text column",
                        liveCard.Findings.Any(f => f.RuleId == "SCALE-HICARD-COLUMN"));
                    Check("dmv SCALE-QNA-INDEX fires when indexed unique text values exceed the 5M ceiling",
                        liveCard.Findings.Any(f => f.RuleId == "SCALE-QNA-INDEX"));
                    Check("dmv rules land in CopilotLimits (score drops below 100)",
                        liveCard.Categories.First(c => c.Category == "CopilotLimits").Score < 100);
                    var offlineAgain = await sessions.Current.ReadAsync(m => new ReadinessAnalyzer().Analyze(m, null));
                    Check("Analyze(model, null) == the offline scan (no dmv leakage into the offline path)",
                        offlineAgain.Findings.Length == offline.Findings.Length && !offlineAgain.Findings.Any(f => f.RuleId.StartsWith("SCALE-")));

                    // Numeric high-cardinality must NOT trip the rules — Q&A indexes only TEXT (the review's FP fix).
                    var numStats = await sessions.Current.ReadAsync(m =>
                    {
                        var st = new ReadinessLiveStats();
                        var num = m.AllColumns.FirstOrDefault(c => !c.IsHidden && c.Type != ColumnType.RowNumber
                            && (c.DataType == DataType.Int64 || c.DataType == DataType.Double || c.DataType == DataType.Decimal));
                        if (num != null) st.Set(num.Table?.Name, num.Name, 9_000_000);   // huge, but numeric
                        return st;
                    });
                    var numScan = await sessions.Current.ReadAsync(m => new ReadinessAnalyzer().Analyze(m, numStats));
                    Check("dmv ignores NUMERIC high-cardinality (Q&A indexes only text — no false positive)",
                        !numScan.Findings.Any(f => f.RuleId.StartsWith("SCALE-")));

                    // BuildLiveStats parser fixture: real COLUMNSTATISTICS column shape; RowNumber skipped; case-insensitive;
                    // graceful-degrade (empty map) when the cardinality column is absent.
                    var rsGood = new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "[Table Name]" }, new ColumnDef { Name = "[Column Name]" }, new ColumnDef { Name = "[Cardinality]" }, new ColumnDef { Name = "[Max Length]" } },
                        Rows = new[]
                        {
                            new object[] { "Sales", "RowNumber-ABC-123", 100L, 0L },
                            new object[] { "Customer", "Name", 18401L, 27L },
                            new object[] { "Product", "Category", 8L, 11L },
                        },
                    };
                    var parsed = LocalEngine.BuildLiveStats(rsGood);
                    Check("BuildLiveStats maps (table,column)->cardinality from the COLUMNSTATISTICS shape",
                        parsed.TryGet("Customer", "Name", out var cn) && cn == 18401 && parsed.TryGet("Product", "Category", out _));
                    Check("BuildLiveStats skips RowNumber rows", !parsed.TryGet("Sales", "RowNumber-ABC-123", out _));
                    Check("BuildLiveStats cardinality lookup is case-insensitive", parsed.TryGet("customer", "NAME", out _));
                    var rsBad = new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "[Table Name]" }, new ColumnDef { Name = "[Column Name]" }, new ColumnDef { Name = "[Distinct Count]" } },
                        Rows = new[] { new object[] { "A", "B", 5L } },
                    };
                    Check("BuildLiveStats degrades gracefully (empty map) when the cardinality column is absent",
                        LocalEngine.BuildLiveStats(rsBad).Cardinality.Count == 0);
                }

                // ---- OBJECT-LEVEL SECURITY (OLS): table + column metadata permissions (CL >= 1400) -----------
                // AllProperties.bim is CL 1570 so the RoleOLSIndexer / ColumnOLSIndexer are live (the CL-1200
                // AdventureWorks model only exercises the guard, in McpSmoke). Each set surfaces via list_roles and
                // each Default is a clean net-zero (the now-empty TablePermission is removed via NoEffect).
                {
                    var olsModel = FindTestData("AllProperties.bim");
                    if (olsModel != null)
                    {
                        await engine.OpenAsync(olsModel);
                        var pick = await sessions.Current.ReadAsync(m =>
                        {
                            var t = m.Tables.FirstOrDefault(x => !(x is CalculationGroupTable) && x.Columns.Any(c => c.Type != ColumnType.RowNumber));
                            var c = t?.Columns.First(col => col.Type != ColumnType.RowNumber);
                            return (Table: t?.Name, Column: c?.Name);
                        });
                        if (pick.Table != null && pick.Column != null)
                        {
                            const string olsRole = "OLS Smoke";
                            var tableRef = "table:" + pick.Table;
                            var columnRef = "column:" + pick.Table + "/" + pick.Column;
                            await engine.CreateRoleAsync(olsRole, "Read", "agent");

                            // Table-level: None hides the table; surfaced under ObjectPermissions.
                            var tNone = await engine.SetTableObjectPermissionAsync(olsRole, tableRef, "None", "agent");
                            var afterT = (await engine.ListRolesAsync()).First(r => r.Name == olsRole);
                            Check("OLS: set_table_ols None applied + surfaced in list_roles",
                                tNone.Changed && afterT.ObjectPermissions.Any(op => op.Table == pick.Table && op.MetadataPermission == "None"));
                            var tNoop = await engine.SetTableObjectPermissionAsync(olsRole, tableRef, "None", "agent");
                            Check("OLS: set_table_ols is net-zero when unchanged", !tNoop.Changed);

                            // Back to Default: the now-empty table permission is removed -> truly net-zero.
                            var tDef = await engine.SetTableObjectPermissionAsync(olsRole, tableRef, "Default", "agent");
                            Check("OLS: set_table_ols Default clears the table OLS (empty permission removed)",
                                tDef.Changed && (await engine.ListRolesAsync()).First(r => r.Name == olsRole).ObjectPermissions.All(op => op.Table != pick.Table));

                            // Column-level: None hides one column; surfaced under that table's Columns; Default clears it.
                            var cNone = await engine.SetColumnObjectPermissionAsync(olsRole, columnRef, "None", "agent");
                            var afterC = (await engine.ListRolesAsync()).First(r => r.Name == olsRole);
                            Check("OLS: set_column_ols None applied + surfaced (column under its table)",
                                cNone.Changed && afterC.ObjectPermissions.Any(op => op.Columns.Any(cp => cp.Column == pick.Column && cp.MetadataPermission == "None")));
                            var cDef = await engine.SetColumnObjectPermissionAsync(olsRole, columnRef, "Default", "agent");
                            Check("OLS: set_column_ols Default clears the column OLS (net-zero)",
                                cDef.Changed && (await engine.ListRolesAsync()).First(r => r.Name == olsRole).ObjectPermissions.All(op => op.Columns.Length == 0));

                            // A calc group cannot carry OLS (matches the vendor's NonCalculatedGroupTables exclusion).
                            Check("OLS: set_table_ols rejects a calculation-group table",
                                await Throws(() => engine.SetTableObjectPermissionAsync(olsRole, "table:CalculationGroup", "None", "agent")));
                            // The column path guards calc-group columns too (sibling of the table guard).
                            var cgCol = await sessions.Current.ReadAsync(m => {
                                var cg = m.Tables.OfType<CalculationGroupTable>().FirstOrDefault();
                                return cg != null && cg.Columns.Count > 0 ? cg.Name + "/" + cg.Columns.First().Name : null;
                            });
                            if (cgCol != null)
                                Check("OLS: set_column_ols rejects a calculation-group column",
                                    await Throws(() => engine.SetColumnObjectPermissionAsync(olsRole, "column:" + cgCol, "None", "agent")));
                            // An unknown permission token is rejected.
                            Check("OLS: an invalid permission token is rejected",
                                await Throws(() => engine.SetTableObjectPermissionAsync(olsRole, tableRef, "Bogus", "agent")));

                            await engine.DeleteRoleAsync(olsRole, "agent");
                            Check("OLS: role removed (net-zero)", (await engine.ListRolesAsync()).All(r => r.Name != olsRole));
                        }
                    }
                }

                // ---- Calc-group authoring (create_calculation_group/item; needs CL >= 1470 — AllProperties is 1570) ----
                {
                    var cgModel = FindTestData("AllProperties.bim");
                    if (cgModel != null)
                    {
                        await engine.OpenAsync(cgModel);
                        var beforeT = (await engine.GetModelGraphAsync()).Tables.Length;
                        var cgRef = await engine.CreateCalculationGroupAsync("Smoke CG", "agent");
                        Check("create_calculation_group returns 'table:Smoke CG'", cgRef == "table:Smoke CG");
                        var ciRef = await engine.CreateCalculationItemAsync("table:Smoke CG", "YTD", "SELECTEDMEASURE()", "agent");

                        // set/clear the calc item's DYNAMIC format-string expression (round-trips via the property grid)
                        await engine.SetCalcItemFormatStringAsync(ciRef, "\"0.0%\"", "agent");
                        var fmtSet = (await engine.GetObjectPropertiesAsync(ciRef)).FirstOrDefault(p => p.Name.Contains("FormatStringExpression"))?.Value;
                        Check("set_calc_item_format_string: the dynamic format expression is set", fmtSet != null && fmtSet.Contains("0.0%"));
                        await engine.SetCalcItemFormatStringAsync(ciRef, "", "agent");
                        var fmtCleared = (await engine.GetObjectPropertiesAsync(ciRef)).FirstOrDefault(p => p.Name.Contains("FormatStringExpression"))?.Value;
                        Check("set_calc_item_format_string: an empty value CLEARS the expression", string.IsNullOrEmpty(fmtCleared));

                        // set the calc group's precedence (round-trips via the property grid)
                        await engine.SetCalcGroupPrecedenceAsync("table:Smoke CG", 7, "agent");
                        var prec = (await engine.GetObjectPropertiesAsync("table:Smoke CG")).FirstOrDefault(p => p.Name.Contains("CalculationGroupPrecedence"))?.Value;
                        Check("set_calc_group_precedence: precedence round-trips (=7)", prec == "7");

                        Check("create_calculation_item returns a resolvable ref + delete_object removes it BY that ref (round-trip)",
                            ciRef == "calcitem:Smoke CG/YTD"
                            && (await engine.DeleteObjectAsync(ciRef, "agent")).Changed
                            && !(await engine.DeleteObjectAsync(ciRef, "agent")).Changed);
                        var del = await engine.DeleteObjectAsync("table:Smoke CG", "agent");
                        Check("delete_object removes the calc group (net-zero)",
                            del.Changed && (await engine.GetModelGraphAsync()).Tables.Length == beforeT);
                    }
                }

                // ---- Tree expansion: the explorer now surfaces every object type with menu-distinct kinds, and the
                //      new ref schemes (calcitem/hierarchy/level/relationship/role/partition) round-trip to properties. ----
                {
                    var txModel = FindTestData("AllProperties.bim");
                    if (txModel != null)
                    {
                        await engine.OpenAsync(txModel);
                        var root = await engine.ListTreeAsync(null);
                        var tbl = root.FirstOrDefault(x => x.Kind == "table");
                        Check("tree: model root lists tables", tbl != null);
                        var tblKids = tbl == null ? System.Array.Empty<TreeNode>() : await engine.ListTreeAsync(tbl.Ref);
                        Check("tree: a table now surfaces its partition(s) as 'partition' nodes (previously only measures+columns)",
                            tblKids.Any(k => k.Kind == "partition"));

                        // Calc group gets its own kind (distinct menu); its items surface as resolvable 'calcitem' nodes.
                        var cg = await engine.CreateCalculationGroupAsync("Tree CG", "agent");
                        var ci = await engine.CreateCalculationItemAsync(cg, "TreeYTD", "SELECTEDMEASURE()", "agent");
                        var cgNode = (await engine.ListTreeAsync(null)).FirstOrDefault(x => x.Ref == cg);
                        Check("tree: a calculation group surfaces with kind 'calcgroup'", cgNode != null && cgNode.Kind == "calcgroup");
                        var cgKids = cgNode == null ? System.Array.Empty<TreeNode>() : await engine.ListTreeAsync(cgNode.Ref);
                        Check("tree: calc-group items surface as 'calcitem' nodes that resolve to properties",
                            cgKids.Any(k => k.Kind == "calcitem" && k.Ref == ci)
                            && (await engine.GetObjectPropertiesAsync(ci)).Length > 0);
                        // Calc items are DAX-editable too (getDax/setDax now handle CalculationItem) — the tree's
                        // single-click → Monaco editing path depends on this round-trip.
                        Check("calc item DAX round-trips through getDax/setDax (the Monaco edit path)",
                            await engine.GetDaxAsync(ci) == "SELECTEDMEASURE()"
                            && (await engine.SetDaxAsync(ci, "CALCULATE(SELECTEDMEASURE())", "agent")).Changed
                            && await engine.GetDaxAsync(ci) == "CALCULATE(SELECTEDMEASURE())");
                        await engine.DeleteObjectAsync(cg, "agent");

                        // Hierarchy → 'level' children.
                        var hTbl = (await engine.ListTreeAsync(null)).FirstOrDefault(x => x.Kind == "table");
                        var firstCol = hTbl == null ? null : (await engine.ListTreeAsync(hTbl.Ref)).FirstOrDefault(k => k.Kind == "column");
                        if (firstCol != null)
                        {
                            var hRef = await engine.CreateHierarchyAsync(hTbl.Ref, "Tree Hierarchy", new[] { firstCol.Name }, "agent");
                            var hNode = (await engine.ListTreeAsync(hTbl.Ref)).FirstOrDefault(k => k.Kind == "hierarchy");
                            Check("tree: a hierarchy surfaces with kind 'hierarchy' and lists 'level' children",
                                hNode != null && (await engine.ListTreeAsync(hNode.Ref)).Any(l => l.Kind == "level"));
                            await engine.DeleteObjectAsync(hRef, "agent");
                        }

                        // Collection folders for relationships/roles surface when present, and their refs resolve.
                        var rootF = await engine.ListTreeAsync(null);
                        var relFolder = rootF.FirstOrDefault(x => x.Kind == "folderRelationships");
                        if (relFolder != null)
                        {
                            var rels = await engine.ListTreeAsync(relFolder.Ref);
                            var rel = rels.FirstOrDefault(r => r.Kind == "relationship");
                            Check("tree: Relationships folder lists 'relationship' nodes that resolve to properties",
                                rel != null && (await engine.GetObjectPropertiesAsync(rel.Ref)).Length > 0);
                        }
                        var roleFolder = rootF.FirstOrDefault(x => x.Kind == "folderRoles");
                        if (roleFolder != null)
                        {
                            var roles = await engine.ListTreeAsync(roleFolder.Ref);
                            var role = roles.FirstOrDefault(r => r.Kind == "role");
                            Check("tree: Roles folder lists 'role' nodes that resolve to properties",
                                role != null && (await engine.GetObjectPropertiesAsync(role.Ref)).Length > 0);
                        }
                    }
                }

                // ---- DIRECT LAKE read-correctness (Phase 1) -------------------------------------
                // No Direct Lake fixture ships, so build one in-memory via the vendored handler and assert the read
                // paths label it honestly (entity binding surfaced, storage rows reported as unknown not 0). This
                // also de-risks the later create-from-scratch path. Wrapped so an unexpected TOM rejection on this
                // environment degrades to a logged skip rather than failing the whole run.
                try
                {
                    using var dlHandler = new TabularModelHandler(1604, null, true);   // PowerBI mode, Direct-Lake-capable CL
                    var dlm = dlHandler.Model;
                    dlHandler.BeginUpdate("seed direct lake");
                    var dlt = dlm.AddTable("Sales");
                    var ep = EntityPartition.CreateNew(dlt, "Sales");
                    ep.EntityName = "FactSales"; ep.SchemaName = "dbo"; ep.Mode = ModeType.DirectLake;
                    foreach (var other in dlt.Partitions.Where(p => !ReferenceEquals(p, ep)).ToList()) other.Delete(); // drop the auto default partition
                    // (Model.DefaultMode=DirectLake is rejected by TOM without full Fabric scaffolding — Phase 2's job.
                    //  An explicit DirectLake-mode Entity partition is enough to make the table Direct Lake here.)
                    dlHandler.EndUpdate();

                    Check("direct-lake: IsModelDirectLake detects an Entity/DirectLake model", DirectLakeInfo.IsModelDirectLake(dlm));
                    Check("direct-lake: IsTableDirectLake detects the table", DirectLakeInfo.IsTableDirectLake(dlt));

                    var dlPart = LocalEngine.BuildDocTable(dlt).Partitions.FirstOrDefault(p => p.SourceType == "Entity");
                    Check("direct-lake: doc partition surfaces the Entity binding (not the null M expression)",
                        dlPart != null && dlPart.Source == "Entity: dbo.FactSales" && dlPart.EntityName == "FactSales"
                        && dlPart.SchemaName == "dbo" && dlPart.Mode == "DirectLake");
                    Console.WriteLine($"[i] direct-lake doc partition: mode={dlPart?.Mode} sourceType={dlPart?.SourceType} source=\"{dlPart?.Source}\"");

                    using var imHandler = new TabularModelHandler(1604, null, true);
                    imHandler.BeginUpdate("seed import"); imHandler.Model.AddTable("ImportSales"); imHandler.EndUpdate();
                    Check("direct-lake: an Import model is NOT flagged as Direct Lake (no false positive)", !DirectLakeInfo.IsModelDirectLake(imHandler.Model));
                }
                catch (Exception dlex) { Console.WriteLine("[i] direct-lake in-memory check skipped (TOM rejected DL construction here): " + dlex.Message); _failures++; }

                // VertiPaq.Compute is a pure function. Assert the tri-state mode keeps resident-only and unknown
                // observations from presenting row totals, while proven Import remains unchanged.
                {
                    ResultSet Rs(string[] names, object[][] rows) => new ResultSet { Columns = names.Select(n => new ColumnDef { Name = n, Type = "x" }).ToArray(), Rows = rows, RowCount = rows.Length };
                    var seg = Rs(new[] { "DIMENSION_NAME", "COLUMN_ID", "USED_SIZE" }, new[] { new object[] { "Sales", "1", 1000L }, new object[] { "Sales", "2", 2000L } });
                    var col = Rs(new[] { "DIMENSION_NAME", "ATTRIBUTE_NAME", "COLUMN_ID", "COLUMN_ENCODING", "DICTIONARY_SIZE", "STRING_INDEX_SIZE" },
                        new[] { new object[] { "Sales", "Amount", "1", "1", 500L, 100L }, new object[] { "Sales", "Qty", "2", "2", 300L, 0L } });
                    var tbl = Rs(new[] { "DIMENSION_NAME", "ROWS_COUNT" }, new[] { new object[] { "Sales", 2300000L } });
                    var imp = VertiPaq.Compute(seg, col, tbl, 25, VpaqStorageMode.Import);
                    Check("vertipaq(import): rows reported (not null), no caveat, mode proven",
                        imp.Tables.Length > 0 && imp.Tables.All(t => t.Rows.HasValue) && imp.StorageMode == VpaqStorageMode.Import && imp.Caveat == null);
                    var dl = VertiPaq.Compute(seg, col, tbl, 25, VpaqStorageMode.DirectLake);
                    Check("vertipaq(direct lake): row counts become unknown (null) + mode + caveat set",
                        dl.StorageMode == VpaqStorageMode.DirectLake && !string.IsNullOrEmpty(dl.Caveat) && dl.Tables.Length > 0 && dl.Tables.All(t => t.Rows == null));
                    Check("vertipaq(direct lake): the flag only affects rows/labelling, not sizes",
                        dl.ModelSize == imp.ModelSize && dl.ModelSize > 0);
                    var unknown = VertiPaq.Compute(seg, col, tbl, 25);
                    Check("vertipaq(unknown): row counts stay unavailable and mode does not fall through to import",
                        unknown.StorageMode == VpaqStorageMode.Unknown && !string.IsNullOrEmpty(unknown.Caveat) && unknown.Tables.All(t => t.Rows == null));
                }

                // ---- CREATE FROM SCRATCH (Phase 2) — build a Fabric-first model from nothing, save, reopen ----
                {
                    var created = await engine.CreateModelAsync("SmokeFromScratch", 1604);
                    Check("create_model: a blank session is created (named, 0 tables)",
                        !string.IsNullOrEmpty(created.SessionId) && created.Tables == 0 && created.ModelName == "SmokeFromScratch");
                    Check("create_model: an unsaved model refuses a no-path save (first save needs a path)",
                        await Throws(() => engine.SaveAsync(null, "TMDL")));

                    var dsName = await engine.CreateDataSourceAsync("FabricSql", "ws.datawarehouse.fabric.microsoft.com", "DW", "agent");
                    Check("create_data_source: structured Fabric SQL data source created", dsName == "FabricSql");
                    var exprName = await engine.CreateNamedExpressionAsync("Lakehouse", "let s = Sql.Database(\"ws.datawarehouse.fabric.microsoft.com\",\"LH\") in s", "agent");
                    Check("create_named_expression: shared M expression created", exprName == "Lakehouse");

                    var salesRef = await engine.CreateImportTableAsync("Sales", "let s = #\"Lakehouse\" in s", "agent");
                    Check("create_import_table: returns the table ref", salesRef == "table:Sales");
                    await engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", "amount", "agent");
                    var typed = await engine.SetColumnDataTypeAsync("column:Sales/Amount", "Double", "agent");
                    var typed2 = await engine.SetColumnDataTypeAsync("column:Sales/Amount", "Double", "agent");
                    Check("set_column_data_type: retypes (changed) then is idempotent (no-op)", typed.Changed && !typed2.Changed);
                    await engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM(Sales[Amount])", "agent");

                    var custRef = await engine.CreateDirectLakeTableAsync("Customer", "DimCustomer", "dbo", "Lakehouse", "agent");
                    Check("create_directlake_table: returns the table ref", custRef == "table:Customer");
                    await engine.CreateCalculatedTableAsync("DateDim", "CALENDARAUTO()", "agent");

                    var doc = await engine.GetDocModelAsync(0);
                    var salesT = doc.Tables.FirstOrDefault(t => t.Name == "Sales");
                    var custT = doc.Tables.FirstOrDefault(t => t.Name == "Customer");
                    Check("from-scratch: Sales has exactly one Import M partition named after the table",
                        salesT != null && salesT.Partitions.Length == 1 && salesT.Partitions[0].Name == "Sales"
                        && salesT.Partitions[0].SourceType == "M" && salesT.Partitions[0].Mode == "Import");
                    Check("from-scratch: Customer has exactly one Direct Lake Entity partition bound to dbo.DimCustomer",
                        custT != null && custT.Partitions.Length == 1 && custT.Partitions[0].SourceType == "Entity"
                        && custT.Partitions[0].Mode == "DirectLake" && custT.Partitions[0].EntityName == "DimCustomer"
                        && custT.Partitions[0].SchemaName == "dbo");

                    var tmp = Path.Combine(Path.GetTempPath(), "semanticus_scratch_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    var saved = await engine.SaveAsync(tmp, "TMDL");
                    Check("create_model: first save with a path writes TMDL files", saved.FileCount > 0);
                    await engine.OpenAsync(tmp);
                    var doc2 = await engine.GetDocModelAsync(0);
                    var names = doc2.Tables.Select(t => t.Name).ToArray();
                    Check("from-scratch round-trip: all three authored tables survive save+reopen",
                        names.Contains("Sales") && names.Contains("Customer") && names.Contains("DateDim"));
                    var cust2 = doc2.Tables.First(t => t.Name == "Customer");
                    Check("from-scratch round-trip: the Direct Lake Entity partition survives reopen",
                        cust2.Partitions.Length == 1 && cust2.Partitions[0].SourceType == "Entity"
                        && cust2.Partitions[0].Mode == "DirectLake" && cust2.Partitions[0].EntityName == "DimCustomer");
                    Check("from-scratch round-trip: the data source + shared expression survive reopen",
                        doc2.DataSources.Any(d => d.Name == "FabricSql") && doc2.Expressions.Any(e => e.Name == "Lakehouse"));
                    Console.WriteLine($"[i] from-scratch model round-tripped: tables=[{string.Join(", ", names)}], {saved.FileCount} TMDL files");
                    try { Directory.Delete(tmp, true); } catch { }
                }

                // ---- MODEL SPEC store (Phase 3a) — set/get + save/load + clear round-trip ----
                {
                    var specJson = "{\"name\":\"SpecModel\",\"storageMode\":\"directLake\",\"compatibilityLevel\":1604,"
                        + "\"source\":{\"kind\":\"fabric-sql\",\"server\":\"ws.datawarehouse.fabric.microsoft.com\",\"database\":\"DW\"},"
                        + "\"tables\":[{\"name\":\"Sales\",\"role\":\"fact\",\"entity\":\"FactSales\",\"schema\":\"dbo\",\"sourceName\":\"Lakehouse\","
                        + "\"columns\":[{\"name\":\"Amount\",\"dataType\":\"Decimal\"},{\"name\":\"CustomerKey\",\"dataType\":\"Int64\",\"isKey\":true}]}],"
                        + "\"relationships\":[{\"fromTable\":\"Sales\",\"fromColumn\":\"CustomerKey\",\"toTable\":\"Customer\",\"toColumn\":\"CustomerKey\"}],"
                        + "\"measures\":[{\"table\":\"Sales\",\"name\":\"Total Sales\",\"dax\":\"SUM(Sales[Amount])\"}],"
                        + "\"timeIntelligence\":[\"YTD\",\"PY\"]}";
                    var setV = await engine.SetSpecAsync(specJson, "agent");
                    Check("set_spec: stored, version bumped, source=manual, name parsed",
                        setV.Spec != null && setV.Version >= 1 && setV.Source == "manual" && setV.Spec.Name == "SpecModel");
                    var got = await engine.GetSpecAsync();
                    Check("get_spec: round-trips the spec (storage mode, tables, columns, measures, relationships, TI)",
                        got.Spec != null && got.Spec.StorageMode == "directLake" && got.Spec.Tables.Length == 1
                        && got.Spec.Tables[0].Columns.Length == 2 && got.Spec.Tables[0].Columns[1].IsKey
                        && got.Spec.Measures.Length == 1 && got.Spec.Relationships.Length == 1 && got.Spec.TimeIntelligence.Length == 2);

                    var specPath = Path.Combine(Path.GetTempPath(), "semanticus_spec_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                    await engine.SaveSpecAsync(specPath);
                    Check("save_spec: wrote a JSON file", File.Exists(specPath));
                    await engine.ClearSpecAsync("agent");
                    Check("clear_spec: empties the spec", (await engine.GetSpecAsync()).Spec == null);
                    var loaded = await engine.LoadSpecAsync(specPath, "agent");
                    Check("load_spec: reloads the spec from disk (source=file, content preserved)",
                        loaded.Spec != null && loaded.Source == "file" && loaded.Spec.Name == "SpecModel" && loaded.Spec.Tables.Length == 1);
                    try { File.Delete(specPath); } catch { }
                }

                // ---- BUILD MODEL FROM SPEC (Phase 3b) — one transactional build, undo, re-run, reopen ----
                {
                    await engine.CreateModelAsync("BuiltModel", 1604);
                    var buildSpec = @"{
                        ""name"":""BuiltModel"",""storageMode"":""import"",""compatibilityLevel"":1604,
                        ""source"":{""kind"":""fabric-sql"",""server"":""ws.datawarehouse.fabric.microsoft.com"",""database"":""DW"",""schema"":""dbo""},
                        ""tables"":[
                          {""name"":""Customer"",""role"":""dimension"",""columns"":[{""name"":""CustomerKey"",""dataType"":""Int64"",""isKey"":true},{""name"":""CustomerName"",""dataType"":""String""}]},
                          {""name"":""Sales"",""role"":""fact"",""entity"":""FactSales"",""columns"":[{""name"":""CustomerKey"",""dataType"":""Int64""},{""name"":""Amount"",""dataType"":""Decimal"",""summarizeBy"":""Sum""}]}
                        ],
                        ""relationships"":[{""fromTable"":""Sales"",""fromColumn"":""CustomerKey"",""toTable"":""Customer"",""toColumn"":""CustomerKey""}],
                        ""measures"":[{""table"":""Sales"",""name"":""Total Sales"",""dax"":""SUM(Sales[Amount])"",""formatString"":""$#,##0""}],
                        ""timeIntelligence"":[""YTD"",""PY""],""timeIntelligenceBaseMeasures"":[""Total Sales""],
                        ""dateTable"":{""name"":""Date"",""markAsDate"":true}
                    }";
                    await engine.SetSpecAsync(buildSpec, "agent");
                    var report = await engine.BuildModelFromSpecAsync("agent");
                    Console.WriteLine($"[i] build_model_from_spec: created {report.Created.Length}, skipped {report.Skipped.Length}, errors {report.Errors.Length}; tables {report.TablesBefore}->{report.TablesAfter}, measures {report.MeasuresBefore}->{report.MeasuresAfter}");
                    foreach (var e in report.Errors.Take(6)) Console.WriteLine("      build error: " + e);
                    Check("build_model_from_spec: no errors, tables + measures created", report.Errors.Length == 0 && report.TablesAfter >= 3 && report.MeasuresAfter >= 3);

                    var bdoc = await engine.GetDocModelAsync(0);
                    var bnames = bdoc.Tables.Select(t => t.Name).ToArray();
                    Check("build: Customer + Sales + Date tables exist", bnames.Contains("Customer") && bnames.Contains("Sales") && bnames.Contains("Date"));
                    Check("build: Date table is marked as a date table", bdoc.Tables.First(t => t.Name == "Date").IsDateTable);
                    Check("build: the relationship was created", bdoc.Graph.Relationships.Any(r => r.FromTable == "Sales" && r.ToTable == "Customer"));
                    var allMeasures = (await engine.ListMeasuresAsync()).Select(x => x.Name).ToArray();
                    Check("build: base measure + time-intelligence (YTD/PY) measures created",
                        allMeasures.Contains("Total Sales") && allMeasures.Contains("Total Sales YTD") && allMeasures.Contains("Total Sales PY"));
                    // Regression (review fix 1): an Import-mode table carrying an `entity` must still build as Import
                    // (M partition), NOT silently as Direct Lake.
                    var salesPart = bdoc.Tables.First(t => t.Name == "Sales").Partitions[0];
                    Check("build: an import table with an entity builds as Import (M partition), not Direct Lake",
                        salesPart.SourceType == "M" && salesPart.Mode == "Import");

                    // ONE undo reverts the WHOLE build.
                    await engine.UndoAsync("agent");
                    Check("build: a single undo reverts the entire build (back to the blank model)",
                        (await engine.GetDocModelAsync(0)).Tables.Length == 0);

                    // Re-run is idempotent: rebuild (redo path not used — build again), then a second build skips everything.
                    await engine.BuildModelFromSpecAsync("agent");
                    var rerun = await engine.BuildModelFromSpecAsync("agent");
                    Check("build: re-running on a built model skips existing objects (idempotent)",
                        rerun.Created.Length == 0 && rerun.Skipped.Length > 0);

                    // Save -> reopen round-trip.
                    var btmp = Path.Combine(Path.GetTempPath(), "semanticus_built_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    await engine.SaveAsync(btmp, "TMDL");
                    await engine.OpenAsync(btmp);
                    var redoc = await engine.GetDocModelAsync(0);
                    Check("build: the built model round-trips (tables + measures survive save+reopen)",
                        redoc.Tables.Select(t => t.Name).Contains("Sales") && redoc.Tables.Select(t => t.Name).Contains("Date")
                        && (await engine.ListMeasuresAsync()).Any(x => x.Name == "Total Sales YTD"));
                    try { Directory.Delete(btmp, true); } catch { }
                }

                // ---- AUTOGENERATE SPEC FROM MODEL (Phase 3c) — propose a starter spec from the open model ----
                {
                    await engine.OpenAsync(bim);   // AdventureWorks (import star schema with relationships)
                    var ag = await engine.AutogenerateSpecFromModelAsync("agent");
                    Check("autogenerate_spec_from_model: produced a spec (import mode, tables, source=autogenerate-model)",
                        ag.Spec != null && ag.Source == "autogenerate-model" && ag.Spec.StorageMode == "import" && ag.Spec.Tables.Length > 0);
                    Check("autogenerate: classified at least one fact AND one dimension table (relationship-role Kimball rule)",
                        ag.Spec.Tables.Any(t => t.Role == "fact") && ag.Spec.Tables.Any(t => t.Role == "dimension"));
                    Check("autogenerate: captured relationships + per-table columns",
                        ag.Spec.Relationships.Length > 0 && ag.Spec.Tables.Any(t => t.Columns.Length > 0));
                    Check("autogenerate: captured existing measures + suggested a time-intelligence set",
                        ag.Spec.Measures.Length > 0 && ag.Spec.TimeIntelligence.Length > 0 && ag.Spec.DateTable != null);
                    Check("autogenerate: time-intelligence bases are the proposed additive measures, not every measure (review fix 5)",
                        ag.Spec.TimeIntelligenceBaseMeasures != null && ag.Spec.TimeIntelligenceBaseMeasures.Length > 0
                        && ag.Spec.TimeIntelligenceBaseMeasures.All(n => n.StartsWith("Total ")));
                    var proposed = ag.Spec.Measures.Count(mm => mm.DisplayFolder == "Base Measures");
                    Console.WriteLine($"[i] autogenerate: {ag.Spec.Tables.Length} tables ({ag.Spec.Tables.Count(t => t.Role == "fact")} fact, {ag.Spec.Tables.Count(t => t.Role == "dimension")} dim), {ag.Spec.Relationships.Length} rels, {ag.Spec.Measures.Length} measures ({proposed} proposed additive)");
                }

                // ---- FABRIC SQL introspection -> spec (Phase 3c) — deterministic mapping (no live endpoint) ----
                {
                    Check("MapSqlType: int->Int64, decimal->Decimal, nvarchar->String, bit->Boolean, datetime2->DateTime, float->Double",
                        FabricSqlSchema.MapSqlType("int") == "Int64" && FabricSqlSchema.MapSqlType("decimal") == "Decimal"
                        && FabricSqlSchema.MapSqlType("nvarchar") == "String" && FabricSqlSchema.MapSqlType("bit") == "Boolean"
                        && FabricSqlSchema.MapSqlType("datetime2") == "DateTime" && FabricSqlSchema.MapSqlType("float") == "Double");

                    var fab = new FabricSchema
                    {
                        Tables = new[]
                        {
                            new FabricTable { Schema = "dbo", Name = "Customer", KeyColumns = new[] { "CustomerKey" }, Columns = new[]
                            {
                                new FabricColumn { Name = "CustomerKey", DataType = "Int64", Ordinal = 1 },
                                new FabricColumn { Name = "CustomerName", DataType = "String", Ordinal = 2 },
                            } },
                            new FabricTable { Schema = "dbo", Name = "Sales", KeyColumns = System.Array.Empty<string>(), Columns = new[]
                            {
                                new FabricColumn { Name = "CustomerKey", DataType = "Int64", Ordinal = 1 },
                                new FabricColumn { Name = "Amount", DataType = "Decimal", Ordinal = 2 },
                            } },
                        },
                        ForeignKeys = new[]
                        {
                            new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "CustomerKey", ToSchema = "dbo", ToTable = "Customer", ToColumn = "CustomerKey" },
                        },
                    };
                    var fspec = LocalEngine.FabricSchemaToSpec(fab, "ws.datawarehouse.fabric.microsoft.com", "DW", "import");
                    Check("fabric->spec: Customer=dimension, Sales=fact (FK-topology Kimball)",
                        fspec.Tables.First(t => t.Name == "Customer").Role == "dimension" && fspec.Tables.First(t => t.Name == "Sales").Role == "fact");
                    Check("fabric->spec: a relationship was inferred from the FK (Sales[CustomerKey] -> Customer[CustomerKey])",
                        fspec.Relationships.Any(r => r.FromTable == "Sales" && r.FromColumn == "CustomerKey" && r.ToTable == "Customer" && r.ToColumn == "CustomerKey"));
                    Check("fabric->spec: key column flagged key+hidden; numeric fact column SummarizeBy=Sum",
                        fspec.Tables.First(t => t.Name == "Customer").Columns.First(c => c.Name == "CustomerKey").IsKey
                        && fspec.Tables.First(t => t.Name == "Customer").Columns.First(c => c.Name == "CustomerKey").Hidden
                        && fspec.Tables.First(t => t.Name == "Sales").Columns.First(c => c.Name == "Amount").SummarizeBy == "Sum");
                    Check("fabric->spec: proposed 'Total Amount' on the fact + import storage + fabric-sql source captured",
                        fspec.Measures.Any(mm => mm.Table == "Sales" && mm.Name == "Total Amount") && fspec.StorageMode == "import"
                        && fspec.Source != null && fspec.Source.Server == "ws.datawarehouse.fabric.microsoft.com");
                    var dlspec = LocalEngine.FabricSchemaToSpec(fab, "x", "DW", "directLake");
                    Check("fabric->spec: directLake mode tags tables with a source binding",
                        dlspec.StorageMode == "directLake" && dlspec.Tables.All(t => t.SourceName == "FabricSource"));
                    Check("fabric->spec: time-intelligence base measures == the proposed additive measures, explicitly set (review fix 5)",
                        fspec.TimeIntelligenceBaseMeasures != null && fspec.TimeIntelligenceBaseMeasures.Length == fspec.Measures.Length
                        && fspec.TimeIntelligenceBaseMeasures.Contains("Total Amount"));

                    // review fix 3: the same table name across two schemas must disambiguate, not collapse.
                    var fab2 = new FabricSchema { Tables = new[]
                    {
                        new FabricTable { Schema = "dbo", Name = "Item", Columns = new[] { new FabricColumn { Name = "Id", DataType = "Int64", Ordinal = 1 } } },
                        new FabricTable { Schema = "staging", Name = "Item", Columns = new[] { new FabricColumn { Name = "Id", DataType = "Int64", Ordinal = 1 } } },
                    }, ForeignKeys = System.Array.Empty<FabricForeignKey>() };
                    var f2spec = LocalEngine.FabricSchemaToSpec(fab2, "x", "DW", "import");
                    Check("fabric->spec: same table name in two schemas is disambiguated, not collapsed (review fix 3)",
                        f2spec.Tables.Length == 2 && f2spec.Tables.Select(t => t.Name).Distinct().Count() == 2);
                }

                // ---- LIVE MODEL via XMLA service principal (READ-ONLY) — the AI-readiness live lane, against a tenant ----
                // Gated on the SP creds AND a configured test dataset (SEMANTICUS_LIVE_XMLA = the XMLA endpoint,
                // SEMANTICUS_LIVE_DB = the dataset). When all are set (CI with the secrets, or a local run) it verifies the
                // live model lane end-to-end: open_live (load TOM from XMLA) → run_dax → vertipaq_scan (LIVE storage stats)
                // → ai_readiness_scan_live (the DMV-cardinality rules). Else it skips (offline-green). READ-ONLY — no writes.
                {
                    var liveXmla = Environment.GetEnvironmentVariable("SEMANTICUS_LIVE_XMLA");
                    var liveDb = Environment.GetEnvironmentVariable("SEMANTICUS_LIVE_DB");
                    if (!HasServicePrincipal() || string.IsNullOrWhiteSpace(liveXmla) || string.IsNullOrWhiteSpace(liveDb))
                        Console.WriteLine("[i] live model: SP + SEMANTICUS_LIVE_XMLA/_DB not all set — skipping live model verification (offline-green).");
                    else
                    {
                        try
                        {
                            Console.WriteLine($"[i] live model: opening '{liveDb}' via XMLA (service principal)…");
                            var open = await engine.OpenLiveAsync(liveXmla, liveDb, "serviceprincipal", null, null);
                            Check("live model: open_live loads the model from the XMLA endpoint (tables > 0)", open != null && open.Tables > 0);
                            var rs = await engine.RunDaxAsync("EVALUATE ROW(\"probe\", 1)", 10);
                            Check("live model: run_dax executes a query against the live model (1 row)", rs != null && rs.Rows.Length == 1);
                            var vpaq = await engine.VertiPaqScanAsync(10);
                            Check("live model: vertipaq_scan returns LIVE storage statistics (tables with stats, no error)", vpaq != null && vpaq.Error == null && (vpaq.Tables?.Length ?? 0) > 0);
                            var live = await engine.AiReadinessScanLiveAsync();
                            Check("live model: ai_readiness_scan_live runs the DMV-cardinality rules on the open live model (graded A–F)", live != null && !string.IsNullOrEmpty(live.Grade));

                            // Start all three without awaiting so the same live XMLA session receives simultaneous
                            // trace-bearing and ordinary query requests through independent public paths. The
                            // per-connection lane must serialize them without deadlock or cross-captured evidence.
                            var concurrentProfile = engine.ProfileDaxAsync("EVALUATE ROW(\"profile_sentinel\", 101)");
                            var concurrentPlan = engine.CaptureQueryPlanAsync("EVALUATE ROW(\"plan_sentinel\", 202)");
                            var concurrentPlain = engine.RunDaxAsync("EVALUATE ROW(\"ordinary_sentinel\", 303)", 10);
                            await Task.WhenAll(concurrentProfile, concurrentPlan, concurrentPlain);
                            Check("live model: concurrent profile, plan and ordinary query complete without trace contamination",
                                string.IsNullOrEmpty(concurrentProfile.Result.Error)
                                && string.IsNullOrEmpty(concurrentPlan.Result.Error)
                                && string.IsNullOrEmpty(concurrentPlain.Result.Error)
                                && concurrentProfile.Result.RowCount == 1
                                && concurrentPlan.Result.RowCount == 1
                                && concurrentPlain.Result.RowCount == 1
                                && Convert.ToInt64(concurrentPlain.Result.Rows[0][0]) == 303);

                            // export_vpax on a LIVE session enriches the .vpax with real VertiPaq storage statistics
                            // (the primary value of a .vpax). Verify the Note + that the re-imported model carries stats.
                            var vpaxPath = Path.Combine(Path.GetTempPath(), "semanticus_air_vpax_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".vpax");
                            try
                            {
                                var vx = await engine.ExportVpaxAsync(vpaxPath);
                                Check("live model: export_vpax enriches with LIVE storage statistics (Exported, tables>0, Note says LIVE)",
                                    vx != null && vx.Exported && vx.Tables > 0 && (vx.Note ?? "").Contains("LIVE"));
                                var content = Dax.Vpax.Tools.VpaxTools.ImportVpax(vpaxPath, importDatabase: false);
                                var card = content.DaxModel.Tables.SelectMany(t => t.Columns).Sum(c => c.ColumnCardinality);
                                Check("live model: the exported .vpax carries real column statistics (sum of cardinality > 0)", card > 0);
                            }
                            finally { try { File.Delete(vpaxPath); } catch { } }

                            // The DAX-equivalence KEYSTONE — the prover that gates apply_change_plan — verified LIVE.
                            // A results-changing rewrite (1→2) must be SKIPPED by the verify gate; an equivalent one
                            // (1→1) applies. The plan mutates the in-memory session only (discarded on disconnect); no
                            // server write. This is the monetization keystone's prover, now exercised against a real model.
                            var ktable = (await engine.ListTreeAsync(null)).FirstOrDefault(t => t.Kind == "table");
                            if (ktable != null)
                            {
                                await engine.ClearPlanAsync("agent");
                                var kmRef = await engine.CreateMeasureAsync(ktable.Ref, "Plan_KeystoneProbe", "1", "agent");
                                await engine.AddPlanItemAsync(kmRef, "set_dax", "2", "rewrite (changes results)", System.Array.Empty<string>(), null, "agent");
                                await engine.AddPlanItemAsync(kmRef, "set_dax", "1", "rewrite (equivalent)", System.Array.Empty<string>(), null, "agent");
                                var kids = (await engine.GetPlanAsync()).Items.Where(i => i.Kind == "set_dax").Select(i => i.Id).ToArray();
                                foreach (var id in kids) await engine.SetPlanItemAsync(id, null, true, "agent");
                                var krep = await engine.ApplyPlanAsync(kids, "agent");
                                var kbad = krep.Items.First(i => i.Title.Contains("changes results"));
                                var kgood = krep.Items.First(i => i.Title.Contains("equivalent"));
                                Console.WriteLine($"[i] live keystone: changing rewrite → {kbad.Status}/{kbad.VerifyState}; equivalent → {kgood.Status}/{kgood.VerifyState}");
                                Check("live keystone: the DAX-equivalence verify gate SKIPS a results-changing rewrite (1→2) against the live model", kbad.Status == "skipped" && kbad.VerifyState == "failed");
                                Check("live keystone: an equivalent rewrite (1→1) passes the verify gate and applies", kgood.Status == "applied");
                                await engine.ClearPlanAsync("agent");
                            }

                            await engine.DisconnectAsync();
                            Console.WriteLine("[i] live model: read-only live verification complete (no writes performed).");
                        }
                        catch (Exception ex) { Check("live model: read-only verification completed without an unhandled error — " + ex.Message.Split('\n')[0], false); }
                    }
                }

                Console.WriteLine();
                if (_failures == 0) { Console.WriteLine("==== P-AIR1 AI-READINESS: PASS ===="); return 0; }
                Console.WriteLine($"==== P-AIR1 AI-READINESS: {_failures} CHECK(S) FAILED ===="); return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("\n==== P-AIR1 AI-READINESS: EXCEPTION ====");
                Console.WriteLine(ex);
                return 2;
            }
            finally { sessions.Dispose(); }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok) _failures++;
        }

        // True when a service principal is configured (same env vars EntraToken.ClientSecret reads). Gates the live block.
        private static bool HasServicePrincipal()
        {
            static bool Set(params string[] names) => names.Any(n => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n)));
            return Set("FABRIC_CLIENT", "AZURE_CLIENT_ID", "POWERBI_CLIENT_ID")
                && Set("FABRIC_SECRET", "AZURE_CLIENT_SECRET", "POWERBI_CLIENT_SECRET")
                && Set("FABRIC_TENANT", "AZURE_TENANT_ID", "POWERBI_TENANT_ID");
        }

        /// <summary>True if the async action throws (used to assert guard rails reject bad input).</summary>
        private static async Task<bool> Throws(Func<Task> action)
        {
            try { await action(); return false; }
            catch { return true; }
        }

        private static string FindTestBim() => FindTestData("AdventureWorks.bim") ?? throw new FileNotFoundException("AdventureWorks.bim not found from " + AppContext.BaseDirectory);

        private static string FindTestData(string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                // submodule (external/TabularEditor) first so a fresh clone / CI runner finds the data; sibling fallback.
                foreach (var root in new[] { Path.Combine("external", "TabularEditor"), "TabularEditor" })
                {
                    var c = Path.Combine(dir.FullName, root, "TOMWrapperTest", "TestData", fileName);
                    if (File.Exists(c)) return c;
                }
                dir = dir.Parent;
            }
            return null;
        }
    }
}

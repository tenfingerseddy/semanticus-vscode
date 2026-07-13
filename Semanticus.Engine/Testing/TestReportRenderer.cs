using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SemEvidence = Semanticus.Engine.Evidence;

namespace Semanticus.Engine
{
    /// <summary>Pure Markdown rendering for one completed Tests-tab run.</summary>
    public static class TestReportRenderer
    {
        public static string Render(TestSuiteRunResult run)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            var b = new StringBuilder();
            b.AppendLine("# Semanticus test report");
            b.AppendLine();
            b.AppendLine("- Model: " + Text(run.ModelName, "Not recorded"));
            b.AppendLine("- Environment: " + Text(run.Environment, run.Live ? "Not recorded" : "Offline"));
            b.AppendLine("- Run: " + Text(run.When, "Not recorded"));
            b.AppendLine("- Duration: " + run.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms");
            b.AppendLine("- Run mode: " + (run.Live
                ? "Live. Data-backed probes were available."
                : "Offline. Live-only probes and reconciliations could not be verified."));
            b.AppendLine();

            var health = run.Health;
            if (health == null)
                b.AppendLine("**Grade not recorded | Coverage not recorded**");
            else
            {
                // I2: this is deliberately one physical Markdown line.
                b.AppendLine("**Grade " + Text(health.Grade, "not recorded") + " | Coverage " + Pct(health.CoveragePct) + "**");
                foreach (var gate in health.GatedBy ?? Array.Empty<string>())
                    b.AppendLine("- Gate: " + Text(gate, "Reason not recorded"));
            }
            b.AppendLine();

            b.AppendLine("## Root causes");
            b.AppendLine();
            var roots = RootCauses(run).ToList();
            if (roots.Count == 0) b.AppendLine("- None recorded.");
            else foreach (var root in roots) b.AppendLine("- " + root);
            b.AppendLine();

            RenderMeasures(b, run.Reconciles);
            RenderInterview(b, run.Interview, run.InterviewNote);
            RenderRelationships(b, run.Relationships);
            RenderSecurity(b, run.Security);
            return b.ToString().TrimEnd().Replace('—', '-').Replace('–', '-') + Environment.NewLine;
        }

        /// <summary>Self-contained, print-ready HTML over the exact Markdown evidence. Presentation can change;
        /// scope cannot: both formats originate from <see cref="Render"/>, so HTML cannot omit a failing section.</summary>
        public static string RenderHtml(TestSuiteRunResult run)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            var markdown = Render(run);
            var body = new StringBuilder();
            var inList = false;
            foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (string.IsNullOrWhiteSpace(line)) { CloseList(body, ref inList); continue; }
                var trimmed = line.TrimStart();
                var indent = line.Length - trimmed.Length;
                if (trimmed.StartsWith("### ", StringComparison.Ordinal)) { CloseList(body, ref inList); body.Append("<h3>").Append(Inline(trimmed.Substring(4))).AppendLine("</h3>"); }
                else if (trimmed.StartsWith("## ", StringComparison.Ordinal)) { CloseList(body, ref inList); body.Append("<h2>").Append(Inline(trimmed.Substring(3))).AppendLine("</h2>"); }
                else if (trimmed.StartsWith("# ", StringComparison.Ordinal)) { CloseList(body, ref inList); body.Append("<h1>").Append(Inline(trimmed.Substring(2))).AppendLine("</h1>"); }
                else if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    if (!inList) { body.AppendLine("<ul>"); inList = true; }
                    body.Append(indent > 0 ? "<li class=\"nested\">" : "<li>").Append(Inline(trimmed.Substring(2))).AppendLine("</li>");
                }
                else { CloseList(body, ref inList); body.Append("<p>").Append(Inline(trimmed)).AppendLine("</p>"); }
            }
            CloseList(body, ref inList);

            var title = WebUtility.HtmlEncode(Text(run.ModelName, "Semanticus") + " test report");
            return "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + "<title>" + title + "</title><style>"
                + ":root{color-scheme:light;--ink:#18202a;--muted:#596577;--line:#dce2e9;--soft:#f5f7f9;--accent:#16875f}"
                + "*{box-sizing:border-box}body{margin:0;background:#eef1f4;color:var(--ink);font:14px/1.55 system-ui,-apple-system,Segoe UI,sans-serif}"
                + "main{max-width:980px;margin:32px auto;padding:52px 64px;background:white;border:1px solid var(--line);box-shadow:0 8px 30px #17202a18}"
                + "h1{font-size:28px;margin:0 0 24px;padding-bottom:16px;border-bottom:3px solid var(--accent)}"
                + "h2{font-size:19px;margin:34px 0 10px;padding-top:12px;border-top:1px solid var(--line)}h3{font-size:15px;margin:24px 0 8px}"
                + "p{margin:8px 0}ul{margin:8px 0 18px;padding-left:24px}li{margin:6px 0}li.nested{margin-left:24px;color:var(--muted)}"
                + "strong{color:#0d6448}code{padding:1px 4px;border-radius:4px;background:var(--soft);font:12px ui-monospace,SFMono-Regular,Consolas,monospace}"
                + "@media print{body{background:white}main{max-width:none;margin:0;padding:20mm 16mm;border:0;box-shadow:none}h2,h3{break-after:avoid}li{break-inside:avoid}}"
                + "</style></head><body><main>" + body + "</main></body></html>\n";
        }

        /// <summary>Map the Tests vocabulary into the product-wide five-word evidence contract. The legacy
        /// Markdown remains a portable companion, but sealed JSON and HTML always originate from this one DTO.</summary>
        public static SemEvidence.EvidenceDoc BuildEvidence(TestSuiteRunResult run)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            var h = run.Health;
            var doc = new SemEvidence.EvidenceDoc
            {
                Id = string.IsNullOrWhiteSpace(run.RunId)
                    ? "test-" + VerifiedEditsStore.Sha256((run.ModelFingerprint ?? run.ModelName ?? "model") + "|" + (run.When ?? "unknown")).Substring(0, 16)
                    : run.RunId,
                Kind = "test-suite",
                Title = Text(run.ModelName, "Model") + " test evidence",
                CreatedUtc = run.When,
                Producer = "run_tests",
                ProducerVersion = typeof(TestReportRenderer).Assembly.GetName().Version?.ToString(),
                Origin = "system",
                ModelName = run.ModelName,
                ModelFingerprint = run.ModelFingerprint,
                Verdict = OverallVerdict(h),
                Coverage = new SemEvidence.Coverage
                {
                    Verified = h?.Passed ?? 0,
                    Total = h?.Checked ?? 0,
                    Unknowns = h?.NotVerifiable ?? 0,
                },
                VerdictCounts = new Dictionary<string, int>
                {
                    ["Verified"] = h?.Passed ?? 0,
                    ["NeedsReview"] = h?.Suspect ?? 0,
                    ["Broken"] = h?.Failed ?? 0,
                    ["Unknown"] = h?.NotVerifiable ?? 0,
                    ["Overridden"] = 0,
                },
            };

            doc.Sections.Add(new SemEvidence.SummarySection
            {
                Title = "Outcome",
                Paragraphs = new List<string>
                {
                    h == null
                        ? "Grade and coverage were not recorded."
                        : $"Grade {Text(h.Grade, "not recorded")} with {Pct(h.CoveragePct)} coverage. {h.RootFailures} root failure(s) explain {h.Failed + h.Suspect} affected observation(s).",
                    run.Live
                        ? "Live data-backed probes were available."
                        : "This run was offline. Live-only probes and reconciliations were not verified.",
                },
            });
            doc.Sections.Add(new SemEvidence.KeyValueSection
            {
                Title = "Run context",
                Pairs = new List<SemEvidence.KeyValuePairRow>
                {
                    Pair("Run", Text(run.RunId, "Not recorded")),
                    Pair("Environment", Text(run.Environment, run.Live ? "Not recorded" : "Offline")),
                    Pair("Duration", run.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms"),
                    Pair("Saved definitions", run.DefinitionCount.ToString(CultureInfo.InvariantCulture)),
                    Pair("Cache cleared for timing", run.CacheCleared ? "Yes" : "No"),
                },
            });

            var roots = RootCauses(run).ToList();
            doc.Sections.Add(new SemEvidence.FindingsSection
            {
                Title = "Root causes",
                Rows = roots.Count == 0
                    ? new List<SemEvidence.FindingRow> { Finding("No root failures recorded", SemEvidence.Verdict.Verified, "The run recorded no proven root failure.") }
                    : roots.Select(x => Finding("Root failure", SemEvidence.Verdict.Broken, x)).ToList(),
            });
            foreach (var gate in h?.GatedBy ?? Array.Empty<string>())
                doc.Sections.Add(new SemEvidence.NoteSection { Title = "Grade gate", Text = Text(gate, "Reason not recorded"), Tone = "warning" });

            doc.Sections.Add(BuildMeasureFindings(run.Reconciles));
            foreach (var o in run.Reconciles ?? Array.Empty<ReconcileOutcome>())
                if (o != null && o.Verdict == Verdict.Fail && o.Rows != null && o.Rows.Length > 0)
                    doc.Sections.Add(new SemEvidence.ProbeSection
                    {
                        Title = Text(o.Title, o.TargetRef ?? "Measure") + " comparison",
                        Probes = o.Rows.Where(x => x != null).Select(x => new SemEvidence.ProbeRow
                        {
                            Query = Text(x.Context, "Context not recorded"),
                            Expected = Number(x.Sql),
                            Actual = Number(x.Dax),
                            Verdict = Map(x.Verdict),
                        }).ToList(),
                    });

            doc.Sections.Add(BuildRelationshipFindings(run.Relationships));
            doc.Sections.Add(BuildSecurityFindings(run.Security));
            doc.Sections.Add(BuildInterviewFindings(run.Interview));
            if (!string.IsNullOrWhiteSpace(run.InterviewNote))
                doc.Sections.Add(new SemEvidence.NoteSection { Title = "Interview store", Text = Reason(run.InterviewNote), Tone = "warning" });
            if (!string.IsNullOrWhiteSpace(run.Note))
                doc.Sections.Add(new SemEvidence.NoteSection { Title = "Run note", Text = Reason(run.Note), Tone = "info" });
            return doc;
        }

        private static SemEvidence.FindingsSection BuildMeasureFindings(ReconcileOutcome[] outcomes)
        {
            var rows = new List<SemEvidence.FindingRow>();
            foreach (var o in outcomes ?? Array.Empty<ReconcileOutcome>())
            {
                if (o == null) continue;
                var detail = Reason(o.Message);
                if (o.TimingVerdict.HasValue)
                    detail += " Timing: " + (o.DurationMs.HasValue ? o.DurationMs.Value.ToString(CultureInfo.InvariantCulture) + " ms" : "not measured")
                        + " against " + (o.BudgetMs.HasValue ? o.BudgetMs.Value.ToString(CultureInfo.InvariantCulture) + " ms" : "no recorded budget")
                        + ", " + VerdictName(o.TimingVerdict.Value) + ". " + Reason(o.TimingDetail);
                if (o.Variants != null && o.Variants.Length > 0)
                    detail += " Variants: " + string.Join("; ", o.Variants.Where(v => v != null).Select(v =>
                        Text(v.Variant, "Unnamed variant") + " [" + VariantName(v.Kind) + "] " + VerdictName(v.Verdict) + ": " + Reason(v.Explanation)));
                rows.Add(Finding(Text(o.Title, o.TargetRef ?? "Unnamed measure test"), Map(o.Verdict), detail));
            }
            if (rows.Count == 0)
                rows.Add(Finding("Saved measure reconciliations", SemEvidence.Verdict.Unknown, "No saved measure reconciliations were part of this run."));
            return new SemEvidence.FindingsSection { Title = "Measures", Rows = rows };
        }

        private static SemEvidence.FindingsSection BuildRelationshipFindings(RelationshipIntegrityReport report)
        {
            var rows = new List<SemEvidence.FindingRow>();
            foreach (var rel in report?.Relationships ?? Array.Empty<RelationshipResult>())
            {
                if (rel == null) continue;
                foreach (var check in rel.Checks ?? Array.Empty<CheckResult>())
                    if (check != null)
                        rows.Add(Finding(Text(rel.Name, "Unnamed relationship") + ": " + CheckName(check.Check), Map(check.Verdict), Reason(check.Message), check.Count));
            }
            foreach (var table in report?.TableRowCounts ?? Array.Empty<TableRowCountResult>())
            {
                if (table == null) continue;
                rows.Add(Finding("Table " + Text(table.ModelTable, "Unnamed table") + " vs " + Code(table.Schema, "?") + "." + Code(table.Entity, "?"),
                    Map(table.Check?.Verdict ?? Verdict.NotVerifiable), Reason(table.Check?.Message)
                    + " Model COUNTROWS " + Count(table.ModelCount) + " at " + Text(table.ModelObservedUtc, "not observed")
                    + "; source COUNT_BIG " + Count(table.SourceCount) + " at " + Text(table.SourceObservedUtc, "not observed") + "."));
            }
            if (rows.Count == 0)
                rows.Add(Finding("Relationship integrity", SemEvidence.Verdict.Unknown, "No relationship or SQL-backed table checks were present."));
            return new SemEvidence.FindingsSection { Title = "Relationships and integrity", Rows = rows };
        }

        private static SemEvidence.FindingsSection BuildSecurityFindings(SecurityStaticReport report)
        {
            var rows = new List<SemEvidence.FindingRow>();
            foreach (var filter in report?.Filters ?? Array.Empty<RoleFilterResult>())
            {
                if (filter == null) continue;
                rows.Add(Finding("Role " + Text(filter.Role, "Unnamed role") + " on " + Text(filter.Table, "Unnamed table"),
                    Map(filter.Check?.Verdict ?? Verdict.NotVerifiable), Reason(filter.Check?.Message) + " Filter: " + Code(filter.FilterPreview, "not recorded") + "."));
            }
            foreach (var role in report?.Ols ?? Array.Empty<RoleOls>())
            {
                if (role == null) continue;
                var visible = Math.Max(0, role.TablesTotal - role.TablesHidden);
                rows.Add(Finding("Object visibility " + Text(role.Role, "Unnamed role"), SemEvidence.Verdict.Unknown,
                    visible.ToString(CultureInfo.InvariantCulture) + " visible tables, " + role.TablesHidden.ToString(CultureInfo.InvariantCulture)
                    + " hidden tables, " + role.ColumnsHidden.ToString(CultureInfo.InvariantCulture) + " hidden columns. Visibility is descriptive, not a pass."));
            }
            if (rows.Count == 0)
                rows.Add(Finding("Security checks", SemEvidence.Verdict.Unknown, "No static role filters or object visibility evidence were present."));
            return new SemEvidence.FindingsSection { Title = "Security", Rows = rows };
        }

        private static SemEvidence.FindingsSection BuildInterviewFindings(InterviewEvidence[] evidence)
        {
            var rows = new List<SemEvidence.FindingRow>();
            foreach (var item in evidence ?? Array.Empty<InterviewEvidence>())
            {
                if (item == null) continue;
                var verdict = item.Outcome switch
                {
                    "Correct" => SemEvidence.Verdict.Verified,
                    "Refused" => SemEvidence.Verdict.Verified,
                    "SilentlyWrong" => SemEvidence.Verdict.Broken,
                    _ => SemEvidence.Verdict.Unknown,
                };
                rows.Add(Finding(Text(item.Question, "Unnamed question"), verdict,
                    InterviewOutcomeName(item.Outcome) + ". Observed " + Text(item.When, "not observed") + ". "
                    + ContractRunDetail(item)
                    + (string.IsNullOrWhiteSpace(item.Detail) ? "No outcome detail was recorded." : Reason(item.Detail))));
            }
            if (rows.Count == 0)
                rows.Add(Finding("Model Interview", SemEvidence.Verdict.Unknown, "No saved interview questions were captured with this run."));
            return new SemEvidence.FindingsSection
            {
                Title = "Behavioral contracts (Model Interview; evidence only; does not change grade or coverage)",
                Rows = rows,
            };
        }

        private static SemEvidence.Verdict OverallVerdict(TestHealth health)
        {
            if (health == null || health.Checked == 0 || health.Passed + health.Failed == 0) return SemEvidence.Verdict.Unknown;
            if (health.Failed > 0) return SemEvidence.Verdict.Broken;
            if (health.Suspect > 0) return SemEvidence.Verdict.NeedsReview;
            return SemEvidence.Verdict.Verified;
        }

        private static SemEvidence.Verdict Map(Verdict verdict) => verdict switch
        {
            Verdict.Pass => SemEvidence.Verdict.Verified,
            Verdict.Fail => SemEvidence.Verdict.Broken,
            Verdict.Suspect => SemEvidence.Verdict.NeedsReview,
            _ => SemEvidence.Verdict.Unknown,
        };

        private static SemEvidence.KeyValuePairRow Pair(string key, string value) => new SemEvidence.KeyValuePairRow { Key = key, Value = value };
        private static SemEvidence.FindingRow Finding(string name, SemEvidence.Verdict verdict, string detail, long? count = null) => new SemEvidence.FindingRow
        {
            Name = name,
            Verdict = verdict,
            Detail = detail,
            Count = count.HasValue && count.Value <= int.MaxValue ? (int?)count.Value : null,
        };

        private static void CloseList(StringBuilder body, ref bool inList)
        {
            if (!inList) return;
            body.AppendLine("</ul>");
            inList = false;
        }

        private static string Inline(string value)
        {
            var encoded = WebUtility.HtmlEncode(value ?? "");
            encoded = Regex.Replace(encoded, "`([^`]+)`", "<code>$1</code>");
            return Regex.Replace(encoded, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
        }

        private static IEnumerable<string> RootCauses(TestSuiteRunResult run)
        {
            foreach (var o in run.Reconciles ?? Array.Empty<ReconcileOutcome>())
            {
                if (o?.Verdict == Verdict.Fail)
                    yield return "Measure " + Text(o.Title, o.TargetRef ?? "Unnamed") + ": " + Reason(o.Message);
                if (o?.TimingVerdict == Verdict.Fail)
                    yield return "Timing " + Text(o.Title, o.TargetRef ?? "Unnamed") + ": " + Reason(o.TimingDetail);
            }
            foreach (var rel in run.Relationships?.Relationships ?? Array.Empty<RelationshipResult>())
                foreach (var check in rel?.Checks ?? Array.Empty<CheckResult>())
                    if (check?.Verdict == Verdict.Fail)
                        yield return "Relationship " + Text(rel.Name, "Unnamed") + ", " + CheckName(check.Check) + ": " + Reason(check.Message);
            foreach (var table in run.Relationships?.TableRowCounts ?? Array.Empty<TableRowCountResult>())
                if (table?.Check?.Verdict == Verdict.Fail)
                    yield return "Table row count " + Text(table.ModelTable, "Unnamed table") + ": " + Reason(table.Check.Message);
            foreach (var filter in run.Security?.Filters ?? Array.Empty<RoleFilterResult>())
                if (filter?.Check?.Verdict == Verdict.Fail)
                    yield return "Security " + Text(filter.Role, "Unnamed role") + " on " + Text(filter.Table, "Unnamed table") + ": " + Reason(filter.Check.Message);
        }

        private static void RenderMeasures(StringBuilder b, ReconcileOutcome[] outcomes)
        {
            b.AppendLine("## Measures");
            b.AppendLine();
            if (outcomes == null || outcomes.Length == 0)
            {
                b.AppendLine("No saved measure reconciliations were part of this run.");
                b.AppendLine();
                return;
            }
            foreach (var o in outcomes.Where(x => x != null))
            {
                var line = "- **" + Text(o.Title, o.TargetRef ?? "Unnamed measure test") + "**: " + VerdictName(o.Verdict) + ". " + Reason(o.Message);
                if (o.TimingVerdict.HasValue)
                {
                    var measured = o.DurationMs.HasValue ? o.DurationMs.Value.ToString(CultureInfo.InvariantCulture) + " ms" : "duration not measured";
                    var budget = o.BudgetMs.HasValue ? o.BudgetMs.Value.ToString(CultureInfo.InvariantCulture) + " ms budget" : "budget not recorded";
                    line += " Timing: " + measured + " vs " + budget + " (" + VerdictName(o.TimingVerdict.Value) + "). " + Reason(o.TimingDetail);
                }
                if (o.Variants != null && o.Variants.Length > 0)
                    line += " Variants: " + string.Join("; ", o.Variants.Where(v => v != null).Select(v =>
                        Text(v.Variant, "Unnamed variant") + " [" + VariantName(v.Kind) + "] " + VerdictName(v.Verdict) + ": " + Reason(v.Explanation)));
                b.AppendLine(line);

                // Compare evidence is intentionally emitted only for a failing measure.
                if (o.Verdict != Verdict.Fail) continue;
                foreach (var row in o.Rows ?? Array.Empty<CompareRow>())
                    if (row != null)
                        b.AppendLine("  - " + Text(row.Context, "Context not recorded")
                            + ": SQL " + Number(row.Sql) + ", model " + Number(row.Dax) + ", delta " + Number(row.Delta)
                            + ", " + VerdictName(row.Verdict) + ". " + Reason(row.Explanation));
            }
            b.AppendLine();
        }

        private static void RenderRelationships(StringBuilder b, RelationshipIntegrityReport report)
        {
            b.AppendLine("## Relationships");
            b.AppendLine();
            var relationships = report?.Relationships?.Where(r => r != null).ToList() ?? new List<RelationshipResult>();
            b.AppendLine("- Data types: " + Signal(relationships.Select(r => r.DataTypeMatch)));
            b.AppendLine("- Key uniqueness: " + Signal(relationships.Select(r => r.KeyUniqueness)));
            b.AppendLine("- Referential integrity: " + Signal(relationships.Select(r => r.ReferentialIntegrity)));
            if (relationships.Count == 0) b.AppendLine("- No relationships were present.");
            foreach (var rel in relationships)
            {
                var orphan = rel.ReferentialIntegrity?.Count;
                var orphanText = orphan.HasValue ? orphan.Value.ToString("N0", CultureInfo.InvariantCulture) + " orphan rows" : "orphan count not measured";
                if (orphan.HasValue && rel.ManyRowCount.HasValue)
                    orphanText += rel.ManyRowCount.Value > 0
                        ? " (" + Pct(100.0 * orphan.Value / rel.ManyRowCount.Value) + " of " + rel.ManyRowCount.Value.ToString("N0", CultureInfo.InvariantCulture) + ")"
                        : " (rate not applicable: 0 many-side rows)";
                else if (orphan.HasValue) orphanText += " (rate not measured)";
                b.AppendLine("- **" + Text(rel.Name, "Unnamed relationship") + "**: " + orphanText
                    + ". Types " + CheckSummary(rel.DataTypeMatch)
                    + "; keys " + CheckSummary(rel.KeyUniqueness)
                    + "; references " + CheckSummary(rel.ReferentialIntegrity)
                    + ". Blank foreign keys: " + Count(rel.BlankForeignKeys)
                    + "; blank keys: " + Count(rel.BlankKeys) + ".");
            }
            b.AppendLine();
            b.AppendLine("### Table row counts");
            b.AppendLine();
            var tables = report?.TableRowCounts?.Where(t => t != null).ToList() ?? new List<TableRowCountResult>();
            if (tables.Count == 0) b.AppendLine("- No SQL-backed physical tables were detected.");
            foreach (var table in tables)
                b.AppendLine("- **" + Text(table.ModelTable, "Unnamed table") + "** vs `"
                    + Code(table.Schema, "?") + "." + Code(table.Entity, "?") + "`: "
                    + VerdictName(table.Check?.Verdict ?? Verdict.NotVerifiable) + ". " + Reason(table.Check?.Message)
                    + " Model COUNTROWS " + Count(table.ModelCount) + " at " + Text(table.ModelObservedUtc, "not observed")
                    + "; source COUNT_BIG " + Count(table.SourceCount) + " at " + Text(table.SourceObservedUtc, "not observed") + ".");
            b.AppendLine();
        }

        private static void RenderInterview(StringBuilder b, InterviewEvidence[] evidence, string note)
        {
            b.AppendLine("## Behavioral contracts (Model Interview)");
            b.AppendLine();
            b.AppendLine("Saved Model Interview questions replay as behavioral evidence only; they do not change the test grade or coverage.");
            b.AppendLine();
            var items = evidence?.Where(x => x != null).ToList() ?? new List<InterviewEvidence>();
            if (items.Count == 0) b.AppendLine("- No saved interview questions were captured with this run.");
            foreach (var item in items)
            {
                var outcome = InterviewOutcomeName(item.Outcome);
                var observed = string.IsNullOrWhiteSpace(item.When) ? "not observed" : Text(item.When, "not observed");
                var detail = string.IsNullOrWhiteSpace(item.Detail) ? "No outcome detail was recorded." : Reason(item.Detail);
                b.AppendLine("- **" + Text(item.Question, "Unnamed question") + "**: " + outcome + ". Observed " + observed + ". " + ContractRunDetail(item) + detail);
            }
            if (!string.IsNullOrWhiteSpace(note)) b.AppendLine("- Store note: " + Reason(note));
            b.AppendLine();
        }

        private static string InterviewOutcomeName(string outcome) => outcome switch
        {
            "Correct" => "Right",
            "Refused" => "Safely said it couldn't answer",
            "SilentlyWrong" => "Confidently wrong",
            "Unverified" => "Couldn't check",
            _ => "Not asked yet",
        };

        private static string ContractRunDetail(InterviewEvidence item)
        {
            if (item == null) return "";
            if (string.Equals(item.ReplayStatus, "chat-only", StringComparison.Ordinal))
                return "Chat-only contract; this Tests run did not grade an assistant response. ";
            if (string.Equals(item.ReplayStatus, "replayed", StringComparison.Ordinal))
                return "Replayed in this Tests run. " + (item.Changed
                    ? "Changed from " + InterviewOutcomeName(item.PreviousOutcome) + ". "
                    : item.PreviousOutcome == null ? "First observation. " : "Unchanged from the previous observation. ");
            return "";
        }

        private static void RenderSecurity(StringBuilder b, SecurityStaticReport report)
        {
            b.AppendLine("## Security");
            b.AppendLine();
            var filters = report?.Filters?.Where(f => f != null).ToList() ?? new List<RoleFilterResult>();
            var ols = report?.Ols?.Where(o => o != null).ToList() ?? new List<RoleOls>();
            if (filters.Count == 0) b.AppendLine("- No static role filters were present.");
            foreach (var filter in filters)
                b.AppendLine("- Filter " + Text(filter.Role, "Unnamed role") + " on " + Text(filter.Table, "Unnamed table")
                    + ": " + VerdictName(filter.Check?.Verdict ?? Verdict.NotVerifiable) + ". " + Reason(filter.Check?.Message)
                    + " Preview: `" + Code(filter.FilterPreview, "not recorded") + "`.");
            if (ols.Count == 0) b.AppendLine("- Object visibility was not available for this model.");
            foreach (var role in ols)
            {
                var visible = Math.Max(0, role.TablesTotal - role.TablesHidden);
                b.AppendLine("- Object visibility " + Text(role.Role, "Unnamed role") + ": "
                    + visible.ToString(CultureInfo.InvariantCulture) + " visible tables, "
                    + role.TablesHidden.ToString(CultureInfo.InvariantCulture) + " hidden tables, "
                    + role.ColumnsHidden.ToString(CultureInfo.InvariantCulture) + " hidden columns.");
            }
            b.AppendLine();
        }

        private static string Signal(IEnumerable<CheckResult> checks)
        {
            var list = (checks ?? Array.Empty<CheckResult>()).Where(c => c != null).ToList();
            return "Pass " + list.Count(c => c.Verdict == Verdict.Pass)
                + ", Fail " + list.Count(c => c.Verdict == Verdict.Fail)
                + ", Suspect " + list.Count(c => c.Verdict == Verdict.Suspect)
                + ", Not verifiable " + list.Count(c => c.Verdict == Verdict.NotVerifiable) + ".";
        }

        private static string CheckSummary(CheckResult check)
            => check == null ? "Not verifiable: reason not recorded" : VerdictName(check.Verdict) + ": " + Reason(check.Message);

        private static string VerdictName(Verdict verdict) => verdict == Verdict.NotVerifiable ? "Not verifiable" : verdict.ToString();

        private static string CheckName(string check) => check switch
        {
            RelationshipChecks.DataTypeMatch => "data types",
            RelationshipChecks.KeyUniqueness => "key uniqueness",
            RelationshipChecks.ReferentialIntegrity => "referential integrity",
            _ => "check",
        };

        private static string VariantName(TiVariantKind kind) => kind switch
        {
            TiVariantKind.Ytd => "YTD",
            TiVariantKind.Qtd => "QTD",
            TiVariantKind.Mtd => "MTD",
            TiVariantKind.PriorYear => "PY",
            TiVariantKind.YearOverYearDelta => "YoY",
            _ => "Unrecognized",
        };

        private static string Reason(string value) => Text(value, "Reason not recorded by the run").TrimEnd('.') + ".";
        private static string Count(long? value) => value.HasValue ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : "not measured";
        private static string Number(decimal? value) => value.HasValue ? value.Value.ToString("G29", CultureInfo.InvariantCulture) : "blank";
        private static string Pct(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        private static string Code(string value, string fallback) => Text(value, fallback).Replace('`', '\'');
        private static string Text(string value, string fallback)
        {
            var text = string.IsNullOrWhiteSpace(value) ? fallback : value;
            return string.Join(" ", text.Replace("\r", " ").Replace("\n", " ")
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}

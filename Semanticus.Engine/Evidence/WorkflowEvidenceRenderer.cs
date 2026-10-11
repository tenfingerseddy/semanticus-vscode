using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine.Evidence
{
    /// <summary>Projects one terminal workflow state into the same sealed document Tests and later sharing use.</summary>
    public static class WorkflowEvidenceRenderer
    {
        /// <summary>What WorkflowEvidenceRenderer appends to the workflow's title for the document title. The renderer
        /// strips it again to name the workflow in the header, so the two never drift.</summary>
        public const string TitleSuffix = " evidence";

        public static EvidenceDoc Build(WorkflowRunState run) => Build(run, null);

        /// <summary>The words a person reads are plain (F16): the workflow's own title and version, strictness as
        /// "Checks must pass", step states in words, and times in <paramref name="zone"/> (the machine's own zone
        /// when null). The machine facts stay exact where a program reads them: the run id, CreatedUtc in ISO UTC,
        /// the producer's workflow name, and the recorded answers and check details.</summary>
        public static EvidenceDoc Build(WorkflowRunState run, TimeZoneInfo zone)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            var multipleOwners = run.OwnerClosure.Definitions.Count > 1;
            var applicableResults = run.Results.Where(x => x.Status != "not_applicable").ToArray();
            var stepVerdicts = applicableResults.Select(StepVerdict).ToArray();
            // Weakest wins, in both directions. The certificate may only ever WEAKEN the headline verdict, never
            // strengthen it: the ladder is blind to warn-strictness rows (FrameRowStatus reads IsHardStep /
            // IsHardVerifyStep only) and the spec says a warn-only skip does not demote it, so a FULL certificate
            // over a run with a skipped warn step or a failed warn-gate verify must not export as Verified.
            var stepWorst = stepVerdicts.Length == 0 ? Verdict.Unknown : Verdicts.Worst(stepVerdicts);
            var overall = run.Certificate == null
                ? stepWorst
                : Verdicts.Worst(new[] { stepWorst, CertificateVerdict(run.Certificate.Level) });
            var overrides = run.Results.Where(x => x.Status == "skipped").Select(x => x.Note).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            var counts = Verdicts.Words.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
            foreach (var verdict in stepVerdicts) counts[verdict.ToString()]++;

            var doc = new EvidenceDoc
            {
                Id = run.RunId,
                Kind = "workflow-run",
                Title = DisplayName(run.Def) + TitleSuffix,
                CreatedUtc = run.FinishedUtc ?? run.StartedUtc,
                Producer = run.Def.Name,
                ProducerVersion = run.Def.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Origin = string.IsNullOrWhiteSpace(run.Origin) ? "system" : run.Origin,
                ModelName = run.ModelName,
                ModelFingerprint = run.ModelFingerprint,
                SessionId = run.SessionId,
                Verdict = overall,
                Coverage = new Coverage
                {
                    Verified = stepVerdicts.Count(x => x == Verdict.Verified),
                    Total = stepVerdicts.Length,
                    Unknowns = stepVerdicts.Count(x => x == Verdict.Unknown),
                },
                VerdictCounts = counts,
                OverrideReason = overall == Verdict.Overridden
                    ? CertificateOverrideReason(run.Certificate, overrides)
                    : null,
            };

            doc.Sections.Add(new SummarySection
            {
                Title = "Run outcome",
                Paragraphs = new[]
                {
                    $"{Named(run.Def)}, {OutcomeWords(run.Status)}.",
                    StepCountSentence(applicableResults),
                }.Concat(run.Certificate == null
                    ? Array.Empty<string>()
                    : new[] { $"The certificate level is {run.Certificate.Level}." }).ToList(),
            });
            doc.Sections.Add(new KeyValueSection
            {
                Title = "Run context",
                Pairs = new List<KeyValuePairRow>
                {
                    Pair("Workflow", Named(run.Def)),
                    Pair("Run", run.RunId),
                    Pair("Started", Evidence.EvidenceTime.Readable(run.StartedUtc, zone)),
                    Pair("Finished", Evidence.EvidenceTime.Readable(run.FinishedUtc, zone)),
                    Pair("Outcome", Capital(OutcomeWords(run.Status))),
                    Pair("Checks", ChecksWords(run.Def.Strictness ?? "hard")),
                },
            });
            if (multipleOwners)
                foreach (var owner in run.OwnerClosure.Definitions)
                    doc.Sections.Add(new KeyValueSection
                    {
                        Title = "Workflow definition: " + DisplayName(owner),
                        Pairs = new List<KeyValuePairRow>
                        {
                            Pair("Workflow", DisplayName(owner)),
                            Pair("Version", VersionWords(owner.Version)),
                            Pair("Checks", ChecksWords(owner.Strictness ?? "hard")),
                            Pair("Checks, as set in Settings", run.OwnerClosure.SettingsStrictness(owner) is string set ? ChecksWords(set) : "Not changed in Settings"),
                        },
                    });
            if (run.Certificate != null)
            {
                doc.Sections.Add(CertificateSection(run.Certificate));
                var frames = run.Certificate.Frames ?? Array.Empty<CertificateFrame>();
                for (var i = 0; i < frames.Length; i++)
                    doc.Sections.Add(CertificateFrameSection(frames[i], i));
            }
            doc.Sections.Add(new StepsSection
            {
                Title = "Steps",
                Steps = run.Results.Select(x => new StepRow
                {
                    Name = x.Title,
                    // The sealed vocabulary stays at five words. A condition that did not apply remains visible
                    // as Unknown, while the coverage/count population above deliberately excludes its row.
                    Verdict = StepVerdict(x),
                    Note = StepNote(x),
                }).ToList(),
            });

            for (var i = 0; i < run.Results.Count; i++)
            {
                var result = run.Results[i];
                var definition = run.Plan[i].Step;
                var pairs = new List<KeyValuePairRow>
                {
                    Pair("Outcome", StepWords(result.Status)),
                    Pair("Instructions", definition?.Instructions ?? "Not recorded"),
                    Pair("Actions", definition?.Ops == null || definition.Ops.Length == 0 ? "None declared" : string.Join(", ", definition.Ops)),
                    Pair("Checks", result.EffectiveStrictness == null ? "This step has no checks" : ChecksWords(result.EffectiveStrictness)),
                };
                if (multipleOwners)
                {
                    var owner = run.RequireCoherentRow(run.Plan[i]);
                    pairs.Insert(0, Pair("Step", run.Plan[i].InstanceId));
                    pairs.Insert(1, Pair("Workflow", DisplayName(owner)));
                    pairs.Insert(2, Pair("Version", VersionWords(owner.Version)));
                    pairs.Insert(3, Pair("Checks", ChecksWords(owner.Strictness ?? "hard")));
                }
                if (SkipReason(result) is string why) pairs.Add(Pair("Why it was skipped", why));
                else if (!string.IsNullOrWhiteSpace(result.Note)) pairs.Add(Pair("Recorded note", result.Note));
                foreach (var answer in result.Answers.OrderBy(x => x.Key, StringComparer.Ordinal))
                    pairs.Add(Pair("Answer: " + answer.Key, Answer(answer.Value)));
                doc.Sections.Add(new KeyValueSection { Title = $"Step {i + 1}: {result.Title}", Pairs = pairs });
            }

            var gates = new List<FindingRow>();
            foreach (var step in run.Results)
                foreach (var verify in step.VerifyResults ?? Array.Empty<VerifyResult>())
                    gates.Add(new FindingRow
                    {
                        Name = step.Title + ": " + verify.Kind,
                        Verdict = VerifyVerdict(step, verify),
                        Detail = string.IsNullOrWhiteSpace(verify.Detail) ? "No detail was recorded." : verify.Detail,
                    });
            if (gates.Count > 0)
                doc.Sections.Add(new FindingsSection { Title = "Gate results", Rows = gates });
            if (!string.IsNullOrWhiteSpace(run.AbortReason))
                doc.Sections.Add(new NoteSection { Title = "Abort reason", Text = run.AbortReason, Tone = "warning" });
            return doc;
        }

        private static Verdict CertificateVerdict(string level) => level switch
        {
            "FULL" => Verdict.Verified,
            "PARTIAL" => Verdict.Unknown,
            "OVERRIDDEN" => Verdict.Overridden,
            _ => Verdict.Unknown,
        };

        private static string CertificateOverrideReason(WorkflowCertificate certificate, string[] stepOverrides)
        {
            if (certificate?.SkippedSteps != null)
            {
                var reasons = certificate.SkippedSteps.Select(x => x?.Reason)
                    .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                if (reasons.Length > 0) return string.Join(" | ", reasons);
            }
            if (stepOverrides != null && stepOverrides.Length > 0) return string.Join(" | ", stepOverrides);
            return $"The folded workflow certificate is OVERRIDDEN (claim {certificate?.ClaimLevel ?? "not recorded"}; computed {certificate?.ComputedLevel ?? "not recorded"}).";
        }

        private static KeyValueSection CertificateSection(WorkflowCertificate certificate) => new KeyValueSection
        {
            Title = "Certificate",
            Pairs = new List<KeyValuePairRow>
            {
                Pair("Level", Text(certificate.Level)),
                Pair("Agent claim", Text(certificate.AgentClaim)),
                Pair("Claim level", Text(certificate.ClaimLevel)),
                Pair("Computed level", Text(certificate.ComputedLevel)),
                Pair("Grid columns", Items(certificate.GridColumns)),
                Pair("Coverage", CoverageItems(certificate.Coverage)),
                Pair("Open grains", Items(certificate.OpenGrains)),
                Pair("Countersigned cells", CountersignItems(certificate.CountersignedCells)),
                Pair("Disputed cells", DisputeItems(certificate.DisputedCells)),
                Pair("Anchor revisions", certificate.AnchorRevisionCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Pair("Form repairs", certificate.FormRepairCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Pair("Coverage surface revisions", RevisionItems(certificate.CoverageSurfaceRevisions)),
                Pair("Skipped steps", SkippedItems(certificate.SkippedSteps)),
                Pair("Iterations total", Number(certificate.IterationsTotal)),
                Pair("Iterations passed", Number(certificate.IterationsPassed)),
                Pair("Iterations failed", Number(certificate.IterationsFailed)),
                Pair("Failed iteration values", Items(certificate.FailedIterationValues)),
            },
        };

        private static KeyValueSection CertificateFrameSection(CertificateFrame frame, int index) => new KeyValueSection
        {
            Title = $"Certificate frame {index + 1}: {Text(frame.Kind)} {Text(frame.StepId)}",
            Pairs = new List<KeyValuePairRow>
            {
                Pair("Kind", Text(frame.Kind)),
                Pair("Step", Text(frame.StepId)),
                Pair("State", Text(frame.State)),
                Pair("Level", Text(frame.Level)),
                Pair("Iteration", Number(frame.IterationIndex)),
                Pair("Loop variable", Text(frame.LoopVariable)),
                Pair("Loop value", Text(frame.LoopValue)),
                Pair("Workflow", Text(frame.Workflow)),
                Pair("Depth", Number(frame.Depth)),
                Pair("Passed bindings", Items(frame.Passed)),
                Pair("Returned bindings", Items(frame.Returned)),
                Pair("Steps and verification", StepItems(frame.Steps)),
                Pair("Grid columns", Items(frame.GridColumns)),
                Pair("Coverage", CoverageItems(frame.Coverage)),
                Pair("Open grains", Items(frame.OpenGrains)),
                Pair("Countersigned cells", CountersignItems(frame.CountersignedCells)),
                Pair("Disputed cells", DisputeItems(frame.DisputedCells)),
                Pair("Anchor revisions", frame.AnchorRevisionCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Pair("Form repairs", frame.FormRepairCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Pair("Coverage surface revisions", RevisionItems(frame.CoverageSurfaceRevisions)),
            },
        };

        private static string Text(string value) => string.IsNullOrWhiteSpace(value) ? "Not recorded" : value;
        private static string Number(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Not recorded";
        private static string Items(IEnumerable<string> values) => Join(values?.Where(x => !string.IsNullOrWhiteSpace(x)));
        private static string CoverageItems(IEnumerable<CertificateGrainCoverage> values) =>
            Join(values?.Where(x => x != null).Select(x => $"{Text(x.ShapeId)}: {Text(x.State)}"));
        private static string CountersignItems(IEnumerable<ShapeMismatchCountersign> values) =>
            Join(values?.Where(x => x != null).Select(x => $"{Text(x.Coordinate)} (candidate {Text(x.CandidateValue)}; witness {Text(x.WitnessValue)}; stated {Text(x.Stated)})"));
        private static string DisputeItems(IEnumerable<ShapeMismatchLedgerCell> values) =>
            Join(values?.Where(x => x != null).Select(x => $"{Text(x.Coordinate)} (candidate {Text(x.CandidateValue)}; witness {Text(x.WitnessValue)})"));
        private static string RevisionItems(IEnumerable<CoverageSurfaceRevision> values) =>
            Join(values?.Where(x => x != null).Select(x => $"{Text(x.StepId)} at {Text(x.TimestampUtc)} (grid +{Items(x.AddedGridColumns)}; open +{Items(x.AddedOpenGrains)})"));
        private static string SkippedItems(IEnumerable<CertificateSkippedStep> values) =>
            Join(values?.Where(x => x != null).Select(x => $"{Text(x.StepId)} ({Text(x.Title)}): {Text(x.Reason)} ({Text(x.EffectiveStrictness)})"));
        private static string StepItems(IEnumerable<StepResult> values) =>
            Join(values?.Where(x => x != null).Select(StepItem));

        private static string StepItem(StepResult step)
        {
            var answers = Join((step.Answers ?? new Dictionary<string, AnswerValue>())
                .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + Answer(x.Value)));
            var verify = VerifyItems(step.VerifyResults);
            var history = Join((step.VerifyHistory ?? new List<VerifyAttempt>())
                .Select(x => $"attempt {x.Ordinal}: {VerifyItems(x.Results)}"));
            return $"{Text(step.StepId)} ({Text(step.Title)}): {Text(step.Status)}; note {Text(step.Note)}; "
                + $"strictness {Text(step.EffectiveStrictness)}; answers {answers}; verify {verify}; history {history}";
        }

        private static string VerifyItems(IEnumerable<VerifyResult> values) =>
            Join(values?.Where(x => x != null).Select(x => $"{Text(x.Kind)}={Text(x.Status)} ({Text(x.Detail)})"));
        private static string Join(IEnumerable<string> values)
        {
            if (values == null) return "None";
            var materialized = values.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            return materialized.Length == 0 ? "None" : string.Join(" | ", materialized);
        }

        private static Verdict StepVerdict(StepResult step)
        {
            if (step == null) return Verdict.Unknown;
            if (step.Status == "skipped") return Verdict.Overridden;
            if (step.Status == "failed") return Verdict.Broken;
            if (step.Status != "passed") return Verdict.Unknown;
            var vrs = step.VerifyResults ?? Array.Empty<VerifyResult>();
            if (vrs.Length == 0) return Verdict.Unknown;   // finished, but nothing was verified
            if (vrs.Any(x => x.Status == "failed")) return Verdict.NeedsReview;
            // `unavailable`/`skipped` on a passed (warn/off) step = evidence could not be produced ⇒ not "verified".
            // `not_applicable` (a conditional that did not apply) is legitimately silent and does NOT downgrade.
            if (vrs.Any(x => x.Status == "unavailable" || x.Status == "skipped")) return Verdict.Unknown;
            if (!vrs.Any(x => x.Status == "passed")) return Verdict.Unknown;
            return Verdict.Verified;
        }

        private static Verdict VerifyVerdict(StepResult step, VerifyResult verify) => verify.Status switch
        {
            "passed" => Verdict.Verified,
            "failed" => step.Status == "passed" ? Verdict.NeedsReview : Verdict.Broken,
            "unavailable" => step.Status == "passed" ? Verdict.Unknown : Verdict.Broken,
            _ => Verdict.Unknown,   // not_applicable / skipped — no evidence, neutral
        };

        private static string StepNote(StepResult step)
        {
            var why = SkipReason(step);
            var bits = new List<string> { why != null ? "Skipped: " + why.TrimEnd('.') : StepWords(step.Status) };
            if (!string.IsNullOrWhiteSpace(step.EffectiveStrictness)) bits.Add(ChecksWords(step.EffectiveStrictness));
            if (why == null && !string.IsNullOrWhiteSpace(step.Note)) bits.Add(step.Note.TrimEnd('.'));
            if (step.Answers.Count > 0) bits.Add(step.Answers.Count == 1 ? "1 answer recorded" : step.Answers.Count + " answers recorded");
            if (step.VerifyResults.Length > 0) bits.Add(step.VerifyResults.Length == 1 ? "1 check result" : step.VerifyResults.Length + " check results");
            return string.Join(". ", bits) + ".";
        }

        // ---- the person's words (F16; docs/product-copy-style.md) ----

        /// <summary>The reason a person gave for a skip, without the runner's "skipped: " marker (WorkflowRunner).</summary>
        private static string SkipReason(StepResult step)
        {
            const string marker = "skipped: ";
            if (step?.Status != "skipped" || string.IsNullOrWhiteSpace(step.Note)) return null;
            return step.Note.StartsWith(marker, StringComparison.Ordinal) ? step.Note.Substring(marker.Length).Trim() : step.Note.Trim();
        }

        private static string DisplayName(WorkflowDef def) => string.IsNullOrWhiteSpace(def?.Title) ? def?.Name ?? "workflow" : def.Title.Trim();
        private static string VersionWords(int version) => "Version " + version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        /// <summary>"Test a complex measure, version 7".</summary>
        internal static string Named(WorkflowDef def) => DisplayName(def) + ", version " + def.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string OutcomeWords(string status) => status switch
        {
            "completed" => "finished",
            "aborted" => "was stopped",
            "active" => "is still running",
            _ => "ended",
        };

        /// <summary>The same words the Studio uses for strictness (workflow-run.tsx).</summary>
        private static string ChecksWords(string strictness) => strictness switch
        {
            "hard" => "Checks must pass",
            "warn" => "Checks warn only",
            "off" => "Checks are off",
            _ => "Checks as the workflow says",
        };

        private static string StepWords(string status) => status switch
        {
            "passed" => "Passed its checks",
            "done" => "Done, with nothing to check",
            "skipped" => "Skipped",
            "failed" => "Failed a check",
            "not_applicable" => "Did not apply",
            "in_progress" => "In progress",
            "pending" => "Not reached",
            _ => Capital(status ?? "Not recorded"),
        };

        /// <summary>"0 of 4 steps passed their checks. 1 was done with nothing to check, and 3 were skipped." Every
        /// applicable step is counted once, in the same words the steps list uses.</summary>
        private static string StepCountSentence(StepResult[] steps)
        {
            string Num(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var passed = steps.Count(x => x.Status == "passed");
            var done = steps.Count(x => x.Status == "done");
            var skipped = steps.Count(x => x.Status == "skipped");
            var failed = steps.Count(x => x.Status == "failed");
            var open = steps.Length - passed - done - skipped - failed;
            var head = $"{Num(passed)} of {Num(steps.Length)} {(steps.Length == 1 ? "step" : "steps")} passed {(steps.Length == 1 ? "its" : "their")} checks.";
            var rest = new List<string>();
            string Was(int n) => n == 1 ? "was" : "were";
            if (done > 0) rest.Add($"{Num(done)} {Was(done)} done with nothing to check");
            if (failed > 0) rest.Add($"{Num(failed)} failed a check");
            if (skipped > 0) rest.Add($"{Num(skipped)} {Was(skipped)} skipped");
            if (open > 0) rest.Add($"{Num(open)} {Was(open)} not reached");
            if (rest.Count == 0) return head;
            var tail = rest.Count == 1 ? rest[0] : string.Join(", ", rest.Take(rest.Count - 1)) + ", and " + rest[rest.Count - 1];
            return head + " " + Capital(tail) + ".";
        }

        private static string Answer(AnswerValue value)
        {
            if (value == null) return "No value recorded";
            if (value.Declined) return "Declined: " + (string.IsNullOrWhiteSpace(value.DeclineReason) ? "No reason recorded" : value.DeclineReason);
            return value.Value ?? "No value recorded";
        }

        private static KeyValuePairRow Pair(string key, string value) => new KeyValuePairRow { Key = key, Value = value };
    }
}

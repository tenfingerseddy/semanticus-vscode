using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine.Evidence
{
    /// <summary>Projects one terminal workflow state into the same sealed document Tests and later sharing use.</summary>
    public static class WorkflowEvidenceRenderer
    {
        public static EvidenceDoc Build(WorkflowRunState run)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            var stepVerdicts = run.Results.Select(StepVerdict).ToArray();
            var overall = stepVerdicts.Length == 0 ? Verdict.Unknown : Verdicts.Worst(stepVerdicts);
            var overrides = run.Results.Where(x => x.Status == "skipped").Select(x => x.Note).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            var counts = Verdicts.Words.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
            foreach (var verdict in stepVerdicts) counts[verdict.ToString()]++;

            var doc = new EvidenceDoc
            {
                Id = run.RunId,
                Kind = "workflow-run",
                Title = (string.IsNullOrWhiteSpace(run.Def.Title) ? run.Def.Name : run.Def.Title) + " evidence",
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
                OverrideReason = overall == Verdict.Overridden ? string.Join(" | ", overrides) : null,
            };

            doc.Sections.Add(new SummarySection
            {
                Title = "Run outcome",
                Paragraphs = new List<string>
                {
                    $"Workflow {run.Def.Name} version {run.Def.Version} finished with status {run.Status}.",
                    $"{stepVerdicts.Count(x => x == Verdict.Verified)} of {stepVerdicts.Length} steps completed without an unknown, failed, or overridden result.",
                },
            });
            doc.Sections.Add(new KeyValueSection
            {
                Title = "Run context",
                Pairs = new List<KeyValuePairRow>
                {
                    Pair("Run", run.RunId),
                    Pair("Started", run.StartedUtc ?? "Not recorded"),
                    Pair("Finished", run.FinishedUtc ?? "Not recorded"),
                    Pair("Status", run.Status),
                    Pair("Workflow version", run.Def.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Pair("Definition strictness", run.Def.Strictness ?? "hard (default)"),
                },
            });
            doc.Sections.Add(new StepsSection
            {
                Title = "Steps",
                Steps = run.Results.Select(x => new StepRow
                {
                    Name = x.Title,
                    Verdict = StepVerdict(x),
                    Note = StepNote(x),
                }).ToList(),
            });

            for (var i = 0; i < run.Results.Length; i++)
            {
                var result = run.Results[i];
                var definition = run.Def.Steps.FirstOrDefault(x => x.Id == result.StepId);
                var pairs = new List<KeyValuePairRow>
                {
                    Pair("Status", result.Status),
                    Pair("Instructions", definition?.Instructions ?? "Not recorded"),
                    Pair("Actions", definition?.Ops == null || definition.Ops.Length == 0 ? "None declared" : string.Join(", ", definition.Ops)),
                    Pair("Effective strictness", result.EffectiveStrictness ?? "No gate"),
                };
                if (!string.IsNullOrWhiteSpace(result.Note)) pairs.Add(Pair("Recorded note", result.Note));
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

        private static Verdict StepVerdict(StepResult step)
        {
            if (step == null) return Verdict.Unknown;
            if (step.Status == "skipped") return Verdict.Overridden;
            if (step.Status == "failed") return Verdict.Broken;
            if (step.Status != "passed") return Verdict.Unknown;
            var vrs = step.VerifyResults ?? Array.Empty<VerifyResult>();
            if (vrs.Any(x => x.Status == "failed")) return Verdict.NeedsReview;
            // `unavailable`/`skipped` on a passed (warn/off) step = evidence could not be produced ⇒ not "verified".
            // `not_applicable` (a conditional that did not apply) is legitimately silent and does NOT downgrade.
            if (vrs.Any(x => x.Status == "unavailable" || x.Status == "skipped")) return Verdict.Unknown;
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
            var bits = new List<string> { "Status: " + step.Status };
            if (!string.IsNullOrWhiteSpace(step.EffectiveStrictness)) bits.Add("Gate: " + step.EffectiveStrictness);
            if (!string.IsNullOrWhiteSpace(step.Note)) bits.Add(step.Note);
            if (step.Answers.Count > 0) bits.Add(step.Answers.Count + " answer(s) recorded");
            if (step.VerifyResults.Length > 0) bits.Add(step.VerifyResults.Length + " verification result(s)");
            return string.Join(". ", bits) + ".";
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

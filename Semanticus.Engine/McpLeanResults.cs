using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine.StartPlan;

namespace Semanticus.Engine
{
    public sealed class LeanResultSummary
    {
        public string NextStep { get; set; }
        public Dictionary<string, int> Counts { get; set; } = new();
        public string Details { get; set; }
        public LeanResultPage Page { get; set; }
    }

    public sealed class LeanResultPage
    {
        public string Item { get; set; }
        public int Total { get; set; }
        public int Offset { get; set; }
        public int? NextOffset { get; set; }
        public object[] Items { get; set; } = Array.Empty<object>();
        public string Note { get; set; }
    }

    // MCP projections only. Engine results and Studio RPC keep every field.
    internal static class McpLeanResults
    {
        internal const int Cap = 7000; // Leave room for the MCP health and change lines.
        private static readonly JsonSerializerOptions Json = ModelContextProtocol.McpJsonUtilities.DefaultOptions;
        internal static int Chars(object value) => JsonSerializer.Serialize(value, Json).Length;

        private sealed class Snapshot
        {
            public Snapshot() { }
            internal string SessionId;
            internal ReadinessPlan Ready;
            internal StartPlanView Start;
        }
        private static readonly ConditionalWeakTable<IEngine, Snapshot> Last = new();

        internal static StartPlanView Start(IEngine engine, StartPlanView view, bool detail = false,
            string item = null, int offset = 0, int max = 40)
        {
            var saved = Last.GetOrCreateValue(engine);
            if (view.KnockOns != null) saved.Start = view;
            if (detail) return view;
            var result = new StartPlanView
            {
                Exists = view.Exists, Id = view.Id, Digest = view.Digest,
                Destination = view.Destination, Source = view.Source, Approval = view.Approval,
                CanBuild = view.CanBuild, BlockedBecause = view.BlockedBecause, Built = view.Built,
                Grades = view.Grades, Words = view.Words, SampleFacts = view.SampleFacts,
                Sample = view.Sample, JevDeclined = view.JevDeclined, Notice = view.Notice, Asked = view.Asked,
                Decide = view.Decide == null ? null : new StartDecide
                { State = view.Decide.State, Round = view.Decide.Round, Reason = view.Decide.Reason },
                Sections = view.Sections.Select(s => new StartSection
                { Id = s.Id, Title = s.Title, Summary = s.Summary, Counts = s.Counts }).ToList(),
                Summary = new LeanResultSummary
                {
                    NextStep = !view.Exists ? "Call start_model_from_sql to make a start plan."
                        : view.Built ? "Read get_model_plan for the work left after the build."
                        : view.CanBuild ? "Call build_start_plan with this digest."
                        : "Read needs_you, answer the questions, then review the plan before building.",
                    Details = "get_start_plan(item: 'tables' | 'links' | 'measures' | 'words' | 'bestpractice' | 'columns' | 'needs_you' | 'edges' | 'knock_ons', offset: 0, max: 40). Use nextOffset for the next page. get_start_plan(detail: true) returns the full Studio view. Knock-on pages describe the last edit with this digest.",
                    Counts = new Dictionary<string, int>
                    {
                        ["lines"] = view.Sections.Sum(s => s.Groups.Sum(g => g.Lines.Count)),
                        ["columns"] = view.Sections.Sum(s => s.Groups.Sum(g => g.Columns?.Count ?? 0)),
                        ["needsYou"] = view.NeedsYou.Count, ["links"] = view.Links.Count,
                        ["knockOns"] = view.KnockOns?.Count ?? 0,
                    },
                },
            };
            foreach (var section in view.Sections)
                result.Summary.Counts[section.Id] = section.Groups.Sum(g => g.Lines.Count);
            if (item != null)
            {
                IEnumerable<object> rows;
                if (item == "columns") rows = view.Sections.SelectMany(s => s.Groups)
                    .SelectMany(g => (g.Columns ?? new List<StartColumn>()).Select(c => (object)new { g.Table, Column = c }));
                else if (item == "needs_you") rows = view.NeedsYou;
                else if (item == "edges") rows = view.Links;
                else if (item == "knock_ons") rows = (view.KnockOns ??
                    (saved.Start?.Id == view.Id && saved.Start.Digest == view.Digest ? saved.Start.KnockOns : null)) ?? new List<StartKnockOn>();
                else
                {
                    var section = view.Sections.SingleOrDefault(s => s.Id == item)
                        ?? throw new ArgumentException("Unknown start-plan item. Use a section id, columns, needs_you, edges or knock_ons.");
                    rows = section.Groups.SelectMany(g => g.Lines.Select(l => (object)new { g.Table, Line = l }));
                }
                Page(result.Summary, rows, item, offset, max, result);
            }
            else
            {
                // Show the first decisions immediately. The count and page instructions name the rest.
                foreach (var need in view.NeedsYou)
                {
                    result.NeedsYou.Add(need);
                    if (Chars(result) > Cap) { result.NeedsYou.RemoveAt(result.NeedsYou.Count - 1); break; }
                }
            }
            return result;
        }

        internal static async Task<ReadinessPlan> Ready(IEngine engine, int maxQueue, bool detail,
            string item, int offset, int max, ReadinessPlan supplied = null)
        {
            var saved = Last.GetOrCreateValue(engine);
            ReadinessPlan full;
            if (item == null)
            {
                full = supplied ?? await engine.MakeAiReadyAsync("agent", maxQueue).ConfigureAwait(false);
                saved.Ready = full;
                saved.SessionId = (await engine.SessionInfoAsync().ConfigureAwait(false))?.SessionId;
            }
            else
            {
                var session = await engine.SessionInfoAsync().ConfigureAwait(false);
                if (saved.Ready == null || session?.SessionId != saved.SessionId)
                    throw new InvalidOperationException("Call make_model_ai_ready without item first for this model, then read its pages.");
                full = saved.Ready;
            }
            if (detail) return full;
            var card = full.Scorecard;
            var result = new ReadinessPlan
            {
                Revision = full.Revision,
                Scorecard = new Scorecard
                {
                    Overall = card.Overall, RawOverall = card.RawOverall, Grade = card.Grade,
                    Categories = card.Categories, Coverage = card.Coverage, GatedBy = card.GatedBy,
                    SafeFixCount = card.SafeFixCount, WaivedCount = card.WaivedCount, Caveat = card.Caveat,
                    OrphanedWaiverCount = card.OrphanedWaiverCount,
                },
                Summary = new LeanResultSummary
                {
                    NextStep = full.AiQueue.Length > 0 ? "Read the queue page and write its descriptions, names and synonyms."
                        : "Read get_model_plan for any remaining work.",
                    Counts = new Dictionary<string, int>
                    {
                        ["applied"] = full.Applied.Length, ["findings"] = card.Findings.Length,
                        ["queue"] = full.AiQueue.Length,
                        ["aiContentRemaining"] = card.Findings.Count(f => f.Fix == nameof(FixKind.AiContent) && !f.Waived),
                        ["ruleErrors"] = card.RuleErrors.Length, ["unevaluatedRules"] = card.UnevaluatedRules.Length,
                        ["orphanedWaivers"] = card.OrphanedWaivers.Length,
                    },
                    Details = "make_model_ai_ready(item: 'queue' | 'applied' | 'findings' | 'rule_errors' | 'unevaluated_rules' | 'orphaned_waivers', offset: 0, max: 40) reads the saved result without applying fixes again. Use nextOffset for the next page. item: 'full' returns the full saved result. The queue contains up to maxQueue items; increase maxQueue on the next run to queue more. Pages keep the result revision even if the model changes.",
                },
            };
            if (item == "full") return full;
            if (item != null)
            {
                IEnumerable<object> rows = item switch
                {
                    "queue" => full.AiQueue, "applied" => full.Applied,
                    "findings" => card.Findings, "rule_errors" => card.RuleErrors,
                    "unevaluated_rules" => card.UnevaluatedRules, "orphaned_waivers" => card.OrphanedWaivers,
                    _ => throw new ArgumentException("Unknown AI-ready item. Use queue, applied, findings, rule_errors, unevaluated_rules, orphaned_waivers or full."),
                };
                Page(result.Summary, rows, item, offset, max, result);
            }
            return result;
        }

        private static void Page(LeanResultSummary summary, IEnumerable<object> rows, string item,
            int offset, int max, object result)
        {
            var all = rows.ToArray();
            offset = Math.Max(0, offset); max = Math.Clamp(max, 1, 100);
            var page = summary.Page = new LeanResultPage { Item = item, Offset = offset, Total = all.Length };
            var values = new List<object>();
            foreach (var row in all.Skip(offset).Take(max))
            {
                values.Add(row); page.Items = values.ToArray();
                if (Chars(result) > Cap)
                {
                    if (values.Count == 1)
                        page.Note = "This one item exceeds the page budget. It is returned whole so no detail is lost.";
                    else { values.RemoveAt(values.Count - 1); page.Items = values.ToArray(); }
                    break;
                }
            }
            page.NextOffset = offset + page.Items.Length < all.Length ? offset + page.Items.Length : null;
        }
    }
}

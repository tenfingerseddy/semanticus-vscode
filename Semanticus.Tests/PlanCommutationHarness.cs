// The commutation gate's harness (Plan-heart lane E2, carried from spike P0b): how each family's row is DONE in a
// throwaway copy, and the pair outcome. The footprints themselves are the ENGINE's (Plan/PlanFootprint.cs), read
// through LocalEngine.FootprintForTestAsync, so this proves the rule the plan uses and not a copy of it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    // ---- one line of a change, as check 2 compares it: target, property, after (and the op) -------------------
    internal sealed record CommutationLine(string Op, string Target, string Property, string After)
    {
        public override string ToString() => Op + " " + Target + (Property == null ? "" : "." + Property) + " = " + After;
        public static CommutationLine Of(ModelPlanChangeLine l) => new CommutationLine(l.Op, l.Target, l.Property, l.After);
    }

    /// <summary>How each family's row is DONE, and how its footprint is derived. One place, so the pair runs
    /// and the footprints read the same lines.</summary>
    internal static class CommutationRows
    {
        /// <summary>"safefix/Visibility" is safefix; a Jev row "jev.ai-schema.table:Sales" is its family, jev.ai-schema.</summary>
        public static string Family(string id) =>
            IsJev(id) ? id.Substring(0, id.IndexOf('.', JevFamily.ItemPrefix.Length)) : id.Substring(0, id.IndexOf('/'));

        /// <summary>A row a Jev family works. It is worked out by the family's own background run, not by propose, and
        /// worked out again at every revision, so it is never cancelled here: cancel on a Jev row dismisses its lines
        /// for the session, which would take the row away rather than let it be worked out again.</summary>
        public static bool IsJev(string id) => JevFamily.IsJevItem(id);

        public static bool EngineProposes(string id) =>
            id.StartsWith("safefix/", StringComparison.Ordinal)
            || id.StartsWith("starter/", StringComparison.Ordinal)
            || id == "cleanup/model";

        /// <summary>The scripted proposal families: the person's choice made the way the row's own diff names it.</summary>
        public static readonly Dictionary<string, string> ScriptedKind = new Dictionary<string, string>
        {
            ["words/model"] = "set_description",
            ["proposal/collisions"] = "set_column_hidden",      // "rename it or hide it": the spike hides
            ["proposal/formats"] = "set_format_string",          // "set its format"
            // "add an explicit measure for it": add_plan_item refuses a create_measure line for a measure that does
            // not exist yet (ReadItemContext, "Object not found"), so the spike stops the implicit total instead.
            ["proposal/implicit-measures"] = "set_summarize_by",
            // "Mark the date table": the row refuses propose, so the person's choice goes on the plan as a line.
            ["datetable/model"] = "mark_date_table",
        };

        /// <summary>Assistant rows composed from lines the fixture seeded (asst/&lt;kind&gt;). Doing one is approving
        /// it; working it out again re-adds the same lines, as the assistant would.</summary>
        public static readonly Dictionary<string, (string Ref, string After)[]> AsstSeeds =
            new Dictionary<string, (string, string)[]>
            {
                // Wide has Sales[Note]; DateMark has Margin % (a measure with no time intelligence) and Date[Month] (a
                // column on the date table that the mark does not key).
                ["asst/set_display_folder"] = new[] { ("column:Sales/Note", "Notes"), ("measure:Sales/Margin %", "Ratios"),
                                                      ("column:Date/Month", "Calendar") },
            };

        /// <summary>Assistant rows whose lines carry ARGUMENTS add_plan_item cannot pass (the creates ApplyOneItem runs:
        /// a column's table and type, a relationship's two ends, a permission's role). They go on the change plan
        /// directly, the way the Jev executor tests seed lines, as the assistant's own lines (source "ai", kept), so
        /// they file under asst/&lt;kind&gt;. Each is worked out from the model as it is, so two orders compare.</summary>
        public static readonly Dictionary<string, Func<Model, ChangeItem[]>> SeededItems =
            new Dictionary<string, Func<Model, ChangeItem[]>>
            {
                ["asst/create_column"] = m => Seeded(m, "column:Customer/Segment", "create_column", null,
                    ("tableRef", "table:Customer"), ("name", "Segment"), ("dataType", "String")),
                ["asst/create_import_table"] = m => Seeded(m, "table:Region", "create_import_table",
                    "let Source = #table ( type table [ Region = text ], { } ) in Source", ("name", "Region")),
                ["asst/create_calculation_group"] = m => Seeded(m, "table:Scenario 2", "create_calculation_group", null, ("name", "Scenario 2")),
                // Round 4 (THE CLEANUP OFFER RULE, version 2): the item reads Sales[Note], a plain unused text column on a fact the
                // cleanup row still offers (it read Customer[Country], which the row no longer offers: Customer has no label to find),
                // so the item still uses a column the cleanup row offers to delete, and the order still matters.
                ["asst/create_calculation_item"] = m => Seeded(m, "table:Scenario", "create_calculation_item",
                    "CALCULATE ( SELECTEDMEASURE (), Sales[Note] = \"NZ\" )", ("calcGroupRef", "table:Scenario"), ("name", "NZ")),
                ["asst/create_role"] = m => Seeded(m, "role:Auditors", "create_role", null, ("name", "Auditors"), ("modelPermission", "Read")),
                ["asst/set_table_permission"] = m => Seeded(m, "table:Sales", "set_table_permission", "Sales[Amount] > 0",
                    ("roleName", "Auditors"), ("tableRef", "table:Sales")),
                // Customer and Product are joined only by the fixture's INACTIVE relationship, so this one may be active
                // until that one is switched on.
                ["asst/create_relationship"] = m => Seeded(m, "column:Customer/Country", "create_relationship", null,
                    ("fromColumnRef", "column:Customer/Country"), ("toColumnRef", "column:Product/Colour")),
                ["asst/set_relationship_active"] = m => Seeded(m, RelationshipFrom(m, "Customer", "CustomerKey"), "set_relationship_active", "true"),
                ["asst/set_relationship_cardinality"] = m => Seeded(m, RelationshipFrom(m, "Sales", "ProductKey"), "set_relationship_cardinality", null,
                    ("fromCardinality", "One"), ("toCardinality", "One")),
                ["asst/generate_time_intelligence"] = m => Seeded(m, "measure:Sales/Total Sales", "generate_time_intelligence", null,
                    ("baseMeasureRef", "measure:Sales/Total Sales"), ("dateColumn", "column:Date/Date"), ("variants", "PY")),
            };

        private static ChangeItem[] Seeded(Model m, string objRef, string kind, string after, params (string Key, string Value)[] args) =>
            new[]
            {
                new ChangeItem
                {
                    ObjectRef = objRef, ObjectName = objRef, Kind = kind, Source = "ai", Target = kind, Risk = "ai",
                    Severity = "Medium", Title = kind + " " + objRef, Rationale = "Seeded by the commutation gate.",
                    // apply_plan skips a line with no content, so a create whose applier reads only its arguments
                    // carries what it makes as its content, the way the change reads to a person.
                    After = after ?? objRef, Status = "approved", Generated = true,
                    Args = args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal),
                },
            };

        /// <summary>A cardinality line on the relationship from <paramref name="table"/>[<paramref name="column"/>], as the
        /// assistant's own seeded line (lane m11/plan-waits, round 2: a cardinality that moves the many side).</summary>
        public static ChangeItem[] SeededCardinality(Model m, string table, string column, string from, string to) =>
            Seeded(m, RelationshipFrom(m, table, column), "set_relationship_cardinality", null, ("fromCardinality", from), ("toCardinality", to));

        private static readonly MethodInfo SourceVersionMethod =
            typeof(LocalEngine).GetMethod("SourceVersionOf", BindingFlags.NonPublic | BindingFlags.Instance);

        private static string RelationshipFrom(Model m, string table, string column) =>
            ObjectRefs.For(m.Relationships.OfType<SingleColumnRelationship>()
                .Single(r => r.FromTable?.Name == table && r.FromColumn?.Name == column));

        /// <summary>Put a seeded row's lines on the change plan, unless its row already holds pending lines.</summary>
        public static async Task SeedItemsAsync(DecisionFixture f, string id)
        {
            var held = (await f.Engine.GetModelPlanAsync()).Items.FirstOrDefault(i => i.Id == id)?.ChangeSet?.Lines?.Length ?? 0;
            if (held > 0) return;
            var items = await f.Sessions.Current.ReadAsync(m => SeededItems[id](m));
            var context = f.Sessions.CurrentContext;
            var s = context.RequireSession();
            // Stamped with the source version the engine's own add stamps, or the approve refuses it as out of date.
            var version = (string)SourceVersionMethod.Invoke(f.Engine, new object[] { s });
            await context.PlanGate.WaitAsync();
            try
            {
                var plan = context.Plans.GetOrStart();
                foreach (var it in items) plan.Add(it, s.Revision, version);
            }
            finally { context.PlanGate.Release(); }
        }

        public static bool HasExecutor(string id) => IsJev(id) || EngineProposes(id) || ScriptedKind.ContainsKey(id) || AsstSeeds.ContainsKey(id)
                                                     || SeededItems.ContainsKey(id);

        /// <summary>Which plan item holds the row's lines once worked out.</summary>
        public static string Holder(string id) =>
            IsJev(id) || EngineProposes(id) ? id
            : AsstSeeds.ContainsKey(id) || SeededItems.ContainsKey(id) ? id
            : id == "words/model" || id == "datetable/model" ? id
            : "asst/" + ScriptedKind[id];

        private static readonly MethodInfo ProposalFamilyMethod =
            typeof(LocalEngine).GetMethod("ProposalFamily", BindingFlags.NonPublic | BindingFlags.Static);

        public static string ProposalFamilyOf(ReadinessFinding f) => (string)ProposalFamilyMethod.Invoke(null, new object[] { f });

        /// <summary>The seeds an assistant or a person would put on the plan to do a scripted row, worked out
        /// from the model and the findings as they are now. Deterministic text, so two orders can compare.</summary>
        public static async Task<List<(string Ref, string Kind, string After)>> ScriptedSeedsAsync(DecisionFixture f, string id)
        {
            var card = await f.Engine.AiReadinessScanAsync();
            var findings = (card?.Findings ?? Array.Empty<ReadinessFinding>()).Where(x => !x.Waived).ToArray();
            var seeds = new List<(string, string, string)>();
            if (AsstSeeds.TryGetValue(id, out var seeded))
            {
                // The assistant does not re-propose a line for an object that is gone.
                var live = await f.Sessions.Current.ReadAsync(m => seeded.Where(x => ObjectRefs.Resolve(m, x.Ref) != null).ToArray());
                foreach (var (r, a) in live) seeds.Add((r, id.Substring("asst/".Length), a));
                return seeds;
            }
            if (id == "datetable/model")
            {
                seeds.Add(("table:Date", "mark_date_table", "Date"));
                return seeds;
            }
            if (id == "words/model")
            {
                foreach (var x in findings
                    .Where(x => x.Fix == nameof(FixKind.AiContent))
                    .Where(x => (x.Category ?? "").IndexOf("Description", StringComparison.OrdinalIgnoreCase) >= 0
                             || (x.RuleId ?? "").StartsWith("DESC-", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.ObjectRef).Distinct(StringComparer.OrdinalIgnoreCase))
                    seeds.Add((x, "set_description", "Describes " + x + "."));
                return seeds;
            }
            var family = id.Substring("proposal/".Length);
            var refs = findings
                .Where(x => x.Fix == nameof(FixKind.Proposal))
                .Where(x => ProposalFamilyOf(x) == family)
                .Select(x => x.ObjectRef)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToArray();
            foreach (var r in refs)
            {
                if (!r.StartsWith("column:", StringComparison.Ordinal)) continue;
                var rest = r.Substring("column:".Length);
                var table = rest.Substring(0, rest.IndexOf('/'));
                var col = rest.Substring(rest.IndexOf('/') + 1);
                switch (family)
                {
                    case "collisions": seeds.Add((r, "set_column_hidden", "true")); break;
                    case "formats": seeds.Add((r, "set_format_string", "#,0")); break;
                    case "implicit-measures":
                        seeds.Add((r, "set_summarize_by", "None"));
                        break;
                }
            }
            return seeds;
        }

        /// <summary>Work the row's lines out on the live copy (propose for the engine's families, the scripted
        /// seeds through add_plan_item for the rest) and read them back off the holder item's change set.
        /// Returns null lines when the row is no longer on the plan.</summary>
        public static async Task<(bool OnPlan, CommutationLine[] Lines)> WorkOutAsync(DecisionFixture f, string id)
        {
            var plan = await SettledPlanAsync(f);
            if (IsJev(id))
            {
                var row = plan.Items.FirstOrDefault(i => i.Id == id);
                return row?.ChangeSet == null || row.Status == ModelPlanStatuses.Done
                    ? (false, Array.Empty<CommutationLine>())
                    : (true, row.ChangeSet.Lines.Select(CommutationLine.Of).ToArray());
            }
            var item = plan.Items.FirstOrDefault(i => i.Id == id);
            // An assistant row exists only while its lines do, so after a cancel it is worked out again by re-adding
            // the same lines rather than by looking for the row.
            if (!AsstSeeds.ContainsKey(id) && !SeededItems.ContainsKey(id) && (item == null || item.Status == ModelPlanStatuses.Done))
                return (false, Array.Empty<CommutationLine>());
            if (EngineProposes(id))
            {
                var r = await f.Engine.ModelPlanActionAsync(id, "propose", "human");
                var set = r.Plan?.Items.FirstOrDefault(i => i.Id == id)?.ChangeSet;
                return (true, (set?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Select(CommutationLine.Of).ToArray());
            }
            if (SeededItems.ContainsKey(id)) await SeedItemsAsync(f, id);
            else foreach (var (objRef, kind, after) in await ScriptedSeedsAsync(f, id))
                await f.Engine.AddPlanItemAsync(objRef, kind, after, null, null, null, "agent");
            plan = await f.Engine.GetModelPlanAsync();
            var holder = plan.Items.FirstOrDefault(i => i.Id == Holder(id));
            return (true, (holder?.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Select(CommutationLine.Of).ToArray());
        }

        /// <summary>The plan once any Jev run for this revision has landed. A fixture with no Jev live has no run, so
        /// this is one plain read plus a wait that returns at once.</summary>
        public static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        public static async Task<ModelPlanChangeSet> SetOfAsync(DecisionFixture f, string id)
        {
            var plan = await f.Engine.GetModelPlanAsync();
            return plan.Items.FirstOrDefault(i => i.Id == Holder(id))?.ChangeSet;
        }

        /// <summary>Keep every line (cleanup arrives dropped; a person doing the row keeps them), then approve
        /// with the digest read. Null on success, the refusal sentence otherwise.</summary>
        public static async Task<string> KeepAllAsync(DecisionFixture f, string id)
        {
            var set = await SetOfAsync(f, id);
            if (set == null) return null;
            foreach (var l in set.Lines.Where(l => !l.Kept && !string.IsNullOrWhiteSpace(l.PlanItemId)))
                await f.Engine.SetPlanItemAsync(l.PlanItemId, null, true, "human");
            return (await SetOfAsync(f, id))?.Digest;
        }

        public static async Task<string> ApproveAsync(DecisionFixture f, string id)
        {
            var set = await SetOfAsync(f, id);
            if (set == null) return "no change set on " + Holder(id);
            await KeepAllAsync(f, id);
            set = await SetOfAsync(f, id);
            var r = await f.Engine.ModelPlanActionAsync(Holder(id), "approve", "human", set.Digest);
            return r.Performed ? null : (r.Sentence ?? "not performed");
        }

        public static async Task CancelAsync(DecisionFixture f, string id)
        {
            if (IsJev(id)) return;
            var set = await SetOfAsync(f, id);
            if (set != null) await f.Engine.ModelPlanActionAsync(Holder(id), "cancel", "human");
        }

        // ---- the footprint: the engine's ------------------------------------------------------------------

        /// <summary>The engine's footprint for a row, over the lines that row's work put on the plan (on the
        /// holder item when the lines file under an assistant row). Taken with every row worked out at one
        /// revision, exactly as the spike did.</summary>
        public static async Task<Footprint> EngineFootprintAsync(DecisionFixture f, string id)
        {
            var set = await SetOfAsync(f, id);
            var (reads, writes, known) = await f.Engine.FootprintForTestAsync(id, set?.Lines ?? Array.Empty<ModelPlanChangeLine>());
            var fp = new Footprint().ReadAll(reads).WriteAll(writes);
            fp.Known = known;
            return fp;
        }

        public static string Why(Footprint a, Footprint b)
        {
            var parts = new List<string>();
            var ab = a.Writes.Intersect(b.Reads, StringComparer.OrdinalIgnoreCase).ToArray();
            var ba = b.Writes.Intersect(a.Reads, StringComparer.OrdinalIgnoreCase).ToArray();
            var ww = a.Writes.Intersect(b.Writes, StringComparer.OrdinalIgnoreCase).Where(k => !PlanFootprint.EventFacets.Contains(k)).ToArray();
            if (ab.Length > 0) parts.Add("A writes what B reads: " + string.Join(", ", ab.Take(3)));
            if (ba.Length > 0) parts.Add("B writes what A reads: " + string.Join(", ", ba.Take(3)));
            if (ww.Length > 0) parts.Add("both write: " + string.Join(", ", ww.Take(3)));
            if (!a.Known || !b.Known) parts.Add("footprint unknown");
            return string.Join("; ", parts);
        }
    }

    /// <summary>Jev for the commutation gate: answers every ai.schema request, whatever its digest (a request's snapshot
    /// changes when its table changes), from a fixed choice per (table, object). Deterministic, so both orders of a
    /// pair hear the same answers. Every other point is told nothing.</summary>
    internal sealed class CommutationJevOracle : Semanticus.Decisions.IDecisionOracle
    {
        private readonly Func<string, string, (string Choice, double Confidence)> _pick;
        public CommutationJevOracle(Func<string, string, (string, double)> pick) { _pick = pick; }
        public bool Available => true;
        public Task<Semanticus.Decisions.DecisionAnswers> AskAsync(Semanticus.Decisions.DecisionRequest request, System.Threading.CancellationToken ct)
        {
            if (request.PointName != Semanticus.Decisions.DecisionRegistry.AiSchema)
                return Task.FromResult(Semanticus.Decisions.DecisionAnswers.NotDecided(request, "not asked in this fixture"));
            if (request.PerQuestion)
            {
                // A table at a time (the four Plan points ask so as shipped, m11/jev-batch): one question per object, named
                // "ai_schema:<object ref>", answered by the object's own (table, name), exactly as a per-object request is.
                var batched = new Dictionary<string, object>();
                foreach (var q in request.Questions)
                {
                    var at = q.Name.IndexOf(':');
                    if (at < 0) continue;
                    var objectRef = q.Name.Substring(at + 1);
                    var bare = objectRef.Substring(objectRef.IndexOf(':') + 1);
                    var slash = bare.IndexOf('/');
                    var (pickedChoice, pickedConfidence) = slash < 0 ? _pick(bare, bare) : _pick(bare.Substring(0, slash), bare.Substring(slash + 1));
                    batched[q.Name] = new Dictionary<string, object> { ["type"] = "choice", ["choice"] = pickedChoice, ["confidence"] = pickedConfidence };
                }
                return Task.FromResult(Semanticus.Decisions.DecisionAnswerReader.Read(request, JsonSerializer.SerializeToElement(batched), "jev-test-1"));
            }
            var table = request.Snapshot.TryGetValue("table_name", out var t) ? t?.ToString() : null;
            var name = request.Snapshot.TryGetValue("object_name", out var n) ? n?.ToString() : null;
            var (choice, confidence) = _pick(table, name);
            var json = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["ai_schema"] = new Dictionary<string, object> { ["type"] = "choice", ["choice"] = choice, ["confidence"] = confidence },
            });
            return Task.FromResult(Semanticus.Decisions.DecisionAnswerReader.Read(request, json, "jev-test-1"));
        }
    }

    internal sealed class PairOutcome
    {
        public string Fixture, A, B;
        public bool Independent;
        public string Why;
        public bool Check1, Check1Raw, Check2, Check3;
        public string Check1Detail, Check2Detail, Check3Detail, RunError;
        public string FamA => CommutationRows.Family(A);
        public string FamB => CommutationRows.Family(B);
    }
}

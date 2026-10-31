using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;

namespace Semanticus.Engine;

public sealed partial class LocalEngine
{
    private async Task<TestDefinition[]> MarkDefinitionsAsync(MarkRun run, TestSuiteInfo saved)
    {
        var formula = run.After.Formulas[run.Reference];
        var identities = TestObjectIdentityStore.Load(TestsDirFor(run.Session));
        var snapshots = await run.Session.ReadAsync(TestObjectIdentityStore.Capture).ConfigureAwait(false);
        bool Matches(TestDefinition definition)
        {
            if (!definition.Enabled) return false;
            if (!string.IsNullOrEmpty(definition.TargetTag)) return definition.TargetTag == formula.Tag;
            if (!string.IsNullOrEmpty(definition.TargetIdentity))
                return identities.Resolve(definition.TargetIdentity, snapshots)?.Ref == run.Reference;
            return string.Equals(definition.TargetRef, run.Reference, StringComparison.OrdinalIgnoreCase);
        }
        return saved.Definitions.Where(Matches).ToArray();
    }

    private Task<ReconcileOutcome> TryMarkDefinitionAsync(TestDefinition definition, MarkRun run)
    {
        if (string.Equals(definition.Kind, TestKinds.MeasureValue, StringComparison.OrdinalIgnoreCase))
        {
            var request = TestSuiteStore.Deserialize<MeasureValueRequest>(definition.ParamsJson);
            if (!string.IsNullOrWhiteSpace(request?.ExpressionDax))
            {
                if (string.Equals(request.ExpressionDax.Trim(), run.After.Formulas[run.Reference].Body?.Trim(), StringComparison.Ordinal))
                {
                    // A saved formula that still matches is checked through its current measure identity.
                    var current = TestSuiteStore.Deserialize<TestDefinition>(TestSuiteStore.Serialize(definition));
                    request.ExpressionDax = null;
                    current.ParamsJson = TestSuiteStore.Serialize(request);
                    return TryTestAsync(current, "agent");
                }
                return Task.FromResult(new ReconcileOutcome
                {
                    Verdict = Verdict.NotVerifiable, Expected = request.ExpectedValue,
                    Message = "The saved check uses a separate formula; add a number for this measure."
                });
            }
        }
        return TryTestAsync(definition, "agent");
    }

    // fx31 (rc30 tester gap 1): the checks of a measure run on the connected model. When a part of the model outside the
    // measures (a column's source or type, a calculated column or table, a partition, a relationship, a calculation group, a
    // function, calendar) differs from what the connected model has, a measure over that part cannot be checked there. The hold follows
    // the model as it is now, never the history: an Undo or Redo back to the connected model's structure ends it.
    internal const string StructureHoldReason = "Publish this change to check it.";

    private sealed class StructureHold
    {
        internal bool All;
        internal readonly HashSet<string> Measures = new(StringComparer.Ordinal);
        internal bool Covers(string reference) => All || Measures.Contains(reference);
    }

    private readonly record struct StructurePart(string Table, string Shape, string Calendar = null);

    /// <summary>What the checks read outside the measures, by object ref. Must run on the dispatcher thread.</summary>
    private static Dictionary<string, StructurePart> MarkStructure(Model model)
    {
        var parts = new Dictionary<string, StructurePart>(StringComparer.Ordinal);
        foreach (var table in model.Tables)
        {
            parts["table:" + table.Name] = new(table.Name, table.GetType().Name + "|" + (table as CalculatedTable)?.Expression);
            foreach (var column in table.Columns)
                parts["column:" + table.Name + "/" + column.Name] = new(table.Name, column.GetType().Name + "|" + column.DataType
                    + "|" + (column as CalculatedColumn)?.Expression + "|" + (column as DataColumn)?.SourceColumn);
            foreach (var partition in table.Partitions)
                parts["partition:" + table.Name + "/" + partition.Name] = new(table.Name, partition.Expression);
            foreach (var calendar in CalendarOps.Raw(table).Calendars)
                parts["calendar:" + table.Name + "/" + calendar.Name] = new(table.Name,
                    ModelCompare.CalendarState(calendar), calendar.Name);
            if (table is CalculationGroupTable group)
            {
                parts["calcgroup:" + table.Name] = new(table.Name, group.CalculationGroupPrecedence.ToString());
                foreach (var item in group.CalculationItems)
                    parts["calcitem:" + table.Name + "/" + item.Name] = new(table.Name, item.Ordinal + "|" + item.Expression + "|" + item.FormatStringExpression);
            }
        }
        foreach (var relationship in model.Relationships.OfType<SingleColumnRelationship>())
            parts["relationship:" + relationship.Name] = new(null, ObjectRefs.For(relationship.FromColumn) + "|" + ObjectRefs.For(relationship.ToColumn)
                + "|" + relationship.IsActive + "|" + relationship.CrossFilteringBehavior + "|" + relationship.FromCardinality + "|" + relationship.ToCardinality);
        foreach (var function in model.Functions)
            parts["function:" + function.Name] = new(null, function.Expression);
        return parts;
    }

    /// <summary>The measures a structure difference touches, through Lineage, or null when the structure is the same. A part that
    /// is gone leaves no lineage to follow: its table stands for it, or every measure when its table is gone too. A relationship
    /// or a calculation group can reach any measure.</summary>
    private static StructureHold StructureHoldFor(IReadOnlyDictionary<string, StructurePart> baseline, Model model)
    {
        var now = MarkStructure(model);
        var differs = baseline.Keys.Union(now.Keys).Where(k => baseline.GetValueOrDefault(k) != now.GetValueOrDefault(k)).ToArray();
        if (differs.Length == 0) return null;
        var hold = new StructureHold();
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in differs)
        {
            var kind = key[..key.IndexOf(':')];
            var part = now.TryGetValue(key, out var current) ? current : baseline[key];
            var table = part.Table;
            if (kind == "calendar")
            {
                // Raw calendar edits have no resolvable ObjectRef. Read their DAX references, including a deleted
                // calendar's name, then follow ordinary lineage through measures, functions and calculated objects.
                foreach (var reference in CalendarReaders(model, part.Calendar))
                {
                    roots.Add(reference);
                    if (reference.StartsWith("measure:", StringComparison.Ordinal)) hold.Measures.Add(reference);
                }
                continue;
            }
            if (kind is "relationship" or "calcgroup" or "calcitem" || kind == "function" && !now.ContainsKey(key)) { hold.All = true; return hold; }
            if (kind == "partition" || !now.ContainsKey(key))
            {
                if (table == null || !now.ContainsKey("table:" + table)) { hold.All = true; return hold; }
                roots.Add("table:" + table);
            }
            else roots.Add(key);
        }
        foreach (var node in Lineage.LineageGraph.ImpactFrom(model, roots).Where(n => n.Kind == "measure")) hold.Measures.Add(node.Ref);
        foreach (var root in roots.Where(r => r.StartsWith("table:", StringComparison.Ordinal)))
            foreach (var measure in model.Tables[root[6..]].Measures) hold.Measures.Add(ObjectRefs.For(measure));
        return hold;
    }

    private static IEnumerable<string> CalendarReaders(Model model, string name)
    {
        var expressions = model.AllMeasures.Select(m => (Ref: ObjectRefs.For(m), Body: m.Expression))
            .Concat(model.Functions.Select(f => (Ref: ObjectRefs.For(f), Body: f.Expression)))
            .Concat(model.AllColumns.OfType<CalculatedColumn>().Select(c => (Ref: ObjectRefs.For(c), Body: c.Expression)))
            .Concat(model.Tables.OfType<CalculatedTable>().Select(t => (Ref: ObjectRefs.For(t), Body: t.Expression)));
        foreach (var expression in expressions)
        {
            var tokens = DaxScan.Tokenize(expression.Body ?? "");
            if (tokens.Where((t, i) => !(i + 1 < tokens.Count && (tokens[i + 1].Delim == '[' || tokens[i + 1].Kind == DaxScan.Kind.LParen)))
                .Any(t => string.Equals(t.Kind == DaxScan.Kind.Word ? t.Text : t.Kind == DaxScan.Kind.Name && t.Delim == '\'' ? t.Inner : null,
                    name, StringComparison.OrdinalIgnoreCase)))
                yield return expression.Ref;
        }
    }

    /// <summary>A mark of a measure the hold covers, as a run stores it: the last result, unchanged, so Undo back finds it. Every
    /// door reads it as Unknown with the reason while the hold lasts (HeldView, fx33). With no result before, Filters, Contexts
    /// and Performance store the reason itself, which no door shows once the hold ends; any other mark is null and the run leaves
    /// it, so it is not shown.</summary>
    private MeasureMark HeldMark(string reference, string name)
    {
        MeasureMark previous;
        lock (measureRunLock) previous = measureCheckCache.GetValueOrDefault(reference)?.Marks.FirstOrDefault(m => m.Name == name);
        if (previous == null || MeasureCheckVerdict.InProgress(previous))
            return name is "Filters" or "Contexts" or "Performance" ? Coded(MeasureMarkJudge.NotRun(name, StructureHoldReason), "held") : null;
        var held = Copy(previous);
        held.Pending = false; held.Progress = null;
        return held;
    }

    // fx33 (rc32 tester gap 1): a held measure's numbers can move once the change is published, so no mark of it is a pass.
    // Unknown is never a pass (spec R8, section 3): each mark that applies reads Unknown with the reason, Totals too.
    private static readonly string[] HeldMarks = { "Totals", "Filters", "SQL", "Trusted number", "Equivalence", "Contexts", "Performance" };

    /// <summary>A finished mark as the doors read it. <paramref name="held"/>: the hold covers its measure; an applying mark with a
    /// verdict reads Unknown with the reason, and keeps when it last ran. Not held: a reason stored for a hold no door shows.</summary>
    private static MeasureMark HeldView(MeasureMark mark, bool held)
    {
        if (!HeldMarks.Contains(mark.Name)) return mark;
        if (!held) return mark.ReasonCode == "held" ? MeasureCheckVerdict.Finish(Waiting(mark.Name), null, false) : mark;
        if (!mark.Applies || mark.Verdict == null) return mark;
        mark.State = "not run"; mark.Sentence = StructureHoldReason; mark.ReasonCode = "held"; mark.Verdict = "unknown";
        mark.Action = MeasureMarkJudge.ActionFor(mark.Name);
        mark.Cells = Array.Empty<MeasureMarkCell>(); mark.Total = null; mark.Timings = Array.Empty<MeasureTimingDetail>(); mark.ActionCodes = Array.Empty<string>();
        return mark;
    }

    /// <summary>The structure hold changes: every measure it covered or covers now is pushed as it reads now, so the tree and the
    /// pages show the hold at once and Undo back brings the last marks back at once (fx33). Called under marksLock.</summary>
    private void SetStructureHold(Session session, StructureHold hold)
    {
        var old = marksStructureHold;
        marksStructureHold = hold;
        if (old == null && hold == null) return;
        lock (measureRunLock)
        {
            if (session == null || _sessions.Current != session || !measureCacheLoaded || DryRunScope.Current != null) return;
            var states = measureCheckCache.Values.Where(s => old?.Covers(s.MeasureRef) == true || hold?.Covers(s.MeasureRef) == true).ToArray()
                .Select(s => { var updated = s with { Version = ++checkVersion }; measureCheckCache[s.MeasureRef] = updated; return FinishCheckState(updated); })
                .ToArray();
            if (states.Length > 0) PublishCheckStates(session, states);
        }
    }
}

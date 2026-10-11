// The composer's change group runs as ONE MutateAsync (composer contract 3.1 and 3.2).
//
// WHY ONE BATCH AND NOT ONE CALL PER STEP. MutateAsync takes a synchronous Action<Model> and makes it one undo
// step (Session.cs MutateAsync); a throw inside it rolls the whole batch back and puts the redo stack back.
// Running each change step through the job executor would make each step its own MutateAsync, and "one Undo"
// would be false. So the whole change group is one executor call, the pseudo-step `change`, and every line in
// it goes through ApplyOneItem, the same applier the Plan uses, so the doors never disagree about what a change
// was.
//
// THE HOOKS OUTSIDE THE BATCH. An op runs three things around its own MutateAsync, and a composed write must
// run the same three or it would be a way round them:
//   before: binding enforcement (EnforceBindingAsync), so a project that requires a workflow for new measures
//           refuses a composed new measure exactly as it refuses the bare op, unless the change IS a run of that
//           workflow (ComposedChange.Workflow);
//   inside: the DAX write rule, which the cores run against the model as the batch has left it;
//   after:  the Verified Edits record for each DAX write (RecordVerifiedModeEditAsync) and the drift baseline
//           for each measure write (RecordWorkflowAuthoredMeasureAsync).
// The live before-checks run first, outside the batch, because they are async and the batch is not.
//
// THE CAUSE (plan-heart contract 8.4). A change group of a draft opens the `draft` cause scope around its one
// MutateAsync, so the commit is Semanticus's, names the draft, and History nests it under the draft. A group
// with no draft (a direct assistant step, contract 3.4) opens none and keeps whatever scope its caller opened:
// a guided step's submission has already opened the workflow run's.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Jev;

namespace Semanticus.Engine.Composer
{
    /// <summary>The ops a composed step may name, and what each becomes. The executable allowlist itself is
    /// lane L1's (OpParameterKinds.Executable); <see cref="Kinds"/> is the applier kind name for each op, which
    /// L1's completeness test checks the allowlist against.</summary>
    public static class ComposedOps
    {
        /// <summary>op -> the ApplyOneItem kind its lines use.</summary>
        public static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["create_import_table"] = "create_import_table",
            ["create_column"] = "create_column",
            ["create_relationship"] = "create_relationship",
            ["set_relationship_crossfilter"] = "set_relationship_crossfilter",
            ["set_relationship_cardinality"] = "set_relationship_cardinality",
            ["set_relationship_active"] = "set_relationship_active",
            ["set_column_data_type"] = "set_data_type",
            ["set_column_hidden"] = "set_column_hidden",
            ["set_summarize_by"] = "set_summarize_by",
            ["set_data_category"] = "set_data_category",
            ["set_sort_by_column"] = "set_sort_by_column",
            ["create_measure"] = "create_measure",
            ["update_measure"] = "set_dax",
            ["set_measure_format"] = "set_measure_format",
            ["set_display_folder"] = "set_display_folder",
            ["set_description"] = "set_description",
            ["set_synonyms"] = "set_synonyms",
            ["rename_object"] = "rename",
            ["mark_date_table"] = "mark_date_table",
            ["create_calculation_group"] = "create_calculation_group",
            ["create_calculation_item"] = "create_calculation_item",
            ["create_role"] = "create_role",
            ["set_table_permission"] = "set_table_permission",
            // The generators (contract 2.3). starter_measure is a template over create_measure, not an op of its
            // own: it compiles to a create_measure line whose formula the template wrote.
            ["starter_measure"] = "create_measure",
            ["generate_date_table"] = "generate_date_table",
            ["generate_time_intelligence"] = "generate_time_intelligence",
        };

        /// <summary>The binding the op enforces today, by the name its op passes to EnforceBindingAsync, or null
        /// for an op that is not bindable. Kept to exactly the ops that bind today: a composed write must be
        /// refused where the bare op is refused, and nowhere else.</summary>
        internal static string BoundOp(string op) => op switch
        {
            "create_measure" => "create_measure",
            "starter_measure" => "create_measure",
            "update_measure" => "update_measure",
            "create_relationship" => "create_relationship",
            "create_calculation_item" => "create_calculation_item",
            _ => null,
        };

        /// <summary>The ops that set who can see which data. Composer contract 5.4 holds every one of them for the
        /// person's own Approve and run, however its filter was supplied, so a change carrying one applies only when
        /// that approval covers it (<see cref="ComposedChange.PersonApproved"/>). A direct assistant step has no such
        /// approval and cannot carry one at all (Sol 6, slice 8, finding 1).</summary>
        internal static bool HeldForPerson(string op) => string.Equals((op ?? "").Trim(), "set_table_permission", StringComparison.Ordinal);

        /// <summary>The Verified Edits op name each DAX-writing op records under today, or null.</summary>
        internal static string VerifiedOp(string op) => op switch
        {
            "create_measure" => "create_measure",
            "starter_measure" => "create_measure",
            "update_measure" => "set_dax",
            "create_calculation_item" => "create_calculation_item",
            _ => null,
        };

        /// <summary>The op name the drift baseline records a measure write under today, or null.</summary>
        internal static string DriftOp(string op) => op switch
        {
            "create_measure" => "create_measure",
            "starter_measure" => "create_measure",
            "update_measure" => "update_measure",
            _ => null,
        };
    }

    /// <summary>A step that could not be turned into change lines: an op the engine cannot run, a missing
    /// argument, a value still waiting on an input. The message is one plain sentence.</summary>
    public sealed class ComposeException : InvalidOperationException
    {
        public int StepNumber { get; }
        public ComposeException(int stepNumber, string message) : base(message) => StepNumber = stepNumber;
    }

    internal enum BlankFill
    {
        /// <summary>A real run: an open blank means the change is not ready.</summary>
        Require,
        /// <summary>A rehearsal: an open blank stands in as a placeholder so its objects exist (contract 7).</summary>
        Placeholder,
    }

    internal sealed class CompiledLine
    {
        public string Op { get; set; }
        public ChangeItem Item { get; set; }
        /// <summary>For a DAX write: the object it lands on and the text written, for the post-hooks.</summary>
        public string DaxRef { get; set; }
        public string Dax { get; set; }
    }

    internal sealed class CompiledStep
    {
        public ComposedStep Step { get; set; }
        public int Number { get; set; }
        public List<CompiledLine> Lines { get; } = new List<CompiledLine>();
    }

    internal sealed class CompiledChange
    {
        public List<CompiledStep> Steps { get; } = new List<CompiledStep>();
        /// <summary>(step number, blank name) of every blank still open, in a Require compile.</summary>
        public List<(int Step, string Blank)> OpenBlanks { get; } = new List<(int, string)>();
        /// <summary>set_column_data_type lines folded into their column's creation (contract 3.3).</summary>
        public int FoldedTypes { get; set; }
    }

    /// <summary>Turns a change group into ChangeItem lines for ApplyOneItem.</summary>
    internal static class ComposedChangeCompiler
    {
        /// <summary>The reserved stand-in for an open name blank. The rehearsal needs SOME name so the object
        /// exists; no check ever reports it (contract 7).</summary>
        internal const string NamePlaceholderPrefix = "__semanticus_blank_";

        internal static CompiledChange Compile(ComposedChange change, BlankFill fill,
                                               string overrideStepId = null, string overrideBlank = null, string overrideText = null)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            var result = new CompiledChange();
            var steps = change.Steps ?? new List<ComposedStep>();
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var number = i + 1;
                var cs = new CompiledStep { Step = step, Number = number };

                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var b in step.Blanks ?? new List<ComposedBlank>())
                {
                    if (string.IsNullOrWhiteSpace(b?.Name)) throw new ComposeException(number, $"Step {number} has a blank with no name.");
                    var text = string.Equals(step.Id, overrideStepId, StringComparison.Ordinal)
                               && string.Equals(b.Name, overrideBlank, StringComparison.Ordinal)
                        ? overrideText : b.Text;
                    if (text == null)
                    {
                        if (fill == BlankFill.Require) { result.OpenBlanks.Add((number, b.Name)); text = PlaceholderFor(b); }
                        else text = PlaceholderFor(b);
                    }
                    values[b.Name] = text;
                }

                foreach (var action in step.Actions ?? new List<ComposedAction>())
                {
                    var args = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var kv in action.Args ?? new Dictionary<string, string>())
                        args[kv.Key] = Fill(kv.Value, values, number);
                    foreach (var line in Lines(action.Op, args, number)) cs.Lines.Add(line);
                }
                result.Steps.Add(cs);
            }
            FoldTypesIntoCreatedColumns(result);
            return result;
        }

        internal static string PlaceholderFor(ComposedBlank b) => (b.Kind ?? "").Trim().ToLowerInvariant() switch
        {
            "dax" => "BLANK()",
            "name" => NamePlaceholderPrefix + b.Name,
            "choice" => b.Choices?.FirstOrDefault() ?? "",
            _ => "",   // description and m: empty is what the op itself accepts before content arrives
        };

        private static readonly System.Text.RegularExpressions.Regex AnyToken =
            new System.Text.RegularExpressions.Regex(@"\{\{[^}]*\}\}");
        private static readonly System.Text.RegularExpressions.Regex BlankToken =
            new System.Text.RegularExpressions.Regex(@"\{\{\s*blanks\.([A-Za-z0-9_\-]+)\s*\}\}");

        private static string Fill(string value, Dictionary<string, string> blanks, int number)
        {
            if (value == null) return null;
            // Inputs, questions and earlier step results are resolved by the draft before a change reaches here,
            // so any token left in an ARGUMENT that is not a blank is a value nobody filled. Checked on the
            // argument before the blanks go in, so a blank's own text may quote braces freely.
            foreach (System.Text.RegularExpressions.Match m in AnyToken.Matches(value))
                if (!BlankToken.IsMatch(m.Value))
                    throw new ComposeException(number, $"Step {number} still has a value to fill in: {m.Value}.");
            return BlankToken.Replace(value, m =>
                blanks.TryGetValue(m.Groups[1].Value, out var t)
                    ? t
                    : throw new ComposeException(number, $"Step {number} uses a blank named {m.Groups[1].Value} that the step does not have."));
        }

        private static string Req(Dictionary<string, string> args, string key, string op, int number) =>
            args.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
                ? v
                : throw new ComposeException(number, $"Step {number} needs {key} for {op}.");

        private static string Opt(Dictionary<string, string> args, string key) => args.TryGetValue(key, out var v) ? v : null;

        private static string TableName(string tableRef) =>
            tableRef != null && tableRef.StartsWith("table:", StringComparison.OrdinalIgnoreCase) ? tableRef.Substring("table:".Length) : tableRef;

        private static string RelRef(string name) =>
            name != null && name.StartsWith("relationship:", StringComparison.OrdinalIgnoreCase) ? name : "relationship:" + name;

        private static CompiledLine Line(string op, string kind, string objectRef, string after, int number,
                                         Dictionary<string, string> args = null, string title = null) =>
            new CompiledLine
            {
                Op = op,
                Item = new ChangeItem
                {
                    Id = $"composed-{number}-{op}",
                    Kind = kind,
                    ObjectRef = objectRef,
                    After = after,
                    Args = args,
                    Source = "composer",
                    Status = "approved",
                    Title = title,
                },
            };

        private static IEnumerable<CompiledLine> Lines(string op, Dictionary<string, string> a, int n)
        {
            op = (op ?? "").Trim();
            if (!ComposedOps.Kinds.TryGetValue(op, out var kind))
                throw new ComposeException(n, $"Step {n} uses {(op.Length == 0 ? "no op" : op)}, which Semanticus cannot run itself. Only a step your assistant does can use it.");
            switch (op)
            {
                case "create_import_table":
                {
                    var name = Req(a, "name", op, n);
                    yield return Line(op, kind, "table:" + name, Opt(a, "mExpression"), n, new Dictionary<string, string> { ["name"] = name });
                    break;
                }
                case "create_column":
                {
                    var table = TableName(Req(a, "tableRef", op, n));
                    var name = Req(a, "name", op, n);
                    yield return Line(op, kind, $"column:{table}/{name}", null, n, new Dictionary<string, string>
                    {
                        ["tableRef"] = "table:" + table, ["name"] = name,
                        ["dataType"] = Opt(a, "dataType") ?? "String", ["sourceColumn"] = Opt(a, "sourceColumn"),
                    });
                    break;
                }
                case "create_relationship":
                    yield return Line(op, kind, Req(a, "fromColumnRef", op, n), null, n, new Dictionary<string, string>
                    {
                        ["fromColumnRef"] = Req(a, "fromColumnRef", op, n), ["toColumnRef"] = Req(a, "toColumnRef", op, n),
                        ["crossFilter"] = Opt(a, "crossFilter"), ["isActive"] = Opt(a, "isActive"), ["cardinality"] = Opt(a, "cardinality"),
                    });
                    break;
                case "set_relationship_crossfilter":
                    yield return Line(op, kind, RelRef(Req(a, "relationshipName", op, n)), Req(a, "crossFilteringBehavior", op, n), n);
                    break;
                case "set_relationship_active":
                    yield return Line(op, kind, RelRef(Req(a, "relationshipName", op, n)), Req(a, "isActive", op, n), n);
                    break;
                case "set_relationship_cardinality":
                    yield return Line(op, kind, RelRef(Req(a, "relationshipName", op, n)), null, n, new Dictionary<string, string>
                    {
                        ["fromCardinality"] = Opt(a, "fromCardinality"), ["toCardinality"] = Opt(a, "toCardinality"),
                    });
                    break;
                case "set_column_data_type":
                    yield return Line(op, kind, Req(a, "columnRef", op, n), Req(a, "dataType", op, n), n);
                    break;
                case "set_column_hidden":
                    yield return Line(op, kind, Req(a, "objRef", op, n), Req(a, "hidden", op, n), n);
                    break;
                case "set_summarize_by":
                    yield return Line(op, kind, Req(a, "objRef", op, n), Req(a, "summarizeBy", op, n), n);
                    break;
                case "set_data_category":
                    yield return Line(op, kind, Req(a, "objRef", op, n), Opt(a, "dataCategory") ?? "", n);
                    break;
                case "set_sort_by_column":
                    yield return Line(op, kind, Req(a, "objRef", op, n), Opt(a, "sortByColumnName") ?? "", n);
                    break;
                case "create_measure":
                {
                    var table = TableName(Req(a, "tableRef", op, n));
                    var name = Req(a, "name", op, n);
                    var expr = Opt(a, "expression") ?? "";
                    var line = Line(op, kind, $"measure:{table}/{name}", expr, n, new Dictionary<string, string> { ["displayFolder"] = Opt(a, "displayFolder") });
                    line.DaxRef = line.Item.ObjectRef; line.Dax = expr;
                    yield return line;
                    break;
                }
                case "starter_measure":
                {
                    // Contract 2.3: the engine writes the formula from a fixed template; no blank reaches anyone.
                    var table = TableName(Req(a, "tableRef", op, n));
                    var column = Req(a, "column", op, n);
                    var option = Req(a, "option", op, n).Trim().ToLowerInvariant();
                    if (!StarterMeasureTemplates.MeasureOptions.Contains(option, StringComparer.Ordinal))
                        throw new ComposeException(n, $"Step {n} asks for a starter measure of kind {option}, which is not one Semanticus writes. The kinds are {string.Join(", ", StarterMeasureTemplates.MeasureOptions)}.");
                    var name = Opt(a, "name");
                    if (string.IsNullOrWhiteSpace(name)) name = StarterMeasureTemplates.Name(option, column);
                    var expr = StarterMeasureTemplates.Dax(option, table, column);
                    var line = Line(op, kind, $"measure:{table}/{name}", expr, n, new Dictionary<string, string> { ["displayFolder"] = Opt(a, "displayFolder") });
                    line.DaxRef = line.Item.ObjectRef; line.Dax = expr;
                    yield return line;
                    var format = Opt(a, "formatString");
                    if (!string.IsNullOrWhiteSpace(format))
                        yield return Line("set_measure_format", "set_measure_format", $"measure:{table}/{name}", format, n);
                    break;
                }
                case "update_measure":
                {
                    var line = Line(op, kind, Req(a, "objRef", op, n), Req(a, "expression", op, n), n);
                    line.DaxRef = line.Item.ObjectRef; line.Dax = line.Item.After;
                    yield return line;
                    break;
                }
                case "set_measure_format":
                    yield return Line(op, kind, Req(a, "objRef", op, n), Req(a, "formatString", op, n), n);
                    break;
                case "set_display_folder":
                {
                    var refs = Opt(a, "refs") ?? Opt(a, "objRef");
                    var list = LocalEngine.ArgList(refs);
                    if (list.Length == 0) throw new ComposeException(n, $"Step {n} needs refs for {op}.");
                    foreach (var r in list) yield return Line(op, kind, r, Opt(a, "folder") ?? "", n);
                    break;
                }
                case "set_description":
                    yield return Line(op, kind, Req(a, "objRef", op, n), Opt(a, "text") ?? "", n);
                    break;
                case "set_synonyms":
                    if (!string.IsNullOrWhiteSpace(Opt(a, "culture")))
                        throw new ComposeException(n, $"Step {n} names a culture for its synonyms. A composed change writes synonyms to the model's own culture only.");
                    yield return Line(op, kind, Req(a, "objRef", op, n), string.Join(",", LocalEngine.ArgList(Opt(a, "terms"))), n);
                    break;
                case "rename_object":
                    yield return Line(op, kind, Req(a, "objRef", op, n), Req(a, "newName", op, n), n);
                    break;
                case "mark_date_table":
                    yield return Line(op, kind, Req(a, "tableRef", op, n), Opt(a, "dateColumn") ?? "", n);
                    break;
                case "create_calculation_group":
                {
                    var name = Req(a, "name", op, n);
                    yield return Line(op, kind, "table:" + name, null, n, new Dictionary<string, string> { ["name"] = name });
                    break;
                }
                case "create_calculation_item":
                {
                    var group = Req(a, "calcGroupRef", op, n);
                    var name = Req(a, "name", op, n);
                    var expr = Opt(a, "expression") ?? "";
                    var line = Line(op, kind, group, expr, n, new Dictionary<string, string> { ["calcGroupRef"] = group, ["name"] = name });
                    line.DaxRef = $"calcitem:{TableName(group)}/{name}"; line.Dax = expr;
                    yield return line;
                    break;
                }
                case "create_role":
                {
                    var name = Req(a, "name", op, n);
                    yield return Line(op, kind, "role:" + name, null, n, new Dictionary<string, string> { ["name"] = name, ["modelPermission"] = Opt(a, "modelPermission") });
                    break;
                }
                case "set_table_permission":
                    yield return Line(op, kind, Req(a, "tableRef", op, n), Opt(a, "filterDax") ?? "", n, new Dictionary<string, string>
                    {
                        ["roleName"] = Req(a, "roleName", op, n), ["tableRef"] = Req(a, "tableRef", op, n),
                    });
                    break;
                case "generate_date_table":
                {
                    var name = string.IsNullOrWhiteSpace(Opt(a, "tableName")) ? "Date" : Opt(a, "tableName").Trim();
                    yield return Line(op, kind, "table:" + name, null, n, new Dictionary<string, string>
                    {
                        ["tableName"] = name, ["startExpr"] = Opt(a, "startExpr"), ["endExpr"] = Opt(a, "endExpr"), ["markAsDate"] = Opt(a, "markAsDate"),
                    });
                    break;
                }
                case "generate_time_intelligence":
                    yield return Line(op, kind, Req(a, "baseMeasureRef", op, n), null, n, new Dictionary<string, string>
                    {
                        ["baseMeasureRef"] = Req(a, "baseMeasureRef", op, n), ["dateColumn"] = Req(a, "dateColumn", op, n),
                        ["variants"] = Opt(a, "variants"), ["displayFolder"] = Opt(a, "displayFolder"),
                    });
                    break;
                default:
                    throw new ComposeException(n, $"Step {n} uses {op}, which Semanticus cannot run itself. Only a step your assistant does can use it.");
            }
        }

        /// <summary>Contract 3.3: a column created by this change has no data, so the set_data_type conversion
        /// check (it needs data facts and refuses an unmeasured one) would refuse it. A type set on a column born
        /// in the same change therefore becomes that column's type at creation, and the line is dropped. The
        /// conversion kind applies only to columns that existed before the run.</summary>
        private static void FoldTypesIntoCreatedColumns(CompiledChange change)
        {
            var created = new Dictionary<string, CompiledLine>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in change.Steps)
            {
                for (var i = 0; i < step.Lines.Count; i++)
                {
                    var line = step.Lines[i];
                    if (line.Item.Kind == "create_column") { created[ColumnKey(line.Item.ObjectRef)] = line; continue; }
                    if (line.Item.Kind != "set_data_type") continue;
                    if (!created.TryGetValue(ColumnKey(line.Item.ObjectRef), out var born)) continue;
                    born.Item.Args["dataType"] = line.Item.After;
                    step.Lines.RemoveAt(i--);
                    change.FoldedTypes++;
                }
            }
        }

        private static string ColumnKey(string columnRef) =>
            (columnRef ?? "").StartsWith("column:", StringComparison.OrdinalIgnoreCase) ? columnRef.Substring("column:".Length) : columnRef ?? "";
    }

    /// <summary>
    /// The job executor for the `change` pseudo-step (contract 3.1). Prepare-phase reads and after steps run
    /// through <see cref="IJobStepExecutor"/> one call per step; the whole change group is ONE call here, and
    /// its result lists every step it applied.
    /// </summary>
    public sealed class ComposedChangeExecutor : IJobStepExecutor
    {
        /// <summary>The pseudo-step id and op name of the change group.</summary>
        public const string ChangeStep = "change";

        private readonly LocalEngine _engine;
        private readonly Func<IJob, ComposedChange> _changeFor;
        private readonly string _origin;

        /// <param name="changeFor">The change group for a job: the draft store (lane L4) supplies it.</param>
        /// <param name="origin">The door that started the run (human or agent).</param>
        public ComposedChangeExecutor(LocalEngine engine, Func<IJob, ComposedChange> changeFor, string origin)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _changeFor = changeFor ?? throw new ArgumentNullException(nameof(changeFor));
            _origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin;
        }

        public async Task<EngineStepResult> ExecuteAsync(IJob job, StepDefinition step, IReadOnlyDictionary<string, string> answers, CancellationToken ct)
        {
            if (!string.Equals(step?.Op, ChangeStep, StringComparison.Ordinal) && !string.Equals(step?.Id, ChangeStep, StringComparison.Ordinal))
                return new EngineStepResult { Ok = false, Note = "This executor runs only the change group." };
            var change = _changeFor(job);
            if (change == null) return new EngineStepResult { Ok = false, Note = "There is no change to make for this run." };
            ct.ThrowIfCancellationRequested();
            var r = await _engine.ApplyComposedChangeAsync(change, _origin).ConfigureAwait(false);
            return new EngineStepResult
            {
                Ok = r.Outcome == ComposedOutcome.Applied,
                Note = r.Reason,
                // The ids of the steps applied: a stop keeps the earlier steps, and they are proven as applied.
                ObligationsMet = r.AppliedStepIds,
                EvidenceDigest = r.Changed ? "revision:" + r.Revision : null,
            };
        }
    }
}

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>
        /// Apply one change group as ONE undo entry (composer contract 3.2): re-run the before-checks, run the
        /// ops' pre-hooks, apply every line of every step before the first failed before-check through
        /// ApplyOneItem inside one MutateAsync, then run the ops' post-hooks.
        /// </summary>
        public Task<ComposedChangeResult> ApplyComposedChangeAsync(ComposedChange change, string origin) =>
            ApplyComposedChangeAsync(change, origin, workflowGateHeld: false);

        /// <param name="workflowGateHeld">True when the caller holds the workflow gate across the commit (a direct
        /// step, so its step cannot move between its checks and its commit). The two hooks that read the runs then
        /// read them without taking the gate again, since it is not re-entrant.</param>
        /// <param name="direct">Set by a direct step, whose verify runs after this commit and may take it back. Its
        /// verified-edit records are then held in it and written only once the step is accepted (that trail is
        /// append-only, so a record written now would outlive the take back), and the workflow-authored hashes are
        /// written now, because the step's own verify reads them, with what each replaced kept so a take back can put
        /// it back (Codex on pull request 395).</param>
        internal async Task<ComposedChangeResult> ApplyComposedChangeAsync(ComposedChange change, string origin, bool workflowGateHeld, DirectStepAudit direct = null)
        {
            var s = _sessions.Require();
            origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin;
            var compiled = ComposedChangeCompiler.Compile(change, BlankFill.Require);
            var total = compiled.Steps.Count;

            if (compiled.OpenBlanks.Count > 0)
            {
                var (n, name) = compiled.OpenBlanks[0];
                return new ComposedChangeResult
                {
                    Outcome = ComposedOutcome.NotReady, Revision = s.Revision, StepCount = total, StopStep = n,
                    Reason = $"Nothing changed. Step {n} is still waiting for {name} to be written.",
                };
            }

            // 0. A row filter runs only on the person's own approval (contract 5.4). Refused here, before any check or
            //    hook, so no caller of this method (a draft, a direct step, a job) can write one without it.
            var held = compiled.Steps.FirstOrDefault(st => st.Lines.Any(l => ComposedOps.HeldForPerson(l.Op)));
            if (held != null && !change.PersonApproved)
                return new ComposedChangeResult
                {
                    Outcome = ComposedOutcome.Failed, Revision = s.Revision, StepCount = total, StopStep = held.Number,
                    Reason = "Nothing changed. " + DraftCopy.HeldRefusal(DraftHolds.RowSecurity),
                };

            // 1. The before-checks, on the model as it is, just before the change.
            var before = await RunBeforeChecksAsync(s, change, compiled).ConfigureAwait(false);
            var failedCheck = before.FirstOrDefault(c => c.Status == ComposedCheckStatus.Failed);
            var stop = failedCheck?.StepNumber ?? 0;
            var toApply = compiled.Steps.Where(st => stop == 0 || st.Number < stop).ToList();
            if (toApply.Count == 0)
                return new ComposedChangeResult
                {
                    Outcome = ComposedOutcome.Stopped, Revision = s.Revision, StepCount = total, StopStep = stop,
                    Reason = failedCheck?.Text, BeforeChecks = before,
                };

            // 2. The pre-hooks the ops run today, outside their own MutateAsync.
            foreach (var line in toApply.SelectMany(st => st.Lines))
            {
                var bound = ComposedOps.BoundOp(line.Op);
                if (bound != null) await ComposedBindingAsync(bound, origin, change.Workflow, workflowGateHeld).ConfigureAwait(false);
            }
            // VerifiedGuardAsync's decision, taken once for the whole change. The DAX itself is checked inside the
            // batch by each core against the model as the batch has left it, which a pre-batch check cannot do for
            // a formula over a table the same change creates.
            var verified = _verifiedMode;

            // 3. One MutateAsync, filed under the draft (contract 8.4).
            var failingStep = 0;
            ComposedMoved moved = null;
            long rev;
            using var cause = string.IsNullOrWhiteSpace(change.DraftId)
                ? default(ChangeCause.Scope?)
                : ChangeCause.Begin(ChangeCauseKinds.Draft, change.Title, ChangeBy.ApproverFor(origin), change.DraftId);
            try
            {
                rev = await s.MutateAsync(origin, string.IsNullOrWhiteSpace(change.Title) ? "composed change" : change.Title, m =>
                {
                    // A value the person changed after the change was made is never written over without asking (UAT
                    // S12-5-004). Checked here, on the dispatcher, so no edit can land between this check and the writes.
                    moved = MovedSinceMade(m, change.Baselines, toApply);
                    if (moved != null) throw new ComposedMovedException();
                    foreach (var st in toApply)
                    {
                        failingStep = st.Number;
                        foreach (var line in st.Lines) ApplyOneItem(m, line.Item, s);
                    }
                    failingStep = 0;
                }).ConfigureAwait(false);
            }
            catch (ComposedMovedException)
            {
                return new ComposedChangeResult
                {
                    Outcome = ComposedOutcome.NotReady, Revision = s.Revision, StepCount = total, StopStep = moved.StepNumber,
                    Reason = "Nothing changed. " + DraftCopy.Overwrite(moved.ObjectRef, moved.Kind), BeforeChecks = before, Moved = moved,
                };
            }
            catch (Exception ex) when (failingStep > 0)
            {
                // The batch rolled back and the redo stack was put back (Session.MutateAsync); nothing changed.
                return new ComposedChangeResult
                {
                    Outcome = ComposedOutcome.Failed, Revision = s.Revision, StepCount = total, StopStep = failingStep,
                    Reason = $"Nothing changed. Step {failingStep} could not be made: {ex.Message}", BeforeChecks = before,
                };
            }

            // 4. The post-hooks the ops run today, after their commit.
            foreach (var line in toApply.SelectMany(st => st.Lines))
            {
                if (line.Dax == null) continue;
                var canonical = await s.ReadAsync(m => ObjectRefs.Resolve(m, line.DaxRef) is TabularEditor.TOMWrapper.ITabularObject o ? ObjectRefs.For(o) : line.DaxRef).ConfigureAwait(false);
                var verifiedOp = ComposedOps.VerifiedOp(line.Op);
                if (verifiedOp != null)
                {
                    var validated = verified && !string.IsNullOrWhiteSpace(line.Dax);
                    var dax = line.Dax;
                    if (direct != null) direct.Records.Add(() => RecordVerifiedModeEditAsync(validated, s, verifiedOp, canonical, dax, rev, origin));
                    else await RecordVerifiedModeEditAsync(validated, s, verifiedOp, canonical, dax, rev, origin).ConfigureAwait(false);
                }
                var driftOp = ComposedOps.DriftOp(line.Op);
                if (driftOp != null) await RecordWorkflowAuthoredMeasureAsync(driftOp, canonical, line.Dax, workflowGateHeld, direct?.Replaced).ConfigureAwait(false);
            }

            return new ComposedChangeResult
            {
                Outcome = stop == 0 ? ComposedOutcome.Applied : ComposedOutcome.Stopped,
                Revision = rev, Changed = true, StepCount = total, StopStep = stop,
                AppliedStepIds = toApply.Select(st => st.Step.Id).ToArray(),
                Reason = failedCheck?.Text,
                BeforeChecks = before,
                Notes = toApply.SelectMany(st => st.Lines).Select(l => l.Item.Note).Where(x => !string.IsNullOrEmpty(x)).ToArray(),
            };
        }

        /// <summary>The kinds whose property is compared against the value the change was made against: the ones
        /// <see cref="ReadItemContext"/> reads a value for.</summary>
        internal static readonly HashSet<string> GuardedKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "set_description", "set_dax", "set_measure_format", "set_summarize_by", "set_data_category",
            "set_column_hidden", "set_sort_by_column", "set_data_type", "rename",
        };

        internal static string BaselineKey(string kind, string objectRef) => kind + "|" + objectRef;

        /// <summary>What the property a line writes says now, or null when its object is not in the model (a step of
        /// the same change may create it; the write itself refuses a missing object).</summary>
        internal static string ValueNow(TabularEditor.TOMWrapper.Model m, string kind, string objectRef)
        {
            if (!GuardedKinds.Contains(kind ?? "")) return null;
            try { return ObjectRefs.Resolve(m, objectRef) == null ? null : ReadItemContext(m, objectRef, kind).before; }
            catch { return null; }
        }

        /// <summary>The first line of these steps whose property no longer says what it said when the change was made.</summary>
        private static ComposedMoved MovedSinceMade(TabularEditor.TOMWrapper.Model m, IReadOnlyDictionary<string, string> baselines,
                                                    IEnumerable<CompiledStep> steps)
        {
            if (baselines == null || baselines.Count == 0) return null;
            foreach (var st in steps)
            foreach (var line in st.Lines)
            {
                var key = BaselineKey(line.Item.Kind, line.Item.ObjectRef);
                if (!baselines.TryGetValue(key, out var was)) continue;
                var now = ValueNow(m, line.Item.Kind, line.Item.ObjectRef);
                if (now == null || string.Equals(now, was, StringComparison.Ordinal)) continue;
                return new ComposedMoved { StepId = st.Step.Id, StepNumber = st.Number, ObjectRef = line.Item.ObjectRef, Kind = line.Item.Kind, Was = was, Now = now };
            }
            return null;
        }

        private sealed class ComposedMovedException : Exception { }

        /// <summary>Binding enforcement for a composed write (contract 3.2, step 2). A composed run of a saved
        /// workflow counts as that workflow's performing step for every op it applies, so a binding that requires
        /// that workflow lets it through. Everything else goes through EnforceBindingAsync unchanged, which is
        /// what makes a composed write refused exactly where the bare op is: a hard binding throws the same
        /// teaching sentence, a warn binding publishes the same advisory, and a corrupt settings file fails
        /// closed the same way.</summary>
        private async Task ComposedBindingAsync(string op, string origin, string performingWorkflow, bool workflowGateHeld)
        {
            if (!string.IsNullOrWhiteSpace(performingWorkflow) && DryRunScope.Current == null && !WorkflowSettingsCorrupt())
            {
                var binding = await ResolveEnforcedBindingAsync(op).ConfigureAwait(false);
                if (binding != null && binding.Require.Contains(performingWorkflow, StringComparer.Ordinal)) return;
            }
            await EnforceBindingAsync(op, origin, workflowGateHeld).ConfigureAwait(false);
        }
    }

    /// <summary>What a direct step's commit leaves to be settled by the step's outcome: the verified-edit records to
    /// write once it is accepted, and, per run and measure, the authored hash each write replaced (null when there
    /// was none), to put back if it is taken back.</summary>
    internal sealed class DirectStepAudit
    {
        public List<Func<Task>> Records { get; } = new List<Func<Task>>();
        public List<(Dictionary<string, string> Hashes, string Measure, string Before)> Replaced { get; } =
            new List<(Dictionary<string, string>, string, string)>();

        /// <summary>The step was accepted, or its writes stayed: write the records.</summary>
        public async Task RecordAsync()
        {
            foreach (var r in Records) await r().ConfigureAwait(false);
            Records.Clear();
        }

        /// <summary>The writes were taken back: put every authored hash back as it was, newest first.</summary>
        public void PutBack()
        {
            for (var i = Replaced.Count - 1; i >= 0; i--)
            {
                var (hashes, measure, before) = Replaced[i];
                if (before == null) hashes.Remove(measure); else hashes[measure] = before;
            }
            Replaced.Clear();
            Records.Clear();
        }
    }
}

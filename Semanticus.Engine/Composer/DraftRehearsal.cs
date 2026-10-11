// The formula check against the draft (composer contract section 7), and the before and written check kinds
// (contract 1.7).
//
// WHY A REHEARSAL. validate_dax checks against the open model, where a table this run adds does not exist yet:
// the frames' Return rate reads Returns[Quantity], and Returns is only in the draft. DaxValidator is a pure
// function of a TOM model, so the check runs inside a rehearsal of the draft instead: MutateAsync under a
// dry-run scope runs the real appliers and always rolls back, with no undo entry, no revision and no broadcast
// (Session.RehearseAsync). The steps before the blank's step apply, the blank's own step applies with the blank
// standing in as a placeholder (so the step's own objects exist), the written checks read the rehearsed model,
// and it all rolls back.
//
// THE RULE valid_dax HOLDS TO. The write path's rule, DaxWriteRefusal (LocalEngine.cs), which refuses ANY
// diagnostic, warnings included, plus the plain-words guard. DaxValidation.Valid alone is not the rule: an
// unknown column is only a warning, so Valid stays true for Returns[Refund], and a check that trusted Valid
// would pass a formula every write door then refuses (Sol 24 Sep).
//
// A CHECK THAT CANNOT RUN IS NOT A PASS. A live check with no connection, or a source nobody can read yet,
// reads "Could not check" and counts as not passed (contract 1.7). This file never turns an unknown into a
// tick.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine.Composer;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        // ---- the rehearsal ---------------------------------------------------------------------------------

        /// <summary>Run the written checks for one blank against a rehearsal of the draft up to its step.
        /// Nothing is written: no undo entry, no revision, no broadcast.</summary>
        public async Task<BlankCheckResult> CheckDraftBlankAsync(ComposedChange change, string stepId, string blankName, string text)
        {
            var s = _sessions.Require();
            var (step, blank, number) = FindBlank(change, stepId, blankName);
            // The blank itself rehearses as a placeholder: its step's objects exist, and the submitted text is
            // checked against the rehearsed model rather than written into it (a bad formula would otherwise fail
            // the rehearsal instead of failing its check).
            var compiled = ComposedChangeCompiler.Compile(change, BlankFill.Placeholder, stepId, blankName, ComposedChangeCompiler.PlaceholderFor(blank));
            var checks = new List<ComposedCheck>();
            foreach (var c in WrittenChecksFor(step, blank))
                if (await ComposedCheckAppliesAsync(c, change.Answers).ConfigureAwait(false)) checks.Add(c);

            var r = await RehearseComposedAsync(s, compiled.Steps.Where(st => st.Number <= number),
                m => checks.Select(c => RunWrittenCheck(m, c, blank, text, number, step.Id)).ToArray()).ConfigureAwait(false);
            if (!r.Ok)
                return new BlankCheckResult { Passed = false, RehearsalProblem = r.Problem };
            return new BlankCheckResult { Passed = r.Value.All(c => c.Status == ComposedCheckStatus.Passed), Checks = r.Value };
        }

        /// <summary>The before-run dry run of a whole change group: does every step apply, and what would it add.
        /// Open blanks rehearse as placeholders.</summary>
        public async Task<DraftRehearsalReport> RehearseComposedChangeAsync(ComposedChange change)
        {
            var s = _sessions.Require();
            var compiled = ComposedChangeCompiler.Compile(change, BlankFill.Placeholder);
            var r = await RehearseComposedAsync(s, compiled.Steps, _ => true).ConfigureAwait(false);
            var report = new DraftRehearsalReport { WouldSucceed = r.Ok, Problem = r.Problem, FailedStep = r.FailedStep, DeltaCount = r.DeltaCount };
            if (r.Ok)
                foreach (var line in compiled.Steps.SelectMany(st => st.Lines))
                    report.Kinds[line.Item.Kind] = report.Kinds.TryGetValue(line.Item.Kind, out var n) ? n + 1 : 1;
            return report;
        }

        /// <summary>Test seam: the validator's own verdict on the rehearsed model, so a test can pin that
        /// Valid alone would have passed what valid_dax refuses.</summary>
        internal async Task<DaxValidation> RehearsedValidityForTestAsync(ComposedChange change, string stepId, string text)
        {
            var s = _sessions.Require();
            var number = (change.Steps ?? new List<ComposedStep>()).FindIndex(st => st.Id == stepId) + 1;
            var compiled = ComposedChangeCompiler.Compile(change, BlankFill.Placeholder);
            var r = await RehearseComposedAsync(s, compiled.Steps.Where(st => st.Number <= number), m => DaxValidator.Validate(m, text)).ConfigureAwait(false);
            return r.Ok ? r.Value : throw new InvalidOperationException(r.Problem);
        }

        private readonly struct Rehearsed<T>
        {
            public bool Ok { get; init; }
            public int FailedStep { get; init; }
            public string Problem { get; init; }
            public T Value { get; init; }
            public int DeltaCount { get; init; }
        }

        /// <summary>Apply the given steps inside one ROLLED-BACK batch and read the rehearsed model before the
        /// rollback. The dry-run scope is an AsyncLocal read by MutateAsync on this thread, set and restored here.</summary>
        private async Task<Rehearsed<T>> RehearseComposedAsync<T>(Session s, IEnumerable<CompiledStep> steps, Func<Model, T> readRehearsed)
        {
            var previous = DryRunScope.Current;
            var dry = new DryRunCollector();
            var failing = 0;
            T value = default;
            try
            {
                DryRunScope.Current = dry;
                await s.MutateAsync("engine", "rehearse a draft", m =>
                {
                    foreach (var st in steps)
                    {
                        failing = st.Number;
                        foreach (var line in st.Lines) ApplyOneItem(m, line.Item, s);
                    }
                    failing = 0;
                    value = readRehearsed(m);
                }).ConfigureAwait(false);
                return new Rehearsed<T> { Ok = true, Value = value, DeltaCount = dry.Deltas.Count };
            }
            catch (Exception ex) when (failing > 0)
            {
                return new Rehearsed<T> { Ok = false, FailedStep = failing, Problem = $"Step {failing} could not be made: {ex.Message}" };
            }
            finally { DryRunScope.Current = previous; }
        }

        private static (ComposedStep Step, ComposedBlank Blank, int Number) FindBlank(ComposedChange change, string stepId, string blankName)
        {
            var steps = change?.Steps ?? new List<ComposedStep>();
            var index = steps.FindIndex(st => string.Equals(st.Id, stepId, StringComparison.Ordinal));
            if (index < 0) throw new InvalidOperationException($"This draft has no step {stepId}.");
            var blank = steps[index].Blanks?.FirstOrDefault(b => string.Equals(b.Name, blankName, StringComparison.Ordinal))
                        ?? throw new InvalidOperationException($"Step {index + 1} has no blank named {blankName}.");
            return (steps[index], blank, index + 1);
        }

        // ---- the written checks ----------------------------------------------------------------------------

        /// <summary>The written checks that apply to one blank: the step's written checks aimed at it (or at no
        /// blank in particular and fitting its kind), plus the one check the write path would enforce anyway and
        /// the author did not list (valid_dax for a formula, valid_m for Power Query). A check the write would
        /// refuse cannot be optional here, or a blank could pass and then fail at the change.</summary>
        private static List<ComposedCheck> WrittenChecksFor(ComposedStep step, ComposedBlank blank)
        {
            var kind = (blank.Kind ?? "").Trim().ToLowerInvariant();
            var list = (step.Checks ?? new List<ComposedCheck>())
                .Where(c => string.Equals(c?.Phase, "written", StringComparison.OrdinalIgnoreCase))
                .Where(c => c.Target == null ? Fits(c.Kind, kind) : string.Equals(c.Target, blank.Name, StringComparison.Ordinal))
                .ToList();
            if (kind == "dax" && !list.Any(c => c.Kind == "valid_dax")) list.Insert(0, new ComposedCheck { Kind = "valid_dax", Phase = "written" });
            if (kind == "m" && !list.Any(c => c.Kind == "valid_m")) list.Insert(0, new ComposedCheck { Kind = "valid_m", Phase = "written" });
            return list;
        }

        private static bool Fits(string check, string blankKind) => (check ?? "") switch
        {
            "valid_dax" or "dax_lint" or "refs_within" => blankKind == "dax",
            "valid_m" => blankKind == "m",
            "max_chars" or "no_formula_text" => blankKind == "description" || blankKind == "name",
            "choice_member" => blankKind == "choice",
            _ => true,   // an unknown kind is reported, never silently skipped
        };

        private static ComposedCheckResult Result(ComposedCheck c, string status, string text, int number, string stepId, string blank) =>
            new ComposedCheckResult { Kind = c.Kind, Phase = c.Phase ?? "written", Status = status, Text = text, StepNumber = number, StepId = stepId, Blank = blank };

        /// <summary>One written check on the rehearsed model. Runs on the dispatcher thread inside the rehearsal.</summary>
        private static ComposedCheckResult RunWrittenCheck(Model m, ComposedCheck c, ComposedBlank blank, string text, int number, string stepId)
        {
            ComposedCheckResult R(string status, string sentence) => Result(c, status, sentence, number, stepId, blank.Name);
            text ??= "";
            switch (c.Kind)
            {
                case "valid_dax":
                {
                    if (string.IsNullOrWhiteSpace(text)) return R(ComposedCheckStatus.Failed, "The formula is empty.");
                    var refusal = DaxWriteRefusal(DaxValidator.Validate(m, text), text, "this formula");
                    return refusal == null ? R(ComposedCheckStatus.Passed, "Valid formula.") : R(ComposedCheckStatus.Failed, refusal);
                }
                case "dax_lint":
                {
                    var ctx = new DaxLintContext();
                    foreach (var t in m.Tables) ctx.TableNames.Add(t.Name);
                    var warning = DaxLint.Analyze(text, ctx).Findings
                        .FirstOrDefault(f => string.Equals(f.Severity, "warning", StringComparison.OrdinalIgnoreCase));
                    return warning == null ? R(ComposedCheckStatus.Passed, "No formula warnings.") : R(ComposedCheckStatus.Failed, warning.Message);
                }
                case "refs_within":
                {
                    var allowed = (c.Of ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                    if (allowed.Length == 0) return R(ComposedCheckStatus.CouldNotCheck, "Could not check: this check names no tables to allow.");
                    var used = DaxValidator.TablesReferenced(m, text);
                    var outside = used.Where(t => !allowed.Contains(t, StringComparer.OrdinalIgnoreCase)).ToArray();
                    return outside.Length == 0
                        ? R(ComposedCheckStatus.Passed, "Uses only " + JoinNames(allowed) + ".")
                        : R(ComposedCheckStatus.Failed, "It uses " + JoinNames(outside) + ", which " + (outside.Length == 1 ? "is" : "are") + " not in " + JoinNames(allowed) + ".");
                }
                case "max_chars":
                {
                    int? limit = c.Max.HasValue ? (int)c.Max.Value : blank.MaxChars ?? (string.Equals(blank.Kind, "description", StringComparison.OrdinalIgnoreCase) ? 200 : (int?)null);
                    if (limit == null) return R(ComposedCheckStatus.CouldNotCheck, "Could not check: this check has no character limit.");
                    return text.Length <= limit.Value
                        ? R(ComposedCheckStatus.Passed, $"Under {limit.Value} characters.")
                        : R(ComposedCheckStatus.Failed, $"It is {text.Length} characters. The limit is {limit.Value}.");
                }
                case "no_formula_text":
                {
                    var bracket = ReBracketRef.Match(text);
                    if (bracket.Success) return R(ComposedCheckStatus.Failed, $"It contains formula text: {bracket.Value}.");
                    foreach (Match call in ReCallToken.Matches(text))
                        if (DaxValidator.IsKnownFunction(call.Groups[1].Value))
                            return R(ComposedCheckStatus.Failed, $"It contains formula text: {call.Groups[1].Value}.");
                    return R(ComposedCheckStatus.Passed, "Names no formula.");
                }
                case "valid_m":
                {
                    try { RefuseMForWrite(text, "this Power Query"); }
                    catch (ArgumentException ex) { return R(ComposedCheckStatus.Failed, ex.Message); }
                    catch (InvalidOperationException ex) { return R(ComposedCheckStatus.Failed, ex.Message); }
                    // Structural only (contract 1.7). A query that is not a let ... in expression passed the bracket
                    // and quote check, but its shape was not checked, and saying so is the honest answer.
                    return ReLetIn.IsMatch(text)
                        ? R(ComposedCheckStatus.Passed, "Brackets, quotes and the let ... in shape are in order.")
                        : R(ComposedCheckStatus.CouldNotCheck, "Could not check: this is not a let ... in query, so only its brackets and quotes were checked.");
                }
                case "choice_member":
                {
                    var options = (c.Of != null && c.Of.Length > 0 ? c.Of : blank.Choices) ?? Array.Empty<string>();
                    if (options.Length == 0) return R(ComposedCheckStatus.CouldNotCheck, "Could not check: there are no options to choose from.");
                    return options.Contains(text, StringComparer.Ordinal)
                        ? R(ComposedCheckStatus.Passed, "One of the options.")
                        : R(ComposedCheckStatus.Failed, $"{(text.Length == 0 ? "An empty answer" : text)} is not one of the options: {string.Join(", ", options)}.");
                }
                default:
                    return R(ComposedCheckStatus.CouldNotCheck, $"Could not check: Semanticus has no check called {c.Kind}.");
            }
        }

        private static string JoinNames(IReadOnlyList<string> names) =>
            names.Count <= 1 ? string.Join("", names)
            : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];

        private static readonly Regex ReBracketRef = new Regex(@"\[[^\]\r\n]+\]", RegexOptions.Compiled);
        private static readonly Regex ReCallToken = new Regex(@"\b([A-Za-z][A-Za-z0-9.]*)\s*\(", RegexOptions.Compiled);
        private static readonly Regex ReLetIn = new Regex(@"^\s*let\b[\s\S]*\bin\b[\s\S]*\S", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ---- the before checks -----------------------------------------------------------------------------

        /// <summary>Every before-check of every step, on the model as it is now (contract 1.7: at prepare, and
        /// again just before the change). Live ones need a connection and say so when there is none.</summary>
        private async Task<ComposedCheckResult[]> RunBeforeChecksAsync(Session s, ComposedChange change, CompiledChange compiled)
        {
            var results = new List<ComposedCheckResult>();
            foreach (var cs in compiled.Steps)
                foreach (var c in (cs.Step.Checks ?? new List<ComposedCheck>()).Where(c => string.Equals(c?.Phase, "before", StringComparison.OrdinalIgnoreCase)))
                    if (await ComposedCheckAppliesAsync(c, change.Answers).ConfigureAwait(false))
                        results.Add(await RunBeforeCheckAsync(s, c, cs).ConfigureAwait(false));
            return results.ToArray();
        }

        /// <summary>Whether a check's <c>when</c> holds (Codex on pull request 395: the condition was dropped, so
        /// every conditional check ran). Read with the same evaluator a workflow's conditions use, the answers
        /// standing as its inputs and any other fact gathered as it is for them. A condition that cannot be read
        /// runs the check: a check is never skipped on a guess.</summary>
        internal async Task<bool> ComposedCheckAppliesAsync(ComposedCheck c, IReadOnlyDictionary<string, string> answers)
        {
            if (string.IsNullOrWhiteSpace(c?.When)) return true;
            var expr = WorkflowPredicate.Parse(c.When, out _);
            if (expr == null) return true;
            var facts = await GatherFactsForAsync(new[] { expr }).ConfigureAwait(false);
            facts.Inputs = (answers ?? new Dictionary<string, string>())
                .ToDictionary(kv => kv.Key, kv => new AnswerValue { Value = kv.Value }, StringComparer.Ordinal);
            return WorkflowPredicate.Evaluate(expr, facts);
        }

        /// <summary>The before-checks alone, for the draft's "Before it runs" block at prepare time.</summary>
        public Task<ComposedCheckResult[]> RunComposedBeforeChecksAsync(ComposedChange change) =>
            RunBeforeChecksAsync(_sessions.Require(), change, ComposedChangeCompiler.Compile(change, BlankFill.Placeholder));

        private async Task<ComposedCheckResult> RunBeforeCheckAsync(Session s, ComposedCheck c, CompiledStep cs)
        {
            ComposedCheckResult R(string status, string sentence) => Result(c, status, sentence, cs.Number, cs.Step.Id, null);
            switch (c.Kind)
            {
                case "name_free":
                {
                    var names = CreatedNames(cs);
                    if (names.Count == 0 && c.Target == null) return R(ComposedCheckStatus.CouldNotCheck, "Could not check: this step creates nothing with a name.");
                    var taken = await s.ReadAsync(m => TakenNames(m, c, cs).FirstOrDefault()).ConfigureAwait(false);
                    return taken == null
                        ? R(ComposedCheckStatus.Passed, (c.Target != null ? Plain(c.Target) + " is" : names.Count == 1 ? names[0].Name + " is" : "The names are") + " free.")
                        : R(ComposedCheckStatus.Failed, $"{taken} is already taken in the model.");
                }
                case "key_unique":
                {
                    var col = ColumnDax(c.Target);
                    if (col == null) return R(ComposedCheckStatus.CouldNotCheck, "Could not check: this check names no column.");
                    var (t, _) = SplitColumnRef(c.Target);
                    var row = await LiveRowAsync($"EVALUATE ROW(\"rows\", COUNTROWS({TableDax(t)}), \"keys\", DISTINCTCOUNTNOBLANK({col}))").ConfigureAwait(false);
                    if (row.Problem != null) return R(ComposedCheckStatus.CouldNotCheck, row.Problem);
                    var rows = Num(row.Values, 0); var keys = Num(row.Values, 1);
                    if (rows == null || keys == null) return R(ComposedCheckStatus.CouldNotCheck, "Could not check: no data yet.");
                    return rows == keys
                        ? R(ComposedCheckStatus.Passed, $"{Plain(c.Target)} has no repeats.")
                        : R(ComposedCheckStatus.Failed, $"{Plain(c.Target)} has repeats: {rows:0} rows but {keys:0} different values.");
                }
                case "dates_within":
                {
                    var col = ColumnDax(c.Target);
                    var date = ColumnDax(c.Of?.FirstOrDefault());
                    if (col == null || date == null) return R(ComposedCheckStatus.CouldNotCheck, "Could not check: this check needs a column and a date column.");
                    var row = await LiveRowAsync($"EVALUATE ROW(\"lo\", MIN({col}), \"hi\", MAX({col}), \"dlo\", MIN({date}), \"dhi\", MAX({date}))").ConfigureAwait(false);
                    if (row.Problem != null) return R(ComposedCheckStatus.CouldNotCheck, row.Problem);
                    var (lo, hi, dlo, dhi) = (Date(row.Values, 0), Date(row.Values, 1), Date(row.Values, 2), Date(row.Values, 3));
                    if (lo == null || hi == null) return R(ComposedCheckStatus.CouldNotCheck, "Could not check: no data yet.");
                    if (dlo == null || dhi == null) return R(ComposedCheckStatus.CouldNotCheck, "Could not check: the date table has no data yet.");
                    var dateTable = SplitColumnRef(c.Of[0]).Table;
                    return lo >= dlo && hi <= dhi
                        ? R(ComposedCheckStatus.Passed, $"Every {Plain(c.Target)} falls inside {dateTable}.")
                        : R(ComposedCheckStatus.Failed, $"Some {Plain(c.Target)} values fall outside {dateTable}: they run from {lo:yyyy-MM-dd} to {hi:yyyy-MM-dd}, and {dateTable} runs from {dlo:yyyy-MM-dd} to {dhi:yyyy-MM-dd}.");
                }
                case "source_matches":
                {
                    var tableRef = c.Target ?? cs.Lines.FirstOrDefault(l => l.Item.Kind == "create_import_table")?.Item.ObjectRef;
                    if (tableRef == null) return R(ComposedCheckStatus.CouldNotCheck, "Could not check: this check names no table.");
                    var table = tableRef.StartsWith("table:", StringComparison.OrdinalIgnoreCase) ? tableRef.Substring("table:".Length) : tableRef;
                    var exists = await s.ReadAsync(m => m.Tables.Contains(table)).ConfigureAwait(false);
                    if (!exists)
                        return R(ComposedCheckStatus.CouldNotCheck, $"Could not check: {table} is not in the model yet, so its source cannot be read before the change.");
                    SourceSchema src;
                    try { src = await GetSourceSchemaAsync("table:" + table, null, null, "engine").ConfigureAwait(false); }
                    catch (Exception ex) { return R(ComposedCheckStatus.CouldNotCheck, "Could not check: " + ex.Message); }
                    if (src == null || !src.Reachable) return R(ComposedCheckStatus.CouldNotCheck, "Could not check: " + (src?.Error ?? "the source could not be read."));
                    var wanted = cs.Lines
                        .Where(l => l.Item.Kind == "create_column" && string.Equals(SplitColumnRef(l.Item.ObjectRef).Table, table, StringComparison.OrdinalIgnoreCase))
                        .Select(l => ArgOf(l.Item, "sourceColumn") ?? ArgOf(l.Item, "name")).Where(x => x != null).ToArray();
                    var missing = wanted.FirstOrDefault(w => !src.Columns.Any(sc => string.Equals(sc.Name, w, StringComparison.OrdinalIgnoreCase)));
                    return missing == null
                        ? R(ComposedCheckStatus.Passed, $"{src.Entity ?? table} has {src.Columns.Length} columns.")
                        : R(ComposedCheckStatus.Failed, $"The source has no column {missing}.");
                }
                default:
                    return R(ComposedCheckStatus.CouldNotCheck, $"Could not check: Semanticus has no check called {c.Kind}.");
            }
        }

        private static string ArgOf(ChangeItem it, string key) => it.Args != null && it.Args.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        /// <summary>The names a step creates, as (kind, table, name), from its lines. A placeholder name for an
        /// open name blank is never reported (contract 7).</summary>
        private static List<(string Kind, string Table, string Name)> CreatedNames(CompiledStep cs)
        {
            var list = new List<(string Kind, string Table, string Name)>();
            foreach (var l in cs.Lines)
            {
                switch (l.Item.Kind)
                {
                    case "create_import_table": case "create_calculation_group":
                        list.Add(("table", null, ArgOf(l.Item, "name"))); break;
                    case "generate_date_table":
                        list.Add(("table", null, ArgOf(l.Item, "tableName"))); break;
                    case "create_column":
                        list.Add(("column", SplitColumnRef(l.Item.ObjectRef).Table, ArgOf(l.Item, "name"))); break;
                    case "create_measure":
                        var (mt, mn) = SplitMeasureRef(l.Item.ObjectRef);
                        list.Add(("measure", mt, mn)); break;
                    case "create_calculation_item":
                        var group = ArgOf(l.Item, "calcGroupRef") ?? "";
                        list.Add(("calcitem", group.StartsWith("table:", StringComparison.OrdinalIgnoreCase) ? group.Substring("table:".Length) : group, ArgOf(l.Item, "name"))); break;
                    case "create_role":
                        list.Add(("role", null, ArgOf(l.Item, "name"))); break;
                }
            }
            return list.Where(x => !string.IsNullOrWhiteSpace(x.Name) && !x.Name.StartsWith(ComposedChangeCompiler.NamePlaceholderPrefix, StringComparison.Ordinal)).ToList();
        }

        private static IEnumerable<string> TakenNames(Model m, ComposedCheck c, CompiledStep cs)
        {
            if (c.Target != null)
            {
                if (ObjectRefs.Resolve(m, c.Target) != null) yield return Plain(c.Target);
                yield break;
            }
            foreach (var (kind, table, name) in CreatedNames(cs))
            {
                var taken = kind switch
                {
                    "table" => m.Tables.Contains(name),
                    "column" => m.Tables.Contains(table) && m.Tables[table].Columns.Contains(name),
                    // A measure name is model-wide in DAX, and it may not clash with a column of its own table.
                    "measure" => m.AllMeasures.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                                 || (m.Tables.Contains(table) && m.Tables[table].Columns.Contains(name)),
                    "calcitem" => m.Tables.Contains(table) && m.Tables[table] is CalculationGroupTable cg && cg.CalculationItems.Contains(name),
                    "role" => FindRole(m, name) != null,
                    _ => false,
                };
                if (taken) yield return name;
            }
        }

        // ---- live reads for the before checks ----------------------------------------------------------

        private async Task<(object[] Values, string Problem)> LiveRowAsync(string query)
        {
            if (_live == null) return (null, "Could not check: not connected.");
            ResultSet rs;
            try { rs = await RunDaxAsync(query, 1, "engine").ConfigureAwait(false); }
            catch (Exception ex) { return (null, "Could not check: " + ex.Message); }
            if (!string.IsNullOrEmpty(rs?.Error)) return (null, "Could not check: " + rs.Error);
            if (rs.Rows == null || rs.Rows.Length == 0) return (null, "Could not check: no data yet.");
            return (rs.Rows[0], null);
        }

        private static double? Num(object[] row, int i)
        {
            if (row == null || i >= row.Length || row[i] == null) return null;
            try { return Convert.ToDouble(row[i], CultureInfo.InvariantCulture); } catch { return null; }
        }

        private static DateTime? Date(object[] row, int i)
        {
            if (row == null || i >= row.Length || row[i] == null) return null;
            if (row[i] is DateTime d) return d;
            return DateTime.TryParse(Convert.ToString(row[i], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) ? p : (DateTime?)null;
        }

        private static (string Table, string Column) SplitColumnRef(string columnRef)
        {
            var raw = columnRef ?? "";
            // Only a column ref, or a bare Table/Column. Any other kind of ref ("measure:...") is not a column.
            if (!raw.StartsWith("column:", StringComparison.OrdinalIgnoreCase) && raw.Contains(':')) return (null, null);
            var rest = raw.StartsWith("column:", StringComparison.OrdinalIgnoreCase) ? raw.Substring("column:".Length) : raw;
            var slash = rest.IndexOf('/');
            return slash <= 0 ? (null, null) : (rest.Substring(0, slash), rest.Substring(slash + 1));
        }

        private static string TableDax(string table) => "'" + (table ?? "").Replace("'", "''") + "'";

        private static string ColumnDax(string columnRef)
        {
            var (t, c) = SplitColumnRef(columnRef);
            return t == null ? null : TableDax(t) + "[" + c.Replace("]", "]]") + "]";
        }

        /// <summary>"column:Sales/SalesLineKey" as a person reads it: Sales[SalesLineKey].</summary>
        private static string Plain(string objRef)
        {
            var (t, c) = SplitColumnRef(objRef);
            if (t != null) return t + "[" + c + "]";
            var colon = (objRef ?? "").IndexOf(':');
            return colon >= 0 ? objRef.Substring(colon + 1) : objRef;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    // ================================================================================================
    // Explain This Number (feature #2) — the engine half. explain_value assembles a deterministic
    // evidence dossier for ONE cell of ONE measure: the value re-derived in the cell's exact filter
    // context (the probe_measure SUMMARIZECOLUMNS shape), the dependency chain, source lineage, top
    // contributors (behind the NON-ADDITIVE GUARD), the why-is-this-blank checklist, and an RLS
    // advisory. FREE, read-only, both doors. The engine runs DAX + reads metadata — it never infers
    // and never narrates (the Summary strings are deterministic templates, not generated prose).
    // Offline degrades to the metadata-only dossier (chain/lineage/RLS) with Evidence.Available=false.
    // Pure decision logic (guards/verdicts) lives in Explain.cs so it is unit-testable offline.
    // ================================================================================================
    public sealed partial class LocalEngine
    {
        private const int ExplainChainDepthCap = 6;     // dependency BFS depth
        private const int ExplainChainNodeCap = 64;     // dependency BFS node budget
        private const int ExplainMemberCap = 5000;      // contributor members scanned (past it: refuse honestly)
        private const int ExplainDriverCap = 4;         // measures a number is built from, worked out beside it
        private const int ExplainSplitColumnCap = 40;   // columns whose number of values is counted when the engine picks the split
        private const int ExplainSplitProbes = 12;      // columns whose split is worked out, the ones with fewest values first
        private const int ExplainSplitProbeRows = 60;   // a column with more values than this in the cell is a list, not a split
        private const int ExplainFilterProbeCap = 6;    // drop-one-filter probes (bounded live cost)

        // A page that re-asks the same explain on its own must not flood the live model (hero 24: about 3,500 queries in 13 minutes).
        // The same request joins the one running, and a repeat within a few seconds gets that answer back.
        private readonly RepeatGuard _explainRepeats = new(TimeSpan.FromSeconds(3));

        public Task<ExplainDossier> ExplainValueAsync(string measureRef, ExplainFilterContext context, bool decompose, string decomposeBy, int topK, string origin)
        {
            // The session id and revision are in the key: an explain asked right after an edit describes the edited model, never the one from before it.
            var cur = _sessions.Current;
            var key = string.Join("\u001f", _sessions.Generation, cur?.Id, cur?.Revision, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_live), origin, measureRef, decompose, decomposeBy, topK,
                System.Text.Json.JsonSerializer.Serialize(context ?? new ExplainFilterContext()));
            return _explainRepeats.RunAsync(key, () => ExplainValueCoreAsync(measureRef, context, decompose, decomposeBy, topK, origin), d => d?.Status == "ok");
        }

        internal const string ExplainDeniedNote = "Your assistant is not allowed to read values from this model's data, so the number was not computed. You can allow it in Your assistant > Permissions.";

        private async Task<ExplainDossier> ExplainValueCoreAsync(string measureRef, ExplainFilterContext context, bool decompose, string decomposeBy, int topK, string origin)
        {
            var s = _sessions.Require();
            if (string.IsNullOrWhiteSpace(measureRef))
                return new ExplainDossier { Status = "error", Error = "measureRef is required: the measure whose number you want explained (e.g. 'measure:Sales/Total Sales', or just its name). Run list_measures to see them." };

            context ??= new ExplainFilterContext();
            var memberFilters = (context.Filters ?? Array.Empty<ExplainFilter>()).Where(f => f != null && !string.IsNullOrWhiteSpace(f.Column)).ToArray();
            var predicates = (context.ExtraPredicates ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray();
            // Finding A (PR #92 review): the visual's AXIS columns are re-created as SUMMARIZECOLUMNS
            // group-by args, so scope-sensitive DAX (ISINSCOPE / HASONEVALUE on an axis column) evaluates in
            // the SAME context the visual used; the single-member TREATAS filters pin the clicked coordinates
            // so exactly one row (the cell) comes back. A bare scalar under TREATAS is a DIFFERENT context and
            // can produce a different number than the one the user clicked.
            var groupBy = (context.GroupBy ?? Array.Empty<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            // Tables the context filters sit on (member filters parse exactly; raw predicates best-effort).
            var filterTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in memberFilters) { var t = ExplainLogic.TableOf(f.Column); if (t != null) filterTables.Add(t); }
            foreach (var p in predicates) foreach (var t in ExplainLogic.TablesInPredicate(p)) filterTables.Add(t);
            // The columns the number is already fixed on or laid out by: a split by one of them would have nothing to split.
            var fixedColumns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in memberFilters.Select(f => f.Column).Concat(groupBy)) { var k = ExplainLogic.ColumnKeyOf(c); if (k != null) fixedColumns.Add(k); }
            foreach (var p in predicates) foreach (var k in ExplainLogic.ColumnsInPredicate(p)) fixedColumns.Add(k);

            // ---- ONE dispatcher read: resolve + chain BFS + lineage + graph + roles + decompose candidates ----
            ExplainSnapshot snap;
            try
            {
                snap = await s.ReadAsync(m => BuildExplainSnapshot(m, measureRef, filterTables, fixedColumns));
            }
            catch (InvalidOperationException ex)
            {
                return new ExplainDossier { Status = "error", Error = ex.Message };
            }

            var d = new ExplainDossier
            {
                Status = "ok",
                Measure = snap.Canonical,
                Name = snap.Name,
                Expression = snap.Expression,
                Context = context,
                Chain = snap.Chain,
                ChainTruncated = snap.ChainTruncated,
                Lineage = snap.Lineage,
                Rls = ExplainLogic.RlsAdvisory(snap.Roles, snap.DepTables),
                Format = snap.Format,
            };

            var exprForEval = Baseline.MeasureRefExpr(snap.Name);
            var memberArgs = await ExplainMemberArgsAsync(memberFilters);
            var cellQuery = BuildExplainQuery(exprForEval, groupBy, memberArgs, predicates);

            var live = _live;
            // The breakdown carries member names and their values, which is rows of the person's data. Where the person has DENIED their
            // assistant data reads (QueryData) on this target, the assistant gets the structure and no numbers. "Ask" is not a denial and
            // nothing is queued, so a default preset answers with no extra question. The person is never gated (the same rule as get_model_facts).
            if (live != null && AgentPolicyGuard.Decide(AgentCapability.QueryData, ResolveTargetLabel(live.DataSource, live.Database), origin, isCommit: true, AgentPolicyStore.Get()).Outcome == GateOutcome.Deny)
            {
                d.Evidence = new ExplainEvidence { Available = false, Query = cellQuery, Note = ExplainDeniedNote };
                d.Summary = ExplainDeniedNote + $" The structure below still shows what feeds {Bracket(snap.Name)}.";
                await FinishExplainAsync(s, d, origin);
                return d;
            }
            if (live == null)
            {
                d.Evidence = new ExplainEvidence { Available = false, Query = cellQuery, Note = "No live connection, so the number itself was not computed. Connect with open_local / open_live (or connect_local / connect_xmla) and run explain_value again for the value, the blank checks, and the breakdown." };
                d.Summary = $"No live connection, so the number itself was not computed. The structure below still shows what feeds {Bracket(snap.Name)}.";
                await FinishExplainAsync(s, d, origin);
                return d;
            }

            // ---- the cell's value, in its exact evaluation context (axis in scope + coordinates pinned) ----
            string evalError = null;
            var rs = await live.ExecuteAsync(cellQuery, 3, 120);   // 1 row expected; 3 detects an under-pinned (ambiguous) cell
            if (rs == null || !string.IsNullOrEmpty(rs.Error)) evalError = rs?.Error ?? "no result";

            if (evalError != null)
            {
                var why = DaxErrorText.Sentence(evalError);
                d.Evidence = new ExplainEvidence { Available = false, Query = cellQuery, Note = "The value query failed: " + why };
                d.Summary = $"The number could not be computed here. {why}{(why.EndsWith('.') ? "" : ".")} If you just created or edited this measure, the live model may not have it yet: deploy or refresh first, then try again.";
                await FinishExplainAsync(s, d, origin);
                return d;
            }

            var pick = PickCellValue(rs, groupBy.Length);
            if (pick.Error != null)
            {
                d.Evidence = new ExplainEvidence { Available = false, Query = cellQuery, Note = pick.Error };
                d.Summary = pick.Error;
                await FinishExplainAsync(s, d, origin);
                return d;
            }
            var raw = pick.Value;
            d.ValueEvaluated = true;
            d.IsBlank = pick.IsBlank;
            d.Value = DaxBench.Fmt(raw);
            d.Evidence = new ExplainEvidence { Available = true, Query = cellQuery };
            if (pick.Note != null) d.Note = AppendNote(d.Note, pick.Note);

            if (d.IsBlank == true)
            {
                d.Blank = await BuildBlankChecklistAsync(live, snap, exprForEval, groupBy, memberFilters, memberArgs, predicates, filterTables);
                d.Summary = $"{Bracket(snap.Name)} shows no value (blank) here. {d.Blank.Summary}";
            }
            else
            {
                if (decompose && IsNum(raw))
                    d.Contributors = await BuildContributorsAsync(live, snap, exprForEval, groupBy, memberArgs, predicates, decomposeBy, topK,
                        Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture), filterTables);
                else if (decompose)
                    d.Contributors = new ExplainContributors { Additive = false, Note = "This value is not a number, so there is no breakdown." };
                d.Drivers = await ExplainDriversAsync(live, snap, groupBy, memberArgs, predicates);
                d.Summary = $"{Bracket(snap.Name)} is {DisplayNumber(raw)} for this selection."
                          + (d.Contributors != null && d.Contributors.Additive ? " The largest parts are listed below." : "");
            }

            if (d.Rls.Length > 0)
                d.Note = AppendNote(d.Note, $"Row-level security exists on this number's tables ({d.Rls.Length} role(s)). This tool reads the model without a role, so what a secured user sees can differ.");

            await FinishExplainAsync(s, d, origin);
            return d;
        }

        // ---- snapshot (dispatcher thread) ----------------------------------------------------------

        private sealed class ExplainSnapshot
        {
            public string Canonical;
            public string Name;
            public string Expression;
            public ExplainChainNode[] Chain;
            public bool ChainTruncated;
            public ExplainLineageEntry[] Lineage;
            public HashSet<string> DepTables;
            public RoleInfo[] Roles;
            public ModelGraph Graph;
            public List<KeyValuePair<string, string>> Bodies;      // display name → DAX (blank-by-design scan)
            public List<string> DecomposeCandidates;               // ordered 'Table'[Column] refs
            public string Format;                                  // the measure's own format string
            public List<(string Name, string Format)> Drivers;     // the measures it uses directly
        }

        private ExplainSnapshot BuildExplainSnapshot(Model m, string measureRef, ISet<string> filterTables, ISet<string> fixedColumns)
        {
            var target = ResolveMeasureForExplain(m, measureRef);
            var canonical = ObjectRefs.For(target);

            var chain = new List<ExplainChainNode>();
            var chainTrunc = false;
            var visited = new HashSet<string>(StringComparer.Ordinal) { canonical };
            var depTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var leafCols = new List<Column>();
            var bodies = new List<KeyValuePair<string, string>> { new(Bracket(target.Name), target.Expression) };
            var queue = new Queue<(ITabularNamedObject Obj, int Depth)>();
            queue.Enqueue((target, 0));

            while (queue.Count > 0)
            {
                var (obj, depth) = queue.Dequeue();
                if (depth >= ExplainChainDepthCap) { chainTrunc = true; continue; }
                if (!(obj is IDaxDependantObject dep)) continue;
                foreach (var child in dep.DependsOn.Keys.OfType<ITabularNamedObject>())
                {
                    var cref = ObjectRefs.For(child);
                    if (!visited.Add(cref)) continue;
                    if (chain.Count >= ExplainChainNodeCap) { chainTrunc = true; break; }
                    var kind = child is CalculatedColumn ? "calccolumn" : ObjectRefs.KindOf(child);
                    chain.Add(new ExplainChainNode { Ref = cref, Name = child.Name, Kind = kind, Depth = depth + 1 });
                    switch (child)
                    {
                        case CalculatedColumn cc:
                            if (cc.Table != null) depTables.Add(cc.Table.Name);
                            bodies.Add(new(cc.Table?.Name + "[" + cc.Name + "]", cc.Expression));
                            queue.Enqueue((cc, depth + 1));
                            break;
                        case Column c:
                            if (c.Table != null) depTables.Add(c.Table.Name);
                            leafCols.Add(c);
                            break;
                        case Measure me:
                            bodies.Add(new(Bracket(me.Name), me.Expression));
                            queue.Enqueue((me, depth + 1));
                            break;
                        case Table t:
                            depTables.Add(t.Name);
                            if (t is IDaxDependantObject) queue.Enqueue((t, depth + 1));
                            break;
                        default:
                            if (child is IDaxDependantObject) queue.Enqueue((child, depth + 1));
                            break;
                    }
                }
            }

            var lineage = leafCols
                .GroupBy(c => ObjectRefs.For(c))
                .Select(g => g.First())
                .OrderBy(c => c.Table?.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ExplainLineageEntry { Column = ObjectRefs.For(c), Table = c.Table?.Name, Source = SourceOf(c.Table) })
                .ToArray();

            var graph = BuildGraph(m);
            return new ExplainSnapshot
            {
                Canonical = canonical,
                Name = target.Name,
                Expression = target.Expression,
                Chain = chain.ToArray(),
                ChainTruncated = chainTrunc,
                Lineage = lineage,
                DepTables = depTables,
                Roles = m.Roles.Select(BuildRoleInfo).ToArray(),
                Graph = graph,
                Bodies = bodies,
                DecomposeCandidates = FindDecomposeCandidates(m, graph, depTables, fixedColumns),
                Format = string.IsNullOrWhiteSpace(target.FormatString) ? null : target.FormatString,
                Drivers = chain.Where(n => n.Depth == 1 && n.Kind == "measure")
                    .Select(n => ObjectRefs.Resolve(m, n.Ref) as Measure).Where(me => me != null)
                    .Select(me => (me.Name, string.IsNullOrWhiteSpace(me.FormatString) ? null : me.FormatString)).Take(ExplainDriverCap).ToList(),
            };
        }

        // Accepts 'measure:Table/Name', '[Name]', or a bare name (unique across tables — ambiguity is
        // reported with the concrete refs so the caller can disambiguate).
        private static Measure ResolveMeasureForExplain(Model m, string measureRef)
        {
            var t = measureRef.Trim();
            if (t.Contains(":"))
            {
                if (ObjectRefs.Resolve(m, t) is Measure byRef) return byRef;
                throw new InvalidOperationException($"{measureRef} was not found or is not a measure: explain_value explains a measure's number. Run list_measures to find the right ref.");
            }
            var name = t.StartsWith("[") && t.EndsWith("]") ? t.Substring(1, t.Length - 2) : t;
            var hits = m.Tables.SelectMany(tb => tb.Measures).Where(me => string.Equals(me.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (hits.Length == 1) return hits[0];
            if (hits.Length > 1)
                throw new InvalidOperationException($"'{name}' exists on more than one table ({string.Join(", ", hits.Select(h => ObjectRefs.For(h)))}): pass the full ref.");
            throw new InvalidOperationException($"No measure named '{name}': run list_measures to see the model's measures.");
        }

        private static string SourceOf(Table t)
        {
            if (t == null) return null;
            if (t is CalculatedTable) return "Calculated table (DAX)";
            var p = t.Partitions?.FirstOrDefault();
            if (p == null) return "(no partition)";
            var head = $"{p.Mode} ({p.SourceType})";
            var ds = p.DataSource?.Name;
            var expr = (p.Expression ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (expr.Length > 90) expr = expr.Substring(0, 90) + "…";
            return head + (ds != null ? " · " + ds : "") + (expr.Length > 0 ? " · " + expr : "");
        }

        // Deterministic split candidates: every visible, non-key column of the dimension tables one active hop from the measure's data tables
        // (filter flows dimension to data), except the columns the number is already fixed on. A table the context filters on stays in: a year
        // is pinned, its months are the split. The order is the model's own, and it breaks ties between splits. First the next level of a
        // pinned level: a later level of the same hierarchy, or, in a date table, a column after the pinned one. Then the other hierarchy
        // levels in hierarchy order. Then the rest, by table and column order as the model holds them.
        private static readonly System.Text.RegularExpressions.Regex KeyLikeName = new(@"(Key|ID|Id)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static List<string> FindDecomposeCandidates(Model m, ModelGraph graph, ISet<string> depTables, ISet<string> fixedColumns)
        {
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in (graph.Relationships ?? Array.Empty<GraphRelationship>()).Where(r => r.IsActive))
            {
                string dim = null;
                if (string.Equals(r.ToCardinality, "One", StringComparison.OrdinalIgnoreCase) && depTables.Contains(r.FromTable)) dim = r.ToTable;
                else if (string.Equals(r.FromCardinality, "One", StringComparison.OrdinalIgnoreCase) && depTables.Contains(r.ToTable)) dim = r.FromTable;
                if (dim == null || depTables.Contains(dim)) continue;
                var gt = graph.Tables?.FirstOrDefault(x => string.Equals(x.Name, dim, StringComparison.OrdinalIgnoreCase));
                if (gt != null && gt.IsHidden) continue;
                tables.Add(dim);
            }

            var found = new List<(int Class, int Table, int Hierarchy, int Level, int Column, string Ref)>();
            var ti = -1;
            foreach (var tb in m.Tables)
            {
                ti++;
                if (!tables.Contains(tb.Name)) continue;
                var levelOf = new Dictionary<string, (int Hierarchy, int Ordinal)>(StringComparer.Ordinal);
                var fixedLevels = new List<HashSet<int>>();
                var h = 0;
                foreach (var hier in tb.Hierarchies)
                {
                    var pinned = new HashSet<int>();
                    foreach (var lv in hier.Levels.OrderBy(l => l.Ordinal))
                    {
                        if (lv.Column == null) continue;
                        if (!levelOf.ContainsKey(lv.Column.Name)) levelOf[lv.Column.Name] = (h, lv.Ordinal);
                        if (fixedColumns.Contains(ExplainLogic.ColumnKey(tb.Name, lv.Column.Name))) pinned.Add(lv.Ordinal);
                    }
                    fixedLevels.Add(pinned);
                    h++;
                }
                var cols = tb.Columns.ToList();
                var isDateTable = string.Equals(tb.DataCategory, "Time", StringComparison.OrdinalIgnoreCase) || cols.Any(c => c.IsKey && c.DataType == DataType.DateTime);
                var firstPinned = cols.FindIndex(c => fixedColumns.Contains(ExplainLogic.ColumnKey(tb.Name, c.Name)));
                for (var ci = 0; ci < cols.Count; ci++)
                {
                    var c = cols[ci];
                    if (c.Type == ColumnType.RowNumber || c.IsHidden || c.IsKey || KeyLikeName.IsMatch(c.Name)) continue;
                    if (c.DataType != DataType.String && c.DataType != DataType.Int64 && c.DataType != DataType.Boolean && c.DataType != DataType.DateTime) continue;
                    if (fixedColumns.Contains(ExplainLogic.ColumnKey(tb.Name, c.Name))) continue;
                    var cls = 2; var hi = int.MaxValue; var lo = 0;
                    if (levelOf.TryGetValue(c.Name, out var at))
                    {
                        hi = at.Hierarchy; lo = at.Ordinal;
                        cls = fixedLevels[at.Hierarchy].Any(o => o < at.Ordinal) ? 0 : 1;
                    }
                    else if (isDateTable && firstPinned >= 0 && ci > firstPinned) cls = 0;
                    found.Add((cls, ti, hi, lo, ci, ColRef(tb.Name, c.Name)));
                }
            }
            return found.OrderBy(f => f.Class).ThenBy(f => f.Table).ThenBy(f => f.Hierarchy).ThenBy(f => f.Level).ThenBy(f => f.Column)
                .Select(f => f.Ref).Take(ExplainSplitColumnCap).ToList();
        }

        /// <summary>The filter argument for each pinned member of the clicked cell. A column the model types as a date gets its members as dates, not text.</summary>
        internal async Task<string[]> ExplainMemberArgsAsync(ExplainFilter[] filters)
        {
            var wanted = filters.Where(f => !f.Empty).Select(f => f.Column).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var dates = wanted.Length == 0 ? new HashSet<string>() : await _sessions.Require().ReadAsync(m => DateColumns(m, wanted));
            return filters.Select(f => f.Empty ? DaxBench.CompileEmptyFilter(f.Column) : DaxBench.CompileMemberFilter(f.Column, f.Members, dates.Contains(f.Column))).ToArray();
        }

        private static HashSet<string> DateColumns(Model m, IEnumerable<string> columnRefs)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in columnRefs)
            {
                var table = ExplainLogic.TableOf(r);
                var open = r.LastIndexOf('['); var close = r.LastIndexOf(']');
                if (table == null || open < 0 || close <= open) continue;
                var name = r.Substring(open + 1, close - open - 1).Replace("]]", "]");
                var col = m.Tables.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase))?.Columns
                    .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (col?.DataType == DataType.DateTime) found.Add(r);
            }
            return found;
        }

        private static string ColRef(string table, string column) => "'" + table.Replace("'", "''") + "'[" + column + "]";
        private static string Bracket(string name) => "[" + name + "]";
        // Prose formatting only ("1,234,567.89") — Value stays DaxBench.Fmt (canonical, machine-comparable).
        private static string DisplayNumber(object raw)
            => raw != null && IsNum(raw)
                ? Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture).ToString("#,0.##", System.Globalization.CultureInfo.InvariantCulture)
                : DaxBench.Fmt(raw);
        private static string FirstLine(string s) => (s ?? "").Split('\n')[0].Trim();
        private static string AppendNote(string existing, string more) => string.IsNullOrEmpty(existing) ? more : existing + " " + more;

        // ---- the cell query (the probe_measure shape + the UI's CALCULATETABLE predicate wrap) -----

        /// <summary>SUMMARIZECOLUMNS(axis, member filter args, "v", expr, "__present", 1) wrapped in
        /// CALCULATETABLE(..., predicates) when raw boolean predicates exist — the exact shape the DAX
        /// Lab visual runs, so the explained value IS the visual's value. Result columns: [axis…], v,
        /// __present (v at index axis.Length); the sentinel keeps an all-blank row observable.</summary>
        internal static string BuildExplainQuery(string expr, string[] axis, string[] memberArgs, string[] predicates)
        {
            axis = (axis ?? Array.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToArray();
            memberArgs = (memberArgs ?? Array.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
            predicates = (predicates ?? Array.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();

            var args = new List<string>();
            foreach (var a in axis) args.Add("    " + a);
            foreach (var f in memberArgs) args.Add("    " + f);
            args.Add("    \"v\", " + DaxBench.InlineScalar(expr));
            args.Add("    \"__present\", 1");
            var sc = "SUMMARIZECOLUMNS(\n" + string.Join(",\n", args) + "\n)";

            string body;
            if (predicates.Length == 0) body = sc;
            else
            {
                var indented = string.Join("\n", sc.Split('\n').Select((l, i) => i == 0 ? l : "    " + l));
                body = "CALCULATETABLE(\n    " + indented + ",\n    " + string.Join(",\n    ", predicates) + "\n)";
            }
            var q = "EVALUATE\n" + body;
            if (axis.Length > 0) q += "\nORDER BY " + string.Join(", ", axis);
            return q;
        }

        /// <summary>Row selection for the header value (Finding A, PR #92 review). The cell query returns
        /// [axis…, v, __present]: exactly ONE row when every axis column is pinned to a single member.
        /// Zero rows with an axis = the visual would not render this row → an honest blank. More than one
        /// row = the context under-pins the cell → REFUSE with recovery guidance, never silently pick a row.</summary>
        internal static (object Value, bool IsBlank, string Error, string Note) PickCellValue(ResultSet rs, int axisCount)
        {
            var rows = rs?.Rows ?? Array.Empty<object[]>();
            if (rows.Length == 0)
                return (null, true, null, axisCount > 0
                    ? "There is no row for these coordinates under these filters, so the visual shows nothing here."
                    : "The query returned no rows.");
            if (rows.Length > 1)
                return (null, false,
                    $"The filter context matches {rows.Length}+ rows, so the cell is ambiguous. Pin each groupBy column with a single-member filter (one member per axis column) so exactly one cell remains.",
                    null);
            var row = rows[0];
            var v = row.Length > axisCount ? row[axisCount] : null;
            return (v, v == null, null, null);
        }

        // ---- why is this blank? ---------------------------------------------------------------------
        // Probes preserve the cell's AXIS (groupBy) so scope-sensitive measures are diagnosed in the same
        // evaluation context the cell used — e.g. IF(ISINSCOPE(...), BLANK(), [Total]) would look "fixed"
        // by removing filters if the probe silently dropped the axis, and the checklist would name the
        // wrong cause. "The number comes back" = ANY cell at this grain has a value (sampled, capped).

        private sealed class ProbeOutcome { public bool Ran; public int Rows; public int NonBlank; }

        private async Task<ProbeOutcome> ProbeCellsAsync(LiveConnection live, string expr, string[] groupBy, string[] memberArgs, string[] predicates)
        {
            try
            {
                var rs = await live.ExecuteAsync(BuildExplainQuery(expr, groupBy, memberArgs, predicates), 1000, 60);
                if (rs == null || !string.IsNullOrEmpty(rs.Error) || rs.Rows == null) return new ProbeOutcome();
                var vIdx = groupBy.Length; var nb = 0;
                foreach (var r in rs.Rows) if (r != null && r.Length > vIdx && r[vIdx] != null) nb++;
                return new ProbeOutcome { Ran = true, Rows = rs.Rows.Length, NonBlank = nb };
            }
            catch { return new ProbeOutcome(); }
        }

        private static string GrainSentence(ProbeOutcome o, string[] groupBy)
            => groupBy.Length == 0
                ? (o.NonBlank > 0 ? "the total has a value without the filters" : "even the unfiltered total is blank")
                : (o.NonBlank > 0 ? $"values appear in {o.NonBlank} of {o.Rows} sampled cell(s) at this grain without the filters"
                                  : $"all {o.Rows} sampled cell(s) at this grain are blank even without the filters");

        private async Task<BlankChecklist> BuildBlankChecklistAsync(LiveConnection live, ExplainSnapshot snap,
            string exprForEval, string[] groupBy, ExplainFilter[] memberFilters, string[] memberArgs, string[] predicates, ISet<string> filterTables)
        {
            var checks = new List<BlankCheck>();
            string likely = null;

            // 1) structure: can each filtered table's filter reach the data at all?
            foreach (var ft in filterTables.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(ExplainFilterProbeCap))
            {
                var (verdict, detail) = ExplainLogic.CheckReachability(snap.Graph, ft, snap.DepTables);
                switch (verdict)
                {
                    case ReachVerdict.NoPath:
                        checks.Add(new BlankCheck { Id = "relationship-path", Result = "cause",
                            Question = $"Can a filter on '{ft}' reach this number's data?",
                            Finding = $"No. '{ft}' has no relationship to the tables this number reads, so filtering it never reaches the data.",
                            Detail = detail });
                        likely ??= "relationship-path";
                        break;
                    case ReachVerdict.InactiveOnly:
                        checks.Add(new BlankCheck { Id = "inactive-relationship", Result = "cause",
                            Question = $"Can a filter on '{ft}' reach this number's data?",
                            Finding = $"Only through a relationship that is switched off (inactive). It is used only when a formula asks for it with USERELATIONSHIP.",
                            Detail = detail });
                        likely ??= "inactive-relationship";
                        break;
                    case ReachVerdict.DirectionBlocked:
                        checks.Add(new BlankCheck { Id = "filter-direction", Result = "cause",
                            Question = $"Can a filter on '{ft}' reach this number's data?",
                            Finding = $"The tables are connected, but filters flow the other way along that path (single direction). Your filter on '{ft}' never reaches the data.",
                            Detail = detail });
                        likely ??= "filter-direction";
                        break;
                    default:
                        checks.Add(new BlankCheck { Id = "relationship-path", Result = "ok",
                            Question = $"Can a filter on '{ft}' reach this number's data?",
                            Finding = "Yes, through active relationships.",
                            Detail = detail });
                        break;
                }
            }

            // 2) empirical: does removing filters bring the number back? (grand first, then drop-one)
            // Probes keep the cell's AXIS (Finding A): dropping it would evaluate a different scope and could
            // name the wrong cause for a scope-branching measure. "Comes back" = any sampled cell at this grain.
            var hasFilters = memberArgs.Length + predicates.Length > 0;
            var grand = await ProbeCellsAsync(live, exprForEval, groupBy, Array.Empty<string>(), Array.Empty<string>());

            if (!grand.Ran)
            {
                checks.Add(new BlankCheck { Id = "filters-remove-all-rows", Result = "skipped",
                    Question = "Do your filters remove every row?",
                    Finding = "Could not re-run the number without filters to compare." });
            }
            else if (grand.NonBlank == 0)
            {
                checks.Add(new BlankCheck { Id = "blank-by-design", Result = "cause",
                    Question = "Is the measure blank even with no filters at all?",
                    Finding = "Yes. Even with every filter removed, this measure returns blank here, so the filters are not the cause. The formula itself (or empty data) is.",
                    Detail = GrainSentence(grand, groupBy) });
                likely = "blank-by-design";
            }
            else if (hasFilters)
            {
                // drop ONE filter unit at a time; the one whose removal brings the value back is the culprit.
                var culprits = new List<string>();
                var units = memberFilters.Select((f, i) => (Label: DescribeFilter(f), Kind: 'm', Index: i))
                    .Concat(predicates.Select((p, i) => (Label: p, Kind: 'p', Index: i)))
                    .Take(ExplainFilterProbeCap).ToList();
                foreach (var u in units)
                {
                    var ma = u.Kind == 'm' ? memberArgs.Where((_, i) => i != u.Index).ToArray() : memberArgs;
                    var pr = u.Kind == 'p' ? predicates.Where((_, i) => i != u.Index).ToArray() : predicates;
                    var probe = await ProbeCellsAsync(live, exprForEval, groupBy, ma, pr);
                    if (probe.Ran && probe.NonBlank > 0) culprits.Add(u.Label);
                }
                if (culprits.Count > 0)
                {
                    checks.Add(new BlankCheck { Id = "filters-remove-all-rows", Result = "cause",
                        Question = "Do your filters remove every row?",
                        Finding = $"Yes. Removing {JoinPlain(culprits)} brings the number back, so that selection has no matching data.",
                        Detail = GrainSentence(grand, groupBy) });
                    likely = "filters-remove-all-rows";   // the empirical proof outranks structural suspicions
                }
                else
                {
                    checks.Add(new BlankCheck { Id = "filters-remove-all-rows", Result = "ok",
                        Question = "Do your filters remove every row?",
                        Finding = "Removing any single filter still leaves the number blank, so no one filter alone explains it (a combination might).",
                        Detail = GrainSentence(grand, groupBy) });
                }
            }

            // 3) formula shape: does the DAX go blank by design?
            var signals = ExplainLogic.BlankByDesignSignals(snap.Bodies);
            if (signals.Length > 0)
            {
                checks.Add(new BlankCheck { Id = "blank-by-design", Result = likely == null ? "maybe" : "ok",
                    Question = "Does the formula itself return blank on purpose in some cases?",
                    Finding = string.Join(" ", signals),
                    Detail = null });
                likely ??= "blank-by-design";
            }

            // 4) RLS advisory — the engine cannot impersonate, so this can only ever be a "maybe".
            var rls = ExplainLogic.RlsAdvisory(snap.Roles, snap.DepTables);
            if (rls.Length > 0)
            {
                checks.Add(new BlankCheck { Id = "rls", Result = "maybe",
                    Question = "Could row-level security hide these rows?",
                    Finding = $"Possibly. {rls.Length} security role(s) filter this number's tables. This tool reads the model without a role, so it cannot confirm what a secured user sees.",
                    Detail = string.Join("; ", rls.Select(r => r.Role + ": " + string.Join(", ", r.Filters.Select(f => f.Table)))) });
            }

            var summary = likely switch
            {
                "filters-remove-all-rows" => "Most likely: your filters leave no matching rows.",
                "relationship-path" => "Most likely: a filter sits on a table with no relationship to this number's data.",
                "inactive-relationship" => "Most likely: the only connection between your filter and the data is an inactive relationship.",
                "filter-direction" => "Most likely: filters cannot flow in that direction along the relationship.",
                "blank-by-design" => "Most likely: the formula itself returns blank here (or the data is empty even unfiltered).",
                _ => "No single cause stands out from these checks.",
            };
            return new BlankChecklist { Checks = checks.ToArray(), LikelyCauseId = likely, Summary = summary };
        }

        private static string DescribeFilter(ExplainFilter f)
            => f.Empty ? $"the empty selection on {f.Column}"
             : $"the filter on {f.Column} ({string.Join(", ", (f.Members ?? Array.Empty<string>()).Take(3))}{((f.Members?.Length ?? 0) > 3 ? ", …" : "")})";

        private static string JoinPlain(List<string> items)
            => items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " or " + items[items.Count - 1];

        // The measures this one is built from, each worked out in the same cell. A driver that will not compute or is blank is left out.
        private async Task<ExplainDriver[]> ExplainDriversAsync(LiveConnection live, ExplainSnapshot snap, string[] groupBy, string[] memberArgs, string[] predicates)
        {
            var drivers = new List<ExplainDriver>();
            foreach (var (name, format) in snap.Drivers ?? new List<(string, string)>())
            {
                try
                {
                    var rs = await live.ExecuteAsync(BuildExplainQuery(Baseline.MeasureRefExpr(name), groupBy, memberArgs, predicates), 3, 60);
                    if (rs == null || !string.IsNullOrEmpty(rs.Error)) continue;
                    var pick = PickCellValue(rs, groupBy.Length);
                    if (pick.Error != null || pick.IsBlank || !IsNum(pick.Value)) continue;
                    drivers.Add(new ExplainDriver { Name = name, Value = DaxBench.Fmt(pick.Value), Format = format });
                }
                catch { /* a driver is a help, never a failure */ }
            }
            return drivers.ToArray();
        }

        // ---- contributors (the one new algorithm — bounded, deterministic, non-additive-guarded) ----

        private async Task<ExplainContributors> BuildContributorsAsync(LiveConnection live, ExplainSnapshot snap,
            string exprForEval, string[] groupBy, string[] memberArgs, string[] predicates, string decomposeBy, int topK, double cellValue, ISet<string> filterTables)
        {
            // The person (or the assistant) named the column: split by exactly that, as asked.
            if (!string.IsNullOrWhiteSpace(decomposeBy))
            {
                var dim = decomposeBy.Trim();
                if (groupBy.Any(g => string.Equals(g, dim, StringComparison.OrdinalIgnoreCase)))
                    return new ExplainContributors { Dimension = dim, Additive = false, Note = "That column is already fixed for this number, so there is nothing to split. Pick a different column." };
                var one = await ReadSplitAsync(live, exprForEval, groupBy, memberArgs, predicates, dim, ExplainMemberCap);
                if (one.Note != null) return new ExplainContributors { Dimension = dim, Additive = false, Note = one.Note };
                var c = ExplainLogic.BuildContributors(dim, cellValue, one.Trial.Members, one.Trial.Truncated, topK);
                if (!c.Additive && !one.Trial.Truncated) c.Spread = ExplainLogic.Spread(one.Trial.Members);
                return c;
            }

            var candidates = snap.DecomposeCandidates ?? new List<string>();
            if (candidates.Count == 0)
                return new ExplainContributors { Additive = false, Note = "No related column was found to break this number down by." };

            // One cheap query counts the values of every candidate; the ones with few values are tried first.
            var counts = await DistinctCountsAsync(live, candidates);
            // A date table holds many columns of few values; they must not use up every probe. Round-robin by table: the first candidate (the date's
            // next level when a date is pinned) leads its table, the table's other columns go fewest values first, and each round takes one per table.
            var eligible = Enumerable.Range(0, candidates.Count).Where(i => counts == null || (counts[i] >= 2 && counts[i] <= 100)).ToList();
            var byTable = eligible.GroupBy(i => ExplainLogic.TableOfRef(candidates[i])).Select(g => g
                .OrderBy(i => i == eligible[0] ? 0 : 1)
                .ThenBy(i => counts != null && counts[i] <= ExplainLogic.SplitFewMax ? 0 : 1)
                .ThenBy(i => counts != null && counts[i] <= ExplainLogic.SplitFewMax ? counts[i] : 0)
                .ThenBy(i => i).ToList()).ToList();   // GroupBy keeps the tables in the order they first appear
            var picked = new List<int>();
            for (var round = 0; picked.Count < ExplainSplitProbes && byTable.Any(t => round < t.Count); round++)
                foreach (var t in byTable)
                    if (round < t.Count && picked.Count < ExplainSplitProbes) picked.Add(t[round]);
            var order = picked.OrderBy(i => i).ToList();   // tried in the model's order, which is the tie-break

            var trials = new List<SplitTrial>();
            foreach (var i in order)
            {
                var r = await ReadSplitAsync(live, exprForEval, groupBy, memberArgs, predicates, candidates[i], ExplainSplitProbeRows);
                if (r.Trial != null) trials.Add(r.Trial);
            }
            return ExplainLogic.PickSplit(trials, cellValue, topK);
        }

        // How many values each candidate column holds in the whole model (null when the server will not say: every candidate is then tried).
        private static async Task<long[]> DistinctCountsAsync(LiveConnection live, List<string> columns)
        {
            try
            {
                var q = "EVALUATE ROW(" + string.Join(", ", columns.Select((c, i) => $"\"c{i}\", DISTINCTCOUNT({c})")) + ")";
                var rs = await live.ExecuteAsync(q, 2, 60);
                var row = rs?.Rows?.FirstOrDefault();
                if (rs == null || !string.IsNullOrEmpty(rs.Error) || row == null || row.Length < columns.Count) return null;
                var counts = new long[columns.Count];
                for (var i = 0; i < counts.Length; i++)
                {
                    if (row[i] == null || !IsNum(row[i])) return null;
                    counts[i] = Convert.ToInt64(row[i], System.Globalization.CultureInfo.InvariantCulture);
                }
                return counts;
            }
            catch { return null; }
        }

        // The cell, further split by one column: each member and its value, "this cell, one member". Note is set when it could not be worked out.
        private async Task<(SplitTrial Trial, string Note)> ReadSplitAsync(LiveConnection live, string exprForEval, string[] groupBy, string[] memberArgs, string[] predicates, string dim, int memberCap)
        {
            // The cell's OWN context (axis + filters, Finding A) with the split column appended as the last axis column.
            var axis = groupBy.Concat(new[] { dim }).ToArray();
            var mIdx = groupBy.Length;        // the split column
            var vIdx = axis.Length;           // the value column
            ResultSet rs;
            try { rs = await live.ExecuteAsync(BuildExplainQuery(exprForEval, axis, memberArgs, predicates), memberCap + 1, 120); }
            catch (Exception ex) { return (null, "The breakdown could not be worked out: " + FirstLine(ex.Message)); }
            if (rs == null || !string.IsNullOrEmpty(rs.Error))
                return (null, "The breakdown could not be worked out: " + FirstLine(rs?.Error ?? "no result"));

            var members = new List<KeyValuePair<string, double>>();
            foreach (var row in rs.Rows ?? Array.Empty<object[]>())
            {
                if (row == null || row.Length <= vIdx || row[vIdx] == null) continue;   // blank member rows contribute nothing
                if (!IsNum(row[vIdx])) return (null, "The values are not numbers here, so there is no breakdown.");
                var member = row[mIdx] == null ? "(blank)" : Convert.ToString(row[mIdx], System.Globalization.CultureInfo.InvariantCulture);
                members.Add(new KeyValuePair<string, double>(member, Convert.ToDouble(row[vIdx], System.Globalization.CultureInfo.InvariantCulture)));
            }
            return (new SplitTrial { Dimension = dim, Members = members, Truncated = rs.Truncated || members.Count > memberCap }, null);
        }

        // ---- shared evidence: audit digest + the live activity broadcast (both doors see it) --------

        internal async Task FinishExplainAsync(Session s, ExplainDossier d, string origin)
        {
            var verdict = d.ValueEvaluated ? (d.IsBlank == true ? "blank-explained" : "explained") : "offline";
            // Audit copy ONLY when a value was actually observed (live) — an offline metadata dossier has no
            // measured evidence to preserve, and a free READ op must not litter the trail with empty records.
            if (d.ValueEvaluated)
                await RecordAnalysisAsync(s, new VerifiedEditRecord
                {
                    SessionId = s.Id, Revision = 0, Origin = origin ?? "agent", Op = "explain_value", ObjectRef = d.Measure,   // 0: a read mutates nothing
                    Verdict = verdict,
                    Summary = Truncate(d.Summary, 400),
                    Evidence = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        value = d.Value,
                        isBlank = d.IsBlank,
                        valueEvaluated = d.ValueEvaluated,
                        context = new
                        {
                            filters = (d.Context?.Filters ?? Array.Empty<ExplainFilter>()).Take(8).Select(f => new { f.Column, members = (f.Members ?? Array.Empty<string>()).Take(4), f.Empty }).ToArray(),
                            extraPredicates = (d.Context?.ExtraPredicates ?? Array.Empty<string>()).Take(4).Select(p => Truncate(p, 200)).ToArray(),
                        },
                        blankLikelyCause = d.Blank?.LikelyCauseId,
                        blankChecks = (d.Blank?.Checks ?? Array.Empty<BlankCheck>()).Select(c => new { c.Id, c.Result, finding = Truncate(c.Finding, 300) }).ToArray(),
                        contributors = d.Contributors == null ? null : new
                        {
                            d.Contributors.Dimension, d.Contributors.Additive, d.Contributors.Truncated,
                            rows = d.Contributors.Rows.Take(8).Select(r => new { r.Member, r.Value, r.Pct }).ToArray(),
                            note = Truncate(d.Contributors.Note, 300),
                        },
                        chain = d.Chain.Length,
                        rlsRoles = d.Rls.Select(r => r.Role).ToArray(),
                        evidenceAvailable = d.Evidence?.Available ?? false,
                    }),
                });
            try
            {
                await PublishActivityAsync(new ActivityEvent
                {
                    Kind = "explain_value", Origin = origin ?? "agent",
                    Label = $"Explained {Bracket(d.Name ?? d.Measure)}" + (d.IsBlank == true ? " (blank)" : d.Value != null ? $" = {d.Value}" : ""),
                    Target = d.Measure, Ok = d.Status == "ok", Result = d,
                });
            }
            catch { /* live feed is best-effort */ }
        }
    }
}

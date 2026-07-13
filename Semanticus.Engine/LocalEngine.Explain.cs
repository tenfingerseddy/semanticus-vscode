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
        private const int ExplainFilterProbeCap = 6;    // drop-one-filter probes (bounded live cost)

        public async Task<ExplainDossier> ExplainValueAsync(string measureRef, ExplainFilterContext context, bool decompose, string decomposeBy, int topK, string origin)
        {
            var s = _sessions.Require();
            if (string.IsNullOrWhiteSpace(measureRef))
                return new ExplainDossier { Status = "error", Error = "measureRef is required — the measure whose number you want explained (e.g. 'measure:Sales/Total Sales', or just its name). Run list_measures to see them." };

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

            // ---- ONE dispatcher read: resolve + chain BFS + lineage + graph + roles + decompose candidates ----
            ExplainSnapshot snap;
            try
            {
                snap = await s.ReadAsync(m => BuildExplainSnapshot(m, measureRef, filterTables));
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
            };

            var exprForEval = Baseline.MeasureRefExpr(snap.Name);
            var memberArgs = memberFilters
                .Select(f => f.Empty ? DaxBench.CompileEmptyFilter(f.Column) : DaxBench.CompileMemberFilter(f.Column, f.Members))
                .ToArray();
            var cellQuery = BuildExplainQuery(exprForEval, groupBy, memberArgs, predicates);

            var live = _live;
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
                d.Evidence = new ExplainEvidence { Available = false, Query = cellQuery, Note = "The value query failed: " + evalError };
                d.Summary = $"The number could not be computed here ({FirstLine(evalError)}). If you just created or edited this measure, the live model may not have it yet: deploy or refresh first, then try again.";
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
                    d.Contributors = new ExplainContributors { Additive = false, Note = "This value is not a number, so there is no sum-of-parts breakdown." };
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
        }

        private ExplainSnapshot BuildExplainSnapshot(Model m, string measureRef, ISet<string> filterTables)
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
                DecomposeCandidates = FindDecomposeCandidates(m, graph, depTables, filterTables),
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
                throw new InvalidOperationException($"{measureRef} was not found or is not a measure — explain_value explains a measure's number. Run list_measures to find the right ref.");
            }
            var name = t.StartsWith("[") && t.EndsWith("]") ? t.Substring(1, t.Length - 2) : t;
            var hits = m.Tables.SelectMany(tb => tb.Measures).Where(me => string.Equals(me.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (hits.Length == 1) return hits[0];
            if (hits.Length > 1)
                throw new InvalidOperationException($"'{name}' exists on more than one table ({string.Join(", ", hits.Select(h => ObjectRefs.For(h)))}) — pass the full ref.");
            throw new InvalidOperationException($"No measure named '{name}' — run list_measures to see the model's measures.");
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

        // Deterministic decompose-dimension candidates: dimension tables one active hop from the measure's
        // data tables (filter flows dimension → data), skipping hidden tables and tables the context
        // already filters; within a table prefer a text column, then a key, then anything visible.
        private static List<string> FindDecomposeCandidates(Model m, ModelGraph graph, ISet<string> depTables, ISet<string> filterTables)
        {
            var tables = new List<string>();
            foreach (var r in (graph.Relationships ?? Array.Empty<GraphRelationship>()).Where(r => r.IsActive))
            {
                string dim = null;
                if (string.Equals(r.ToCardinality, "One", StringComparison.OrdinalIgnoreCase) && depTables.Contains(r.FromTable)) dim = r.ToTable;
                else if (string.Equals(r.FromCardinality, "One", StringComparison.OrdinalIgnoreCase) && depTables.Contains(r.ToTable)) dim = r.FromTable;
                if (dim == null || filterTables.Contains(dim) || tables.Contains(dim)) continue;
                var gt = graph.Tables?.FirstOrDefault(x => string.Equals(x.Name, dim, StringComparison.OrdinalIgnoreCase));
                if (gt != null && gt.IsHidden) continue;
                tables.Add(dim);
            }
            tables.Sort(StringComparer.OrdinalIgnoreCase);

            var refs = new List<string>();
            foreach (var name in tables)
            {
                if (!m.Tables.Contains(name)) continue;
                var tb = m.Tables[name];
                var visible = tb.Columns.Where(c => c.Type != ColumnType.RowNumber && !c.IsHidden).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var pick = visible.FirstOrDefault(c => c.DataType == DataType.String && !c.IsKey)
                        ?? visible.FirstOrDefault(c => c.IsKey)
                        ?? visible.FirstOrDefault();
                if (pick != null) refs.Add(ColRef(tb.Name, pick.Name));
            }
            return refs;
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

        // ---- contributors (the one new algorithm — bounded, deterministic, non-additive-guarded) ----

        private async Task<ExplainContributors> BuildContributorsAsync(LiveConnection live, ExplainSnapshot snap,
            string exprForEval, string[] groupBy, string[] memberArgs, string[] predicates, string decomposeBy, int topK, double cellValue, ISet<string> filterTables)
        {
            var dim = !string.IsNullOrWhiteSpace(decomposeBy) ? decomposeBy.Trim() : snap.DecomposeCandidates.FirstOrDefault();
            if (dim == null)
                return new ExplainContributors { Additive = false, Note = "No related dimension column was found to break this number down by. Pass decomposeBy (a 'Table'[Column] one relationship hop from the data) to choose one." };
            if (groupBy.Any(g => string.Equals(g, dim, StringComparison.OrdinalIgnoreCase)))
                return new ExplainContributors { Dimension = dim, Additive = false, Note = "That column already pins this cell (it is one of the visual's axes), so there is nothing to split. Pick a different column (pass decomposeBy)." };

            // The cell's OWN context (axis + filters — Finding A) with the decompose dim appended as the last
            // axis column, so each part is exactly "this cell, further split by one member".
            var axis = groupBy.Concat(new[] { dim }).ToArray();
            var mIdx = groupBy.Length;        // the dim member column
            var vIdx = axis.Length;           // the value column
            ResultSet rs;
            try { rs = await live.ExecuteAsync(BuildExplainQuery(exprForEval, axis, memberArgs, predicates), ExplainMemberCap + 1, 120); }
            catch (Exception ex) { return new ExplainContributors { Dimension = dim, Additive = false, Note = "The breakdown query failed: " + FirstLine(ex.Message) }; }
            if (rs == null || !string.IsNullOrEmpty(rs.Error))
                return new ExplainContributors { Dimension = dim, Additive = false, Note = "The breakdown query failed: " + FirstLine(rs?.Error ?? "no result") + (string.IsNullOrWhiteSpace(decomposeBy) ? "" : " Check the decomposeBy column ref ('Table'[Column].)") };

            var members = new List<KeyValuePair<string, double>>();
            var allNumeric = true;
            foreach (var row in rs.Rows ?? Array.Empty<object[]>())
            {
                if (row == null || row.Length <= vIdx || row[vIdx] == null) continue;   // blank member rows contribute nothing
                if (!IsNum(row[vIdx])) { allNumeric = false; break; }
                var member = row[mIdx] == null ? "(blank)" : Convert.ToString(row[mIdx], System.Globalization.CultureInfo.InvariantCulture);
                members.Add(new KeyValuePair<string, double>(member, Convert.ToDouble(row[vIdx], System.Globalization.CultureInfo.InvariantCulture)));
            }
            if (!allNumeric)
                return new ExplainContributors { Dimension = dim, Additive = false, Note = "The values are not numbers here, so there is no sum-of-parts breakdown." };

            var truncated = rs.Truncated || members.Count > ExplainMemberCap;
            var c = ExplainLogic.BuildContributors(dim, cellValue, members, truncated, topK);
            if (string.IsNullOrWhiteSpace(decomposeBy) && snap.DecomposeCandidates.Count > 1)
                c.Note = AppendNote(c.Note, "Other columns to try: " + string.Join(", ", snap.DecomposeCandidates.Where(x => x != dim).Take(3)) + " (pass decomposeBy).");
            return c;
        }

        // ---- shared evidence: audit digest + the live activity broadcast (both doors see it) --------

        private async Task FinishExplainAsync(Session s, ExplainDossier d, string origin)
        {
            var verdict = d.ValueEvaluated ? (d.IsBlank == true ? "blank-explained" : "explained") : "offline";
            // Audit copy ONLY when a value was actually observed (live) — an offline metadata dossier has no
            // measured evidence to preserve, and a free READ op must not litter the trail with empty records.
            if (d.ValueEvaluated)
                await RecordVerifiedEditAsync(s, new VerifiedEditRecord
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

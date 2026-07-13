using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    // ================================================================================================
    // Explain This Number (feature #2) — wire DTOs + the PURE decision logic. The engine assembles a
    // deterministic evidence dossier for one cell of one measure (value re-derived in the cell's exact
    // filter context, dependency chain, source lineage, top contributors, the why-is-this-blank
    // checklist, RLS advisory); it NEVER narrates — the user's own Claude / the human reads the dossier.
    // FREE (read-only, no gate). Everything here is pure so the honesty rules (the non-additive guard,
    // the reachability verdicts) are unit-testable OFFLINE; the query orchestration lives in
    // LocalEngine.Explain.cs. Filter-context vocabulary is the probe_measure one (ProbeFilter/TREATAS).
    // ================================================================================================

    /// <summary>One slicer-like filter on a column — the probe_measure ProbeFilter shape, reused so a
    /// pivot cell maps 1:1 (a Row/Col key = a single-member filter; Empty = the empty selection).</summary>
    public sealed class ExplainFilter
    {
        public string Column { get; set; }                                    // 'Table'[Column]
        public string[] Members { get; set; } = Array.Empty<string>();        // selected members (TREATAS)
        public bool Empty { get; set; }                                       // true ⇒ empty selection (FILTER(ALL,FALSE()))
    }

    /// <summary>The cell's filter context. GroupBy is echoed for provenance (a fully pinned cell has no
    /// free axis); ExtraPredicates are raw DAX boolean lines from the UI's filter well (passed as
    /// SUMMARIZECOLUMNS filter args verbatim, the same way pivot_measure takes them).</summary>
    public sealed class ExplainFilterContext
    {
        public string[] GroupBy { get; set; } = Array.Empty<string>();
        public ExplainFilter[] Filters { get; set; } = Array.Empty<ExplainFilter>();
        public string[] ExtraPredicates { get; set; } = Array.Empty<string>();
    }

    /// <summary>One node of the measure's dependency chain (BFS over DependsOn, depth from the measure).</summary>
    public sealed class ExplainChainNode
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }    // measure | column | calccolumn | table | function | …
        public int Depth { get; set; }      // 1 = directly referenced by the measure
    }

    /// <summary>Where one leaf column's data comes from (its table's partition source, summarized).</summary>
    public sealed class ExplainLineageEntry
    {
        public string Column { get; set; }  // column ref
        public string Table { get; set; }
        public string Source { get; set; }  // e.g. "Import (M): Sql.Database(\"srv\",\"DW\")…" | "Calculated (DAX)"
    }

    public sealed class ExplainContributorRow
    {
        public string Member { get; set; }
        public string Value { get; set; }   // formatted (DaxBench.Fmt)
        public double? Pct { get; set; }    // share of the cell's value (null when the total is 0)
    }

    /// <summary>Sum-of-parts breakdown of the cell over one dimension — ONLY when the parts provably sum
    /// to the total. Additive=false means the guard fired: Rows stays EMPTY (a breakdown would mislead
    /// for distinct counts / ratios / MIN-MAX / semi-additive measures) and Note says so plainly.</summary>
    public sealed class ExplainContributors
    {
        public string Dimension { get; set; }       // 'Table'[Column] (null = no dimension could be chosen)
        public bool Additive { get; set; }
        public ExplainContributorRow[] Rows { get; set; } = Array.Empty<ExplainContributorRow>();
        public bool Truncated { get; set; }         // the member scan hit its cap — parts are incomplete
        public string Note { get; set; }
    }

    /// <summary>One check of the why-is-this-blank checklist. Result vocabulary:
    /// ok (ruled out) | cause (likely cause found) | maybe (possible, can't confirm) | skipped (couldn't run).</summary>
    public sealed class BlankCheck
    {
        public string Id { get; set; }        // filters-remove-all-rows | relationship-path | inactive-relationship | filter-direction | blank-by-design | rls
        public string Question { get; set; }  // plain language, no jargon
        public string Result { get; set; }
        public string Finding { get; set; }   // plain-language answer
        public string Detail { get; set; }    // the technical specifics (tables / relationship names / DAX)
    }

    public sealed class BlankChecklist
    {
        public BlankCheck[] Checks { get; set; } = Array.Empty<BlankCheck>();
        public string LikelyCauseId { get; set; }   // the strongest proven signal (null = couldn't pin one down)
        public string Summary { get; set; }         // one plain sentence for the panel headline
    }

    /// <summary>A security role whose row filters touch this number's tables. ADVISORY ONLY: the engine
    /// has no view-as/impersonation, so this lists roles that WOULD filter the data — never "computed
    /// under role X".</summary>
    public sealed class ExplainRlsRole
    {
        public string Role { get; set; }
        public TablePermissionInfo[] Filters { get; set; } = Array.Empty<TablePermissionInfo>();
    }

    /// <summary>Optional live evidence (server timings for the cell's query). Absent/Available=false
    /// offline or when the endpoint refuses a trace — the dossier renders fully without it.</summary>
    public sealed class ExplainEvidence
    {
        public bool Available { get; set; }
        public string Query { get; set; }       // the exact DAX the value came from (echoed for transparency)
        public long? TotalMs { get; set; }
        public long? FeMs { get; set; }
        public long? SeMs { get; set; }
        public int? SeQueries { get; set; }
        public string Note { get; set; }
    }

    /// <summary>The Explain-This-Number dossier — ONE shape across all three transports (op return,
    /// the explain_value ActivityEvent, the audit-trail evidence digest).</summary>
    public sealed class ExplainDossier
    {
        public string Status { get; set; }          // ok | error
        public string Measure { get; set; }         // canonical ref
        public string Name { get; set; }
        public string Expression { get; set; }      // the measure's current DAX body
        public ExplainFilterContext Context { get; set; }
        public bool ValueEvaluated { get; set; }    // false offline (metadata-only dossier)
        public string Value { get; set; }           // formatted; null when not evaluated
        public bool? IsBlank { get; set; }          // null when not evaluated
        public string Summary { get; set; }         // the plain-language lead sentence (the UI headline)
        public ExplainChainNode[] Chain { get; set; } = Array.Empty<ExplainChainNode>();
        public bool ChainTruncated { get; set; }
        public ExplainLineageEntry[] Lineage { get; set; } = Array.Empty<ExplainLineageEntry>();
        public ExplainContributors Contributors { get; set; }   // null when blank / offline / decompose=false
        public BlankChecklist Blank { get; set; }                // null unless the value is blank
        public ExplainRlsRole[] Rls { get; set; } = Array.Empty<ExplainRlsRole>();
        public ExplainEvidence Evidence { get; set; } = new ExplainEvidence();
        public string Note { get; set; }
        public string Error { get; set; }
    }

    // ================================================================================================
    // The pure logic. No TOM, no queries — inputs in, verdicts out (unit-tested in ExplainValueTests).
    // ================================================================================================

    /// <summary>Can a filter on FilterTable reach the measure's data tables, and if not, why not?</summary>
    public enum ReachVerdict
    {
        SameTable,          // the filter sits on a data table itself
        Connected,          // an active relationship path exists AND filters flow along it
        DirectionBlocked,   // connected by active relationships, but the filter flows the other way
        InactiveOnly,       // connected only through inactive relationship(s) (needs USERELATIONSHIP)
        NoPath,             // no relationship path at all
    }

    public static class ExplainLogic
    {
        // Sum-vs-total tolerance — the DaxBench.ValuesEqual band (float reorder noise, never a real gap).
        public static bool SumMatchesTotal(double sum, double total)
        {
            var diff = Math.Abs(sum - total);
            var scale = Math.Max(Math.Abs(sum), Math.Abs(total));
            return diff <= 1e-9 || diff <= scale * 1e-7;
        }

        /// <summary>"'Table'[Column]" / "Table[Column]" → "Table" (null when unparseable).</summary>
        public static string TableOf(string columnRef)
        {
            if (string.IsNullOrWhiteSpace(columnRef)) return null;
            var t = columnRef.Trim();
            var br = t.IndexOf('[');
            if (br <= 0) return null;
            var head = t.Substring(0, br).Trim();
            if (head.Length >= 2 && head[0] == '\'' && head[head.Length - 1] == '\'')
                return head.Substring(1, head.Length - 2).Replace("''", "'");
            return head.Length > 0 ? head : null;
        }

        /// <summary>Every 'Table' quoted-name mentioned in a raw DAX predicate (for filter-table discovery).</summary>
        public static IEnumerable<string> TablesInPredicate(string dax)
        {
            if (string.IsNullOrEmpty(dax)) yield break;
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(dax, @"'((?:[^']|'')+)'\s*\["))
                yield return m.Groups[1].Value.Replace("''", "'");
        }

        /// <summary>Reachability of the measure's data tables from a filter table, honoring filter-flow
        /// direction (One→Many always; Many→One only for both-directions or one-to-one relationships).
        /// Detail names the path (or the inactive relationships standing in the way).</summary>
        public static (ReachVerdict Verdict, string Detail) CheckReachability(
            ModelGraph graph, string filterTable, ISet<string> depTables)
        {
            if (graph == null || string.IsNullOrEmpty(filterTable) || depTables == null || depTables.Count == 0)
                return (ReachVerdict.NoPath, null);
            if (depTables.Contains(filterTable)) return (ReachVerdict.SameTable, filterTable);

            var rels = graph.Relationships ?? Array.Empty<GraphRelationship>();

            // 1) active + direction-honoring: the way a real slicer's filter actually flows.
            if (Bfs(rels, filterTable, depTables, activeOnly: true, honorDirection: true, out var path))
                return (ReachVerdict.Connected, string.Join(" → ", path));

            // 2) active, ignoring direction: connected, but the filter can't flow that way.
            if (Bfs(rels, filterTable, depTables, activeOnly: true, honorDirection: false, out path))
                return (ReachVerdict.DirectionBlocked, string.Join(" → ", path));

            // 3) all relationships, ignoring direction: only inactive edge(s) connect them.
            if (Bfs(rels, filterTable, depTables, activeOnly: false, honorDirection: false, out path))
            {
                var inactive = InactiveOnPath(rels, path);
                return (ReachVerdict.InactiveOnly, string.Join(" → ", path)
                    + (inactive.Count > 0 ? " (inactive: " + string.Join(", ", inactive) + ")" : ""));
            }

            return (ReachVerdict.NoPath, filterTable);
        }

        // Filter propagation across one relationship: from the ONE side to the MANY side; both ways for
        // both-directions cross-filter or a one-to-one relationship.
        private static bool Flows(GraphRelationship r, string from, string to)
        {
            var both = string.Equals(r.CrossFilter, "BothDirections", StringComparison.OrdinalIgnoreCase)
                       || (IsOne(r.FromCardinality) && IsOne(r.ToCardinality));
            if (both) return true;
            // filter on the One side reaches the Many side
            if (IsOne(r.FromCardinality) && r.FromTable == from && r.ToTable == to) return true;
            if (IsOne(r.ToCardinality) && r.ToTable == from && r.FromTable == to) return true;
            return false;
        }
        private static bool IsOne(string cardinality) => string.Equals(cardinality, "One", StringComparison.OrdinalIgnoreCase);

        private static bool Bfs(GraphRelationship[] rels, string start, ISet<string> targets,
            bool activeOnly, bool honorDirection, out List<string> path)
        {
            path = null;
            var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [start] = null };
            var queue = new Queue<string>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var r in rels)
                {
                    if (activeOnly && !r.IsActive) continue;
                    string next = null;
                    if (r.FromTable == cur && !parent.ContainsKey(r.ToTable ?? "")) next = r.ToTable;
                    else if (r.ToTable == cur && !parent.ContainsKey(r.FromTable ?? "")) next = r.FromTable;
                    if (next == null) continue;
                    if (honorDirection && !Flows(r, cur, next)) continue;
                    parent[next] = cur;
                    if (targets.Contains(next)) { path = Unwind(parent, next); return true; }
                    queue.Enqueue(next);
                }
            }
            return false;
        }

        private static List<string> Unwind(Dictionary<string, string> parent, string end)
        {
            var path = new List<string>();
            for (var cur = end; cur != null; cur = parent[cur]) path.Add(cur);
            path.Reverse();
            return path;
        }

        private static List<string> InactiveOnPath(GraphRelationship[] rels, List<string> path)
        {
            var names = new List<string>();
            for (var i = 0; i + 1 < path.Count; i++)
            {
                var a = path[i]; var b = path[i + 1];
                var r = rels.FirstOrDefault(x => !x.IsActive
                    && ((x.FromTable == a && x.ToTable == b) || (x.FromTable == b && x.ToTable == a)));
                if (r != null) names.Add($"{r.FromTable}[{r.FromColumn}] → {r.ToTable}[{r.ToColumn}]");
            }
            return names;
        }

        /// <summary>THE NON-ADDITIVE GUARD + the breakdown. Only when Σ(parts) provably equals the cell's
        /// value does a sum-of-parts list ship; otherwise Rows stays empty and the note says why in plain
        /// words. A truncated member scan can't prove additivity, so it also refuses (honestly).</summary>
        public static ExplainContributors BuildContributors(
            string dimension, double total, IReadOnlyList<KeyValuePair<string, double>> memberValues,
            bool truncated, int topK)
        {
            topK = Math.Max(1, Math.Min(topK <= 0 ? 5 : topK, 25));
            var c = new ExplainContributors { Dimension = dimension, Truncated = truncated };
            if (memberValues == null || memberValues.Count == 0)
            {
                c.Additive = false;
                c.Note = "No values to break down for this selection.";
                return c;
            }
            if (truncated)
            {
                c.Additive = false;
                c.Note = "This column has too many values to check that the parts add up to the total, so no breakdown is shown. Try a column with fewer values (pass decomposeBy).";
                return c;
            }
            var sum = memberValues.Sum(kv => kv.Value);
            if (!SumMatchesTotal(sum, total))
            {
                c.Additive = false;
                c.Note = "The parts do not add up to the total for this measure (" + DaxBench.Fmt(sum) + " vs " + DaxBench.Fmt(total) + "). "
                       + "That is normal for distinct counts, ratios, min/max, and opening/closing balances. A top-contributors list would be misleading here, so none is shown.";
                return c;
            }
            c.Additive = true;
            var ordered = memberValues.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            var top = ordered.Take(topK).ToList();
            var rows = top.Select(kv => new ExplainContributorRow
            {
                Member = kv.Key,
                Value = DaxBench.Fmt(kv.Value),
                Pct = total == 0 ? (double?)null : Math.Round(kv.Value / total * 100, 1),
            }).ToList();
            if (ordered.Count > top.Count)
            {
                var rest = total - top.Sum(kv => kv.Value);
                rows.Add(new ExplainContributorRow
                {
                    Member = $"(everything else, {ordered.Count - top.Count} more)",
                    Value = DaxBench.Fmt(rest),
                    Pct = total == 0 ? (double?)null : Math.Round(rest / total * 100, 1),
                });
                c.Truncated = true;
            }
            c.Rows = rows.ToArray();
            c.Note = $"The largest parts of this number, split by {dimension}. The parts add up to the total.";
            return c;
        }

        /// <summary>Static blank-by-construction signals in the chain's DAX bodies (DIVIDE / ISBLANK /
        /// BLANK()) — the offline half of the blank-by-design check. Capped, plain-language lines.</summary>
        public static string[] BlankByDesignSignals(IEnumerable<KeyValuePair<string, string>> namedBodies)
        {
            var lines = new List<string>();
            foreach (var kv in namedBodies ?? Array.Empty<KeyValuePair<string, string>>())
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(kv.Value, @"\bDIVIDE\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    lines.Add($"{kv.Key} uses DIVIDE, which returns blank when the denominator is zero or blank.");
                else if (System.Text.RegularExpressions.Regex.IsMatch(kv.Value, @"\bISBLANK\s*\(|\bBLANK\s*\(\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    lines.Add($"{kv.Key} deliberately returns blank in some cases (it uses BLANK or ISBLANK).");
                if (lines.Count >= 4) break;
            }
            return lines.ToArray();
        }

        /// <summary>Roles whose row filters touch the measure's data tables — the RLS ADVISORY (the engine
        /// cannot impersonate a role, so this is "would filter", never "did filter").</summary>
        public static ExplainRlsRole[] RlsAdvisory(RoleInfo[] roles, ISet<string> depTables)
        {
            if (roles == null || depTables == null || depTables.Count == 0) return Array.Empty<ExplainRlsRole>();
            return roles
                .Select(r => new ExplainRlsRole
                {
                    Role = r.Name,
                    Filters = (r.TableFilters ?? Array.Empty<TablePermissionInfo>())
                        .Where(f => f.Table != null && depTables.Contains(f.Table)).ToArray(),
                })
                .Where(r => r.Filters.Length > 0)
                .ToArray();
        }
    }
}

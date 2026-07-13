using System;
using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;

namespace Semanticus.Engine.Lineage
{
    /// <summary>
    /// Assembles the offline (Phase-1) lineage graph and computes forward Impact + the reverse Safe-to-remove sweep,
    /// purely from TOM — the model-internal half of the "Measure Killer" feature. Every method runs ON the dispatcher
    /// thread (called inside <c>Session.ReadAsync</c>) because it walks live wrapper objects. Report usage can be
    /// composed by the report-aware operations; these base results stay explicitly model-only.
    ///
    /// The safe-to-remove sweep is deliberately CONSERVATIVE — it advises DELETION, so it must NEVER report "safe"
    /// for an object that is in use. It accounts for every model-side referencer scope: DAX dependents, relationship
    /// keys, sort-by, hierarchies, variations (date drill-down), AlternateOf (aggregations), GroupByColumns,
    /// Object-Level Security (a metadata control invisible to the DAX graph), RLS row filters, and calculation items
    /// (whose visibility TOM's AnyVisible cannot see). When a referencer's live-ness can't be determined offline it
    /// returns the tri-state "caution" verdict rather than claiming the object dead. Spec: docs/lineage-impact-plan.md.
    /// </summary>
    internal static class LineageGraph
    {
        public const string ModelOnlyCaveat =
            "Model-only. Published-report field usage is excluded from this result. A column or measure used only by a report " +
            "can still appear unused. Analyze the relevant reports or run an impact assessment before deleting.";

        public const string ImpactCaveat =
            "Model-only. Downstream published reports and visuals are excluded from this impact set. Run an impact assessment with the relevant report definitions before changing it.";

        // ---- Full graph (get_lineage) ----------------------------------------------------------------------------

        public static LineageResult Build(Model m)
        {
            var nodes = new List<LineageNode>();
            var nodeRefs = new HashSet<string>(StringComparer.Ordinal);
            void AddNode(LineageNode n) { if (!string.IsNullOrEmpty(n.Ref) && nodeRefs.Add(n.Ref)) nodes.Add(n); }
            var edges = new List<LineageEdge>();

            foreach (var t in m.Tables)
            {
                var tref = ObjectRefs.For(t);
                AddNode(new LineageNode { Ref = tref, Name = t.Name, Kind = TableKind(t), IsHidden = t.IsHidden });

                foreach (var c in t.Columns.Where(c => c.Type != ColumnType.RowNumber))
                {
                    var cref = ObjectRefs.For(c);
                    AddNode(new LineageNode { Ref = cref, Name = c.Name, Kind = c is CalculatedColumn ? "calcColumn" : "column", Table = t.Name, IsHidden = c.IsHidden });
                    edges.Add(new LineageEdge { From = tref, To = cref, Kind = "contains" });
                }
                foreach (var ms in t.Measures)
                {
                    var mref = ObjectRefs.For(ms);
                    AddNode(new LineageNode { Ref = mref, Name = ms.Name, Kind = "measure", Table = t.Name, IsHidden = ms.IsHidden });
                    edges.Add(new LineageEdge { From = tref, To = mref, Kind = "contains" });
                }
            }

            // DAX dependency edges (dependant -> dependency). Backfill a node for any dependency target that isn't
            // already a node (a user-defined Function/UDF or a Calendar) so the edge is SURFACED, not silently dropped
            // by the dangling-edge filter below.
            foreach (var dep in DaxDependantsForGraph(m))
            {
                var fromRef = ObjectRefs.For((ITabularObject)dep);
                if (string.IsNullOrEmpty(fromRef)) continue;
                foreach (var target in dep.DependsOn.Keys)
                {
                    var toRef = ObjectRefs.For(target);
                    if (string.IsNullOrEmpty(toRef) || fromRef == toRef) continue;
                    if (!nodeRefs.Contains(toRef))
                        AddNode(new LineageNode { Ref = toRef, Name = (target as ITabularNamedObject)?.Name, Kind = ObjectRefs.KindOf(target) });
                    edges.Add(new LineageEdge { From = fromRef, To = toRef, Kind = "dependsOn" });
                }
            }

            // Relationship edges (FK column -> PK column).
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
            {
                if (r.FromColumn == null || r.ToColumn == null) continue;
                edges.Add(new LineageEdge { From = ObjectRefs.For(r.FromColumn), To = ObjectRefs.For(r.ToColumn), Kind = "relationship" });
            }

            // Source -> table edges (pragmatic M resolution).
            var (sources, srcEdges) = MReferenceResolver.Resolve(m);
            foreach (var s in sources)
                AddNode(new LineageNode { Ref = s.Ref, Name = s.Name, Kind = s.Kind == "unresolved" ? "unresolved" : "source", Detail = s.Detail });
            foreach (var e in srcEdges)
                edges.Add(new LineageEdge { From = e.SourceRef, To = e.TableRef, Kind = "source" });

            return new LineageResult
            {
                Nodes = nodes.ToArray(),
                Edges = DistinctEdges(edges.Where(e => nodeRefs.Contains(e.From) && nodeRefs.Contains(e.To))),
                Caveat = ModelOnlyCaveat,
            };
        }

        // ---- Forward impact (impact_of) --------------------------------------------------------------------------

        public static ImpactResult Impact(Model m, string objRef)
        {
            var root = ObjectRefs.Resolve(m, objRef)
                ?? throw new InvalidOperationException($"{objRef} not found.");

            var ordered = WalkImpact(new List<ITabularObject> { root });
            return new ImpactResult
            {
                Root = ObjectRefs.For(root),
                RootName = (root as ITabularNamedObject)?.Name,
                RootKind = ObjectRefs.KindOf(root),
                Impacted = ordered,
                Measures = ordered.Count(n => n.Kind == "measure"),
                Columns = ordered.Count(n => n.Kind == "column" || n.Kind == "calcColumn"),
                Tables = ordered.Count(n => n.Kind == "table"),
                Relationships = ordered.Count(n => n.Kind == "relationship"),
                Other = ordered.Count(n => n.Kind != "measure" && n.Kind != "column" && n.Kind != "calcColumn" && n.Kind != "table" && n.Kind != "relationship"),
                Caveat = ImpactCaveat,
            };
        }

        /// <summary>Multi-source blast radius (the health probe's per-commit path): ONE BFS seeded with EVERY
        /// resolvable root — a single shared seen/queue makes the cost O(graph) regardless of how many roots a
        /// mega-batch commit produced, so there is no root cap and no truncation that could silently suppress a
        /// real blast radius (the old per-root loop walked only the first 64 roots in arbitrary delta order).
        /// All roots are seeded as SEEN before the walk, so a root downstream of another root is never counted as
        /// its impact — the same "an edited object is the change, not its impact" rule the single-root path applies.
        /// Unresolvable roots (deleted objects) are skipped — the same v1 posture as the single-root catch.</summary>
        public static ImpactNode[] ImpactFrom(Model m, IEnumerable<string> objRefs)
        {
            var roots = new List<ITabularObject>();
            foreach (var r in objRefs)
            {
                var o = ObjectRefs.Resolve(m, r);
                if (o != null) roots.Add(o);
            }
            return WalkImpact(roots);
        }

        // The shared BFS core: seeds every root (relationship roots contribute their endpoint columns — a
        // relationship has no DAX branch of its own; its impact surface is its endpoints and everything that
        // propagates through them), then walks DAX dependants + structural column usage + table containment.
        private static ImpactNode[] WalkImpact(IReadOnlyList<ITabularObject> roots)
        {
            var impacted = new List<ImpactNode>();
            var seen = new HashSet<ITabularObject>(roots);
            var queue = new Queue<(ITabularObject obj, int depth)>();

            foreach (var root in roots)
            {
                if (root is SingleColumnRelationship rel)
                {
                    foreach (var col in new[] { rel.FromColumn, rel.ToColumn })
                        if (col != null && seen.Add(col)) { impacted.Add(NodeOf(col, 1, "relationship")); queue.Enqueue((col, 1)); }
                }
                else
                {
                    queue.Enqueue((root, 0));
                }
            }

            while (queue.Count > 0)
            {
                var (obj, d) = queue.Dequeue();

                if (obj is IDaxObject dax)                              // transitive DAX dependants (+ RLS filters)
                {
                    foreach (var r in dax.ReferencedBy)
                    {
                        var ro = (ITabularObject)r;
                        if (seen.Add(ro))
                        {
                            impacted.Add(NodeOf(ro, d + 1, r is TablePermission ? "rls" : "dax"));
                            queue.Enqueue((ro, d + 1));
                        }
                    }
                }

                if (obj is Column col)                                  // structural dependants (columns only)
                {
                    foreach (var r in col.UsedInRelationships) if (seen.Add(r)) impacted.Add(NodeOf(r, d + 1, "relationship"));
                    foreach (var h in col.UsedInHierarchies) if (seen.Add(h)) impacted.Add(NodeOf(h, d + 1, "hierarchy"));
                    foreach (var sc in col.UsedInSortBy) if (seen.Add(sc)) { impacted.Add(NodeOf(sc, d + 1, "sortBy")); queue.Enqueue((sc, d + 1)); }
                    foreach (var v in col.UsedInVariations) if (seen.Add(v)) impacted.Add(NodeOf(v, d + 1, "variation"));
                    foreach (var a in col.UsedInAlternateOfs) if (seen.Add(a)) impacted.Add(NodeOf(a, d + 1, "aggregation"));
                }

                if (obj is Table tbl)                                   // a table root impacts its own children (+ their dependants)
                {
                    foreach (var c in tbl.Columns.Where(c => c.Type != ColumnType.RowNumber))
                        if (seen.Add(c)) { impacted.Add(NodeOf(c, d + 1, "contains")); queue.Enqueue((c, d + 1)); }
                    foreach (var ms in tbl.Measures)
                        if (seen.Add(ms)) { impacted.Add(NodeOf(ms, d + 1, "contains")); queue.Enqueue((ms, d + 1)); }
                    if (tbl is CalculationGroupTable cg)
                        foreach (var ci in cg.CalculationItems)
                            if (seen.Add(ci)) { impacted.Add(NodeOf(ci, d + 1, "contains")); queue.Enqueue((ci, d + 1)); }
                }
            }

            return impacted.OrderBy(n => n.Depth).ThenBy(n => n.Kind).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        // ---- Reverse safe-to-remove sweep (unused_objects) -------------------------------------------------------

        public static UnusedResult Unused(Model m) => Unused(m, null, ModelOnlyCaveat);

        // reportUsedRefs (optional): model object refs that published/local reports use — any candidate in this set is
        // excluded (a report uses it), closing the model-only blind spot for descriptive columns no measure references.
        public static UnusedResult Unused(Model m, ISet<string> reportUsedRefs, string caveat)
        {
            // Pre-index every NON-DAX structural column usage ONCE (O(R + C + H)) so per-column evaluation is an O(1)
            // set lookup — avoids the O(columns x relationships) rescan and covers all the structural referencer
            // scopes a column can have: relationship keys, sort-by targets, hierarchy levels, variation default
            // columns, aggregation (AlternateOf) base columns, and grouping (GroupByColumns) members.
            var structural = new HashSet<Column>();
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
            {
                if (r.FromColumn != null) structural.Add(r.FromColumn);
                if (r.ToColumn != null) structural.Add(r.ToColumn);
            }
            foreach (var t in m.Tables)
            {
                foreach (var c in t.Columns)
                {
                    if (c.SortByColumn != null) structural.Add(c.SortByColumn);
                    foreach (var v in c.Variations) if (v.DefaultColumn != null) structural.Add(v.DefaultColumn);
                    if (c.AlternateOf?.BaseColumn != null) structural.Add(c.AlternateOf.BaseColumn);
                    if (c.GroupByColumns != null) foreach (var gbc in c.GroupByColumns) structural.Add(gbc);
                }
                foreach (var h in t.Hierarchies)
                    foreach (var lvl in h.Levels) if (lvl.Column != null) structural.Add(lvl.Column);
            }

            var items = new List<UnusedItem>();
            foreach (var t in m.Tables)
            {
                foreach (var ms in t.Measures)
                {
                    var v = EvaluateMeasure(ms, reportUsedRefs);
                    if (v != null) items.Add(v);
                }
                foreach (var c in t.Columns.Where(c => c.Type != ColumnType.RowNumber))
                {
                    var v = EvaluateColumn(c, structural, reportUsedRefs);
                    if (v != null) items.Add(v);
                }
            }

            var ordered = items
                .OrderBy(i => i.Verdict == "safe" ? 0 : i.Verdict == "usedByUnusedOnly" ? 1 : 2)
                .ThenBy(i => i.Table, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new UnusedResult
            {
                Items = ordered,
                SafeCount = ordered.Count(i => i.Verdict == "safe"),
                UsedByUnusedOnlyCount = ordered.Count(i => i.Verdict == "usedByUnusedOnly"),
                CautionCount = ordered.Count(i => i.Verdict == "caution"),
                Caveat = caveat,
            };
        }

        private static UnusedItem EvaluateMeasure(Measure ms, ISet<string> reportUsedRefs)
        {
            if (reportUsedRefs != null && reportUsedRefs.Contains(ObjectRefs.For(ms))) return null;   // a report uses it
            var rb = ms.ReferencedBy;
            var deep = rb.Count == 0 ? null : rb.Deep();
            if (deep != null && (UsedInRls(deep) || UsedInCalcItem(deep) || AnyVisibleReferencer(deep))) return null;
            return Verdict(ms, "measure", ms.Table?.Name, ms.IsHidden, rb, deep,
                "No model object references this measure.");
        }

        private static UnusedItem EvaluateColumn(Column c, HashSet<Column> structural, ISet<string> reportUsedRefs)
        {
            // Hard STRUCTURAL use (independent of DAX): relationship key / sort-by / hierarchy / variation default /
            // aggregation base / grouping member. Any of these means the column is in use regardless of references.
            if (structural.Contains(c)) return null;
            // Object-Level Security is a governance control stored as METADATA (not DAX), so it never appears in
            // ReferencedBy — guard it explicitly so an OLS-secured column is never reported "safe to remove".
            if (HasObjectLevelSecurity(c)) return null;
            if (reportUsedRefs != null && reportUsedRefs.Contains(ObjectRefs.For(c))) return null;   // a report uses it

            var rb = c.ReferencedBy;
            var deep = rb.Count == 0 ? null : rb.Deep();
            if (deep != null && (UsedInRls(deep) || UsedInCalcItem(deep) || AnyVisibleReferencer(deep))) return null;
            return Verdict(c, c is CalculatedColumn ? "calcColumn" : "column", c.Table?.Name, c.IsHidden, rb, deep,
                "No measure / relationship / hierarchy / sort-by / variation / aggregation / OLS uses this column (model-only).");
        }

        private static UnusedItem Verdict(ITabularNamedObject obj, string kind, string table, bool hidden,
            ReferencedByList rb, HashSet<IDaxDependantObject> deep, string safeReason)
        {
            string verdict, reason;
            if (rb.Count == 0) { verdict = "safe"; reason = safeReason; }
            else if (deep != null && HasUnclassifiableReferencer(deep))
            {
                verdict = "caution";
                reason = "Referenced by an object whose live-ness can't be determined offline (e.g. a partition data-coverage expression) — verify before removing.";
            }
            else { verdict = "usedByUnusedOnly"; reason = "Referenced only by objects that are themselves unused (hidden/dead)."; }

            return new UnusedItem
            {
                Ref = ObjectRefs.For(obj), Name = obj.Name, Kind = kind, Table = table, IsHidden = hidden,
                Verdict = verdict, RefCount = rb.Count, BlockedBy = ReferrerLabels(rb), Reason = reason,
            };
        }

        // ---- Report-aware analysis (Phase 3 — local PBIR; cloud getDefinition feeds the SAME parser) -------------

        public static ReportAnalysisResult AnalyzeReports(Model m, IReadOnlyList<(string path, ReportDefinitionReader.ParseResult result)> parsed)
            => AnalyzeReports(m, parsed.Select(x => (x.path, (string)null, x.result)).ToList());

        // Error-aware overload (the cloud transport): each report may carry a scrubbed `error` (RDL/paginated, a
        // getDefinition blocked by an encrypted sensitivity label, or a fetch failure). An errored report counts as
        // unreadable and degrades the caveat — fail-loud, so the safe-to-remove verdict is never silently overstated.
        public static ReportAnalysisResult AnalyzeReports(Model m, IReadOnlyList<(string path, string error, ReportDefinitionReader.ParseResult result)> parsed)
        {
            var reports = new List<ReportUsage>();
            var allUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int read = 0, unreadable = 0, totalUnresolved = 0;

            foreach (var (path, error, pr) in parsed)
            {
                var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ext = new List<string>();
                foreach (var f in pr.Fields)
                {
                    if (f.IsExtension) { if (!string.IsNullOrEmpty(f.Property)) ext.Add(f.Property); continue; }
                    var modelRef = Reconcile(m, f);
                    if (modelRef != null) resolved.Add(modelRef);   // a name miss just means the report uses a field not in THIS model — ignore
                }
                bool ok = pr.DefinitionFound && error == null;
                if (ok) read++; else unreadable++;
                foreach (var r in resolved) allUsed.Add(r);
                totalUnresolved += pr.Unresolved;

                // Page+visual drill-down: group the page/visual-stamped occurrences (those that carry a page) and
                // reconcile each visual's distinct model refs. A visual that references no model field is dropped.
                var visuals = new List<ReportVisualUsage>();
                foreach (var grp in pr.Occurrences.Where(o => o.Page != null)
                             .GroupBy(o => (o.Page, o.Visual)))
                {
                    var vrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string vtype = null;
                    foreach (var f in grp)
                    {
                        if (f.VisualType != null) vtype = f.VisualType;
                        if (f.IsExtension) continue;
                        var mr = Reconcile(m, f);
                        if (mr != null) vrefs.Add(mr);
                    }
                    if (vrefs.Count == 0) continue;
                    visuals.Add(new ReportVisualUsage
                    {
                        Page = grp.Key.Page, Visual = grp.Key.Visual, VisualType = vtype,
                        UsedRefs = vrefs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    });
                }

                reports.Add(new ReportUsage
                {
                    Path = path, Name = ReportName(path), Read = ok,
                    FieldCount = resolved.Count, Unresolved = pr.Unresolved,
                    UsedRefs = resolved.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    ExtensionMeasures = ext.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    Visuals = visuals.OrderBy(v => v.Page, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Visual, StringComparer.OrdinalIgnoreCase).ToArray(),
                    Error = error,
                });
            }

            // Coverage honesty (fail-loud): a "safe to remove" verdict is only trustworthy when report coverage is
            // COMPLETE. Two ways it isn't — (a) some reports couldn't be read (paginated/RDL, sensitivity-label block,
            // fetch error) so a field they use is invisible; (b) reports WERE read but NONE of their fields matched the
            // open model (read > 0 yet allUsed empty) — almost always the wrong model is open, so the "usage" the
            // caveat claims is illusory. In either case a would-be "safe" item must NOT assert safe.
            bool nothingMatched = read > 0 && allUsed.Count == 0;
            bool coverageIncomplete = read > 0 && (unreadable > 0 || nothingMatched);

            var caveat = read == 0
                ? "No readable PBIR report definition was found. (Legacy .pbix / PBIRLegacy / paginated reports are not parsed.) The safe-to-remove list below is MODEL-ONLY."
                : $"Includes field usage from {read} report(s)"
                  + (unreadable > 0 ? $"; {unreadable} report(s) could not be read (paginated/RDL, blocked by a sensitivity label, or unreadable)" : "")
                  + (totalUnresolved > 0 ? $"; {totalUnresolved} report reference(s) could not be attributed (verify 'safe' items)" : "")
                  + (nothingMatched ? "; but NONE of their fields matched the open model — likely the wrong model is open, so the list below is effectively model-only" : "")
                  + ". A field used only by a report NOT included here can still appear as unused.";

            var unused = Unused(m, read == 0 ? null : allUsed, caveat);
            // Demote bare "safe" to "caution" when coverage is incomplete — the machine-readable verdict (what BOTH
            // doors act on) must never overstate safe just because the prose caveat warns. usedByUnusedOnly/caution stay.
            if (coverageIncomplete)
                unused = DemoteSafeToCaution(unused, nothingMatched
                    ? "Report coverage is incomplete — no report field matched the open model (likely the wrong model is open), so a report may use this. Verify before removing."
                    : "Report coverage is incomplete — a report whose definition could not be read may use this. Verify before removing.");

            return new ReportAnalysisResult
            {
                Reports = reports.ToArray(),
                ReportsRead = read,
                ReportsUnreadable = unreadable,
                ModelFieldsUsed = allUsed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                Unused = unused,
                Caveat = caveat,
            };
        }

        // When report coverage is incomplete, a "safe" verdict can't be honoured (a report we couldn't read — or the
        // reports for a different model — might use the field). Flip those items to the tri-state "caution" (the same
        // posture the offline sweep uses for an unclassifiable referencer) and recount, so neither door can act on a
        // false "safe". Leaves usedByUnusedOnly / already-caution items untouched.
        private static UnusedResult DemoteSafeToCaution(UnusedResult r, string reason)
        {
            foreach (var i in r.Items)
                if (i.Verdict == "safe") { i.Verdict = "caution"; i.Reason = reason; }
            return new UnusedResult
            {
                Items = r.Items,
                SafeCount = r.Items.Count(i => i.Verdict == "safe"),
                UsedByUnusedOnlyCount = r.Items.Count(i => i.Verdict == "usedByUnusedOnly"),
                CautionCount = r.Items.Count(i => i.Verdict == "caution"),
                Caveat = r.Caveat,
            };
        }

        // Map a parsed (Entity, Property, kind) reference to a model object ref BY NAME (TOM is the source of truth —
        // the report layer has no LineageTag). Returns null when no model object matches (a different/renamed field).
        private static string Reconcile(Model m, ReportDefinitionReader.FieldRef f)
        {
            if (string.IsNullOrEmpty(f.Property)) return null;
            if (f.Kind == "measure")
            {
                // Prefer the measure on its stated home table; else any measure of that name (measures are model-global).
                Measure hit = null;
                if (!string.IsNullOrEmpty(f.Entity) && m.Tables.Contains(f.Entity))
                    hit = m.Tables[f.Entity].Measures.FirstOrDefault(x => string.Equals(x.Name, f.Property, StringComparison.OrdinalIgnoreCase));
                hit = hit ?? m.Tables.SelectMany(t => t.Measures).FirstOrDefault(x => string.Equals(x.Name, f.Property, StringComparison.OrdinalIgnoreCase));
                return hit != null ? ObjectRefs.For(hit) : null;
            }
            if (!string.IsNullOrEmpty(f.Entity) && m.Tables.Contains(f.Entity))
            {
                var col = m.Tables[f.Entity].Columns.FirstOrDefault(x => string.Equals(x.Name, f.Property, StringComparison.OrdinalIgnoreCase));
                return col != null ? ObjectRefs.For(col) : null;
            }
            return null;
        }

        private static string ReportName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var name = System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        // ---- referencer-visibility helpers (all take a pre-computed transitive Deep() set; one walk per object) ---

        // Referenced (directly or transitively) by an RLS row-filter — a governance use that AnyVisible can't see
        // (a TablePermission isn't a hideable object). Guards a false "safe".
        private static bool UsedInRls(HashSet<IDaxDependantObject> deep) => deep.OfType<TablePermission>().Any();

        // Referenced by a calculation item — a live use AnyVisible can't see (CalculationItem is IDaxDependantObject
        // but NOT IHideableObject, so it never counts as "visible"). Calc groups are commonly hidden, which made the
        // whole chain read as dead. Guards a false "usedByUnusedOnly"/"safe".
        private static bool UsedInCalcItem(HashSet<IDaxDependantObject> deep) => deep.OfType<CalculationItem>().Any();

        // TOM's ReferencedByList.AnyVisible applies COLUMN visibility (table-hidden => invisible) to EVERY referencer,
        // which wrongly demotes a measure on a hidden _Measures/KPIs home table (a measure is visible iff !IsHidden,
        // regardless of its table — Measure.IsVisible). Re-evaluate with the correct per-kind rule.
        private static bool AnyVisibleReferencer(HashSet<IDaxDependantObject> deep)
        {
            foreach (var o in deep)
            {
                if (o is Measure msr) { if (!msr.IsHidden) return true; continue; }       // measure: !IsHidden (table irrelevant)
                if ((o as KPI)?.Measure is Measure km && !km.IsHidden) return true;        // KPI rides its measure's visibility
                if ((o as IHideableObject)?.IsHidden == false
                    && (o as ITabularTableObject)?.Table?.IsHidden == false) return true;  // column-style: object + table visible
            }
            return false;
        }

        // After RLS / calc-item / visible checks have cleared the object from candidacy, anything left in the
        // transitive referencer set that we can't classify for visibility (not a hideable object, not a KPI) —
        // chiefly a Partition's DataCoverageDefinition — means we cannot honestly call the object dead => CAUTION.
        private static bool HasUnclassifiableReferencer(HashSet<IDaxDependantObject> deep)
            => deep.Any(o => !(o is IHideableObject) && !(o is KPI));

        // Any role applies non-default column-level OLS to this column (CL>=1400 only; the indexer is null below that).
        private static bool HasObjectLevelSecurity(Column c)
        {
            var ols = c.ObjectLevelSecurity;
            if (ols == null) return false;
            return c.Model.Roles.Any(r => ols[r] != MetadataPermission.Default);
        }

        // ---- shared helpers --------------------------------------------------------------------------------------

        private static string[] ReferrerLabels(ReferencedByList rb) =>
            rb.OfType<ITabularNamedObject>()
              .Select(o => o.Name)
              .Where(s => !string.IsNullOrEmpty(s))
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
              .Take(12)
              .ToArray();

        private static ImpactNode NodeOf(ITabularObject o, int depth, string via) => new ImpactNode
        {
            Ref = ObjectRefs.For(o),
            Name = (o as ITabularNamedObject)?.Name,
            Kind = o is CalculatedColumn ? "calcColumn" : ObjectRefs.KindOf(o),   // match the graph-side kind + UI glyph
            Depth = depth,
            Via = via,
        };

        private static string TableKind(Table t) =>
            t is CalculationGroupTable ? "calcGroup" : t is CalculatedTable ? "calcTable" : "table";

        // Dependants whose DependsOn endpoints are graph nodes (calc tables / calc columns / measures).
        private static IEnumerable<IDaxDependantObject> DaxDependantsForGraph(Model m)
        {
            foreach (var t in m.Tables)
            {
                if (t is IDaxDependantObject td) yield return td;                  // calculated tables
                foreach (var c in t.Columns)
                    if (c is IDaxDependantObject cd) yield return cd;              // calculated columns
                foreach (var ms in t.Measures) yield return ms;                   // measures
            }
        }

        // Dedup by the (kind, from, to) tuple — no string delimiter, so an object name containing '/' or ':' can
        // never cause a boundary-ambiguous key collision (which would silently drop a distinct edge).
        private static LineageEdge[] DistinctEdges(IEnumerable<LineageEdge> edges)
        {
            var seen = new HashSet<(string, string, string)>();
            var outp = new List<LineageEdge>();
            foreach (var e in edges)
                if (seen.Add((e.Kind, e.From, e.To))) outp.Add(e);
            return outp.ToArray();
        }
    }
}

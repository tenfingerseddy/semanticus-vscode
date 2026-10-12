using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed class FindingFixItem
    {
        public string System { get; set; }
        public string RuleId { get; set; }
        public string ObjRef { get; set; }
    }

    public sealed class ObjectDuplicateItem
    {
        public string ObjRef { get; set; }
        public string NewName { get; set; }
        public string TargetRef { get; set; }
    }

    public sealed class DocSectionItem
    {
        public string ObjRef { get; set; }
        public string SectionKey { get; set; }
        public string Markdown { get; set; }
        public string ExpectedMarkdown { get; set; }
    }

    public sealed class PerspectiveMemberItem
    {
        public string PerspectiveRef { get; set; }
        public string ObjRef { get; set; }
        public bool Include { get; set; }
    }

    public sealed class ObjectPermissionItem
    {
        public string RoleName { get; set; }
        public string ObjRef { get; set; }
        public string Permission { get; set; }
    }

    public sealed partial class LocalEngine
    {
        public async Task<BatchEditResult> UnwaiveFindingsAsync(FindingFixItem[] items, string origin)
        {
            RequireProFeature();
            var context = _sessions.CurrentContext;
            var s = context.RequireSession();
            var result = await WriteBatchAsync(s, items, origin, "unwaive findings", x => x?.ObjRef,
                (m, x, _) => x == null ? throw new ArgumentException("A finding is required.") : RemoveFindingWaiver(m, x.System, x.RuleId, x.ObjRef),
                (m, x) => ((x?.System ?? "").Trim().ToLowerInvariant(), (x?.RuleId ?? "").Trim().ToLowerInvariant(),
                    WaiverStore.IsRuleLevel(x?.ObjRef) ? "" : WaiverKey(m, x.ObjRef).ToLowerInvariant()));
            if (result.Applied > 0) await SyncPlanWithWaiversAsync(context, s);
            NoteBatch(s.Id, origin, result.Applied, "unwaived", "findings", "plan");
            return result;
        }

        public async Task<BatchEditResult> ApplyFixesAsync(FindingFixItem[] items, string origin)
        {
            RequireProFeature();
            var s = _sessions.Require();
            var result = await WriteBatchAsync(s, items, origin, "apply fixes", x => x?.ObjRef, (m, x, _) =>
            {
                if (x == null) throw new ArgumentException("A finding is required.");
                if (ObjectRefs.Resolve(m, x.ObjRef) == null) throw new InvalidOperationException($"{x.ObjRef} not found.");
                var changed = (x.System ?? "").Trim().ToLowerInvariant() switch
                {
                    "air" => ApplyReadinessFixCore(m, x.RuleId, x.ObjRef),
                    "bpa" => ApplyBpaFixCore(m, x.RuleId, x.ObjRef),
                    _ => throw new ArgumentException("system must be 'bpa' or 'air'."),
                };
                return (changed, null);
            }, (m, x) => ((x?.System ?? "").Trim().ToLowerInvariant(), (x?.RuleId ?? "").Trim().ToLowerInvariant(), ObjectRefs.Resolve(m, x?.ObjRef)));
            NoteBatch(s.Id, origin, result.Applied, "fixed", "findings", "plan");
            return result;
        }

        public async Task<BatchEditResult> DuplicateObjectsAsync(ObjectDuplicateItem[] items, string origin)
        {
            RequireProFeature();
            var s = _sessions.Require();
            // A slot per item: a rejected item takes back the items before it and runs the good ones again, so a list that only grows would name a copy twice.
            var made = new string[items?.Length ?? 0];
            var result = await WriteBatchAsync(s, items, origin, "duplicate objects", x => x?.ObjRef, (m, x, i) =>
            {
                made[i] = null;
                if (x == null) throw new ArgumentException("An object is required.");
                made[i] = DuplicateObjectCore(m, x.ObjRef, x.NewName, x.TargetRef);
                return (true, null);
            });
            result.Created = made.Where(x => x != null).ToArray();
            NoteBatch(s.Id, origin, result.Applied, "duplicated", "objects", "edit/properties");
            return result;
        }

        public async Task<BatchEditResult> SetDocSectionsAsync(DocSectionItem[] items, string origin)
        {
            RequireProFeature();
            var s = _sessions.Require();
            var result = await WriteBatchAsync(s, items, origin, "set doc sections", x => x?.ObjRef, (m, x, _) =>
            {
                if (x == null) throw new ArgumentException("A section is required.");
                return (ApplyDocSection(m, x.ObjRef, x.SectionKey, x.Markdown, origin, x.ExpectedMarkdown), null);
            }, (m, x) => (ResolveDocTarget(m, x?.ObjRef), x?.SectionKey));
            NoteBatch(s.Id, origin, result.Applied, "wrote", "sections", "review/docs");
            return result;
        }

        public async Task<BatchEditResult> SetPerspectiveMembersAsync(PerspectiveMemberItem[] items, string origin)
        {
            RequireProFeature();
            var s = _sessions.Require();
            var result = await WriteBatchAsync(s, items, origin, "set perspective members", x => x?.ObjRef, (m, x, _) =>
            {
                if (x == null) throw new ArgumentException("A perspective member is required.");
                return (ApplyPerspectiveMember(m, s, x.PerspectiveRef, x.ObjRef, x.Include), null);
            }, (m, x) => (ObjectRefs.Resolve(m, x?.PerspectiveRef), ObjectRefs.Resolve(m, x?.ObjRef)));
            NoteBatch(s.Id, origin, result.Applied, "changed", "perspective cells", "edit/advanced");
            return result;
        }

        public async Task<BatchEditResult> SetObjectPermissionsAsync(ObjectPermissionItem[] items, string origin)
        {
            RequireProFeature();
            var s = _sessions.Require();
            var result = await WriteBatchAsync(s, items, origin, "set object permissions", x => x?.ObjRef, (m, x, _) =>
            {
                if (x == null) throw new ArgumentException("A permission is required.");
                return (ObjectRefs.Resolve(m, x.ObjRef) switch
                {
                    Table => ApplyTableObjectPermission(m, x.RoleName, x.ObjRef, x.Permission),
                    Column => ApplyColumnObjectPermission(m, x.RoleName, x.ObjRef, x.Permission),
                    _ => throw new InvalidOperationException($"{x.ObjRef} is not a table or column."),
                }, null);
            }, (m, x) => (FindRole(m, x?.RoleName), ObjectRefs.Resolve(m, x?.ObjRef)));
            NoteBatch(s.Id, origin, result.Applied, "changed", "permission cells", "edit/advanced");
            return result;
        }

        private string DuplicateObjectCore(Model m, string objRef, string newName, string targetRef)
        {
            var obj = ObjectRefs.Resolve(m, objRef) ?? throw new InvalidOperationException($"{objRef} not found. Check the ref with get_object, or run list_objects / search_model to find it.");

            // Optional paste-target CONTAINER (the tree's copy/paste). Resolving to the source's own container
            // collapses to the classic beside-the-original duplicate, so a caller can always pass its selection.
            ITabularNamedObject target = null;
            if (!string.IsNullOrWhiteSpace(targetRef))
                target = ObjectRefs.Resolve(m, targetRef) ?? throw new InvalidOperationException(
                    $"{targetRef} not found. Pass a table ref (e.g. 'table:Sales') to copy into, or omit targetRef to duplicate beside the original.");

            ITabularObject copy;
            switch (obj)
            {
                case Measure me:
                {
                    var tt = CrossTableTarget(target, me.Table, "a measure");
                    if (tt == null) { copy = me.Clone(newName); break; }
                    // Measure names are model-wide unique and must not collide with a column on the landing
                    // table. Clone() names against the SOURCE collection, so cross-table we name against the
                    // TARGET ourselves: clone under a throwaway name, rename inside the same undo batch.
                    var final = UniqueCopyName(string.IsNullOrEmpty(newName) ? me.Name : newName,
                        n => tt.Model.AllMeasures.Any(x => NameEq(x.Name, n)) || tt.Columns.Any(x => NameEq(x.Name, n)));
                    var mc = me.Clone(TempCloneName(), true, tt);
                    mc.Name = final;
                    copy = mc;
                    break;
                }
                // Columns and hierarchies stay on their own table: a column's data (source binding / row
                // context) and a hierarchy's levels only exist there — a cross-table clone would dangle.
                case CalculatedTableColumn ctc:
                    RefuseCrossTable(target, ctc.Table, "a calc-table column", "it is produced by its table's DAX expression. Duplicate it on its own table, or copy the expression into the target table's DAX");
                    copy = ctc.Clone(newName); break;
                case CalculatedColumn cc:
                    RefuseCrossTable(target, cc.Table, "a column", "a column's data lives in its own table. Duplicate it there, or create a new calculated column on the target table");
                    copy = cc.Clone(newName); break;
                case DataColumn dc:
                    RefuseCrossTable(target, dc.Table, "a column", "a data column is bound to its own table's source query. Duplicate it there, or add the column in the target table's source");
                    copy = dc.Clone(newName); break;
                case Hierarchy h:
                    RefuseCrossTable(target, h.Table, "a hierarchy", "its levels are built from its own table's columns. Duplicate it there, or create a new hierarchy on the target table from that table's columns");
                    copy = h.Clone(newName); break;
                case CalculationItem ci:
                {
                    var tg = target == null || ReferenceEquals(target, ci.CalculationGroupTable) ? null
                        : target as CalculationGroupTable ?? throw new InvalidOperationException(
                            $"Can't paste a calculation item onto {ObjectRefs.KindOf(target)} '{target.Name}'. The target must be a calculation group (pass its 'table:' ref).");
                    if (tg == null) { copy = ci.Clone(newName); break; }
                    var final = UniqueCopyName(string.IsNullOrEmpty(newName) ? ci.Name : newName,
                        n => tg.CalculationItems.Any(x => NameEq(x.Name, n)));
                    var cic = ci.Clone(TempCloneName(), tg.CalculationGroup);
                    cic.Name = final;
                    copy = cic;
                    break;
                }
                case Table t:   // Table, CalculatedTable (incl. field parameters), CalculationGroupTable — Clone dispatches internally
                    if (target != null && !ReferenceEquals(target, t)) throw new InvalidOperationException(
                        "Tables, calculation groups and field parameters duplicate at model scope. Omit targetRef.");
                    copy = t.Clone(newName); break;
                default:
                    throw new InvalidOperationException($"Duplicate is not supported for {ObjectRefs.KindOf(obj)} (supported: measure, calculated/data/calc-table column, hierarchy, calculation item, table / calculated table / field parameter, calculation group).");
            }
            var newRef = ObjectRefs.For(copy);
            return newRef;
        }

        private bool ApplyReadinessFixCore(Model m, string ruleId, string objRef)
        {
            var changed = false;
            switch (ruleId)
            {
                case "VIS-FK":
                    if (ObjectRefs.Resolve(m, objRef) is Column fk && !fk.IsHidden) { fk.IsHidden = true; changed = true; }
                    break;
                case "FMT-SUMMARIZE":
                case "SUMMARIZE-DIMENSION":   // same deterministic fix: a non-additive column should not auto-aggregate
                    if (ObjectRefs.Resolve(m, objRef) is Column sc && sc.SummarizeBy != AggregateFunction.None)
                    { sc.SummarizeBy = AggregateFunction.None; changed = true; }
                    break;
                case "CAT-GEO":
                    if (ObjectRefs.Resolve(m, objRef) is Column gc)
                    {
                        var cat = ReadinessRuleSet.GeoCategory(gc.Name);
                        if (cat != null && gc.DataCategory != cat) { gc.DataCategory = cat; changed = true; }
                    }
                    break;
                case "DIM-PRIMARY-NAME":
                    // Only the column the finding names, and only while its dimension has no row label: a second
                    // row label is not what the finding asked for.
                    if (ObjectRefs.Resolve(m, objRef) is Column lc)
                    {
                        var dim = ReadinessRuleSet.DimensionRowLabels(m).FirstOrDefault(d => ReferenceEquals(d.Label, lc));
                        if (dim.Label != null && !dim.Clean) changed = SafeFixes.MarkRowLabel(lc);
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Rule '{ruleId}' has no deterministic safe fix; use Claude (get_fix_prompt) or a Proposal review.");
            }
            return changed;
        }

        private bool ApplyBpaFixCore(Model m, string ruleId, string objRef)
        {
            var rule = GetBpaRules(m).FirstOrDefault(r => string.Equals(r.ID, ruleId, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"Unknown BPA rule '{ruleId}'. Run bpa_scan to see the rule ids in force, or load_rules with system='bpa' to add a rule set.");
            var obj = ObjectRefs.Resolve(m, objRef) ?? throw new InvalidOperationException($"Object not found: {objRef}. Run list_objects or search_model to find the exact ref (e.g. measure:Table/Name, column:Table/Name).");
            // A deterministic fix exists to CLEAR A FINDING, so it never OVERWRITES an object the rule does not
            // flag. Without this, apply_fix writes wherever it is pointed: DATECOLUMN_FORMATSTRING's fix would
            // replace a person's Long Date with the ISO form, which is exactly what its description now promises
            // never happens. Re-applying a fix that already landed is NOT that case — the value is already what
            // the fix writes, so it is a quiet no-op (changed:false), which keeps a second call idempotent.
            // The scan is the SAME evaluation bpa_scan runs (waivers included, so an accepted finding is still
            // fixable on request), narrowed to this one object by the scoped overload.
            var (fixProp, fixBefore, fixAfter) = BpaAnalyzer.PreviewFix(rule, obj);
            if (fixProp != null && !string.Equals(fixBefore, fixAfter, StringComparison.Ordinal)
                && !BpaAnalyzer.Analyze(m, new[] { rule }, new HashSet<string>(new[] { objRef }, StringComparer.OrdinalIgnoreCase))
                    .Violations.Any(v => string.Equals(v.ObjectRef, objRef, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{objRef} does not violate {rule.ID} right now, so this fix would overwrite {fixProp} ('{fixBefore}' to '{fixAfter}') for no finding. Nothing was changed. Run bpa_scan for the current findings.");
            return BpaAnalyzer.ApplyFix(m, rule, obj);
        }

        private bool ApplyTableObjectPermission(Model m, string roleName, string tableRef, string permission)
        {
            var changed = false;
            var role = FindRole(m, roleName) ?? throw new InvalidOperationException($"Role '{roleName}' not found. Run list_roles to see roles; create one with create_role.");
            if (role.MetadataPermission == null) throw new InvalidOperationException("Object-level security requires compatibility level 1400 or higher.");
            var table = ResolveTable(m, tableRef) ?? throw new InvalidOperationException($"Table not found: {tableRef}. Run list_objects to see the model's tables.");
            if (table is CalculationGroupTable)
                throw new InvalidOperationException($"Object-level security cannot be applied to a calculation group table ('{table.Name}').");
            var p = ParseMetadataPermission(permission);
            if (role.MetadataPermission[table] != p)
            {
                role.MetadataPermission[table] = p;
                changed = true;
                // Default leaves an otherwise-empty TablePermission behind; remove it (NoEffect also guards a still-
                // present RLS filter / column OLS) so Default is a clean removal — mirrors clearing an RLS filter.
                if (p == MetadataPermission.Default)
                {
                    var tp = role.TablePermissions.FindByName(table.Name);
                    if (tp != null && tp.NoEffect) tp.Delete();
                }
            }
            return changed;
        }

        private bool ApplyColumnObjectPermission(Model m, string roleName, string columnRef, string permission)
        {
            var changed = false;
            var role = FindRole(m, roleName) ?? throw new InvalidOperationException($"Role '{roleName}' not found. Run list_roles to see roles; create one with create_role.");
            if (role.MetadataPermission == null) throw new InvalidOperationException("Object-level security requires compatibility level 1400 or higher.");
            if (!(ObjectRefs.Resolve(m, columnRef) is Column col)) throw new InvalidOperationException($"{columnRef} is not a column. Pass a 'column:Table/Name' ref; run list_columns on the table to see its columns.");
            // Mirror the table-OLS calc-group guard: a calc group isn't part of the OLS surface (the vendor's
            // RoleOLSIndexer excludes them), so setting column OLS on one would orphan a TablePermission.
            if (col.Table is CalculationGroupTable)
                throw new InvalidOperationException($"Object-level security cannot be applied to a calculation group column ('{col.Table.Name}'[{col.Name}]).");
            var p = ParseMetadataPermission(permission);
            if (col.ObjectLevelSecurity[role] != p)
            {
                col.ObjectLevelSecurity[role] = p;
                changed = true;
                if (p == MetadataPermission.Default)
                {
                    var tp = role.TablePermissions.FindByName(col.Table.Name);
                    if (tp != null && tp.NoEffect) tp.Delete();
                }
            }
            return changed;
        }

        private bool ApplyPerspectiveMember(Model m, Session s, string perspectiveRef, string objRef, bool include)
        {
            if (!(ObjectRefs.Resolve(m, perspectiveRef) is Perspective p))
                throw new InvalidOperationException($"{perspectiveRef} is not a perspective. Run get_perspectives to see them; create one with create_perspective.");
            var obj = ObjectRefs.Resolve(m, objRef) ?? throw new InvalidOperationException($"{objRef} not found. Check the ref with get_object, or run list_objects / search_model to find it.");
            if (!(obj is ITabularPerspectiveObject po))
                throw new InvalidOperationException($"{objRef} ({ObjectRefs.KindOf(obj)}) can't be put in a perspective. Only tables, columns, measures and hierarchies can.");
            // Setting a table cascades to its columns/measures/hierarchies (PerspectiveTableIndexer).
            var was = po.InPerspective[p];
            var tables = m.Database.TOMDatabase.Model.Perspectives[p.Name].PerspectiveTables;
            var table = obj as Table;
            var hadTable = table != null && tables.Contains(table.Name);
            po.InPerspective[p] = include;
            var now = po.InPerspective[p];
            var hasTable = table != null && tables.Contains(table.Name);
            var changed = was != now || hadTable != hasTable;
            // The wrapper reports no change for a perspective member, so the History and the person's question would name
            // nothing: say which object went in or out of which perspective.
            if (changed)
                s.RecordDelta(new ChangeDelta { Kind = "update", Ref = ObjectRefs.For((ITabularObject)po), Props = new[] { "InPerspective" },
                    Before = was || hadTable ? p.Name : null, After = now || hasTable ? p.Name : null });
            return changed;
        }

        private static bool ApplyDocSection(Model m, string objRef, string sectionKey, string markdown, string origin, string expectedMarkdown = null)
        {
            if (string.IsNullOrWhiteSpace(sectionKey)) throw new InvalidOperationException("A section key is required.");
            var newVal = markdown ?? "";
            var ao = ResolveDocTarget(m, objRef) ?? throw new InvalidOperationException($"setDocSection: {objRef} cannot carry documentation narrative.");
            var env = ReadDocEnvelope(ao);
            var sections = (System.Text.Json.Nodes.JsonObject)env["sections"];
            var old = DocStr(sections[sectionKey]);
            if (string.IsNullOrEmpty(newVal) ? old == null : old == newVal) return false;
            GuardExpectedValue("section", expectedMarkdown, DocStr(sections[sectionKey]), newVal);
            if (string.IsNullOrEmpty(newVal)) sections.Remove(sectionKey);
            else sections[sectionKey] = newVal;
            env["author"] = origin;
            env["updatedUtc"] = DateTime.UtcNow.ToString("o");
            // public SetAnnotation(name,value) is undoable + change-tracked (broadcasts); value=null removes it.
            ao.SetAnnotation(DocAnnotation, sections.Count == 0 ? null : env.ToJsonString());
            return true;
        }

        private (bool Changed, string Warning) RemoveFindingWaiver(Model m, string system, string ruleId, string objRef)
        {
            system = (system ?? "").Trim().ToLowerInvariant();
            if (system != "bpa" && system != "air") throw new ArgumentException("system must be 'bpa' or 'air'.");
            if (string.IsNullOrWhiteSpace(ruleId)) throw new ArgumentException("A rule id is required.");
            bool ruleLevel = WaiverStore.IsRuleLevel(objRef);
            var removed = false;
            string warning = null;
            // Same data-preservation contract as WaiveFindingAsync: never let a Remove+Save destroy an unreadable
            // store — preserve it aside first, or refuse (PreserveCorrupt throws ⇒ the mutation rolls back).
            var recs = WaiverStore.LoadForWrite(m, out var corruptRaw);
            if (corruptRaw != null)
            {
                var backup = WaiverStore.PreserveCorrupt((IAnnotationObject)m, WaiverStore.Annotation, corruptRaw);
                warning = $"The existing waiver data was corrupt and could not be fully read; it was preserved to the '{backup}' annotation before this change. Recover any lost waivers from there.";
            }
            removed = WaiverStore.Remove(recs, system, ruleId.Trim(), ruleLevel ? null : WaiverKey(m, objRef));
            if (!removed && !ruleLevel) removed = WaiverStore.Remove(recs, system, ruleId.Trim(), objRef.Trim());   // an older store trimmed the name
            WaiverStore.Save(m, recs);
            // A finding that reads as accepted only because another acceptance covers its change has no record of its own to
            // remove; say which acceptance to undo instead of reporting a quiet no-op.
            if (!removed && !ruleLevel && warning == null)
            {
                var about = system == "air" ? RuleAboutTable.ForReadiness(ruleId.Trim())
                    : RuleAboutTable.ForBpa(GetBpaRules(m).FirstOrDefault(r => string.Equals(r.ID, ruleId.Trim(), StringComparison.OrdinalIgnoreCase)));
                if (about?.Property != null && DeclinedChangesOf(m).DeclinesAsk(system, ruleId.Trim(), WaiverKey(m, objRef), about.Property))
                    warning = "Nothing to undo here: this finding reads as accepted because you accepted the same change under another rule. Undo that acceptance instead.";
            }
            // The same problem accepted on the other page closes this one too (PairedAcceptances); say which acceptance to undo.
            if (!removed && !ruleLevel && warning == null
                && PairedAcceptances.TwinAcceptance(recs, system, ruleId.Trim(), WaiverKey(m, objRef)) != null)
                warning = "Nothing to undo here: this finding reads as accepted because you accepted the same finding on "
                    + (system == "air" ? "Model quality" : "AI readiness") + ". Undo that acceptance instead.";
            if (system == "bpa" && !ruleLevel)
            {
                var o = ObjectRefs.Resolve(m, objRef);
                if (o is IAnnotationObject annotations)
                {
                    // TE-only ignores and legacy consolidation are writes even without a Semanticus record.
                    var before = annotations.GetAnnotations().ToDictionary(x => x, annotations.GetAnnotation);
                    WaiverStore.WriteTeIgnore(o, ruleId.Trim(), false);
                    var after = annotations.GetAnnotations().ToDictionary(x => x, annotations.GetAnnotation);
                    removed |= before.Count != after.Count || before.Any(x => !after.TryGetValue(x.Key, out var value) || value != x.Value);
                }
            }
            return (removed || corruptRaw != null, warning);
        }

        private static bool ApplyMeasureFormatExpression(Model m, string objRef, string formatExpression, string expectedFormatExpression = null)
        {
            var expr = string.IsNullOrWhiteSpace(formatExpression) ? null : formatExpression.Trim();
            var changed = false;
            if (!(ObjectRefs.Resolve(m, objRef) is Measure me))
                throw new InvalidOperationException($"{objRef} is not a measure: pass a 'measure:Table/Name' ref; run list_measures to see the model's measures. (This sets a DYNAMIC format string; for a static literal use set_measure_format.)");
            // A measure dynamic format string (FormatStringDefinition) needs CL 1601+ (verified vs the MS TMDL docs:
            // "1550 is below the required level of 1601 for the FormatStringDefinition property"). Clearing is always
            // safe (below 1601 the property can't exist, so it's a no-op).
            if (expr != null && (m.Database?.CompatibilityLevel ?? 0) < 1601)
                throw new InvalidOperationException($"A measure dynamic format string requires compatibility level 1601 or higher (this model is {m.Database?.CompatibilityLevel ?? 0}). Raise Compatibility level in Properties, or set a static format.");
            if (expr != null)
            {
                var v = DaxValidator.Validate(m, expr);
                RefuseDaxForWrite(v, expr, "this format expression");
            }
            var before = me.FormatStringExpression;
            GuardExpectedValue("format expression", expectedFormatExpression, before, expr);   // the grid's starting point against what is live NOW, in this turn
            if (!string.Equals(before ?? string.Empty, expr ?? string.Empty, StringComparison.Ordinal))
            {
                me.FormatStringExpression = expr;   // wrapper auto-clears the static FormatString when non-blank
                changed = true;
            }
            return changed;
        }
    }
}

// The four plan executor kinds the groundwork contract adds (jev-groundwork-contract.md section 2,
// "Adapters"): set_sort_by_column, set_data_type with a conversion check, set_is_available_in_mdx, and a
// validated set_property for the enum settings in the TOM census.
//
// WHY THEY LIVE HERE AND NOT INLINE IN ApplyOneItem. Each one carries a real check, and the check is the
// point of the kind: a change that would lose data, a sort that is not one to one, a column something still
// sorts by, a value that is not one of the setting's choices. ApplyOneItem's switch names each kind and
// calls in here, so the switch stays one line per kind and the checks read as what they are.
//
// HOW A REFUSAL TRAVELS. Every refusal is an InvalidOperationException whose message is one plain sentence
// naming the object. Inside apply_plan that sentence becomes the line's note and the line is marked failed;
// inside an approval it rolls the whole set back and is the sentence the person reads. Nothing is written
// before a check has passed, so a refused line never leaves a half change behind.
//
// FACTS THESE CHECKS NEED. Three of them need to know something about the DATA: whether two columns go together
// one to one, whether a decimal column holds only whole numbers, and whether a whole number column holds only
// values a decimal number keeps exactly (a double stops at 2^53: 9007199254740993 comes back as
// 9007199254740992). Those come from the model facts
// (contract section 1) through JevFacts(), which reads the snapshot already built for the current revision.
// A fact nobody measured (no snapshot yet, or no data) is treated as unknown and refused, never as a pass.
// So is a fact read from a sample: each of these is a claim about EVERY row (JevModelFactsAdapter).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>The data facts the data type check needs beyond <see cref="IJevModelFacts"/>: whether every
    /// value of a column has an exact double, and whether every value fits a fixed decimal. Null when not
    /// proven (not measured, or measured on a sample). Kept apart from the families' interface because no
    /// family reads them; only the executor does.</summary>
    internal interface IExactNumberFacts
    {
        bool? ExactInDouble(string columnRef);
        bool? FitsFixedDecimal(string columnRef);
    }

    public sealed partial class LocalEngine
    {
        // ---- set_sort_by_column ---------------------------------------------------------------------------

        private void ApplySortByColumn(Model m, ChangeItem it, ITabularNamedObject obj, IJevModelFacts given = null)
        {
            if (!(obj is Column c))
                throw new InvalidOperationException($"{it.ObjectRef} is not a column, so it cannot be sorted by another column.");

            // Clearing a sort needs no facts: nothing can be wrong about sorting a column by itself again.
            if (IsSortByNone(it.After)) { c.SortByColumn = null; return; }

            var structural = SortByProblem(c, it.After);
            if (structural != null) throw new InvalidOperationException(structural);

            var sort = c.Table.Columns[it.After.Trim()];
            var facts = given ?? JevFacts();
            var mine = facts?.OneToOneWith(ObjectRefs.For(c));
            var theirs = facts?.OneToOneWith(ObjectRefs.For(sort));
            if (mine == null && theirs == null)
                throw new InvalidOperationException(
                    // "In full": a table too big to read whole was measured on a sample, and one unseen row can
                    // break a one to one pairing, so a sample leaves this unknown too.
                    $"{c.Name} and {sort.Name} have not been measured in full, so nobody knows whether they go together "
                    + $"one to one. {c.Name} was not sorted by {sort.Name}.");

            var sortRef = ObjectRefs.For(sort);
            var ownRef = ObjectRefs.For(c);
            var oneToOne = (mine?.Contains(sortRef, StringComparer.OrdinalIgnoreCase) ?? false)
                        || (theirs?.Contains(ownRef, StringComparer.OrdinalIgnoreCase) ?? false);
            if (!oneToOne)
                throw new InvalidOperationException(
                    $"{sort.Name} is not one to one with {c.Name}: some {c.Name} values would need more than one sort "
                    + $"position, so Power BI would refuse the sort. {c.Name} was not sorted by {sort.Name}.");

            c.SortByColumn = sort;
        }

        private static string LiveSortBy(ITabularNamedObject obj) =>
            (obj as Column) == null ? null : ((Column)obj).SortByColumn?.Name ?? SortByNoneToken;

        // ---- set_data_type --------------------------------------------------------------------------------

        private void ApplyDataType(ChangeItem it, ITabularNamedObject obj, IJevModelFacts given = null)
        {
            if (!(obj is DataColumn c))
                throw new InvalidOperationException(
                    $"{it.ObjectRef} is not a data column. Only a column read from the source has a data type you "
                    + "can set; a calculated column takes its type from its formula.");

            DataType to;
            try { to = ParseDataType(it.After); }
            catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message); }
            if (c.DataType == to) return;

            var facts = given ?? JevFacts();
            var exact = facts as IExactNumberFacts;
            var loss = DataTypeLoss(c, to, facts?.WholeNumbers(ObjectRefs.For(c)),
                                    exact?.ExactInDouble(ObjectRefs.For(c)), exact?.FitsFixedDecimal(ObjectRefs.For(c)));
            if (loss != null) throw new InvalidOperationException(loss);
            c.DataType = to;
        }

        /// <summary>Why changing this column's type would lose data, in one sentence, or null when every value
        /// it can hold survives the change. Decided from the TYPES, which is what is known offline, plus the two
        /// measured facts that can make a change safe: every value is whole, and every value has an exact
        /// double.</summary>
        internal static string DataTypeLoss(Column c, DataType to, bool? wholeNumbers, bool? exactInDouble = null, bool? fitsFixedDecimal = null)
        {
            var from = c.DataType;
            string A(DataType t) => PlainColumnType(t);

            // Widenings: every value of the old type has an exact value in the new one.
            if (to == DataType.String) return null;

            // NOT a widening. A double holds whole numbers exactly only up to 2^53 either side of zero, and a
            // whole number column reaches 2^63, so 9007199254740993 would come back as 9007199254740992. Only
            // facts read from every row can prove the values stay below that.
            if (from == DataType.Int64 && to == DataType.Double)
            {
                if (exactInDouble == true) return null;
                return exactInDouble == false
                    ? $"{c.Name} has whole numbers too large for a {A(to)} to hold exactly, so changing it would change them. It was left as a {A(from)}."
                    : $"{c.Name} has not been measured in full, so nobody knows whether every value fits in a {A(to)} exactly. Very large whole numbers would change, so it was left as a {A(from)}.";
            }
            // NOT a widening either. A fixed decimal stops at 922,337,203,685,477 either side of zero, far short
            // of a whole number column's 2^63, so a larger value would not fit at all.
            if (from == DataType.Int64 && to == DataType.Decimal)
            {
                if (fitsFixedDecimal == true) return null;
                return fitsFixedDecimal == false
                    ? $"{c.Name} has whole numbers too large for a {A(to)} to hold, so changing it would lose them. It was left as a {A(from)}."
                    : $"{c.Name} has not been measured in full, so nobody knows whether every value fits in a {A(to)}. Very large whole numbers would not fit, so it was left as a {A(from)}.";
            }
            if (from == DataType.Boolean && to == DataType.Int64) return null;

            if (to == DataType.Int64 && (from == DataType.Double || from == DataType.Decimal))
            {
                if (wholeNumbers == true) return null;
                return wholeNumbers == false
                    ? $"{c.Name} has values with a part after the decimal point, so changing it to a {A(to)} would lose that part. It was left as a {A(from)}."
                    : $"{c.Name} has not been measured in full, so nobody knows whether every value is whole. Changing it to a {A(to)} could lose the part after the decimal point, so it was left as a {A(from)}.";
            }

            if (from == DataType.String)
                return $"{c.Name} holds text, and text can hold values that are not a {A(to)}, so changing it would lose them. It was left as text.";

            return $"Changing {c.Name} from a {A(from)} to a {A(to)} would lose data, so it was left as a {A(from)}.";
        }

        // ---- set_is_available_in_mdx ----------------------------------------------------------------------

        private static void ApplyAvailableInMdx(ChangeItem it, ITabularNamedObject obj)
        {
            if (!(obj is Column c))
                throw new InvalidOperationException($"{it.ObjectRef} is not a column.");

            if (!TryParseBool(it.After, out var available))
                throw new InvalidOperationException(
                    $"'{it.After}' is not true or false, so {c.Name} was left as it is.");

            if (!available)
            {
                // The same conditions the best-practice rule checks before it recommends turning this off: a
                // column something sorts by, a column in a hierarchy, a column a variation defaults to, and a
                // column that is itself sorted all need their attribute to stay available.
                var sortedBy = c.UsedInSortBy.FirstOrDefault();
                if (sortedBy != null)
                    throw new InvalidOperationException(
                        $"{sortedBy.Name} sorts by {c.Name}, so {c.Name} has to stay available to sort it. It was left as it is.");
                var hierarchy = c.UsedInHierarchies.FirstOrDefault();
                if (hierarchy != null)
                    throw new InvalidOperationException(
                        $"{c.Name} is a level of the {hierarchy.Name} hierarchy, so it has to stay available. It was left as it is.");
                if (c.UsedInVariations.Any())
                    throw new InvalidOperationException(
                        $"A variation uses {c.Name}, so it has to stay available. It was left as it is.");
                if (c.SortByColumn != null)
                    throw new InvalidOperationException(
                        $"{c.Name} is sorted by {c.SortByColumn.Name}, so it has to stay available for the sort. It was left as it is.");
            }

            c.IsAvailableInMDX = available;
        }

        private static bool TryParseBool(string text, out bool value)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "true": case "yes": case "1": value = true; return true;
                case "false": case "no": case "0": value = false; return true;
                default: value = false; return false;
            }
        }

        // ---- set_property (census enums) ------------------------------------------------------------------

        /// <summary>The enum settings the plan may set through set_property, by object type and property, with
        /// the choices the census lists for each (docs/redesign/jev-audit/tom-census.json, Microsoft.
        /// AnalysisServices.Tabular 19.114.0). A SUBSET of the census's enum rows, on purpose:
        ///   DataType has its own kind, with the conversion check a plain enum write would skip.
        ///   Permissions (MetadataPermission, ModelPermission) change who can see what, and a plan line is not
        ///   the place for a security change.
        ///   Storage modes (Partition.Mode, Model.DefaultMode) need a refresh to mean anything.
        ///   Identity fields (Kind, Type, ContentType, ArtifactType, TranslatedProperty, PolicyType) are not
        ///   choices at all; they say what an object IS.
        /// A setting not listed here is refused by name.</summary>
        private static readonly Dictionary<string, Dictionary<string, string[]>> CensusEnums = BuildCensusEnums();

        private static Dictionary<string, Dictionary<string, string[]>> BuildCensusEnums()
        {
            var columnSettings = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["Alignment"] = new[] { "Default", "Left", "Right", "Center" },
                ["SummarizeBy"] = new[] { "Default", "None", "Sum", "Min", "Max", "Count", "Average", "DistinctCount" },
                ["EncodingHint"] = new[] { "Default", "Hash", "Value" },
            };
            return new Dictionary<string, Dictionary<string, string[]>>(StringComparer.Ordinal)
            {
                ["DataColumn"] = columnSettings,
                ["CalculatedColumn"] = columnSettings,
                ["CalculatedTableColumn"] = columnSettings,
                ["Hierarchy"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["HideMembers"] = new[] { "Default", "HideBlankMembers" },
                },
                ["SingleColumnRelationship"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["CrossFilteringBehavior"] = new[] { "OneDirection", "BothDirections", "Automatic" },
                    ["SecurityFilteringBehavior"] = new[] { "OneDirection", "BothDirections", "None" },
                    ["JoinOnDateBehavior"] = new[] { "DateAndTime", "DatePartOnly" },
                },
                ["Model"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["ValueFilterBehavior"] = new[] { "Automatic", "Independent", "Coalesced" },
                    ["SelectionExpressionBehavior"] = new[] { "Automatic", "Visual", "NonVisual" },
                    ["DirectLakeBehavior"] = new[] { "Automatic", "DirectLakeOnly", "DirectQueryOnly" },
                },
            };
        }

        private static void ApplySetProperty(ChangeItem it, ITabularNamedObject obj)
        {
            if (obj == null)
                throw new InvalidOperationException($"Object not found: {it.ObjectRef}. Fix the ref with set_plan_item or drop the item, then apply_plan again.");

            var name = obj.Name;
            var property = (it.Target ?? "").Trim();
            var typeName = obj.GetType().Name;
            if (property.Length == 0
                || !CensusEnums.TryGetValue(typeName, out var settings)
                || !settings.TryGetValue(property, out var choices))
                throw new InvalidOperationException(
                    $"{(property.Length == 0 ? "This change" : property)} is not a setting the plan can change on {name} this way. "
                    + "Only settings with a fixed list of choices can be set here.");

            var value = (it.After ?? "").Trim();
            var choice = choices.FirstOrDefault(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));
            if (choice == null)
                throw new InvalidOperationException(
                    $"{(value.Length == 0 ? "An empty value" : value)} is not one of the choices for {property} on {name}. "
                    + $"The choices are {string.Join(", ", choices)}.");

            var pi = obj.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
            if (pi == null || !pi.CanWrite || !pi.PropertyType.IsEnum
                || !Enum.TryParse(pi.PropertyType, choice, true, out var parsed) || !Enum.IsDefined(pi.PropertyType, parsed))
                throw new InvalidOperationException(
                    $"{name} cannot take {property} {choice} in this model, so it was left as it is.");

            if (obj is Column sc && property == "SummarizeBy") GuardSummarizeBy(sc, (AggregateFunction)parsed);
            pi.SetValue(obj, parsed);
        }

        // ---- create_measure -------------------------------------------------------------------------------
        //
        // The starter measures family's kind (StarterMeasuresFamily.cs). The line's ref names the measure it would
        // create ("measure:Sales/Total Amount") and After is the formula. It goes through CreateMeasureCore, the
        // same path create_measure takes on both doors, which refuses a formula the model's DAX check does not
        // pass. A name already taken anywhere in the model is refused in a plain sentence rather than letting the
        // wrapper pick "Total Amount 1": a measure nobody asked for, under a name nobody reviewed, is not the
        // change that was approved.

        private static void ApplyCreateMeasure(Model m, ChangeItem it, ITabularNamedObject existing)
        {
            var (tableName, name) = SplitMeasureRef(it.ObjectRef);
            if (tableName == null)
                throw new InvalidOperationException(
                    $"{it.ObjectRef} does not name a measure to add. It should read measure:Table/Name.");

            // The same line applied twice (a retry, or undo then redo through the plan) finds its own measure.
            if (existing is Measure had)
            {
                if (PlanTextEquals(had.Expression, it.After)) { it.Note = "applied: already in the model, so nothing changed"; return; }
                throw new InvalidOperationException(
                    $"A measure named {name} already exists with a different formula, so it was not added again.");
            }
            if (!m.Tables.Contains(tableName))
                throw new InvalidOperationException($"{tableName} is not a table in this model any more, so {name} was not added.");
            var table = m.Tables[tableName];
            var taken = StarterMeasureTemplates.NameTaken(m, table, name);
            if (taken != null) throw new InvalidOperationException(taken);
            // The folder rides the same core create_measure uses, so a composed measure is born filed too.
            var created = CreateMeasureCore(table, name, it.After, Arg(it, "displayFolder"));
            // A measure a family adds on purpose (a column's base measure) is never offered back as unused.
            Lineage.LineageGraph.MarkAddedOnPurpose(created, Arg(it, "addedOnPurpose"));
        }

        /// <summary>"measure:Sales/Total Amount" into its table and its name, or (null, null).</summary>
        internal static (string Table, string Name) SplitMeasureRef(string objRef)
        {
            const string prefix = "measure:";
            if (objRef == null || !objRef.StartsWith(prefix, StringComparison.Ordinal)) return (null, null);
            var rest = objRef.Substring(prefix.Length);
            var slash = rest.IndexOf('/');
            if (slash <= 0 || slash == rest.Length - 1) return (null, null);
            return (rest.Substring(0, slash), rest.Substring(slash + 1));
        }

        private static string LiveSetProperty(ChangeItem it, ITabularNamedObject obj)
        {
            var pi = obj?.GetType().GetProperty((it.Target ?? "").Trim(), BindingFlags.Public | BindingFlags.Instance);
            return pi == null || !pi.PropertyType.IsEnum ? null : pi.GetValue(obj)?.ToString();
        }

        // ==================================================================================================
        // The composer's kinds (composer contract 3.2, lane L3): one applier per op the composer may run,
        // used by the op, the Plan and the composer alike. A second copy of any of these is a defect.
        //
        // HOW THE PIECES FIT. Each op below now builds its object through a synchronous core (this section, or
        // the one it already had: CreateImportTableCore, CreateColumnCore, CreateRelationshipCore,
        // CreateMeasureCore). The op wraps its core in its own MutateAsync; the applier reads the line's
        // arguments and calls the SAME core inside whatever batch it is in, so a composed change of seven steps
        // is one batch and one undo entry. A core checks before it writes and refuses in one sentence, and a
        // core that writes DAX holds it to the write path's rule (RefuseDaxForWrite) against the model as this
        // batch has left it, which is how a formula over a table the same change created passes.
        //
        // WHERE THE ARGUMENTS TRAVEL. ObjectRef and After keep the meaning every older kind gives them (the
        // object, the new value). What a create needs beyond that goes in ChangeItem.Args, by the op's own
        // argument names, so the line reads the same as the op call it stands for.
        // ==================================================================================================

        private static string Arg(ChangeItem it, string key) =>
            it.Args != null && it.Args.TryGetValue(key, out var v) ? v : null;

        /// <summary>An array argument (variants, refs, terms) is carried as a JSON array; a plain value is a
        /// one-item list. Null or empty is an empty list.</summary>
        internal static string[] ArgList(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
            var t = value.Trim();
            if (t.StartsWith("[", StringComparison.Ordinal))
            {
                try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(t) ?? Array.Empty<string>(); }
                catch (System.Text.Json.JsonException) { throw new InvalidOperationException($"'{value}' is not a list of values."); }
            }
            return new[] { t };
        }

        private static bool? ArgBool(ChangeItem it, string key)
        {
            var v = Arg(it, key);
            if (string.IsNullOrWhiteSpace(v)) return null;
            if (TryParseBool(v, out var b)) return b;
            throw new InvalidOperationException($"'{v}' is not true or false.");
        }

        // ---- create_import_table --------------------------------------------------------------------------

        private static void ApplyCreateImportTable(Model m, ChangeItem it)
        {
            var name = Arg(it, "name") ?? TableNameOf(it.ObjectRef);
            CreateImportTableCore(m, name, it.After);
        }

        // ---- create_column --------------------------------------------------------------------------------

        private static void ApplyCreateColumn(Model m, ChangeItem it)
        {
            var tableRef = Arg(it, "tableRef");
            if (!(ObjectRefs.Resolve(m, tableRef) is Table t))
                throw new InvalidOperationException($"{tableRef} is not a table, so the column {Arg(it, "name")} was not added.");
            CreateColumnCore(t, Arg(it, "name"), Arg(it, "dataType"), Arg(it, "sourceColumn"));
        }

        // ---- create_relationship --------------------------------------------------------------------------

        private static void ApplyCreateRelationship(Model m, ChangeItem it)
        {
            var fromRef = Arg(it, "fromColumnRef");
            var toRef = Arg(it, "toColumnRef");
            if (!(ObjectRefs.Resolve(m, fromRef) is Column from))
                throw new InvalidOperationException($"{fromRef} is not a column, so the relationship was not added.");
            if (!(ObjectRefs.Resolve(m, toRef) is Column to))
                throw new InvalidOperationException($"{toRef} is not a column, so the relationship was not added.");
            CreateRelationshipCore(m, from, to, Arg(it, "crossFilter"), ArgBool(it, "isActive"), Arg(it, "cardinality"));
        }

        // ---- set_relationship_active and set_relationship_cardinality -------------------------------------

        private static SingleColumnRelationship RelationshipFor(Model m, string objRef) =>
            m.Relationships.OfType<SingleColumnRelationship>().FirstOrDefault(r => r.Name == RelName(objRef))
            ?? throw new InvalidOperationException($"Relationship '{RelName(objRef)}' not found. Run get_model_summary (or list_objects) to see relationship names; create one with create_relationship.");

        private static void ApplyRelationshipActive(Model m, ChangeItem it)
        {
            if (!TryParseBool(it.After, out var active))
                throw new InvalidOperationException($"'{it.After}' is not true or false, so the relationship was left as it is.");
            SetRelationshipCore(m, RelationshipFor(m, it.ObjectRef), null, active);
        }

        private static void ApplyRelationshipCardinality(Model m, ChangeItem it) =>
            SetRelationshipCardinalityCore(RelationshipFor(m, it.ObjectRef), Arg(it, "fromCardinality"), Arg(it, "toCardinality"));

        /// <summary>The cross filter and active settings of one relationship: the core set_relationship_crossfilter
        /// and set_relationship_active run. True when anything changed.</summary>
        private static bool SetRelationshipCore(Model m, SingleColumnRelationship rel, string crossFilteringBehavior, bool? isActive)
        {
            var changed = false;
            if (crossFilteringBehavior != null)
            {
                if (!Enum.TryParse<CrossFilteringBehavior>(crossFilteringBehavior, true, out var cf)
                    || !Enum.IsDefined(typeof(CrossFilteringBehavior), cf)
                    || (cf != CrossFilteringBehavior.OneDirection && cf != CrossFilteringBehavior.BothDirections))
                    throw new InvalidOperationException("Cross-filter direction must be single direction or both directions.");
                if (rel.CrossFilteringBehavior != cf) { rel.CrossFilteringBehavior = cf; changed = true; }
            }
            if (isActive.HasValue && rel.IsActive != isActive.Value)
            {
                if (isActive.Value) GuardNoOtherActiveOnTablePair(m, rel.FromColumn, rel.ToColumn, rel);
                rel.IsActive = isActive.Value; changed = true;
            }
            return changed;
        }

        /// <summary>A relationship's end cardinalities (Many or One). A null or empty end is left as it is.</summary>
        private static bool SetRelationshipCardinalityCore(SingleColumnRelationship rel, string fromCardinality, string toCardinality)
        {
            RelationshipEndCardinality Parse(string v, RelationshipEndCardinality current)
            {
                if (string.IsNullOrWhiteSpace(v)) return current;
                if (!Enum.TryParse<RelationshipEndCardinality>(v, true, out var c) || (c != RelationshipEndCardinality.One && c != RelationshipEndCardinality.Many))
                    throw new InvalidOperationException($"Cardinality must be 'One' or 'Many' (got '{v}').");
                return c;
            }
            var from = Parse(fromCardinality, rel.FromCardinality);
            var to = Parse(toCardinality, rel.ToCardinality);
            var changed = false;
            if (rel.FromCardinality != from) { rel.FromCardinality = from; changed = true; }
            if (rel.ToCardinality != to) { rel.ToCardinality = to; changed = true; }
            return changed;
        }

        // ---- create_calculation_group and create_calculation_item -----------------------------------------

        private static CalculationGroupTable CreateCalculationGroupCore(Model m, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A calculation group name is required.");
            if ((m.Database?.CompatibilityLevel ?? 0) < 1470)
                throw new InvalidOperationException($"Calculation groups require compatibility level 1470 or higher (this model is {m.Database?.CompatibilityLevel ?? 0}; raise Compatibility level in Properties).");
            if (m.Tables.Contains(name)) throw new InvalidOperationException($"A table named '{name}' already exists.");
            return m.AddCalculationGroup(name);
        }

        private static CalculationItem CreateCalculationItemCore(CalculationGroupTable cg, string name, string expression)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A calculation item name is required.");
            if (cg.CalculationItems.Contains(name)) throw new InvalidOperationException($"Calculation group '{cg.Name}' already has an item '{name}'.");
            // The write path's rule, against the model as this batch has left it (as CreateMeasureCore does).
            if (!string.IsNullOrWhiteSpace(expression))
                RefuseDaxForWrite(DaxValidator.Validate(cg.Model, expression), expression, "this new calculation item");
            return cg.AddCalculationItem(name, expression ?? string.Empty);
        }

        private static void ApplyCreateCalculationGroup(Model m, ChangeItem it) =>
            CreateCalculationGroupCore(m, Arg(it, "name") ?? TableNameOf(it.ObjectRef));

        private static void ApplyCreateCalculationItem(Model m, ChangeItem it)
        {
            var groupRef = Arg(it, "calcGroupRef") ?? it.ObjectRef;
            if (!(ObjectRefs.Resolve(m, groupRef) is CalculationGroupTable cg))
                throw new InvalidOperationException($"{groupRef} is not a calculation group, so the item {Arg(it, "name")} was not added.");
            CreateCalculationItemCore(cg, Arg(it, "name"), it.After);
        }

        // ---- create_role and set_table_permission ---------------------------------------------------------

        private static ModelRole CreateRoleCore(Model m, string name, string modelPermission)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A role name is required.");
            if (FindRole(m, name) != null) throw new InvalidOperationException($"A role named '{name}' already exists.");
            var role = m.AddRole(name);
            if (!string.IsNullOrWhiteSpace(modelPermission)) role.ModelPermission = ParsePermission(modelPermission);
            return role;
        }

        private static void ApplyCreateRole(Model m, ChangeItem it) =>
            CreateRoleCore(m, Arg(it, "name"), Arg(it, "modelPermission"));

        /// <summary>A role's row filter on one table; an empty filter removes it. The filter is held to the write
        /// path's rule against the model as this batch has left it. Promoted is true when setting the filter
        /// lifted the role's permission from None.</summary>
        private static (bool Changed, bool Promoted, string Permission) SetTablePermissionCore(Model m, string roleName, string tableRef, string filterDax, string expectedFilter = null)
        {
            var role = FindRole(m, roleName) ?? throw new InvalidOperationException($"Role '{roleName}' not found. Run list_roles to see roles; create one with create_role.");
            var table = ResolveTable(m, tableRef) ?? throw new InvalidOperationException($"Table not found: {tableRef}. Run list_objects to see the model's tables.");
            // A calc group isn't a row-bearing table; the vendored RoleRLSIndexer deliberately excludes calc groups
            // (NonCalculationGroupTables) and its setter has no Keys check, so guard here against an orphaned permission.
            if (table is CalculationGroupTable)
                throw new InvalidOperationException($"RLS row-filters cannot be applied to a calculation group table ('{table.Name}').");
            if (!string.IsNullOrWhiteSpace(filterDax))
                RefuseDaxForWrite(DaxValidator.Validate(m, filterDax), filterDax, "this row filter");
            var before = role.ModelPermission;
            var current = role.RowLevelSecurity[table] ?? "";
            var next = string.IsNullOrWhiteSpace(filterDax) ? "" : filterDax;
            GuardExpectedValue("row filter", expectedFilter, current, next);   // a UI save: the filter the editor started from against what is live, in this turn
            var changed = false;
            if (!string.Equals(current, next, StringComparison.Ordinal))
            {
                role.RowLevelSecurity[table] = next.Length == 0 ? null : next;   // null removes the filter/permission
                changed = true;
            }
            return (changed, before == ModelPermission.None && role.ModelPermission != ModelPermission.None, role.ModelPermission.ToString());
        }

        private static void ApplySetTablePermission(Model m, ChangeItem it)
        {
            var (changed, promoted, _) = SetTablePermissionCore(m, Arg(it, "roleName"), Arg(it, "tableRef") ?? it.ObjectRef, it.After);
            if (!changed) it.Note = "applied: the row filter was already this, so nothing changed";
            else if (promoted) it.Note = "applied: the role could not read the model before, so setting a row filter gave it read permission";
        }

        // ---- the generators: generate_date_table and generate_time_intelligence ---------------------------

        private static GeneratedObject GenerateDateTableCore(Model m, string tableName, string startExpr, string endExpr, bool markAsDate)
        {
            tableName = string.IsNullOrWhiteSpace(tableName) ? "Date" : tableName.Trim();
            var start = string.IsNullOrWhiteSpace(startExpr) ? "DATE(YEAR(TODAY())-5,1,1)" : startExpr.Trim();
            var end = string.IsNullOrWhiteSpace(endExpr) ? "DATE(YEAR(TODAY()),12,31)" : endExpr.Trim();
            if (m.Tables.Contains(tableName)) throw new InvalidOperationException($"A table named '{tableName}' already exists.");
            // F-249 sweep: start and end are caller DAX spliced into the table expression, so they take the shared
            // write guard. Checked alone: the full expression's [Date] is CALENDAR's own column, not a model one.
            RefuseDaxForWrite(DaxValidator.Validate(m, start), start, "the date table's start date");
            RefuseDaxForWrite(DaxValidator.Validate(m, end), end, "the date table's end date");
            var expr =
$@"ADDCOLUMNS(
    CALENDAR({start}, {end}),
    ""Year"", YEAR([Date]),
    ""Quarter"", ""Q"" & ROUNDUP(MONTH([Date]) / 3, 0),
    ""Quarter Number"", ROUNDUP(MONTH([Date]) / 3, 0),
    ""Year Quarter"", YEAR([Date]) & ""-Q"" & ROUNDUP(MONTH([Date]) / 3, 0),
    ""Half Year"", ""H"" & IF(MONTH([Date]) <= 6, 1, 2),
    ""Month Number"", MONTH([Date]),
    ""Month"", FORMAT([Date], ""mmmm""),
    ""Year Month"", FORMAT([Date], ""yyyy-MM""),
    ""Year Month Sort"", YEAR([Date]) * 100 + MONTH([Date]),
    ""Day"", DAY([Date]),
    ""Day of Week Number"", WEEKDAY([Date], 2),
    ""Day of Week"", FORMAT([Date], ""dddd""),
    ""Month Offset"", (YEAR([Date]) - YEAR(TODAY())) * 12 + (MONTH([Date]) - MONTH(TODAY())),
    ""Day Offset"", DATEDIFF(TODAY(), [Date], DAY),
    ""Is Current Month"", YEAR([Date]) = YEAR(TODAY()) && MONTH([Date]) = MONTH(TODAY()),
    ""Is Current Year"", YEAR([Date]) = YEAR(TODAY()),
    ""Is Today"", [Date] = TODAY(),
    ""Is Past"", [Date] < TODAY()
)";
            var ct = m.AddCalculatedTable(tableName, expr);
            // CALENDAR emits one column named Date. It is declared now, the way the calendar templates declare it
            // (LocalEngine.CalendarTemplates.cs; a processed model infers the same column), so the table can be marked
            // before it is ever deployed. Marked means the category AND the date column as the key (MarkDateTableCore,
            // joint verifier DT2): setting the category alone left a table the plan line and the op both called marked
            // with no key, because a calculated table has no columns of its own until it is processed.
            var date = ct.AddCalculatedTableColumn("Date", "[Date]", null, DataType.DateTime);
            date.IsNameInferred = true;
            if (markAsDate) MarkDateTableCore(ct, date.Name);
            return new GeneratedObject { Ref = ObjectRefs.For(ct), Name = ct.Name, Expression = expr };
        }

        private static void GenerateTimeIntelligenceCore(Model m, string baseMeasureRef, string dateColumn, string[] variants, string displayFolder,
                                                         List<GeneratedObject> created, List<string> skipped)
        {
            if (!(ObjectRefs.Resolve(m, baseMeasureRef) is Measure baseMeasure))
                throw new InvalidOperationException($"{baseMeasureRef} is not a measure. generate_time_intelligence wraps a base measure; run list_measures to find one.");
            var table = baseMeasure.Table ?? throw new InvalidOperationException("Base measure has no table.");
            var baseName = baseMeasure.Name;
            var baseRef = $"[{baseName}]";
            var dateCol = ResolveDateColumnDax(m, dateColumn);
            // F-249 sweep: a raw date column is caller DAX spliced into every generated measure; guard it once here.
            RefuseDaxForWrite(DaxValidator.Validate(m, dateCol), dateCol, "the date column");
            var fmt = baseMeasure.FormatString;
            var folder = string.IsNullOrWhiteSpace(displayFolder) ? "Time Intelligence" : displayFolder;

            var wanted = (variants == null || variants.Length == 0)
                ? new[] { "YTD", "QTD", "MTD", "PY", "YoY", "YoYPct" }
                : variants;

            foreach (var v in wanted)
            {
                var (suffix, expr, vfmt) = BuildTiVariant(v, baseRef, dateCol, fmt);
                if (suffix == null) { skipped.Add($"{v} (unknown variant)"); continue; }
                var name = $"{baseName} {suffix}";
                if (table.Measures.Contains(name)) { skipped.Add(name + " (exists)"); continue; }
                var me = table.AddMeasure(name, expr, folder);
                if (!string.IsNullOrEmpty(vfmt)) me.FormatString = vfmt;
                Lineage.LineageGraph.MarkAddedOnPurpose(me, "a time-intelligence measure of " + baseName);
                created.Add(new GeneratedObject { Ref = ObjectRefs.For(me), Name = me.Name, Expression = expr });
            }
        }

        private static void ApplyGenerateDateTable(Model m, ChangeItem it) =>
            GenerateDateTableCore(m, Arg(it, "tableName"), Arg(it, "startExpr"), Arg(it, "endExpr"), ArgBool(it, "markAsDate") ?? true);

        private static void ApplyGenerateTimeIntelligence(Model m, ChangeItem it)
        {
            var created = new List<GeneratedObject>();
            var skipped = new List<string>();
            GenerateTimeIntelligenceCore(m, Arg(it, "baseMeasureRef") ?? it.ObjectRef, Arg(it, "dateColumn"),
                ArgList(Arg(it, "variants")), Arg(it, "displayFolder"), created, skipped);
            if (skipped.Count > 0) it.Note = "applied: skipped " + string.Join(", ", skipped);
        }

        /// <summary>"table:Returns" or "Returns" into "Returns".</summary>
        private static string TableNameOf(string tableRef) =>
            tableRef != null && tableRef.StartsWith("table:", StringComparison.OrdinalIgnoreCase) ? tableRef.Substring("table:".Length) : tableRef;
    }
}

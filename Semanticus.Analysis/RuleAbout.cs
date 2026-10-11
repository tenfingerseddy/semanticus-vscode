// What each rule is ABOUT: the one (object kind, property) a person is deciding when they accept its finding
// (m11/rule-clashes round 6).
//
// Accepting a finding is a decision about a change, not about a rule id. "Key column is visible" (VIS-KEY) is accepted
// because the person keeps a business code visible; that is a decision about the IsHidden of that column, and every other
// rule that would hide the column (Model quality's HIDE_FOREIGN_KEYS, the plan's own VIS-FK line) is asking to undo it.
// So each rule states, once, what its ask is about, whatever its fix kind is: a mapped fix, a Proposal, or none.
//
// Two shapes of acceptance:
//   Keep (the default). The rule's ask is "keep what is there" (a name, a format, a description) or every rule that writes
//        the property writes the same one value (hide, SummarizeBy None, IsKey, a data category). Accepting it declines ANY
//        write to (object, property).
//   Own  The property carries several findings' different asks (the AI-instructions line, where three findings each want
//        something else in one text), or sibling rules write different values on the same object (a date format and a
//        month format; a rename to the table's own name beside a clearer name). Accepting one declines ITS OWN ask only,
//        and, where the rule writes a value that can be named, that value. Every expression rule (LIMIT-DAX-LENGTH, the
//        BP-DAX rules, the Model quality DAX rules) is Own: each asks a different rewrite of one text property, and no
//        path writes an expression from a finding, so the promise loses nothing by leaving them apart (round 7, F2).
//
// A row never over-declines (round 7): an acceptance may only read another finding as accepted when that finding asks the
// same thing. RuleClashesRound7Tests checks that every row's property is one its own rule's condition reads.
//
// The Model quality (BPA) rules that carry a FixExpression are read off that expression, so a rule loaded later is covered
// with no edit. The rest are listed below with the property they are about, or with the reason they decline nothing.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Analysis
{
    public sealed class RuleAbout
    {
        /// <summary>The object-ref prefixes the rule's findings sit on ("column", "measure", "table", "obj" for the model).</summary>
        public string[] Kinds { get; }
        /// <summary>The property as the plan names it ("isHidden", "formatString"); null when the rule declines nothing.</summary>
        public string Property { get; }
        /// <summary>True when an acceptance declines the rule's own ask only (see the file comment).</summary>
        public bool OwnAsk { get; }
        /// <summary>Own only: the value the rule would write on this object ref, when it can be named.</summary>
        public Func<string, string> OwnValue { get; }
        /// <summary>Why the rule declines nothing (Property is null).</summary>
        public string Reason { get; }
        /// <summary>True when the finding sits on the model but the write is on an object of <see cref="Kinds"/> ("no marked
        /// date table" is a finding about the model; the write is one table's data category). An acceptance of the model-level
        /// finding, or of the rule as a whole, declines that write on any object of those kinds.</summary>
        public bool FromModel { get; private set; }

        private RuleAbout(string[] kinds, string property, bool own, Func<string, string> ownValue, string reason)
        { Kinds = kinds; Property = property; OwnAsk = own; OwnValue = ownValue; Reason = reason; }

        public bool DeclinesSomething => Property != null;

        public static RuleAbout Keep(string kinds, string property) => new RuleAbout(kinds.Split(','), property, false, null, null);
        public static RuleAbout Own(string kinds, string property, Func<string, string> value = null) => new RuleAbout(kinds.Split(','), property, true, value, null);
        public static RuleAbout KeepFromModel(string kinds, string property) => new RuleAbout(kinds.Split(','), property, false, null, null) { FromModel = true };
        public static RuleAbout Nothing(string reason) => new RuleAbout(Array.Empty<string>(), null, false, null, reason);
        internal static RuleAbout OwnLiteral(string kinds, string property, string literal) => Own(kinds, property, _ => literal);
    }

    /// <summary>The rule-to-property table: readiness rules by id, and the Model quality rules that have no FixExpression to
    /// read it from. Complete for every bundled rule, which a test pins.</summary>
    public static class RuleAboutTable
    {
        private const string Col = "column", Meas = "measure", Tab = "table", Model = "obj", Hier = "hierarchy", Rel = "relationship";

        private static string TableNameOf(string objectRef)
        {
            if (string.IsNullOrEmpty(objectRef) || !objectRef.StartsWith("column:", StringComparison.Ordinal)) return null;
            var body = objectRef.Substring("column:".Length);
            var slash = body.IndexOf('/');
            return slash <= 0 ? null : body.Substring(0, slash);
        }

        private static readonly RuleAbout Advisory = RuleAbout.Nothing("advice about the model as a whole; no single property is written");
        private static readonly RuleAbout Structural = RuleAbout.Nothing("the change adds, removes or restructures objects; no single property is written");
        private static readonly RuleAbout EitherOr = RuleAbout.Nothing("an either-or proposal (change it one way or the other); no single property is asked for");

        /// <summary>AI-readiness rules by id.</summary>
        public static readonly IReadOnlyDictionary<string, RuleAbout> Readiness = new Dictionary<string, RuleAbout>(StringComparer.OrdinalIgnoreCase)
        {
            // Descriptions
            ["DESC-MEASURE"] = RuleAbout.Keep(Meas, "description"),
            ["DESC-ECHO"] = RuleAbout.Keep(Meas, "description"),
            ["DESC-LONG"] = RuleAbout.Keep(Meas, "description"),
            ["DESC-TABLE"] = RuleAbout.Keep(Tab, "description"),
            ["DESC-COLUMN"] = RuleAbout.Keep(Col, "description"),
            ["DESC-PLACEHOLDER"] = RuleAbout.Keep(Tab + "," + Col, "description"),
            ["DESC-LONG-OBJECT"] = RuleAbout.Keep(Tab + "," + Col, "description"),
            ["DESC-ECHO-OBJECT"] = RuleAbout.Keep(Tab + "," + Col, "description"),
            ["DESC-CALCGROUP-ITEMS"] = RuleAbout.Keep(Tab, "description"),
            ["DESC-HIERARCHY"] = RuleAbout.Keep(Hier, "description"),
            ["DAC-CALC-GROUP"] = RuleAbout.Keep(Tab, "description"),
            // Naming
            ["NAME-MEASURE"] = RuleAbout.Keep(Meas, "name"),
            ["NAME-COLUMN"] = RuleAbout.Keep(Col, "name"),
            ["NAME-COLUMN-ID"] = RuleAbout.Keep(Col, "name"),
            ["NAME-TABLE"] = RuleAbout.Keep(Tab, "name"),
            ["NAME-DUP"] = RuleAbout.Keep(Col, "name"),   // it evaluates visible columns only
            ["NAME-INVALID-CHARS"] = RuleAbout.Keep(Tab + "," + Col + "," + Meas, "name"),   // measures, columns and tables; not hierarchies
            ["NAME-TECH-PREFIX"] = RuleAbout.Keep(Tab, "name"),
            ["NAME-GENERIC"] = RuleAbout.Keep(Tab + "," + Col + "," + Meas, "name"),
            ["NAME-HIERARCHY"] = RuleAbout.Keep(Hier, "name"),
            // Its fix names a column as its dimension's row label (IsDefaultLabel, Kane 27 Sep: it no longer renames the column), so
            // accepting it declines that one write on the column its finding is filed on, and never a rename.
            ["DIM-PRIMARY-NAME"] = RuleAbout.Keep(Col, "isDefaultLabel"),
            // Synonyms
            ["SYN-SCHEMA"] = RuleAbout.Keep(Model, "linguisticSchema"),
            ["SYN-LSDL-XML"] = RuleAbout.Keep(Model, "linguisticSchema"),
            ["SYN-ENTITY-ORPHAN"] = RuleAbout.Keep(Model, "linguisticSchema"),
            ["SYN-FIELD"] = RuleAbout.Keep(Meas + "," + Col, "synonyms"),
            ["SYN-TABLE"] = RuleAbout.Keep(Tab, "synonyms"),
            ["SYN-COLLIDE"] = RuleAbout.Keep(Meas + "," + Col + "," + Tab, "synonyms"),
            // Visibility
            ["SEC-OLS-AWARENESS"] = RuleAbout.Nothing("governance information about object-level security; nothing is asked to change"),
            ["VIS-FK"] = RuleAbout.Keep(Col, "isHidden"),
            ["VIS-KEY"] = RuleAbout.Keep(Col, "isHidden"),
            ["VIS-TECH-COLUMN"] = RuleAbout.Keep(Col, "isHidden"),
            ["VIS-HELPER"] = RuleAbout.Keep(Meas, "isHidden"),
            ["VIS-TABLE-ALL-HIDDEN"] = RuleAbout.Keep(Tab, "isHidden"),
            // Formatting and aggregation
            ["FMT-SUMMARIZE"] = RuleAbout.Keep(Col, "summarizeBy"),
            ["SUMMARIZE-DIMENSION"] = RuleAbout.Keep(Col, "summarizeBy"),
            ["DAC-IMPLICIT-MEASURE"] = RuleAbout.Keep(Col, "summarizeBy"),
            ["FMT-MEASURE"] = RuleAbout.Keep(Meas, "formatString"),
            ["FMT-COLUMN"] = RuleAbout.Keep(Col, "formatString"),
            ["CAT-GEO"] = RuleAbout.Keep(Col, "dataCategory"),
            ["CAT-URL"] = RuleAbout.Keep(Col, "dataCategory"),
            ["FMT-DATATYPE"] = RuleAbout.Keep(Col, "dataType"),
            ["FMT-SORTBY"] = RuleAbout.Keep(Col, "sortByColumn"),
            // Relationships
            ["REL-BIDI"] = RuleAbout.Keep(Rel, "crossFilteringBehavior"),
            ["REL-M2M"] = RuleAbout.Keep(Rel, "cardinality"),   // what the rule checks is the cardinality; it says nothing about the direction of filtering
            ["REL-INACTIVE"] = RuleAbout.Keep(Rel, "isActive"),
            ["REL-SNOWFLAKE"] = Structural,
            ["REL-DISCONNECTED"] = Structural,
            ["REL-HIERARCHY-MISSING"] = Structural,
            ["REL-HIERARCHY-SINGLE-LEVEL"] = Structural,
            ["DATE-AMBIGUOUS"] = EitherOr,
            ["DATE-MARK"] = RuleAbout.KeepFromModel(Tab, "dataCategory"),   // the finding is on the model; the write is the marked table's data category and key
            ["CAL-TI-NO-CALENDAR"] = Advisory,
            ["CAL-MAPPING"] = RuleAbout.Nothing("the change is a calendar column mapping, which is not a property of the object the finding sits on"),
            // Copilot limits
            ["LIMIT-SCALE"] = Advisory,
            ["LIMIT-QNA-INDEX"] = Advisory,
            ["LIMIT-DATAAGENT-TABLES"] = Advisory,
            ["LIMIT-DAX-LENGTH"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["SCALE-HICARD-COLUMN"] = EitherOr,
            ["SCALE-QNA-INDEX"] = Advisory,
            // The DAX lint rules each ask a different rewrite of the object's expression, so an acceptance declines its own ask only.
            ["BP-DAX-IFERROR"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-IFERROR-DIV"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-IFERROR-SEARCH"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-SUMMARIZE-EXT"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-BARE-TABLE-FILTER"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-VAR-ALIAS"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-ISBLANK-EQ"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-DIV-GUARD"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-ERROR-FN"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-MEASURE-PREDICATE"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-EARLIER"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-VAR-UNUSED"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-SELECTEDVALUE"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-DISTINCTCOUNT"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["BP-DAX-REMOVEFILTERS"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            // Data agent configuration
            ["DAC-QNA-STORAGE"] = Advisory,
            ["CFG-PREP-MODE"] = Advisory,
            ["DAC-QNA-DISABLED"] = Advisory,
            ["DAC-VERIFIED-ANSWERS"] = RuleAbout.Nothing("verified answers are authored in the service, not written to a model property"),
            ["DAC-QNA-NAME-COLLISIONS"] = EitherOr,
            // One line carries several findings' asks about the instructions text, so an acceptance declines its own ask.
            ["DAC-AI-INSTRUCTIONS"] = RuleAbout.Own(Model, "aiInstructions"),
            ["DAC-AI-INSTRUCTIONS-LEN"] = RuleAbout.Own(Model, "aiInstructions"),
            ["DAC-GLOSSARY-GAP"] = RuleAbout.Own(Model, "aiInstructions"),
            ["DAC-AI-DATA-SCHEMA"] = RuleAbout.Nothing("the AI data schema is authored in the service; the change is a list of excluded fields, not one property"),
            ["AISCHEMA-DEP-MISSING"] = RuleAbout.Nothing("the change edits the AI data schema's field list, not one property"),
            ["DAC-COPILOT-TOOLING-FORMAT"] = Advisory,
            ["DAC-PERSPECTIVE-NOT-SCOPE"] = Advisory,
            ["DAC-FIELD-PARAM-COMPLEXITY"] = Advisory,
            ["MEAS-DUP-EXPR"] = RuleAbout.Nothing("the change removes or merges a duplicate measure; no single property is written"),
            ["DAX-REFERENCE"] = RuleAbout.Nothing("the change points a formula at objects the model has; which object it should name is the author's choice, so no one property is written"),
            ["BP-AUTO-DATETIME"] = RuleAbout.Nothing("the change removes the automatic date tables or turns a model setting off; no single property of the object is written"),
        };

        /// <summary>Model quality (BPA) rules by id that have no FixExpression to read the target from.</summary>
        public static readonly IReadOnlyDictionary<string, RuleAbout> ModelQuality = new Dictionary<string, RuleAbout>(StringComparer.OrdinalIgnoreCase)
        {
            ["FORMULA_REFERENCE_NOT_IN_MODEL"] = RuleAbout.Nothing("the change points a formula at objects the model has; which object it should name is the author's choice, so no one property is written"),
            ["OBJECTS_WITH_NO_DESCRIPTION"] = RuleAbout.Keep(Tab + "," + Meas + "," + Col, "description"),
            ["PROVIDE_FORMAT_STRING_FOR_MEASURES"] = RuleAbout.Keep(Meas, "formatString"),
            ["SEM_COLUMN_NEEDS_FORMAT_STRING"] = RuleAbout.Keep(Col, "formatString"),
            ["FORMATTING_MEASURE_FORMAT_STRING"] = RuleAbout.Keep(Meas, "formatString"),
            ["FORMATTING_NUMERIC_COLUMN_FORMAT"] = RuleAbout.Keep(Col, "formatString"),
            ["ADD_DATA_CATEGORY_FOR_COLUMNS"] = RuleAbout.Keep(Col, "dataCategory"),
            ["RELATIONSHIP_COLUMNS_SHOULD_BE_OF_INTEGER_DATA_TYPE"] = RuleAbout.Keep(Col, "dataType"),
            ["DATA_COLUMNS_MUST_HAVE_A_SOURCE_COLUMN"] = RuleAbout.Keep(Col, "sourceColumn"),
            ["MONTH_(AS_A_STRING)_MUST_BE_SORTED"] = RuleAbout.Keep(Col, "sortByColumn"),
            ["MANY-TO-MANY_RELATIONSHIPS_SHOULD_BE_SINGLE-DIRECTION"] = RuleAbout.Keep(Rel, "crossFilteringBehavior"),
            ["CHECK_IF_BI-DIRECTIONAL_AND_MANY-TO-MANY_RELATIONSHIPS_ARE_VALID"] = RuleAbout.Keep(Rel, "crossFilteringBehavior"),
            ["DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE"] = RuleAbout.Keep(Tab, "dataCategory"),
            ["SPECIAL_CHARS_IN_OBJECT_NAMES"] = RuleAbout.Keep(Tab + "," + Meas + "," + Col + "," + Hier, "name"),
            ["TRIM_OBJECT_NAMES"] = RuleAbout.Keep(Tab + "," + Meas + "," + Col + "," + Hier, "name"),
            ["OBJECTS_SHOULD_NOT_START_OR_END_WITH_A_SPACE"] = RuleAbout.Keep(Tab + "," + Meas + "," + Col + "," + Hier, "name"),
            ["FIRST_LETTER_OF_OBJECTS_MUST_BE_CAPITALIZED"] = RuleAbout.Keep(Tab + "," + Meas + "," + Col + "," + Hier, "name"),
            ["DAX_COLUMNS_FULLY_QUALIFIED"] = RuleAbout.Own(Meas, "expression"),
            ["DAX_MEASURES_UNQUALIFIED"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["USE_THE_TREATAS_FUNCTION_INSTEAD_OF_INTERSECT"] = RuleAbout.Own(Meas, "expression"),
            ["USE_THE_DIVIDE_FUNCTION_FOR_DIVISION"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["AVOID_USING_THE_IFERROR_FUNCTION"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["MEASURES_SHOULD_NOT_BE_DIRECT_REFERENCES_OF_OTHER_MEASURES"] = RuleAbout.Own(Meas, "expression"),
            ["FILTER_COLUMN_VALUES"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["FILTER_MEASURE_VALUES_BY_COLUMNS"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["AVOID_USING_'1-(X/Y)'_SYNTAX"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["EVALUATEANDLOG_SHOULD_NOT_BE_USED_IN_PRODUCTION_MODELS"] = RuleAbout.Own(Meas, "expression"),
            ["EXPRESSION_RELIANT_OBJECTS_MUST_HAVE_AN_EXPRESSION"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["SEM_UNFINISHED_MARKER_IN_EXPRESSION"] = RuleAbout.Own(Meas + "," + Col, "expression"),
            ["DAX_AVOID_IFERROR"] = RuleAbout.Own(Meas, "expression"),   // scoped to measures
            ["DAX_AVOID_FILTER_TABLE_REFERENCE"] = RuleAbout.Own(Meas, "expression"),   // scoped to measures
            ["MAINTENANCE_MEASURE_DESCRIPTION"] = RuleAbout.Keep(Meas, "description"),
            ["MAINTENANCE_TABLE_DESCRIPTION"] = RuleAbout.Keep(Tab, "description"),
            ["PERFORMANCE_FLOATING_POINT_DATA_TYPE"] = RuleAbout.Keep(Col, "dataType"),
            // Structure, advice and design rules: the fix adds, removes or restructures something, or is a choice the model's
            // design owns, so accepting one declines no property write.
            ["AVOID_BI-DIRECTIONAL_RELATIONSHIPS_AGAINST_HIGH-CARDINALITY_COLUMNS"] = RuleAbout.Nothing("the change is on the relationship the column joins, which this rule does not point at"),
            ["REDUCE_USAGE_OF_LONG-LENGTH_COLUMNS_WITH_HIGH_CARDINALITY"] = Structural,
            ["SPLIT_DATE_AND_TIME"] = Structural,
            ["LARGE_TABLES_SHOULD_BE_PARTITIONED"] = Structural,
            ["REDUCE_USAGE_OF_CALCULATED_COLUMNS_THAT_USE_THE_RELATED_FUNCTION"] = Structural,
            ["SNOWFLAKE_SCHEMA_ARCHITECTURE"] = Structural,
            // Its condition is DATE-MARK's exactly (no table is marked Time with a date key), and so is its change: a table's data category.
            // The finding is on the model, the write is on a table, so an acceptance declines that write on any table.
            ["MODEL_SHOULD_HAVE_A_DATE_TABLE"] = RuleAbout.KeepFromModel(Tab, "dataCategory"),
            ["REMOVE_AUTO-DATE_TABLE"] = Structural,
            ["AVOID_EXCESSIVE_BI-DIRECTIONAL_OR_MANY-TO-MANY_RELATIONSHIPS"] = Advisory,
            ["LIMIT_ROW_LEVEL_SECURITY_(RLS)_LOGIC"] = Advisory,
            ["MODEL_USING_DIRECT_QUERY_AND_NO_AGGREGATIONS"] = Advisory,
            ["MINIMIZE_POWER_QUERY_TRANSFORMATIONS"] = Advisory,
            ["AVOID_USING_MANY-TO-MANY_RELATIONSHIPS_ON_TABLES_USED_FOR_DYNAMIC_ROW_LEVEL_SECURITY"] = Advisory,
            ["UNPIVOT_PIVOTED_(MONTH)_DATA"] = Structural,
            ["REDUCE_USAGE_OF_CALCULATED_TABLES"] = Structural,
            ["REMOVE_REDUNDANT_COLUMNS_IN_RELATED_TABLES"] = Structural,
            ["MEASURES_USING_TIME_INTELLIGENCE_AND_MODEL_IS_USING_DIRECT_QUERY"] = Advisory,
            ["REDUCE_NUMBER_OF_CALCULATED_COLUMNS"] = Advisory,
            ["AVOID_DUPLICATE_MEASURES"] = Structural,
            ["INACTIVE_RELATIONSHIPS_THAT_ARE_NEVER_ACTIVATED"] = Structural,
            ["AVOID_STRUCTURED_DATA_SOURCES_WITH_PROVIDER_PARTITIONS"] = Advisory,
            ["AVOID_THE_USERELATIONSHIP_FUNCTION_AND_RLS_AGAINST_THE_SAME_TABLE"] = Advisory,
            ["RELATIONSHIP_COLUMNS_SAME_DATA_TYPE"] = RuleAbout.Nothing("the change is on a column of the relationship, which this rule does not point at"),
            ["FIX_REFERENTIAL_INTEGRITY_VIOLATIONS"] = RuleAbout.Nothing("the fix is in the data, not in a model property"),
            ["ENSURE_TABLES_HAVE_RELATIONSHIPS"] = Structural,
            ["CALCULATION_GROUPS_WITH_NO_CALCULATION_ITEMS"] = Structural,
            ["FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS"] = RuleAbout.Keep(Col, "formatString"),   // its description asks for the format string "Yes";"Yes";"No"
            ["SEM_RELATIONSHIP_KEY_NAMES_SHOULD_MATCH"] = RuleAbout.Nothing("the change renames a column of the relationship, which this rule does not point at"),
            ["SEM_MEASURES_NEED_DISPLAY_FOLDERS"] = RuleAbout.Nothing("the change writes the display folder of the table's measures, not a property of the table"),
            ["SEM_COLUMNS_NEED_DISPLAY_FOLDERS"] = RuleAbout.Nothing("the change writes the display folder of the table's columns, not a property of the table"),
            ["PARTITION_NAME_SHOULD_MATCH_TABLE_NAME_FOR_SINGLE_PARTITION_TABLES"] = RuleAbout.Nothing("the change renames the table's partition, not a property of the table"),
        };

        // A fix expression names its own target: "IsHidden = true", "Name = string.Concat( ... )". A call such as Delete() or
        // "Partitions[0].Name = it.Name" is not a property of the flagged object and declines nothing.
        private static readonly Regex AssignmentTarget = new Regex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(?!=)", RegexOptions.Compiled);

        /// <summary>The Model quality rule's ask, read off its FixExpression when it has one, else from the table above;
        /// null when the rule is in neither (a rule someone loaded, whose ask is not known), which declines nothing.</summary>
        public static RuleAbout ForBpa(BpaRule rule)
        {
            if (rule == null) return null;
            var fix = rule.FixExpression;
            if (!string.IsNullOrWhiteSpace(fix))
            {
                var m = AssignmentTarget.Match(fix);
                if (m.Success)
                {
                    var prop = m.Groups[1].Value;
                    var kinds = KindsOfScope(rule.Scope);
                    var own = OwnBpaWriters.Contains(rule.ID ?? "") || string.Equals(prop, "Expression", StringComparison.OrdinalIgnoreCase);
                    var literal = own && BpaAnalyzer.TryFixLiteral(rule, out var l) ? l : null;
                    return own ? RuleAbout.Own(kinds, Camel(prop), literal == null ? null : _ => literal) : RuleAbout.Keep(kinds, Camel(prop));
                }
                if (fix.TrimStart().StartsWith("Delete", StringComparison.OrdinalIgnoreCase)) return Structural;
            }
            return ModelQuality.TryGetValue(rule.ID ?? "", out var about) ? about : null;
        }

        /// <summary>Model quality rules whose ask is one value among siblings that write different values on the same
        /// object: the date format and the month format both fire on a date column named "Month Start Date". Accepting one
        /// declines that value, not every format for the column.</summary>
        internal static readonly ISet<string> OwnBpaWriters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "DATECOLUMN_FORMATSTRING", "MONTHCOLUMN_FORMATSTRING" };

        /// <summary>The readiness rule's ask, or null for a rule that is not in the table (a custom rule declares no write).</summary>
        public static RuleAbout ForReadiness(string ruleId) =>
            ruleId != null && Readiness.TryGetValue(ruleId, out var about) ? about : null;

        private static string Camel(string prop) => prop.Length == 0 ? prop : char.ToLowerInvariant(prop[0]) + prop.Substring(1);

        /// <summary>The ref prefixes the scopes of a BPA rule sit on.</summary>
        internal static string KindsOfScope(string scope)
        {
            var kinds = new List<string>();
            foreach (var s in (scope ?? "").Split(',').Select(x => x.Trim()))
            {
                var k = s switch
                {
                    "DataColumn" or "CalculatedColumn" or "CalculatedTableColumn" or "Column" => "column",
                    "Measure" => "measure",
                    "Table" or "CalculatedTable" or "CalculationGroup" => "table",
                    "Hierarchy" => "hierarchy",
                    "Level" => "level",
                    "Relationship" => "relationship",
                    "Partition" => "partition",
                    "Perspective" => "perspective",
                    "ModelRole" => "role",
                    "CalculationItem" => "calcitem",
                    "Model" => "obj",
                    _ => null,
                };
                if (k != null && !kinds.Contains(k)) kinds.Add(k);
            }
            return string.Join(",", kinds);
        }
    }
}

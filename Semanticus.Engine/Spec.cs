using System;

namespace Semanticus.Engine
{
    // ============================================================================================
    // The model SPEC — a structured, dual-drive description of a model to BUILD (or refine): storage
    // mode + Fabric source, tables (columns + types + star-schema role), relationships, core measures,
    // time-intelligence. Claude and the human iterate it (the Spec tab + MCP), it is auto-generated as
    // far as possible first, then build_model_from_spec materialises it via the Phase-2 primitives.
    // The live spec is a session-held artifact (SpecStore) that broadcasts on the ChangeBus
    // (spec/didChange) so the Spec tab and the user's Claude watch it assemble live. Mirrors Plan.cs.
    // ============================================================================================

    public sealed class SpecColumn
    {
        public string Name { get; set; }
        public string DataType { get; set; }        // String | Int64 | Decimal | Double | DateTime | Boolean
        public string SourceColumn { get; set; }    // underlying physical column (defaults to Name)
        public bool IsKey { get; set; }
        public bool Hidden { get; set; }
        public string SummarizeBy { get; set; }     // None | Sum | Count | ... (optional)
        /// <summary>The column's lineage tag, when the spec fixes it (a start plan allocates identities once, from a seed).
        /// Null leaves the tag the model gives a new column.</summary>
        public string LineageTag { get; set; }
    }

    public sealed class SpecTable
    {
        public string Name { get; set; }
        public string Role { get; set; }            // fact | dimension | date | calculated | isolated
        public string Entity { get; set; }          // Direct Lake: the lakehouse/warehouse entity (physical table)
        public string Schema { get; set; }          // Direct Lake / source schema
        public string MExpression { get; set; }     // Import: the M expression text
        public string CalculatedExpression { get; set; } // calculated table: the DAX table expression
        public string SourceName { get; set; }      // the data source / shared expression to bind (Direct Lake)
        public SpecColumn[] Columns { get; set; } = Array.Empty<SpecColumn>();
        /// <summary>The table's lineage tag, when the spec fixes it. Null leaves the tag the model gives a new table.</summary>
        public string LineageTag { get; set; }
    }

    public sealed class SpecRelationship
    {
        public string FromTable { get; set; }       // From end (FK side under the default many-to-one)
        public string FromColumn { get; set; }
        public string ToTable { get; set; }         // To end (lookup side under the default many-to-one)
        public string ToColumn { get; set; }
        // Reads From->To, maps to TOM RelationshipEndCardinality: manyToOne (DEFAULT, = today's FK->PK behavior)
        // | oneToOne | oneToMany | manyToMany. Null/empty = manyToOne, so specs written before this field are unchanged.
        public string Cardinality { get; set; }
        public string CrossFilter { get; set; }     // OneDirection | BothDirections (optional)
        public bool? IsActive { get; set; }
        /// <summary>Plain words for why the draft chose something the person may not expect, today only why a link
        /// is drafted switched off. Null when there is nothing to explain. Read by people; the build ignores it.</summary>
        public string Note { get; set; }
        /// <summary>The relationship's name, when the spec fixes it. Null leaves the new GUID name the model gives it, so two
        /// builds of one spec differ in their link names; a start plan sets it so they do not.</summary>
        public string Name { get; set; }
    }

    public sealed class SpecMeasure
    {
        public string Table { get; set; }
        public string Name { get; set; }
        public string Dax { get; set; }
        public string FormatString { get; set; }
        public string DisplayFolder { get; set; }
        public string Description { get; set; }
        /// <summary>The measure's lineage tag, when the spec fixes it. The time-intelligence measures made from it take tags
        /// worked out from this one and the variant, so they are fixed too.</summary>
        public string LineageTag { get; set; }
        // Input alias: agents often send "expression" for the formula. Never written back.
        [System.Text.Json.Serialization.JsonPropertyName("expression")]
        public string Expression
        {
            get => null;
            set { if (string.IsNullOrWhiteSpace(Dax) && !string.IsNullOrWhiteSpace(value)) Dax = value; }
        }
    }

    public sealed class SpecSource
    {
        public string Kind { get; set; }            // fabric-sql  (future: onelake)
        public string Server { get; set; }          // Fabric SQL endpoint, e.g. xxx.datawarehouse.fabric.microsoft.com
        public string Database { get; set; }
        public string Schema { get; set; }
        /// <summary>The lineage tag of the shared source expression the build adds, when the spec fixes it.</summary>
        public string LineageTag { get; set; }
    }

    public sealed class SpecDateTable
    {
        public string Name { get; set; } = "Date";
        /// <summary>The date column of a table the spec already has. When <see cref="Name"/> is one of the spec's own
        /// tables, the build marks that table as the date table (keyed on this column, or on its one date column
        /// when this is empty) instead of adding a CALENDAR table beside it.</summary>
        public string DateColumn { get; set; }
        public string StartExpr { get; set; }
        public string EndExpr { get; set; }
        /// <summary>The month and day the fiscal year ends, written month/day (6/30). The year-to-date measure is written
        /// with it. Null means a calendar year, which is DAX's own default, so nothing is added.</summary>
        public string FiscalYearEnd { get; set; }
        public bool MarkAsDate { get; set; } = true;
        /// <summary>A plain sentence for people about why the draft chose this date table, or none. The build ignores it
        /// and repeats it in its report, whether or not <see cref="Name"/> is blank (a source with no date table, or none any fact links to, leaves it blank and no calendar is added; or a marked table with some facts left out of time intelligence).</summary>
        public string Note { get; set; }
    }

    /// <summary>The whole spec document (serialised to/from JSON, edited by both doors).</summary>
    public sealed class ModelSpec
    {
        public string Name { get; set; }
        public int CompatibilityLevel { get; set; } = 1604;
        public string StorageMode { get; set; }     // import | directLake
        public SpecSource Source { get; set; }
        public SpecTable[] Tables { get; set; } = Array.Empty<SpecTable>();
        public SpecRelationship[] Relationships { get; set; } = Array.Empty<SpecRelationship>();
        public SpecMeasure[] Measures { get; set; } = Array.Empty<SpecMeasure>();
        public string[] TimeIntelligence { get; set; } = Array.Empty<string>(); // YTD,QTD,MTD,PY,YoY,YoYPct
        public string[] TimeIntelligenceBaseMeasures { get; set; } = Array.Empty<string>(); // base measure names for TI (empty = all)
        /// <summary>The time measures, by name, the build does NOT write although the variants above and their base measures would give them (the start plan's dropped ones).
        /// Null when none is left out, so a spec that never had any is unchanged.</summary>
        public string[] TimeIntelligenceLeftOut { get; set; }
        public SpecDateTable DateTable { get; set; }
        /// <summary>Plain words for what the draft could not build as the source declared it, today a foreign key of several
        /// columns (Power BI links on one). The build repeats each in its notes. Null when there is none, so a spec that
        /// never had any is unchanged.</summary>
        public string[] Unresolved { get; set; }
    }

    /// <summary>Wire snapshot of the current spec (broadcast on spec/didChange + returned by the spec tools).</summary>
    public sealed class SpecView
    {
        public long Version { get; set; }           // bumped on every change
        public string Source { get; set; }          // manual | autogenerate-model | autogenerate-fabric | file
        public ModelSpec Spec { get; set; }         // null when no spec is loaded
    }

    /// <summary>Result of build_model_from_spec: what was created/skipped + before→after counts.</summary>
    public sealed class SpecBuildReport
    {
        public long Revision { get; set; }
        public string[] Created { get; set; } = Array.Empty<string>();   // refs created
        public string[] Skipped { get; set; } = Array.Empty<string>();   // already existed (re-run safe)
        public string[] Errors { get; set; } = Array.Empty<string>();    // per-item failures (build is otherwise tolerant)
        public string[] Notes { get; set; } = Array.Empty<string>();     // plain words for choices the build made on purpose
        public int TablesBefore { get; set; }
        public int TablesAfter { get; set; }
        public int MeasuresBefore { get; set; }
        public int MeasuresAfter { get; set; }
        public string Note { get; set; }
    }

    /// <summary>Holds the one current spec for the session + a version counter. NOT internally locked: access is
    /// serialised by the engine's <c>_specGate</c> (the single owner of spec-state concurrency across both doors),
    /// so an inner lock would be dead weight. Mirrors <see cref="PlanStore"/>.</summary>
    public sealed class SpecStore
    {
        private ModelSpec _current;
        private string _source = "manual";
        private long _version;

        public long Version => _version;
        public string Source => _source;
        public ModelSpec Current => _current;

        public void Set(ModelSpec spec, string source) { _current = spec; _source = source ?? "manual"; _version++; }
        public void Clear() { _current = null; _source = "manual"; _version++; }
        public ModelSpec Require() => _current
            ?? throw new InvalidOperationException("No spec is loaded. Autogenerate one (autogenerate_spec_from_model / autogenerate_spec_from_fabric), set_spec, or load_spec first.");
        public SpecView View() => new SpecView { Version = _version, Source = _source, Spec = _current };
    }
}

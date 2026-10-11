// The model facts snapshot (M4 groundwork contract, section 1): gather once per model change, decide many.
//
// One snapshot carries everything the Jev families read about a model: its settings, its structure, and the
// DERIVED facts measured from its data. The shape is the frozen contract's, field for field. Two rules shape
// every type in this file:
//
//   Absent is not zero. A model opened from files has no data, so `Data` is "none" and every `Measured` block is
//   null. A fact that could not be measured stays null rather than reading as a measured 0 or false.
//
//   Derived only. Nothing here holds a row, a raw value, a minimum, a maximum or a sample. Values are read inside
//   the engine to compute shares and flags and are dropped there; ModelFactsTests scans the serialized snapshot
//   for the fixture's literal values to hold this.

using System;
using System.Collections.Generic;

namespace Semanticus.Engine.Facts
{
    public static class ModelFactsData
    {
        public const string Live = "live";
        public const string Local = "local";
        public const string None = "none";
        /// <summary>No connection to any copy of the model, but a sample of the SOURCE it was built from, read when the model
        /// was started and saved beside it (.semanticus/source-facts.json). Derived facts only, like every other kind.</summary>
        public const string SourceSample = "sourceSample";
    }

    public sealed class ModelFacts
    {
        /// <summary>The model's durable identity (the same anchor the Primer and the evidence library key on), or
        /// null for an unsaved model with no live origin.</summary>
        public string ModelIdentity { get; set; }
        public long Revision { get; set; }
        public DateTime MeasuredAt { get; set; }
        /// <summary>live | local | sourceSample | none. None: no data this snapshot may speak for, so column facts are absent.
        /// SourceSample: no connection, but facts from a sample of the source the model was built from, kept beside it.</summary>
        public string Data { get; set; } = ModelFactsData.None;
        public TableFacts[] Tables { get; set; } = Array.Empty<TableFacts>();
        /// <summary>Moved from the Storage tab (F-230): unused, dictionary-heavy, hash-heavy, summarize.</summary>
        public StorageOpportunity[] StorageOpportunities { get; set; } = Array.Empty<StorageOpportunity>();
        public Dictionary<string, FactProducer> Producers { get; set; } = new Dictionary<string, FactProducer>(StringComparer.Ordinal);
    }

    public sealed class TableFacts
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        /// <summary>Counted with a row count query when the table was measured; null otherwise.</summary>
        public long? Rows { get; set; }
        /// <summary>Storage bytes from the storage scan, only when the scanned model is the model being edited.</summary>
        public long? Bytes { get; set; }
        /// <summary>import | directQuery | dual | directLake | calculated | unknown.</summary>
        public string StorageMode { get; set; }
        public bool IsDateTableMarked { get; set; }
        public ColumnFacts[] Columns { get; set; } = Array.Empty<ColumnFacts>();
    }

    public sealed class ColumnFacts
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool IsHidden { get; set; }
        public string SummarizeBy { get; set; }
        public string DataCategory { get; set; }
        /// <summary>The sort-by column's ref, or null.</summary>
        public string SortByColumn { get; set; }
        public string FormatString { get; set; }
        public bool IsAvailableInMdx { get; set; }
        public bool IsKey { get; set; }
        /// <summary>none | one | many. A column on the one side of any relationship reads "one".</summary>
        public string RelationshipRole { get; set; } = "none";
        /// <summary>Measures that depend on this column, directly or through other objects.</summary>
        public int UsedByMeasures { get; set; }
        /// <summary>Null when no reports were checked (or a chosen report is still unread).</summary>
        public bool? UsedInReports { get; set; }
        public MeasuredFacts Measured { get; set; }
    }

    /// <summary>Derived facts only. Present only when the snapshot's Data is not "none" and the table was measured.</summary>
    public sealed class MeasuredFacts
    {
        /// <summary>Distinct values in the measured rows, counting blank as one value (DISTINCTCOUNT semantics).</summary>
        public long Distinct { get; set; }
        public double BlankShare { get; set; }
        public bool WholeNumbers { get; set; }
        public bool UniqueOnEveryRow { get; set; }
        public bool LooksLikeYear { get; set; }
        public bool LooksLikeMonthName { get; set; }
        public bool LooksLikeMonthNumber { get; set; }
        public bool LooksLikeDayName { get; set; }
        public bool LooksLikeQuarter { get; set; }
        /// <summary>Whole numbers from 1 to 53, 2 to 53 of them: a day, week, month or period number.</summary>
        public bool LooksLikePeriodNumber { get; set; }
        public PlaceShare PlaceShare { get; set; } = new PlaceShare();
        public double UrlShare { get; set; }
        public double ImageUrlShare { get; set; }
        public double EmailShare { get; set; }
        /// <summary>Same-table columns where distinct(pair) == distinct(each).</summary>
        public string[] OneToOneWith { get; set; } = Array.Empty<string>();
        /// <summary>Null unless the storage scan measured the model being edited.</summary>
        public long? DictionaryBytes { get; set; }
        public long? DataBytes { get; set; }
        public long? Cardinality { get; set; }
        /// <summary>True when the table was measured on a bounded sample of its rows.</summary>
        public bool Sampled { get; set; }

        /// <summary>Every measured value has an exact double, which is what proves a whole number column can
        /// become a decimal number without changing a value. INTERNAL on purpose: it is read by the plan
        /// executor's data type check and is not part of the contract's snapshot, so it is never serialized
        /// and never reaches the page or the assistant.</summary>
        internal bool ExactInDouble { get; set; }

        /// <summary>Every measured value fits a fixed decimal (four places, +/- 922,337,203,685,477.5807), which
        /// is what proves a whole number column can become a fixed decimal. Internal, like ExactInDouble.</summary>
        internal bool FitsFixedDecimal { get; set; }

        /// <summary>For a date column only (null otherwise): every value is a whole day and no day is missing between
        /// the first and the last. With <see cref="UniqueOnEveryRow"/> it is what makes a column a date table's key.
        /// Internal like ExactInDouble: read by the date table family (Plan/JevFamilies/DateTableFamily.cs) and not
        /// part of the contract's snapshot, so it is never serialized. Meaningless on a sample, which the family
        /// checks through <see cref="Sampled"/>.</summary>
        internal bool? ContiguousDays { get; set; }
    }

    /// <summary>Shares of distinct non-blank text values that match the bundled gazetteer and postcode patterns.
    /// City is always null: no small, licence-clean city list is bundled (see Facts/Data/README.md).</summary>
    public sealed class PlaceShare
    {
        public double Country { get; set; }
        public double Region { get; set; }
        public double? City { get; set; }
        public double Postcode { get; set; }
    }

    /// <summary>Which producer made a fact, how much of the model it covers, and how fresh it is.</summary>
    public sealed class FactProducer
    {
        public string Producer { get; set; }
        public string Coverage { get; set; }
        public string Freshness { get; set; }
    }

    /// <summary>One storage finding, the shape the Storage tab renders (moved from its computeOpportunities).</summary>
    public sealed class StorageOpportunity
    {
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        /// <summary>Can remove | Worth reviewing | Behavior cleanup.</summary>
        public string Group { get; set; }
        public int Severity { get; set; }
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Table { get; set; }
        public string Column { get; set; }
        public string Message { get; set; }
        /// <summary>unused | dict | hash | summarize.</summary>
        public string Kind { get; set; }
        /// <summary>Storage weight. Ranks within a group, never stands in for severity.</summary>
        public long Bytes { get; set; }
        public string Encoding { get; set; }
        public long? Data { get; set; }
        public long? Dict { get; set; }
        public long? Hash { get; set; }
        public long? Total { get; set; }
        public double? PctModel { get; set; }
        public int? PctTable { get; set; }
        public string Effect { get; set; }
    }
}

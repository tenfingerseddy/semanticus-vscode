// REVIEW EQUALS BUILD, for every property (Sol, join 7, UAT-J7-S2-008). Join 6 made the review list every table, measure and link the build writes, and the
// build refuse when it wrote an object the review did not show. The build still wrote a "Base Measures" folder on every measure and the review never said so:
// an object was reviewed, a property of it was not. This file is the same rule one level down.
//
// HOW IT WORKS. While the manifest's structure is built, a journal listens to the model handler's ObjectChanged event, the one place every property the build
// sets after an object is made is announced (the Session's own change collector listens to the same event). When the structure is built, each property that
// ended up different from where it started must be one of:
//   1. SHOWN: the review says it. A line carries it in Sets (a measure's folder and number format, a link that is off, the date table's category) or a column in
//      the table's column list says it (hidden, key, a summarize setting that is not Power BI's own).
//   2. A KNOWN DEFAULT: something the build sets that Power BI would set the same way itself, or that holds no content. Each has a written reason below, and a
//      test holds the list to "every entry has a reason".
// A property that is neither stops the build, names the object and the property, and writes nothing (the batch's own rollback, like an unreviewed object).
//
// WHAT IS NOT CHECKED HERE, said plainly: the lines. A kept line is shown on the review with its object, its title and its value, and it writes what it says;
// a rename of a column also rewrites the DAX of the measures that use it, which no line names, so a property-by-property compare of the lines would refuse good builds.
// The one thing a line writes that no line names is a synonym Power BI derives from an object's own name, checked by UnreviewedSynonym after the lines run.

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine.StartPlan
{
    /// <summary>Listens to every property change made to a model between its creation and its disposal, so the build can ask afterwards what it wrote.</summary>
    internal sealed class StartWriteJournal : IDisposable
    {
        private readonly IDisposable _listening;
        private readonly List<(ITabularObject Obj, string Property, object Old, object New)> _entries = new List<(ITabularObject, string, object, object)>();

        public StartWriteJournal(Session s)
        {
            _listening = s.Listen(OnChanged);
        }

        private void OnChanged(object sender, ObjectChangedEventArgs e)
        {
            if (e?.TabularObject != null && !string.IsNullOrEmpty(e.PropertyName)) _entries.Add((e.TabularObject, e.PropertyName, e.OldValue, e.NewValue));
        }

        public void Dispose() { _listening.Dispose(); }

        /// <summary>One entry per (object, property): the value it started from and the value it ended on. A property set and set back is not a write.</summary>
        public IEnumerable<(ITabularObject Obj, string Property, string Old, string New)> Net()
        {
            var order = new List<(ITabularObject, string)>();
            var first = new Dictionary<(ITabularObject, string), object>();
            var last = new Dictionary<(ITabularObject, string), object>();
            foreach (var (obj, prop, old, now) in _entries)
            {
                var key = (obj, prop);
                if (!first.ContainsKey(key)) { first[key] = old; order.Add(key); }
                last[key] = now;
            }
            foreach (var key in order)
            {
                var (a, b) = (Text(first[key]), Text(last[key]));
                if (!string.Equals(a, b, StringComparison.Ordinal)) yield return (key.Item1, key.Item2, a, b);
            }
        }

        private static string Text(object v) => v == null ? "" : v is bool b ? (b ? "True" : "False") : v.ToString();
    }

    /// <summary>A thing the build sets that the review need not show, and why. The check knows these by name; a test holds each one to a reason.</summary>
    internal sealed class StartKnownDefault
    {
        public string Id;
        public string Reason;
        public Func<ITabularObject, string, string, StartPlanReview.Context, bool> Applies;
    }

    internal static class StartPlanReview
    {
        /// <summary>What the review says, in the form the check reads: each reviewed property by object and name, and the date table the review marks.</summary>
        internal sealed class Context
        {
            public readonly Dictionary<string, string> Shown = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string DateTable;
            public string DateColumn;
            /// <summary>The tables the review brings in, by name, and the links it lists, by their two ends (StartPlanViewBuilder.LinkKey).</summary>
            public readonly HashSet<string> Tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public string Server, Database;
        }

        internal static string Key(string objRef, string property) => objRef + "|" + property;

        /// <summary>Everything the review shows being set, read from the same view the person reads (never a second list).</summary>
        internal static Context ContextOf(StartPlanDoc doc)
        {
            var c = new Context();
            var view = StartPlanViewBuilder.Build(doc);
            // A link that is off is NOT kept (the line says so) but the build still writes it, switched off: its Sets count.
            foreach (var l in view.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Lines).Where(l => (l.Kept || l.Item?.Kind == "create_relationship") && l.Sets != null))
                foreach (var s in l.Sets) c.Shown[Key(s.Ref, s.Property)] = s.Value ?? "";
            foreach (var col in view.Sections.SelectMany(s => s.Groups).Where(g => g.Columns != null).SelectMany(g => g.Columns).Where(x => x.Included))
            {
                c.Shown[Key(col.Ref, "DataType")] = col.DataType ?? "";
                c.Shown[Key(col.Ref, "SourceColumn")] = col.SourceName ?? col.Name;
                if (col.Hidden) c.Shown[Key(col.Ref, "IsHidden")] = "True";
                if (col.Key) c.Shown[Key(col.Ref, "IsKey")] = "True";
                if (col.Summarize != null) c.Shown[Key(col.Ref, "SummarizeBy")] = col.Summarize;
            }
            var manifest = doc?.Manifest ?? new StructureManifest();
            if (!string.IsNullOrWhiteSpace(manifest.DateTable?.Name)) { c.DateTable = manifest.DateTable.Name; c.DateColumn = manifest.DateTable.DateColumn; }
            var (tables, _, links) = StartPlanViewBuilder.Reviewed(doc);
            foreach (var t in tables) c.Tables.Add(t.StartsWith("table:", StringComparison.OrdinalIgnoreCase) ? t.Substring(6) : t);
            foreach (var l in links) c.Links.Add(l);
            c.Server = manifest.Server; c.Database = manifest.Database;
            return c;
        }

        private static bool IsNumeric(Column c) => c.DataType is DataType.Int64 or DataType.Decimal or DataType.Double;

        /// <summary>The summarize setting Power BI Desktop gives an imported column of this type by itself: Sum for a number, None for anything else.</summary>
        internal static string PowerBiSummarize(Column c) => IsNumeric(c) ? "Sum" : "None";

        /// <summary>The things the build sets that the review does not show, each with the reason. (Sol's triage of UAT-J7-S1-004, 3 Oct: "Power BI's own import defaults".)</summary>
        internal static readonly StartKnownDefault[] KnownDefaults =
        {
            new StartKnownDefault
            {
                Id = "lineage-tag",
                Reason = "An id Power BI keeps for an object so a rebuild of the same plan gives the same model. It holds no content, and the object's name is on the review.",
                Applies = (o, p, v, c) => p is "LineageTag" or "SourceLineageTag",
            },
            new StartKnownDefault
            {
                Id = "summarize-power-bi-default",
                Reason = "Power BI Desktop's own import default: it adds up a number column (Sum) and leaves any other column alone (None). A summarize setting that is NOT this default is shown on the column.",
                Applies = (o, p, v, c) => p == "SummarizeBy" && o is Column col && string.Equals(v, PowerBiSummarize(col), StringComparison.OrdinalIgnoreCase),
            },
            new StartKnownDefault
            {
                Id = "date-table-key",
                Reason = "Marking a table as the date table makes its date column the table's key. The review shows the 'mark as date table' line, and the key follows from it.",
                Applies = (o, p, v, c) => p == "IsKey" && v == "True" && o is Column col && c.DateTable != null
                                         && string.Equals(col.Table?.Name, c.DateTable, StringComparison.OrdinalIgnoreCase)
                                         && string.Equals(col.Name, c.DateColumn, StringComparison.OrdinalIgnoreCase),
            },
            new StartKnownDefault
            {
                Id = "one-key-per-table",
                Reason = "A table holds one key column in the model. When the build marks a second column of a table as part of its declared key, the column that held the key lets go. The keys are on the column list.",
                Applies = (o, p, v, c) => p == "IsKey" && v == "False" && o is Column col && col.Table != null && col.Table.Columns.Any(x => !ReferenceEquals(x, col) && x.IsKey),
            },
            new StartKnownDefault
            {
                Id = "source-connection",
                Reason = "The connection to your own source, named at the top of the plan (its server and database). The build writes it once so every table can read from it.",
                Applies = (o, p, v, c) =>
                    (o.ObjectType == ObjectType.Expression && p == "Expression" && Has(v, c.Server) && Has(v, c.Database))
                    || (o.ObjectType == ObjectType.DataSource && ((p == "Server" && Same(v, c.Server)) || (p == "Database" && Same(v, c.Database)) || (p == "Protocol" && Same(v, "tds")))),
            },
            new StartKnownDefault
            {
                Id = "table-binding",
                Reason = "How a table reads its rows from the source table the plan names: its partition. The table's own line on the review says it is brought in, and the source is at the top.",
                Applies = (o, p, v, c) => o is Partition part && part.Table != null && c.Tables.Contains(part.Table.Name)
                                         && p is "Expression" or "Mode" or "Name" or "EntityName" or "SchemaName" or "ExpressionSource" or "SourceType",
            },
            new StartKnownDefault
            {
                Id = "source-key-mark",
                Reason = "Semanticus's own note that the source declared a column a key (the Key flag on the column's line, or the date column the plan marks). It is bookkeeping and holds no content.",
                Applies = (o, p, v, c) => p == "Annotations" && o is Column col && (v ?? "").Contains(SourceKeys.Annotation, StringComparison.Ordinal)
                                         && (c.Shown.ContainsKey(Key(ObjectRefs.For(col), "IsKey"))
                                             || (c.DateTable != null && string.Equals(col.Table?.Name, c.DateTable, StringComparison.OrdinalIgnoreCase) && string.Equals(col.Name, c.DateColumn, StringComparison.OrdinalIgnoreCase))),
            },
            new StartKnownDefault
            {
                Id = "link-ends",
                Reason = "The two columns a link joins. They are the link line itself: the review names the tables and the column, and the link is found by those ends.",
                Applies = (o, p, v, c) => o is SingleColumnRelationship r && p is "FromColumn" or "ToColumn"
                                         && c.Links.Contains(StartPlanViewBuilder.LinkKey(r.FromTable?.Name, r.FromColumn?.Name, r.ToTable?.Name, r.ToColumn?.Name)),
            },
            new StartKnownDefault
            {
                Id = "synonym-own-name",
                Reason = "Power BI's Q&A lists an object's own name (and the name in lower case) as a term for it when a synonym is first written. It adds no meaning the name does not already have. Checked by UnreviewedSynonym.",
                Applies = (o, p, v, c) => false,   // a synonym is not a property of the journal; IsOwnNameSynonym below is the rule
            },
        };

        private static bool Same(string a, string b) => !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool Has(string text, string part) => !string.IsNullOrEmpty(part) && (text ?? "").IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>True when a synonym term is only the object's own name (ignoring case, spaces, underscores and hyphens).</summary>
        internal static bool IsOwnNameSynonym(string objectName, string term) => Squash(objectName) == Squash(term) && Squash(term).Length > 0;

        private static string Squash(string s, bool keepSymbols = false) =>
            new string((s ?? "").Where(c => keepSymbols ? !char.IsWhiteSpace(c) : char.IsLetterOrDigit(c)).Select(c => keepSymbols ? c : char.ToLowerInvariant(c)).ToArray());

        /// <summary>The ref the review uses for an object. A link is named by its two ends (its own name is a fresh id when the plan has no seed).</summary>
        internal static string RefOf(ITabularObject obj)
        {
            if (obj is SingleColumnRelationship r)
                return "link:" + StartPlanViewBuilder.LinkKey(r.FromTable?.Name, r.FromColumn?.Name, r.ToTable?.Name, r.ToColumn?.Name);
            return ObjectRefs.For(obj);
        }

        private static string ValueOf(ITabularObject obj, string property)
        {
            try
            {
                var v = obj.GetType().GetProperty(property)?.GetValue(obj);
                return v == null ? "" : v is bool b ? (b ? "True" : "False") : v.ToString();
            }
            catch { return null; }
        }

        /// <summary>The first property the build set that the review did not show, in plain words, or null. Runs inside the build's delegate right after the
        /// structure is built, so a build that wrote an unseen property takes itself back and writes nothing.</summary>
        internal static string UnreviewedWrite(StartWriteJournal journal, StartPlanDoc doc)
        {
            var ctx = ContextOf(doc);
            foreach (var (obj, property, old, now) in journal.Net())
            {
                // An annotation list or a linked object has no text of its own: the text the journal heard is the one to read.
                var value = property is "Annotations" or "FromColumn" or "ToColumn" ? now : ValueOf(obj, property) ?? now;
                var objRef = RefOf(obj);
                if (ctx.Shown.TryGetValue(Key(objRef, property), out var shown)
                    && (property == "Expression" ? Squash(shown, keepSymbols: true) == Squash(value, keepSymbols: true) : string.Equals(shown, value, StringComparison.Ordinal))) continue;
                if (KnownDefaults.Any(k => k.Applies(obj, property, value, ctx))) continue;
                return "the " + Words(property) + " of " + NameOf(obj) + " (" + (string.IsNullOrEmpty(value) ? "empty" : value) + ")";
            }
            return null;
        }

        private static string NameOf(ITabularObject obj) => obj switch
        {
            SingleColumnRelationship r => "the link from " + r.FromTable?.Name + " to " + r.ToTable?.Name,
            Measure m => m.Name,
            Column c => c.Table?.Name + "[" + c.Name + "]",
            ITabularNamedObject n => n.Name,
            _ => "the model",
        };

        /// <summary>A property name in words: DisplayFolder is "display folder".</summary>
        internal static string Words(string property)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ch in property ?? "")
            {
                if (char.IsUpper(ch) && sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(ch));
            }
            return property switch { "IsHidden" => "hidden setting", "IsKey" => "key setting", "FormatString" => "number format", "IsActive" => "on or off setting", _ => sb.ToString() };
        }

        /// <summary>The first synonym in the built model that no kept line wrote and that is not just the object's own name, or null. Runs after the lines are made.</summary>
        internal static string UnreviewedSynonym(Model m, StartPlanDoc doc)
        {
            var written = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in doc.Lines.Where(l => l.Kept && l.Item != null && string.Equals(l.Item.Kind, "set_synonyms", StringComparison.OrdinalIgnoreCase)))
            {
                var forward = StartPlanWords.Forward(doc, l.Item.ObjectRef ?? "");
                if (!written.TryGetValue(forward, out var set)) written[forward] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in StartPlanWords.Terms(l.Item.After)) set.Add(Squash(t));
            }
            var cultures = m.Cultures.ToList();
            if (cultures.Count == 0) return null;
            IEnumerable<TabularNamedObject> objects = m.Tables.Cast<TabularNamedObject>()
                .Concat(m.Tables.SelectMany(t => t.Columns).Cast<TabularNamedObject>())
                .Concat(m.AllMeasures.Cast<TabularNamedObject>());
            foreach (var obj in objects)
                foreach (var culture in cultures)
                {
                    string terms;
                    try { terms = TabularEditor.TOMWrapper.Linguistics.SynonymHelper.GetSynonyms(obj, culture); } catch { continue; }
                    foreach (var term in StartPlanWords.Terms(terms))
                    {
                        if (IsOwnNameSynonym(obj.Name, term)) continue;
                        if (written.TryGetValue(ObjectRefs.For(obj), out var ok) && ok.Contains(Squash(term))) continue;
                        return "the synonym \"" + term + "\" for " + obj.Name;
                    }
                }
            return null;
        }
    }
}

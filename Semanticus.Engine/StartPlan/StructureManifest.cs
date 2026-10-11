// The structure manifest of a spec-first start (M11, lane sf-manifest): everything the structure of a new model
// is decided from, as one plain document, and the ONE pure function that turns it into the spec the build reads.
//
//   Derive(schema, overrides, decisions, seed)  ->  StructureManifest  ->  ToSpec()  ->  ModelSpec
//
// Nothing here reads a model, a connection or the clock, so the same input always gives the same manifest and the
// same spec. The default manifest (no overrides, no decisions, no seed) gives exactly the spec the two-step path
// has always given (LocalEngine.FabricSchemaToSpec now calls this), so a new start and the old draft cannot drift.
//
// WHAT IS HERE THAT THE OLD DRAFT DID NOT HAVE
//  - Base measures are a PICK (sum, average, count...) held apart from the column's summarize-by setting, because
//    the summarize family expresses its pick as a separate measure and sets the column to None (design S4). A pick of
//    none removes the measure; time intelligence is derived from the bases whose pick is sum, and only those.
//  - A key of two or more columns builds no link. It is one unresolved line that names the single combined key it
//    would need (design S9). It still counts for table roles and for "this column is a key, not a number to add".
//  - The fiscal year end flows into the year-to-date measure.
//  - Identities (relationship names, lineage tags) are allocated once from a seed, as a pure function of the seed
//    and the SOURCE name, so a rename never moves one and a rebuild always repeats them (design S3). With no seed
//    the spec carries none, and the model gives its own, which is today's behaviour.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Semanticus.Engine;

namespace Semanticus.Engine.StartPlan
{
    /// <summary>A table in the manifest. Excluded tables stay listed (so they can be put back) but build nothing.</summary>
    public sealed class ManifestTable
    {
        public bool Include { get; set; } = true;
        public string Name { get; set; }             // the name in the model
        public string SourceSchema { get; set; }
        public string SourceName { get; set; }       // the physical table
        /// <summary>fact | dimension | isolated, from the declared keys. The date table is marked by <see cref="ManifestDateTable"/>.</summary>
        public string Role { get; set; }
        public string Identity { get; set; }         // its lineage tag; null when no seed was given
        public List<ManifestColumn> Columns { get; set; } = new List<ManifestColumn>();
    }

    public sealed class ManifestColumn
    {
        public bool Include { get; set; } = true;
        public string Name { get; set; }
        public string SourceColumn { get; set; }
        public string DataType { get; set; }
        public bool IsKey { get; set; }
        public bool Hidden { get; set; }
        /// <summary>The column's own summarize-by setting. Apart from the base measure's pick on purpose (design S4).</summary>
        public string SummarizeBy { get; set; }
        public string Identity { get; set; }
    }

    /// <summary>One declared foreign key. A key of one column pair builds a link; a key of several is unresolved.</summary>
    public sealed class ManifestLink
    {
        public string Identity { get; set; }         // the relationship's name; null when no seed was given
        public string ConstraintName { get; set; }
        public string FromTable { get; set; }
        public string[] FromColumns { get; set; } = Array.Empty<string>();
        public string ToTable { get; set; }
        public string[] ToColumns { get; set; } = Array.Empty<string>();
        public bool Active { get; set; } = true;
        /// <summary>Plain words for why the link is off, or (when <see cref="Unresolved"/>) why it is not built and what it would need.</summary>
        public string Note { get; set; }
        /// <summary>True when the link cannot be built as declared (a composite key, a column left out): it is NOT built and
        /// becomes one Needs-you line, with <see cref="Note"/> as its words.</summary>
        public bool Unresolved { get; set; }
        /// <summary>True when no foreign key was declared for this pair of tables and the plan guessed the link from the names (LinkGuesser). A declared key always wins, so a
        /// guessed link never sits beside one for the same two tables.</summary>
        public bool Guessed { get; set; }
        /// <summary>How the names matched: same_name | table_then_key | ends_with. Only on a guessed link.</summary>
        public string GuessKind { get; set; }
        /// <summary>What says the other end is a key: declared (a primary key) | sample (unique on every sampled row) | name (named like the table's own key, and no rows checked).</summary>
        public string GuessEvidence { get; set; }
        /// <summary>The other tables the same column also matched. Those links are off; this one was kept on.</summary>
        public string[] GuessAlso { get; set; } = Array.Empty<string>();
        /// <summary>A guessed link the person (or their assistant) dropped. It is not built, it is not counted in the table roles, and keeping it brings it back.</summary>
        public bool Dropped { get; set; }
        public string DroppedBy { get; set; }
    }

    public sealed class ManifestDateTable
    {
        /// <summary>The table marked as the date table. Empty when the source has none, or none any fact links to.</summary>
        public string Name { get; set; } = "";
        public string DateColumn { get; set; }
        /// <summary>The month and day the fiscal year ends, written month/day (6/30), or null for a calendar year.</summary>
        public string FiscalYearEnd { get; set; }
        /// <summary>Why there is no date table, when the date finder had something to say. Read by ToSpec.</summary>
        public string AutoNote { get; set; }
    }

    /// <summary>A measure that is one aggregate of one column. Its pick is not the column's summarize-by.</summary>
    public sealed class ManifestBaseMeasure
    {
        public string Table { get; set; }
        public string Column { get; set; }
        /// <summary>sum | average | min | max | count | distinctcount (the engine's starter options; none is no row).</summary>
        public string Pick { get; set; }
        public string Name { get; set; }
        /// <summary>rule | jev | assistant | person</summary>
        public string DecidedBy { get; set; } = "rule";
        public string Identity { get; set; }
        /// <summary>The time measures of this base measure the person left out (the kind, such as "YoY", and who decided: person | assistant). The build leaves them out
        /// and the review shows them as dropped, from this one list (<see cref="StructureManifest.AllTimeMeasures"/>). Empty for a plan that never dropped one.</summary>
        public Dictionary<string, string> DroppedTimeKinds { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A question for the person that the manifest could not settle itself.</summary>
    public sealed class ManifestNeed
    {
        public string Kind { get; set; }             // composite-key | link-left-out | link-not-turned-on | date-table | fiscal-year-end | name-clash | pick-unusable
        public string Subject { get; set; }
        public string Text { get; set; }
    }

    // ---- the inputs that change the default ---------------------------------------------------------------------------

    public sealed class ManifestTableOverride
    {
        public string Table { get; set; }            // the source name (dim_date or dbo.dim_date)
        public bool? Include { get; set; }
        public string Name { get; set; }
    }

    public sealed class ManifestColumnOverride
    {
        public string Table { get; set; }
        public string Column { get; set; }           // the source column
        public bool? Include { get; set; }
        public string Name { get; set; }
        public bool? Hidden { get; set; }
        public string SummarizeBy { get; set; }
    }

    public sealed class ManifestLinkOverride
    {
        public string Identity { get; set; }         // the link's identity, when a seed was given; else the three below (names as the model has them)
        public string FromTable { get; set; }
        public string FromColumn { get; set; }
        public string ToTable { get; set; }
        public bool? Active { get; set; }
    }

    public sealed class ManifestDateOverride
    {
        /// <summary>The table to mark; "" says there is none; null leaves the finder's choice.</summary>
        public string Name { get; set; }
        public string DateColumn { get; set; }
        public string FiscalYearEnd { get; set; }
    }

    public sealed class ManifestOverrides
    {
        public ManifestTableOverride[] Tables { get; set; } = Array.Empty<ManifestTableOverride>();
        public ManifestColumnOverride[] Columns { get; set; } = Array.Empty<ManifestColumnOverride>();
        public ManifestLinkOverride[] Links { get; set; } = Array.Empty<ManifestLinkOverride>();
        public ManifestDateOverride DateTable { get; set; }
    }

    /// <summary>One pick for a base measure: the column, what to do with it, and who decided. Pick none removes it.</summary>
    public sealed class ManifestDecision
    {
        public string Table { get; set; }
        public string Column { get; set; }
        public string Pick { get; set; }
        public string Name { get; set; }
        public string DecidedBy { get; set; } = "jev";
    }

    /// <summary>One time-intelligence measure the manifest gives: which base measure, which kind (YTD, QTD, MTD, PY, YoY,
    /// YoYPct) and the measure's name. Derived from the final bases and the links; never edited by hand.</summary>
    public sealed class ManifestTimeIntelligence
    {
        public string BaseMeasure { get; set; }
        public string Kind { get; set; }
        public string Name { get; set; }
    }

    /// <summary>One time measure the build writes, in full: the base measure it follows, the kind, its name, the formula and the format it will carry. The review lists
    /// these lines and the build writes them from the same list (<see cref="StructureManifest.TimeMeasures"/>), so the build never writes a time measure the person
    /// was not shown (Sol, join 6, A1).</summary>
    public sealed class ManifestTimeMeasure
    {
        public string Table { get; set; }
        public string BaseMeasure { get; set; }
        public string Kind { get; set; }
        public string Name { get; set; }
        public string Dax { get; set; }
        public string Format { get; set; }
        /// <summary>True when the person (or their assistant) dropped it: the review shows it left out, and the build does not write it.</summary>
        public bool Dropped { get; set; }
        public string DroppedBy { get; set; }
    }

    /// <summary>What time intelligence the manifest gives today as a whole (the variants, their bases, the plain-words
    /// note for what was left out), worked out from its state each time it is asked (<see cref="StructureManifest.DeriveTimeIntelligence"/>).</summary>
    public sealed class ManifestTimeIntelligenceSummary
    {
        public string[] Variants { get; set; } = Array.Empty<string>();
        public string[] BaseMeasures { get; set; } = Array.Empty<string>();
        public string Note { get; set; }
    }

    internal static class StartIdentity
    {
        /// <summary>A GUID worked out from a seed and what it names. Null without a seed, so a default spec carries none.</summary>
        internal static string Of(string seed, string kind, string key)
        {
            if (string.IsNullOrEmpty(seed)) return null;
            return Hash(seed + "\u001f" + kind + "\u001f" + key);
        }

        /// <summary>A GUID worked out from another tag and a label (a time-intelligence measure from its base measure).</summary>
        internal static string Derived(string tag, string label) => Hash(tag + "\u001f" + label);

        private static string Hash(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            var g = new byte[16];
            Array.Copy(bytes, g, 16);
            return new Guid(g).ToString("D");
        }
    }

    public sealed class StructureManifest
    {
        /// <summary>The folders the build files its measures in. The review says them (UAT-J7-S2-008), so the build and the review read one name.</summary>
        public const string BaseMeasuresFolder = "Base Measures";
        public const string TimeIntelligenceFolder = "Time Intelligence";
        public static readonly string[] TimeIntelligenceVariants = { "YTD", "QTD", "MTD", "PY", "YoY", "YoYPct" };

        public string Server { get; set; }
        public string Database { get; set; }
        /// <summary>import | directLake</summary>
        public string StorageMode { get; set; } = "import";
        public string IdentitySeed { get; set; }
        public List<ManifestTable> Tables { get; set; } = new List<ManifestTable>();
        public List<ManifestLink> Links { get; set; } = new List<ManifestLink>();
        /// <summary>Null until a date table is looked for; Derive always sets it (an empty Name says there is none).</summary>
        public ManifestDateTable DateTable { get; set; }
        public List<ManifestBaseMeasure> BaseMeasures { get; set; } = new List<ManifestBaseMeasure>();
        /// <summary>The time measures a person or the assistant left out (<see cref="ManifestBaseMeasure.DroppedTimeKinds"/>) on a rule-made base measure that a link change took away
        /// (the fact table lost its last link, so it was isolated and had no measures), by the base measure's stable identity. They return with the base when the links do, so
        /// dropping every guessed link and keeping them again does not bring a dropped "year to date" back (Sol, join 8, K10).</summary>
        public Dictionary<string, Dictionary<string, string>> SuspendedTimeDrops { get; set; } = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        /// <summary>One row per time-intelligence measure, derived from the final bases and the links. Derive fills it and
        /// <see cref="RefreshTimeIntelligence"/> refreshes it after an edit; ToSpec never reads it, it works the list out
        /// afresh, so a hand-edited manifest cannot give a stale spec.</summary>
        public List<ManifestTimeIntelligence> TimeIntelligence { get; set; } = new List<ManifestTimeIntelligence>();
        public List<ManifestNeed> Needs { get; set; } = new List<ManifestNeed>();

        /// <summary>The shared source expression's lineage tag; null without a seed.</summary>
        public string SourceIdentity { get; set; }

        // ===========================================================================================================
        // Derive
        // ===========================================================================================================

        public static StructureManifest Derive(FabricSchema schema, ManifestOverrides overrides, IReadOnlyList<ManifestDecision> decisions,
            string seed, string server = null, string database = null, string storageMode = "import", LinkGuessFacts guess = null)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            overrides ??= new ManifestOverrides();
            decisions ??= Array.Empty<ManifestDecision>();
            var cmp = StringComparer.OrdinalIgnoreCase;
            var needs = new List<ManifestNeed>();
            var directLake = string.Equals(storageMode?.Trim(), "directLake", StringComparison.OrdinalIgnoreCase);
            var schemaTables = schema.Tables ?? Array.Empty<FabricTable>();

            // ---- which tables are in, and what they are called -------------------------------------------------
            ManifestTableOverride TableOverride(FabricTable t) => (overrides.Tables ?? Array.Empty<ManifestTableOverride>())
                .FirstOrDefault(o => cmp.Equals(o.Table, t.Name) || cmp.Equals(o.Table, LocalEngine.SchemaKey(t.Schema, t.Name)));
            bool In(FabricTable t) => TableOverride(t)?.Include != false;
            var included = schemaTables.Where(In).ToArray();
            var includedKeys = new HashSet<string>(included.Select(t => LocalEngine.SchemaKey(t.Schema, t.Name)), cmp);

            // TOM table names are flat (no schema), so the same table name in two schemas is qualified, and ONLY then.
            // Counted over the tables that are in, so leaving one of two out leaves the other under its own name.
            var nameCounts = included.GroupBy(t => t.Name ?? "", cmp).ToDictionary(g => g.Key, g => g.Count(), cmp);
            string DisplayName(string sch, string tbl) => (nameCounts.TryGetValue(tbl ?? "", out var n) && n > 1) ? ((sch ?? "") + "_" + tbl) : tbl;
            var finalName = new Dictionary<string, string>(cmp);
            foreach (var t in included)
            {
                var o = TableOverride(t);
                finalName[LocalEngine.SchemaKey(t.Schema, t.Name)] = !string.IsNullOrWhiteSpace(o?.Name) ? o.Name.Trim() : DisplayName(t.Schema, t.Name);
            }
            string NameOf(string sch, string tbl) => finalName.TryGetValue(LocalEngine.SchemaKey(sch, tbl), out var n) ? n : DisplayName(sch, tbl);
            foreach (var g in finalName.GroupBy(kv => kv.Value, cmp).Where(g => g.Count() > 1))
                needs.Add(new ManifestNeed { Kind = "name-clash", Subject = g.Key, Text = $"Two tables would both be called {g.Key}. Give one of them another name." });

            // ---- roles, from the declared keys. A key of several columns still says which table is the many side ---
            var groups = schema.ForeignKeyGroups()
                .Where(g => includedKeys.Contains(LocalEngine.SchemaKey(g.FromSchema, g.FromTable)) && includedKeys.Contains(LocalEngine.SchemaKey(g.ToSchema, g.ToTable)))
                .ToArray();
            // A table's key to ITSELF counts on neither side (verifier R2-H1): a credit note that names the invoice it
            // credits is still a fact, and the self link itself is drafted off below either way.
            var crossGroups = groups.Where(g => !cmp.Equals(LocalEngine.SchemaKey(g.FromSchema, g.FromTable), LocalEngine.SchemaKey(g.ToSchema, g.ToTable))).ToArray();
            var referenced = new HashSet<string>(crossGroups.Select(g => LocalEngine.SchemaKey(g.ToSchema, g.ToTable)), cmp);
            var referencing = new HashSet<string>(crossGroups.Select(g => LocalEngine.SchemaKey(g.FromSchema, g.FromTable)), cmp);

            ManifestColumnOverride ColumnOverride(FabricTable t, string col) => (overrides.Columns ?? Array.Empty<ManifestColumnOverride>())
                .FirstOrDefault(o => (cmp.Equals(o.Table, t.Name) || cmp.Equals(o.Table, LocalEngine.SchemaKey(t.Schema, t.Name))) && cmp.Equals(o.Column, col));

            // ---- links guessed from names, when the caller asked for them: only for a pair of tables the source declares no key for. They count for the
            // table roles below like declared links do (a table a guessed link points at is a dimension, one that points is a fact), so the rule finds the
            // fact table and its measures exactly as it does with declared keys. A drop later recomputes the roles (RefreshFromLinks).
            var guesses = new List<LinkGuesser.Guess>();
            if (guess != null)
            {
                var viewTables = included.Select(t => new FabricTable
                {
                    Schema = t.Schema, Name = t.Name, KeyColumns = t.KeyColumns,
                    Columns = (t.Columns ?? Array.Empty<FabricColumn>()).Where(c => ColumnOverride(t, c.Name)?.Include != false).ToArray(),
                }).ToList();
                guesses = LinkGuesser.GuessLinks(viewTables, LinkGuesser.DeclaredPairs(groups, includedKeys), guess, LinkGuesser.DeclaredLinks(groups, includedKeys));
                foreach (var g in guesses)
                {
                    referencing.Add(LocalEngine.SchemaKey(g.From.Schema, g.From.Name));
                    referenced.Add(LocalEngine.SchemaKey(g.To.Schema, g.To.Name));
                }
            }

            // The working draft: the same SpecTable and SpecRelationship the old draft built, so the date finder and
            // the link rule (both unchanged) read the same shapes. Each carries its source key for the manifest.
            var work = new List<(FabricTable Source, SpecTable Spec, ManifestTable Row)>();
            var allRows = new List<ManifestTable>();
            foreach (var t in schemaTables)
            {
                var k = LocalEngine.SchemaKey(t.Schema, t.Name);
                var tableIn = includedKeys.Contains(k);
                var keyset = new HashSet<string>(t.KeyColumns ?? Array.Empty<string>(), cmp);
                var role = RoleOf(referenced.Contains(k), referencing.Contains(k), t.Name);
                var cols = (t.Columns ?? Array.Empty<FabricColumn>()).Select(c =>
                {
                    var co = ColumnOverride(t, c.Name);
                    var isKey = keyset.Contains(c.Name);
                    return new ManifestColumn
                    {
                        Include = co?.Include != false,
                        Name = !string.IsNullOrWhiteSpace(co?.Name) ? co.Name.Trim() : c.Name,
                        SourceColumn = c.Name, DataType = c.DataType, IsKey = isKey,
                        Hidden = co?.Hidden ?? isKey,        // key columns hidden by default (AI-readiness best practice)
                        SummarizeBy = !string.IsNullOrWhiteSpace(co?.SummarizeBy) ? co.SummarizeBy.Trim()
                            : LocalEngine.IsNumericSpecType(c.DataType) && !isKey ? "Sum" : "None",
                        Identity = StartIdentity.Of(seed, "column", k + "\u001f" + c.Name),
                    };
                }).ToList();
                var row = new ManifestTable
                {
                    Include = tableIn, Name = tableIn ? NameOf(t.Schema, t.Name) : t.Name, SourceSchema = t.Schema, SourceName = t.Name,
                    Role = role, Identity = StartIdentity.Of(seed, "table", k), Columns = cols,
                };
                allRows.Add(row);
                if (!tableIn) continue;
                foreach (var g in cols.Where(c => c.Include).GroupBy(c => c.Name, cmp).Where(g => g.Count() > 1))
                    needs.Add(new ManifestNeed { Kind = "name-clash", Subject = row.Name + "/" + g.Key, Text = $"Two columns in {row.Name} would both be called {g.Key}. Give one of them another name." });
                var spec = new SpecTable
                {
                    Name = row.Name, Role = role, Entity = t.Name, Schema = t.Schema,
                    Columns = cols.Where(c => c.Include).Select(c => new SpecColumn
                    {
                        Name = c.Name, DataType = c.DataType, SourceColumn = c.SourceColumn, IsKey = c.IsKey, Hidden = c.Hidden, SummarizeBy = c.SummarizeBy,
                    }).ToArray(),
                };
                work.Add((t, spec, row));
            }
            var specTables = work.Select(w => w.Spec).ToArray();
            var rowByKey = new Dictionary<string, ManifestTable>(cmp);
            foreach (var r in allRows) rowByKey.TryAdd(LocalEngine.SchemaKey(r.SourceSchema, r.SourceName), r);
            ManifestColumn ColumnRow(FabricTable t, string source) =>
                rowByKey[LocalEngine.SchemaKey(t.Schema, t.Name)].Columns.FirstOrDefault(c => cmp.Equals(c.SourceColumn, source));
            string ColumnNameOf(FabricTable t, string source) => ColumnRow(t, source)?.Name ?? source;
            // A column the schema does not list is the build's to report, as before.
            bool ColumnIn(FabricTable t, string source) => ColumnRow(t, source)?.Include != false;
            FabricTable SourceTable(string sch, string tbl) => schemaTables.First(x => cmp.Equals(LocalEngine.SchemaKey(x.Schema, x.Name), LocalEngine.SchemaKey(sch, tbl)));

            // ---- links ---------------------------------------------------------------------------------------------
            var links = new List<ManifestLink>();
            var rels = new List<SpecRelationship>();
            var relOfLink = new Dictionary<ManifestLink, SpecRelationship>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var g in groups)
            {
                var from = SourceTable(g.FromSchema, g.FromTable); var to = SourceTable(g.ToSchema, g.ToTable);
                var fromName = NameOf(g.FromSchema, g.FromTable); var toName = NameOf(g.ToSchema, g.ToTable);
                var fromCols = g.FromColumns.Select(c => ColumnNameOf(from, c)).ToArray();
                var toCols = g.ToColumns.Select(c => ColumnNameOf(to, c)).ToArray();
                var idKey = LocalEngine.SchemaKey(g.FromSchema, g.FromTable) + "\u001f"
                    + (string.IsNullOrEmpty(g.ConstraintName) ? string.Join(",", g.FromColumns) + "\u001f" + LocalEngine.SchemaKey(g.ToSchema, g.ToTable) + "\u001f" + string.Join(",", g.ToColumns) : g.ConstraintName);
                seen[idKey] = seen.TryGetValue(idKey, out var nth) ? nth + 1 : 0;
                var link = new ManifestLink
                {
                    Identity = StartIdentity.Of(seed, "link", seen[idKey] == 0 ? idKey : idKey + "\u001f" + seen[idKey]),
                    ConstraintName = g.ConstraintName, FromTable = fromName, FromColumns = fromCols, ToTable = toName, ToColumns = toCols, Active = true,
                };
                links.Add(link);
                if (g.IsComposite)
                {
                    link.Active = false;
                    link.Unresolved = true;
                    link.Note = $"{fromName} links to {toName} on {fromCols.Length} columns together ({(fromCols.SequenceEqual(toCols, cmp) ? LocalEngine.JoinPlain(fromCols) : LocalEngine.JoinPlain(fromCols) + " to " + LocalEngine.JoinPlain(toCols))}). "
                        + "Power BI links on one column only, so no link was built. To link them, add one combined key made from "
                        + $"{LocalEngine.JoinPlain(fromCols)} to both tables, then link on it.";
                    needs.Add(new ManifestNeed { Kind = "composite-key", Subject = fromName + " to " + toName, Text = link.Note });
                    continue;
                }
                var left = new[] { (from, g.FromColumns[0]), (to, g.ToColumns[0]) }.FirstOrDefault(x => !ColumnIn(x.Item1, x.Item2));
                if (left.Item1 != null)
                {
                    link.Active = false;
                    link.Unresolved = true;
                    link.Note = $"The link from {fromName} to {toName} was not built, because {ColumnNameOf(left.Item1, left.Item2)} is left out of {NameOf(left.Item1.Schema, left.Item1.Name)}.";
                    needs.Add(new ManifestNeed { Kind = "link-left-out", Subject = fromName + " to " + toName, Text = link.Note });
                    continue;
                }
                var rel = new SpecRelationship { FromTable = fromName, FromColumn = fromCols[0], ToTable = toName, ToColumn = toCols[0] };
                rels.Add(rel);
                relOfLink[link] = rel;
            }
            var relArray = rels.ToArray();
            LocalEngine.DraftRelationshipActivity(relArray, specTables);

            // A person's (or the assistant's) switch on a link comes after the draft's own rule, and a link turned ON is
            // refused when it would give a table a second path, so an override can never make a build that fails.
            foreach (var o in overrides.Links ?? Array.Empty<ManifestLinkOverride>())
            {
                if (o.Active == null) continue;
                var link = links.FirstOrDefault(l => relOfLink.ContainsKey(l) && (!string.IsNullOrEmpty(o.Identity) ? l.Identity == o.Identity
                    : cmp.Equals(l.FromTable, o.FromTable) && cmp.Equals(l.FromColumns[0], o.FromColumn) && (string.IsNullOrEmpty(o.ToTable) || cmp.Equals(l.ToTable, o.ToTable))));
                if (link == null) continue;
                var rel = relOfLink[link];
                if (o.Active == false)
                {
                    rel.IsActive = false;
                    rel.Note = "Turned off in the plan. A measure can still use this one with USERELATIONSHIP.";
                    continue;
                }
                if (rel.IsActive != false) continue;
                var others = new LocalEngine.ActiveLinks(cmp);
                foreach (var r in relArray.Where(r => r != rel && r.IsActive != false && !cmp.Equals(r.FromTable, r.ToTable))) others.Add(r);
                var twin = others.Between(rel.FromTable, rel.ToTable);
                var via = twin == null ? others.SecondPathVia(rel.FromTable, rel.ToTable) : null;
                if (cmp.Equals(rel.FromTable, rel.ToTable) || twin != null || via != null)
                {
                    needs.Add(new ManifestNeed
                    {
                        Kind = "link-not-turned-on", Subject = rel.FromTable + " to " + rel.ToTable,
                        Text = $"The link from {rel.FromTable} to {rel.ToTable} stays off, because turning it on would give {(twin != null ? rel.FromTable : via?.From ?? rel.FromTable)} a second path to {(twin != null ? rel.ToTable : via?.To ?? rel.ToTable)}. Turn the other link off first.",
                    });
                    continue;
                }
                rel.IsActive = true; rel.Note = null;
            }
            foreach (var (link, rel) in relOfLink) { link.Active = rel.IsActive != false; link.Note = rel.Note; }

            // The guessed links come after the declared ones are settled, so a declared link is never moved by a guess: each is placed among the links that are on, in the
            // declared rule's own words (a twin of another link is off, a link that would give a table a second path is off), and a column two tables answer to is already off.
            if (guesses.Count > 0)
            {
                var guessedLinks = LinkGuesser.MakeLinks(guesses, seed, g => NameOf(g.Schema, g.Name), (g, c) => ColumnNameOf(SourceTable(g.Schema, g.Name), c));
                var guessedRels = guessedLinks.Select(LinkGuesser.Rel).ToList();
                LinkGuesser.PlaceLinks(relArray, guessedRels, specTables, guessedLinks.Select(l => LinkGuesser.KindRank(l.GuessKind)).ToList());
                for (var i = 0; i < guessedLinks.Count; i++)
                {
                    guessedLinks[i].Active = guessedRels[i].IsActive != false;
                    guessedLinks[i].Note = guessedRels[i].Note ?? guessedLinks[i].Note;
                }
                links.AddRange(guessedLinks);
                rels.AddRange(guessedRels);
                relArray = rels.ToArray();
            }

            // A column that is a key at either end of a link is a key, not a number to add up. A composite key's columns
            // count too, though they build no link: nobody totals half a key.
            var fkColumns = new HashSet<string>(
                rels.SelectMany(r => new[] { r.FromTable + "\u001f" + r.FromColumn, r.ToTable + "\u001f" + r.ToColumn })
                    .Concat(links.Where(l => l.Unresolved).SelectMany(l => l.FromColumns.Select(c => l.FromTable + "\u001f" + c)
                        .Concat(l.ToColumns.Select(c => l.ToTable + "\u001f" + c)))),
                cmp);

            // ---- the date table --------------------------------------------------------------------------------------
            var date = new ManifestDateTable();
            var found = LocalEngine.FindSourceDateTable(specTables, relArray, fkColumns);
            var dateOverride = overrides.DateTable;
            SpecTable markedTable = found.Table; string markedColumn = found.Column;
            if (dateOverride?.Name != null)
            {
                if (dateOverride.Name.Length == 0) { markedTable = null; markedColumn = null; }
                else
                {
                    var asked = specTables.FirstOrDefault(t => cmp.Equals(t.Name, dateOverride.Name))
                        ?? work.Where(w => cmp.Equals(w.Source.Name, dateOverride.Name)).Select(w => w.Spec).FirstOrDefault();
                    var dates = asked?.Columns.Where(c => c.DataType == "DateTime").ToArray() ?? Array.Empty<SpecColumn>();
                    var wanted = string.IsNullOrWhiteSpace(dateOverride.DateColumn) ? null : dates.FirstOrDefault(c => cmp.Equals(c.Name, dateOverride.DateColumn));
                    if (asked == null || dates.Length == 0 || (!string.IsNullOrWhiteSpace(dateOverride.DateColumn) && wanted == null))
                        needs.Add(new ManifestNeed { Kind = "date-table", Subject = dateOverride.Name, Text = $"{dateOverride.Name} cannot be the date table, because it has no date column{(string.IsNullOrWhiteSpace(dateOverride.DateColumn) ? "" : " called " + dateOverride.DateColumn)}. The draft's own choice stands." });
                    else
                    {
                        markedTable = asked;
                        markedColumn = (wanted ?? dates.FirstOrDefault(c => c.IsKey) ?? (dates.Length == 1 ? dates[0] : null) ?? dates[0]).Name;
                    }
                }
            }
            date.Name = markedTable?.Name ?? "";
            date.DateColumn = markedColumn;
            date.AutoNote = markedTable == null && dateOverride?.Name?.Length != 0 ? found.NoneNote : null;
            if (!string.IsNullOrWhiteSpace(dateOverride?.FiscalYearEnd))
            {
                var fy = NormalizeYearEnd(dateOverride.FiscalYearEnd);
                if (fy == null) needs.Add(new ManifestNeed { Kind = "fiscal-year-end", Subject = dateOverride.FiscalYearEnd, Text = $"The fiscal year end '{dateOverride.FiscalYearEnd}' is not a month and day. Write it like 6/30. Year-to-date uses the calendar year until then." });
                else date.FiscalYearEnd = fy == "12/31" ? null : fy;   // 12/31 is DAX's own default, so nothing is added
            }

            // ---- base measures: the rule's pick, then each decision --------------------------------------------------
            var picks = new List<ManifestBaseMeasure>();
            ManifestDecision DecisionFor(FabricTable t, ManifestColumn c) => decisions.LastOrDefault(d => d != null
                && (cmp.Equals(d.Table, t.Name) || cmp.Equals(d.Table, LocalEngine.SchemaKey(t.Schema, t.Name)) || cmp.Equals(d.Table, NameOf(t.Schema, t.Name)))
                && (cmp.Equals(d.Column, c.SourceColumn) || cmp.Equals(d.Column, c.Name)));
            var usedDecisions = new HashSet<ManifestDecision>();
            foreach (var w in work)
                foreach (var c in w.Row.Columns.Where(c => c.Include))
                {
                    var d = DecisionFor(w.Source, c);
                    string rule = StarterMeasureTemplates.None;
                    if (w.Spec.Role == "fact" && LocalEngine.IsNumericSpecType(c.DataType))
                        rule = StarterMeasureTemplates.RuleChoice(c.SourceColumn, c.IsKey, fkColumns.Contains(w.Spec.Name + "\u001f" + c.Name) ? "many" : "none", c.SummarizeBy);
                    var pick = d != null ? (d.Pick ?? "").Trim().ToLowerInvariant() : rule;
                    if (d != null)
                    {
                        usedDecisions.Add(d);
                        if (!PickFits(pick, c.DataType))
                        {
                            needs.Add(new ManifestNeed { Kind = "pick-unusable", Subject = w.Spec.Name + "/" + c.Name, Text = $"'{d.Pick}' cannot be used on {c.Name} in {w.Spec.Name}, so the draft's own choice stands." });
                            d = null; pick = rule;
                        }
                    }
                    if (pick == StarterMeasureTemplates.None || !StarterMeasureTemplates.IsOption(pick)) continue;
                    picks.Add(new ManifestBaseMeasure
                    {
                        Table = w.Spec.Name, Column = c.Name, Pick = pick,
                        Name = !string.IsNullOrWhiteSpace(d?.Name) ? d.Name.Trim() : StarterMeasureTemplates.Name(pick, c.Name),
                        DecidedBy = d != null ? (string.IsNullOrWhiteSpace(d.DecidedBy) ? "jev" : d.DecidedBy) : "rule",
                        Identity = StartIdentity.Of(seed, "measure", LocalEngine.SchemaKey(w.Source.Schema, w.Source.Name) + "\u001f" + c.SourceColumn),
                    });
                }
            foreach (var d in decisions.Where(d => d != null && !usedDecisions.Contains(d)))
                needs.Add(new ManifestNeed { Kind = "pick-unusable", Subject = d.Table + "/" + d.Column, Text = $"A pick for {d.Column} in {d.Table} was left out, because there is no such column in the plan." });
            foreach (var g in picks.GroupBy(p => p.Table + "\u001f" + p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                needs.Add(new ManifestNeed { Kind = "name-clash", Subject = g.First().Name, Text = $"Two measures in {g.First().Table} would both be called {g.First().Name}. Give one of them another name." });

            var manifest = new StructureManifest
            {
                Server = server ?? schema.Server, Database = database ?? schema.Database,
                StorageMode = directLake ? "directLake" : "import",
                IdentitySeed = seed,
                SourceIdentity = StartIdentity.Of(seed, "source", "FabricSource"),
                Tables = allRows, Links = links, DateTable = date, BaseMeasures = picks, Needs = needs,
            };
            manifest.RefreshTimeIntelligence();
            return manifest;
        }

        // A pick has to make sense for the column: a total or an average of a number, a lowest or highest of a number or a
        // date, a count of anything. A string does not add up, and DAX would refuse the measure at its first use.
        private static bool PickFits(string pick, string dataType)
        {
            var numeric = LocalEngine.IsNumericSpecType(dataType);
            switch (pick)
            {
                case StarterMeasureTemplates.None: return true;
                case StarterMeasureTemplates.Sum:
                case StarterMeasureTemplates.Average: return numeric;
                case StarterMeasureTemplates.Min:
                case StarterMeasureTemplates.Max: return numeric || dataType == "DateTime";
                case StarterMeasureTemplates.Count:
                case StarterMeasureTemplates.DistinctCount: return true;
                default: return false;
            }
        }

        /// <summary>"6/30", "06/30" and "6-30" are 6/30. Null when it is not a real month and day (2/29 is not one: a year
        /// end that only exists in some years is not a year end).</summary>
        internal static string NormalizeYearEnd(string text)
        {
            var m = System.Text.RegularExpressions.Regex.Match((text ?? "").Trim(), @"^(\d{1,2})\s*[/\-]\s*(\d{1,2})$");
            if (!m.Success) return null;
            int month = int.Parse(m.Groups[1].Value), day = int.Parse(m.Groups[2].Value);
            if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(2001, month)) return null;
            return month + "/" + day;
        }

        // ===========================================================================================================
        // Time intelligence, derived
        // ===========================================================================================================

        /// <summary>The year-to-date, prior-year and other time measures this manifest gives. Only a base measure whose pick
        /// is sum gets them (an average or a count over a period is not a running total), and only when its table has an
        /// ACTIVE link of its own to the marked date table: a "YTD" over a date table that does not filter the fact returns
        /// the plain total for every period and says nothing (verifier round 5, F1). The note names what was left out.</summary>
        public ManifestTimeIntelligenceSummary DeriveTimeIntelligence()
        {
            var cmp = StringComparer.OrdinalIgnoreCase;
            var additive = BuiltBases().Where(b => b.Pick == StarterMeasureTemplates.Sum).ToArray();
            var marked = MarkedDateTable();
            if (marked == null) return new ManifestTimeIntelligenceSummary();
            var own = new HashSet<string>((Links ?? new List<ManifestLink>()).Where(l => l.Active && !l.Unresolved && !l.Dropped && cmp.Equals(l.ToTable, marked.Name)).Select(l => l.FromTable), cmp);
            var bases = additive.Where(x => own.Contains(x.Table)).ToArray();
            string note = null;
            var left = additive.Select(x => x.Table).Distinct(cmp).Where(t => !own.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToArray();
            if (left.Length > 0)
            {
                var got = bases.Select(x => x.Table).Distinct(cmp).OrderBy(t => t, StringComparer.Ordinal).ToArray();
                note = (got.Length > 0
                        ? $"Year-to-date and prior-year measures were made for {LocalEngine.JoinPlain(got)}. None were made for {LocalEngine.JoinPlain(left)}, because "
                        : "No year-to-date or prior-year measures were made, because ")
                    + $"{(left.Length == 1 ? "it has" : "each has")} no link of its own to {marked.Name}"
                    + $"{(got.Length > 0 ? "" : " (" + LocalEngine.JoinPlain(left) + ")")}. "
                    + $"Link {(left.Length == 1 ? "it" : "each")} to {marked.Name}, and they can be added then.";
            }
            return new ManifestTimeIntelligenceSummary
            {
                Variants = bases.Length > 0 ? TimeIntelligenceVariants : Array.Empty<string>(),
                BaseMeasures = bases.Select(x => x.Name).ToArray(),
                Note = note,
            };
        }

        /// <summary>The time measures this manifest has the build write, each with its formula: for every base measure <see cref="DeriveTimeIntelligence"/> gives, one
        /// per variant, over the marked date table's date column. THE ONE LIST: the review's lines (StartPlanViewBuilder) are made from it, and the build's own
        /// names and formulas come from the same two functions (<see cref="TimeMeasureName"/>, LocalEngine.SpecTiDax), so what the review shows is what the build writes.</summary>
        public IEnumerable<ManifestTimeMeasure> TimeMeasures() => AllTimeMeasures().Where(t => !t.Dropped);

        /// <summary>The time measures the build WOULD write if none were dropped, each marked when it was. The review lists all of them (a dropped one reads as left out, so it can
        /// be kept again); the build writes <see cref="TimeMeasures"/>, the ones that are not.</summary>
        public IEnumerable<ManifestTimeMeasure> AllTimeMeasures()
        {
            var ti = DeriveTimeIntelligence();
            var marked = MarkedDateTable();
            if (marked == null || ti.Variants.Length == 0 || string.IsNullOrWhiteSpace(DateTable?.DateColumn)) yield break;
            var dateCol = $"'{marked.Name.Replace("'", "''")}'[{DateTable.DateColumn.Replace("]", "]]")}]";
            var yearEnd = string.IsNullOrWhiteSpace(DateTable.FiscalYearEnd) ? null : NormalizeYearEnd(DateTable.FiscalYearEnd);
            foreach (var baseName in ti.BaseMeasures)
            {
                var b = BuiltBases().FirstOrDefault(x => x.Name == baseName && x.Pick == StarterMeasureTemplates.Sum);
                if (b == null) continue;
                foreach (var variant in ti.Variants)
                {
                    var dax = LocalEngine.SpecTiDax(variant, $"[{baseName}]", dateCol, out var format, yearEnd);
                    if (dax == null) continue;
                    var by = (string)null;
                    var dropped = b.DroppedTimeKinds != null && b.DroppedTimeKinds.TryGetValue(variant, out by);
                    yield return new ManifestTimeMeasure { Table = b.Table, BaseMeasure = baseName, Kind = variant, Name = TimeMeasureName(baseName, variant), Dax = dax, Format = format, Dropped = dropped, DroppedBy = by };
                }
            }
        }

        /// <summary>The name of a base measure's time measure ("Total Amount YTD"). The review and the build both take it from here.</summary>
        public static string TimeMeasureName(string baseMeasure, string variant) => $"{baseMeasure} {variant}";

        /// <summary>Whether a base measure is in the plan right now: its table and its column are both brought in. A base whose column or table was left out is
        /// KEPT in the manifest, never removed (the person's own pick, and the name the assistant's words are about, come back with the column), and builds nothing.</summary>
        public bool IsBuilt(ManifestBaseMeasure b)
        {
            if (b == null) return false;
            var t = (Tables ?? new List<ManifestTable>()).FirstOrDefault(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, b.Table));
            return t != null && t.Include && t.Columns.Any(c => c.Include && StringComparer.OrdinalIgnoreCase.Equals(c.Name, b.Column));
        }

        /// <summary>The base measures in the plan right now (<see cref="IsBuilt"/>).</summary>
        public IEnumerable<ManifestBaseMeasure> BuiltBases() => (BaseMeasures ?? new List<ManifestBaseMeasure>()).Where(IsBuilt);

        /// <summary>A table a link points at is a dimension, one that only points is a fact. K23 (9 Oct): a table that is pointed at (a return names its sale) but also
        /// points at other tables, and is named like a fact table (fact_sales), is a fact too, so its links to the dimensions are the ones kept on when two paths meet.</summary>
        internal static string RoleOf(bool referenced, bool referencing, string sourceName) =>
            referenced ? (referencing && LinkGuesser.NamedLikeAFact(sourceName) ? "fact" : "dimension") : referencing ? "fact" : "isolated";

        /// <summary>The plan follows its links: after a guessed link is dropped, kept again or reconsidered, the table roles, the starter measures the RULE chose and the date table
        /// the finder chose are worked out again from the links that stand. What a person or Jev decided is never touched: a measure decided by anyone but the rule stays as it is
        /// (a measure the person dropped stays dropped), and so does a pick of the person's. With the same links this gives the roles, the measures and the date table
        /// <see cref="Derive"/> gave, so a plan whose links never changed is unchanged by it.</summary>
        internal void RefreshFromLinks()
        {
            var cmp = StringComparer.OrdinalIgnoreCase;
            var links = Links ?? new List<ManifestLink>();
            var live = links.Where(l => !l.Dropped && !cmp.Equals(l.FromTable, l.ToTable)).ToList();
            var referenced = new HashSet<string>(live.Select(l => l.ToTable), cmp);
            var referencing = new HashSet<string>(live.Select(l => l.FromTable), cmp);
            foreach (var t in Tables) t.Role = RoleOf(referenced.Contains(t.Name), referencing.Contains(t.Name), t.SourceName ?? t.Name);

            // A column that is a key at either end of a link built, or of one that cannot be built, is not a number to add up (Derive's fkColumns).
            var builtLinks = links.Where(l => !l.Unresolved && !l.Dropped && LinkGuesser.BothEndsIn(this, l)).ToList();
            var fk = new HashSet<string>(
                builtLinks.SelectMany(l => new[] { l.FromTable + "\u001f" + l.FromColumns[0], l.ToTable + "\u001f" + l.ToColumns[0] })
                    .Concat(links.Where(l => l.Unresolved && !l.Dropped).SelectMany(l => l.FromColumns.Select(c => l.FromTable + "\u001f" + c).Concat(l.ToColumns.Select(c => l.ToTable + "\u001f" + c)))),
                cmp);

            BaseMeasures ??= new List<ManifestBaseMeasure>();
            foreach (var t in Tables.Where(t => t.Include))
                foreach (var c in t.Columns.Where(c => c.Include))
                {
                    var existing = BaseMeasures.FirstOrDefault(b => cmp.Equals(b.Table, t.Name) && cmp.Equals(b.Column, c.Name));
                    var rule = t.Role == "fact" && LocalEngine.IsNumericSpecType(c.DataType)
                        ? StarterMeasureTemplates.RuleChoice(c.SourceColumn, c.IsKey, fk.Contains(t.Name + "\u001f" + c.Name) ? "many" : "none", c.SummarizeBy)
                        : StarterMeasureTemplates.None;
                    var wanted = rule != StarterMeasureTemplates.None && StarterMeasureTemplates.IsOption(rule);
                    if (existing == null)
                    {
                        if (!wanted) continue;
                        var made = new ManifestBaseMeasure
                        {
                            Table = t.Name, Column = c.Name, Pick = rule, Name = StarterMeasureTemplates.Name(rule, c.Name), DecidedBy = "rule",
                            Identity = StartPlanDecide.BaseMeasureIdentity(this, t.Name, c.Name),
                        };
                        // The base is back: the time measures that were left out when it went are left out again.
                        if (SuspendedTimeDrops != null && !string.IsNullOrEmpty(made.Identity) && SuspendedTimeDrops.Remove(made.Identity, out var back))
                            made.DroppedTimeKinds = new Dictionary<string, string>(back, StringComparer.OrdinalIgnoreCase);
                        BaseMeasures.Add(made);
                    }
                    else if (existing.DecidedBy == "rule")
                    {
                        if (!wanted)
                        {
                            // Taken away, not forgotten: the choices made on its time measures wait under its identity (a suspended base is not in the list, so it is not built).
                            if (!string.IsNullOrEmpty(existing.Identity) && existing.DroppedTimeKinds != null && existing.DroppedTimeKinds.Count > 0)
                            {
                                SuspendedTimeDrops ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                                SuspendedTimeDrops[existing.Identity] = new Dictionary<string, string>(existing.DroppedTimeKinds, StringComparer.OrdinalIgnoreCase);
                            }
                            BaseMeasures.Remove(existing);
                        }
                        else existing.Pick = rule;
                    }
                }

            // The date table is the finder's own choice (nothing in a plan sets it by hand), so it follows the links the facts have to it.
            if (DateTable != null)
            {
                var found = LocalEngine.FindSourceDateTable(LinkGuesser.SpecTablesOf(this), builtLinks.Select(LinkGuesser.Rel).ToArray(), fk);
                DateTable.Name = found.Table?.Name ?? "";
                DateTable.DateColumn = found.Column;
                DateTable.AutoNote = found.Table == null ? found.NoneNote : null;
            }
            RefreshTimeIntelligence();
        }

        /// <summary>Refill <see cref="TimeIntelligence"/> from the bases and links as they stand now. Call it after an edit.</summary>
        public void RefreshTimeIntelligence()
        {
            var ti = DeriveTimeIntelligence();
            TimeIntelligence = ti.BaseMeasures.SelectMany(b => ti.Variants.Select(v => new ManifestTimeIntelligence { BaseMeasure = b, Kind = v, Name = b + " " + v })).ToList();
        }

        internal ManifestTable MarkedDateTable() =>
            string.IsNullOrWhiteSpace(DateTable?.Name) ? null
                : (Tables ?? new List<ManifestTable>()).FirstOrDefault(t => t.Include && StringComparer.OrdinalIgnoreCase.Equals(t.Name, DateTable.Name));

        // ===========================================================================================================
        // ToSpec
        // ===========================================================================================================

        public ModelSpec ToSpec()
        {
            var directLake = string.Equals(StorageMode, "directLake", StringComparison.OrdinalIgnoreCase);
            var included = (Tables ?? new List<ManifestTable>()).Where(t => t.Include).ToArray();

            var tables = included.Select(t => new SpecTable
            {
                Name = t.Name, Role = t.Role, Entity = t.SourceName, Schema = t.SourceSchema,   // Entity stays the bare physical name
                SourceName = directLake ? "FabricSource" : null,
                LineageTag = t.Identity,
                Columns = t.Columns.Where(c => c.Include).Select(c => new SpecColumn
                {
                    Name = c.Name, DataType = c.DataType, SourceColumn = c.SourceColumn, IsKey = c.IsKey, Hidden = c.Hidden,
                    SummarizeBy = c.SummarizeBy, LineageTag = c.Identity,
                }).ToArray(),
            }).ToArray();

            // The links that can be built: one column pair each. A composite or left-out link is a line for the person.
            var tableByNameEarly = tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            // A link is built only when both of its ends are: a table or a column left out takes the links that join on it out of the build with it (they stay in the
            // manifest, so putting the table back brings them back). Building one would fail the whole build on a column that is not there.
            bool EndIn(string table, string[] columns) =>
                tableByNameEarly.TryGetValue(table ?? "", out var et) && columns.All(c => et.Columns.Any(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, c)));
            var rels = (Links ?? new List<ManifestLink>()).Where(l => !l.Unresolved && !l.Dropped && EndIn(l.FromTable, l.FromColumns) && EndIn(l.ToTable, l.ToColumns)).Select(l => new SpecRelationship
            {
                FromTable = l.FromTable, FromColumn = l.FromColumns[0], ToTable = l.ToTable, ToColumn = l.ToColumns[0],
                IsActive = l.Active ? (bool?)null : false, Note = l.Active ? null : l.Note, Name = l.Identity,
            }).ToArray();

            var tableByName = tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            var measures = (BaseMeasures ?? new List<ManifestBaseMeasure>())
                // A base the person dropped (its pick is none) is not in the spec: the spec holds only what the build writes, so the build, the review and the spec are one list.
                .Where(b => !string.Equals(b.Pick, StarterMeasureTemplates.None, StringComparison.OrdinalIgnoreCase) && tableByName.TryGetValue(b.Table, out var bt) && bt.Columns.Any(c => c.Name == b.Column))
                .Select(b => new SpecMeasure
                {
                    Table = b.Table, Name = b.Name,
                    Dax = StarterMeasureTemplates.Dax(b.Pick, b.Table, b.Column), DisplayFolder = BaseMeasuresFolder, LineageTag = b.Identity,
                }).ToArray();

            var ti = DeriveTimeIntelligence();
            var marked = MarkedDateTable();
            SpecTable markedSpec = null;
            string noDateNote = null;
            if (marked != null && tableByName.TryGetValue(marked.Name, out markedSpec))
            {
                // A table has one key column, and a marked date table's key is its date column. The declared primary key
                // stays hidden: the relationships still join on it.
                markedSpec.Role = "date";
                foreach (var c in markedSpec.Columns) c.IsKey = c.Name == DateTable.DateColumn;
            }
            else
            {
                markedSpec = null;
                noDateNote = DateTable?.AutoNote;
                var additiveCount = BuiltBases().Count(b => b.Pick == StarterMeasureTemplates.Sum);
                if (noDateNote == null) noDateNote = LocalEngine.NoDateTableNote(tables, additiveCount > 0);
            }

            return new ModelSpec
            {
                Name = Database,
                CompatibilityLevel = 1604,
                StorageMode = directLake ? "directLake" : "import",
                Source = new SpecSource { Kind = "fabric-sql", Server = Server, Database = Database, LineageTag = SourceIdentity },
                Tables = tables,
                Relationships = rels,
                Measures = measures,
                Unresolved = (Links ?? new List<ManifestLink>()).Where(l => l.Unresolved).Select(l => l.Note).ToArray() is { Length: > 0 } lines ? lines : null,
                TimeIntelligence = ti.Variants,
                TimeIntelligenceBaseMeasures = ti.BaseMeasures,
                TimeIntelligenceLeftOut = AllTimeMeasures().Where(t => t.Dropped).Select(t => t.Name).ToArray() is { Length: > 0 } left ? left : null,
                DateTable = markedSpec != null
                    ? new SpecDateTable { Name = markedSpec.Name, DateColumn = DateTable.DateColumn, MarkAsDate = true, Note = ti.Note, FiscalYearEnd = DateTable.FiscalYearEnd }
                    : new SpecDateTable { Name = "", MarkAsDate = false, Note = noDateNote },
            };
        }
    }
}

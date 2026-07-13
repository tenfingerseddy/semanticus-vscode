using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine.Lineage
{
    /// <summary>
    /// Resolves each table's partition source(s) to upstream "source" nodes + source→table edges, purely from TOM
    /// (offline) — correction #4 of the plan (source→table is an M-PARSE problem, not an API lookup). This is a
    /// PRAGMATIC first cut, deliberately NOT a full M parser: a small comment/string-aware lexer (which preserves
    /// <c>#"quoted"</c> identifiers — those are names, not strings) extracts the identifiers a partition's M
    /// references, intersects them with the model's shared expressions (<see cref="Model.Expressions"/>) to draw
    /// table→sharedExpression edges, and records the data-access connector the query calls. Direct Lake / structured
    /// partitions are read structurally. A partition we can't attribute is marked UNRESOLVED rather than silently
    /// dropped (the honesty gate). Fuller M parsing — recursive expression chains, merge-query multi-source — is a
    /// clean follow-up. See docs/lineage-impact-plan.md.
    /// </summary>
    internal static class MReferenceResolver
    {
        public sealed class Source { public string Ref; public string Name; public string Kind; public string Detail; }
        public sealed class Edge { public string SourceRef; public string TableRef; }

        // Known M data-access library namespaces (the connector behind a query) — used only to LABEL the source node;
        // not exhaustive (an unknown dotted call simply doesn't register as a connector).
        private static readonly HashSet<string> ConnectorNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Sql", "Excel", "Csv", "Json", "Xml", "Web", "OData", "Odbc", "Oracle", "MySQL", "PostgreSQL", "Snowflake",
            "GoogleBigQuery", "AmazonRedshift", "AzureStorage", "AzureDataLake", "DataLake", "Lakehouse", "Fabric",
            "PowerBI", "PowerPlatform", "Dataverse", "CommonDataService", "SharePoint", "Folder", "File", "Access",
            "Db2", "Teradata", "SapHana", "SapBusinessWarehouse", "Salesforce", "Databricks", "AnalysisServices",
            "Sybase", "Informix", "Vertica", "Impala", "Spark", "Hdfs", "Kusto", "AzureKusto", "MicrosoftAzureConsumptionInsights",
        };

        // M language keywords — never a reference to a shared expression, so they must not draw a source edge.
        private static readonly HashSet<string> MKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "let", "in", "each", "if", "then", "else", "type", "as", "is", "meta", "and", "or", "not",
            "section", "shared", "try", "otherwise", "error", "true", "false", "null",
        };

        public static (List<Source> sources, List<Edge> edges) Resolve(Model m)
        {
            var sources = new Dictionary<string, Source>(StringComparer.Ordinal);   // de-duped by ref
            var edges = new List<Edge>();
            var exprByName = m.Expressions.ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);

            void AddEdge(Source src, string tableRef)
            {
                if (!sources.ContainsKey(src.Ref)) sources[src.Ref] = src;
                if (!edges.Any(e => e.SourceRef == src.Ref && e.TableRef == tableRef))
                    edges.Add(new Edge { SourceRef = src.Ref, TableRef = tableRef });
            }

            foreach (var t in m.Tables)
            {
                var tableRef = ObjectRefs.For(t);
                var attributed = false;
                var hasSourcePartition = false;

                foreach (var p in t.Partitions)
                {
                    switch (p.SourceType)
                    {
                        case PartitionSourceType.M:
                        case PartitionSourceType.PolicyRange:   // incremental-refresh ranges are M too
                        {
                            hasSourcePartition = true;
                            var (idents, connector) = Lex(SafeExpression(p));
                            foreach (var id in idents)                          // table → each shared expression it references
                                if (exprByName.TryGetValue(id, out var e)) { AddEdge(ExprSource(e), tableRef); attributed = true; }
                            if (connector != null)                              // table → the data-access connector it calls directly
                            { AddEdge(new Source { Ref = "source:connector/" + connector, Name = connector, Kind = "connector", Detail = "M connector" }, tableRef); attributed = true; }
                            break;
                        }
                        case PartitionSourceType.Entity:        // Direct Lake / entity binding
                        {
                            hasSourcePartition = true;
                            var ep = p as EntityPartition;
                            if (ep?.ExpressionSource != null && exprByName.TryGetValue(ep.ExpressionSource.Name, out var e))
                            { AddEdge(ExprSource(e), tableRef); attributed = true; }
                            else if (p.DataSource != null) { AddEdge(DataSourceSource(p.DataSource), tableRef); attributed = true; }
                            break;
                        }
                        case PartitionSourceType.Query:         // legacy native query against a data source
                        {
                            hasSourcePartition = true;
                            if (p.DataSource != null) { AddEdge(DataSourceSource(p.DataSource), tableRef); attributed = true; }
                            break;
                        }
                        // Calculated / CalculationGroup / Inferred / Parquet / None: upstream is DAX or system — no source node.
                    }
                }

                // A data-bearing table we could not attribute to any source → mark UNRESOLVED (never silently drop it).
                if (hasSourcePartition && !attributed)
                    AddEdge(new Source { Ref = "source:unresolved/" + t.Name, Name = "Unresolved source", Kind = "unresolved", Detail = "could not resolve a source from the partition definition" }, tableRef);
            }

            return (sources.Values.ToList(), edges);
        }

        private static Source ExprSource(NamedExpression e) =>
            new Source { Ref = "source:expr/" + e.Name, Name = e.Name, Kind = "query", Detail = "shared expression" };

        private static Source DataSourceSource(DataSource d) =>
            new Source { Ref = "source:datasource/" + d.Name, Name = d.Name, Kind = "connector", Detail = d.GetType().Name };

        // p.Expression is the M text for M/PolicyRange partitions; guard defensively in case the wrapper restricts the
        // getter for an exotic partition kind (we'd rather attribute nothing than throw the whole graph build).
        private static string SafeExpression(Partition p)
        {
            try { return p.Expression ?? ""; } catch { return ""; }
        }

        /// <summary>
        /// A small comment/string-aware lexer over an M expression. Returns the identifiers that appear in a
        /// REFERENCE position (so they can match a shared-expression name) plus the first data-access connector call
        /// ("Namespace.Function"). It deliberately EXCLUDES: <c>//</c> + <c>/* */</c> comments, <c>"string literals"</c>,
        /// M keywords, the head of a dotted path (<c>Sql.Database</c> — a namespace, not an expression), <c>#intrinsic</c>
        /// tokens (<c>#date</c>/<c>#table</c>/…), and let/record BINDING names (the LHS of <c>name = …</c>, which is a
        /// definition, not a reference). <c>#"quoted identifiers"</c> are preserved. This prevents a local let-variable
        /// or step name (e.g. the near-universal <c>Source = …</c>) from drawing a phantom source edge to a shared
        /// expression of the same name. Not a full parser — enough for the common staging-query pattern (correction #4).
        /// </summary>
        private static (HashSet<string> idents, string connector) Lex(string src)
        {
            var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);    // identifiers in REFERENCE position
            var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // let/record binding NAMES (definitions)
            string connector = null;
            int i = 0, n = src?.Length ?? 0;

            int NextNonSpace(int from) { int j = from; while (j < n && char.IsWhiteSpace(src[j])) j++; return j; }
            // A '=' that is an assignment (binding), not '=>' (lambda) or '==' (not valid M, but defensive).
            bool IsBindingAt(int eq) => eq < n && src[eq] == '=' && !(eq + 1 < n && (src[eq + 1] == '>' || src[eq + 1] == '='));

            while (i < n)
            {
                char c = src[i];

                if (c == '/' && i + 1 < n && src[i + 1] == '/')                 // line comment
                { i += 2; while (i < n && src[i] != '\n') i++; continue; }

                if (c == '/' && i + 1 < n && src[i + 1] == '*')                 // block comment
                { i += 2; while (i + 1 < n && !(src[i] == '*' && src[i + 1] == '/')) i++; i = Math.Min(n, i + 2); continue; }

                if (c == '#' && i + 1 < n && src[i + 1] == '"')                 // #"quoted identifier"
                {
                    i += 2; var sb = new StringBuilder();
                    while (i < n)
                    {
                        if (src[i] == '"')
                        {
                            if (i + 1 < n && src[i + 1] == '"') { sb.Append('"'); i += 2; continue; }   // "" escape inside the name
                            i++; break;
                        }
                        sb.Append(src[i]); i++;
                    }
                    if (sb.Length > 0) { var name = sb.ToString(); if (IsBindingAt(NextNonSpace(i))) bound.Add(name); else refs.Add(name); }
                    continue;
                }

                if (c == '#' && i + 1 < n && (char.IsLetter(src[i + 1]) || src[i + 1] == '_'))   // #intrinsic (#date, #table, #binary…) — skip
                {
                    i++; while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                    continue;
                }

                if (c == '"')                                                   // string literal — ignore contents
                {
                    i++;
                    while (i < n)
                    {
                        if (src[i] == '"') { if (i + 1 < n && src[i + 1] == '"') { i += 2; continue; } i++; break; }
                        i++;
                    }
                    continue;
                }

                if (char.IsLetter(c) || c == '_')                              // bare identifier
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                    var head = src.Substring(start, i - start);

                    if (i < n && src[i] == '.')                                 // dotted path (Namespace.Function…) — a connector access, NOT an expression ref
                    {
                        int dotStart = start;
                        while (i < n && src[i] == '.')
                        {
                            i++;
                            int seg = i;
                            while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                            if (i == seg) break;                                // a lone '.' (e.g. a number) — stop
                        }
                        var dotted = src.Substring(dotStart, i - dotStart);
                        int paren = NextNonSpace(i);
                        if (connector == null && paren < n && src[paren] == '(' && ConnectorNamespaces.Contains(head))
                            connector = dotted;
                        continue;                                               // do NOT treat the head as a shared-expression reference
                    }

                    if (MKeywords.Contains(head)) continue;                     // M keyword — never an expression ref
                    if (IsBindingAt(NextNonSpace(i))) { bound.Add(head); continue; }   // let/record binding NAME (definition, not reference)
                    refs.Add(head);
                    continue;
                }

                i++;
            }

            refs.ExceptWith(bound);   // a name defined as a binding HERE is not a reference to a shared expression
            return (refs, connector);
        }
    }
}

using System.Text;
using System.Text.Json;

namespace Semanticus.DaxSpike;

public sealed record CorpusItem(string File, string Kind, string Dax);

// Harvests real DAX out of the repo's test models. Two sources:
//  - .bim  (Model.bim JSON): authoritative, System.Text.Json, exact expression kinds.
//  - .tmdl (TMDL text): high-confidence line forms only (measure/calculated-column/calculationItem),
//    deliberately skipping partition `source =` so we never feed M (Power Query) code to a DAX parser.
public static class Corpus
{
    public static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (System.IO.File.Exists(Path.Combine(d.FullName, "Semanticus.sln"))) return d.FullName;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root (Semanticus.sln)");
    }

    private static IEnumerable<string> Walk(string root, string ext)
    {
        foreach (var f in Directory.EnumerateFiles(root, ext, SearchOption.AllDirectories))
        {
            var p = f.Replace('\\', '/');
            if (p.Contains("/bin/") || p.Contains("/obj/") || p.Contains("/.claude/") || p.Contains("/.git/")) continue;
            yield return f;
        }
    }

    // ---------- BIM ----------
    private static string? ExprText(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return e.GetString();
        if (e.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var line in e.EnumerateArray())
            {
                if (!first) sb.Append('\n');
                sb.Append(line.GetString());
                first = false;
            }
            return sb.ToString();
        }
        return null;
    }

    private static void Add(List<CorpusItem> outp, string file, string kind, JsonElement parent, string prop)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(prop, out var e))
        {
            var s = ExprText(e);
            if (!string.IsNullOrWhiteSpace(s)) outp.Add(new CorpusItem(file, kind, s!));
        }
    }

    public static List<CorpusItem> HarvestBim(string root)
    {
        var outp = new List<CorpusItem>();
        foreach (var file in Walk(root, "*.bim"))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8).TrimStart('﻿')); }
            catch { continue; }
            using (doc)
            {
                var rel = file.Replace('\\', '/');
                var r = doc.RootElement;
                var model = r.TryGetProperty("model", out var mm) ? mm : r;
                if (!model.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Array) goto roles;
                foreach (var t in tables.EnumerateArray())
                {
                    if (t.TryGetProperty("measures", out var meas) && meas.ValueKind == JsonValueKind.Array)
                        foreach (var me in meas.EnumerateArray())
                        {
                            Add(outp, rel, "measure", me, "expression");
                            if (me.TryGetProperty("formatStringDefinition", out var fsd)) Add(outp, rel, "measure.formatString", fsd, "expression");
                            if (me.TryGetProperty("detailRowsDefinition", out var drd)) Add(outp, rel, "measure.detailRows", drd, "expression");
                        }
                    if (t.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
                        foreach (var c in cols.EnumerateArray())
                            if (c.TryGetProperty("type", out var ct) && ct.GetString() == "calculated")
                                Add(outp, rel, "calcColumn", c, "expression");
                    if (t.TryGetProperty("partitions", out var parts) && parts.ValueKind == JsonValueKind.Array)
                        foreach (var p in parts.EnumerateArray())
                            if (p.TryGetProperty("source", out var src) && src.TryGetProperty("type", out var st) && st.GetString() == "calculated")
                                Add(outp, rel, "calcTable", src, "expression");
                    if (t.TryGetProperty("defaultDetailRowsDefinition", out var ddrd)) Add(outp, rel, "table.detailRows", ddrd, "expression");
                    if (t.TryGetProperty("calculationGroup", out var cg) && cg.TryGetProperty("calculationItems", out var cis) && cis.ValueKind == JsonValueKind.Array)
                        foreach (var ci in cis.EnumerateArray())
                        {
                            Add(outp, rel, "calcItem", ci, "expression");
                            if (ci.TryGetProperty("formatStringDefinition", out var cfsd)) Add(outp, rel, "calcItem.formatString", cfsd, "expression");
                        }
                }
            roles:
                if (model.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
                    foreach (var role in roles.EnumerateArray())
                        if (role.TryGetProperty("tablePermissions", out var tps) && tps.ValueKind == JsonValueKind.Array)
                            foreach (var tp in tps.EnumerateArray())
                                Add(outp, rel, "rls", tp, "filterExpression");
            }
        }
        return outp;
    }

    // ---------- TMDL ----------
    // TMDL uses tab/space indentation; a DAX property is `measure|column|calculationItem NAME = <expr?>`.
    // If the remainder after '=' is empty, the expression is the following block indented deeper than
    // the property line, until a line at <= the property indent. This mirrors the TMDL block form.
    public static List<CorpusItem> HarvestTmdl(string root)
    {
        var outp = new List<CorpusItem>();
        foreach (var file in Walk(root, "*.tmdl"))
        {
            string[] lines;
            try { lines = File.ReadAllLines(file, Encoding.UTF8); }
            catch { continue; }
            var rel = file.Replace('\\', '/');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                string? kind =
                    trimmed.StartsWith("measure ") ? "measure" :
                    trimmed.StartsWith("column ") ? "calcColumn" :
                    trimmed.StartsWith("calculationItem ") ? "calcItem" : null;
                if (kind == null) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;                             // regular column (no '='): not DAX
                int indent = line.Length - trimmed.Length;
                var rest = line.Substring(eq + 1).Trim();

                // Triple-backtick fenced multi-line expression: body is between the fences.
                if (rest.StartsWith("```"))
                {
                    var fb = new StringBuilder();
                    int f = i + 1;
                    for (; f < lines.Length; f++)
                    {
                        if (lines[f].Trim() == "```") break;
                        if (fb.Length > 0) fb.Append('\n');
                        fb.Append(lines[f].TrimStart());
                    }
                    var fbody = fb.ToString();
                    if (fbody.Length > 0) outp.Add(new CorpusItem(rel, kind, fbody));
                    i = f;
                    continue;
                }
                if (rest.Length > 0)
                {
                    outp.Add(new CorpusItem(rel, kind, rest));    // inline expression
                    continue;
                }
                // block form: gather deeper-indented following lines, STOPPING at the first sibling TMDL
                // property (`key: value`) or child declaration — those are metadata, never DAX, and
                // swallowing them would fabricate parse failures.
                var sb = new StringBuilder();
                int j = i + 1;
                for (; j < lines.Length; j++)
                {
                    var bl = lines[j];
                    if (bl.Trim().Length == 0) { sb.Append('\n'); continue; }
                    int bi = bl.Length - bl.TrimStart().Length;
                    if (bi <= indent) break;
                    if (IsTmdlSibling(bl.TrimStart())) break;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(bl.Substring(Math.Min(indent + 1, bi)));  // strip one indent level, keep rest
                }
                var body = sb.ToString().Trim('\n');
                if (body.Length > 0) outp.Add(new CorpusItem(rel, kind, body));
                i = j - 1;
            }
        }
        return outp;
    }

    // A line that is TMDL metadata, not a DAX-expression continuation: a `key: value` property, or a
    // child declaration/keyword. DAX body lines never start this way.
    private static readonly System.Text.RegularExpressions.Regex TmdlProp =
        new(@"^[A-Za-z_][A-Za-z0-9_]*:", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex TmdlDecl =
        new(@"^(measure|column|table|partition|calculationItem|calculationGroup|hierarchy|level|role|annotation|changedProperty|extendedProperty|relationship|variation|kpi|detailRowsDefinition|formatStringDefinition|defaultDetailRowsDefinition|refreshPolicy|expression|ref|member|perspective)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    // bare boolean TMDL flags carry no colon (e.g. `isHidden`, `isKey`) — still metadata, not DAX.
    private static readonly System.Text.RegularExpressions.Regex TmdlFlag =
        new(@"^(isHidden|isDefault|isKey|isNullable|isAvailableInMDX|isAvailableInMdx|isPrivate|isProcessed|showAllValues|isUnique)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    private static bool IsTmdlSibling(string trimmed) => TmdlProp.IsMatch(trimmed) || TmdlDecl.IsMatch(trimmed) || TmdlFlag.IsMatch(trimmed);

    public static List<CorpusItem> HarvestAll()
    {
        var root = RepoRoot();
        var all = new List<CorpusItem>();
        all.AddRange(HarvestBim(root));
        all.AddRange(HarvestTmdl(root));
        return all;
    }
}

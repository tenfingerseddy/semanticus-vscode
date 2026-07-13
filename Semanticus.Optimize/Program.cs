using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

// Demo optimiser. Two modes:
//   dump  <bimPath> <outJson>                 -> full structure (tables/cols/measures + expressions) for authoring
//   apply <bimPath> <contentJson> <outFolder> -> apply authored content, save optimised model, print before/after grade
if (args.Length < 2) { Console.Error.WriteLine("usage: optimize dump <bim> <out.json>  |  apply <bim> <content.json> <outFolder>"); return 1; }
var mode = args[0].ToLowerInvariant();
var bim = args[1];

var sessions = new SessionManager();
var engine = new LocalEngine(sessions);
await engine.OpenAsync(bim);
var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

static string Ref(string kind, string table, string name) => name == null ? $"{kind}:{table}" : $"{kind}:{table}/{name}";

if (mode == "dump")
{
    var card = await engine.AiReadinessScanAsync();
    var dump = await sessions.Current.ReadAsync(m =>
    {
        var tables = new List<object>();
        foreach (var t in m.Tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var cols = t.Columns.Where(c => c.Type != ColumnType.RowNumber).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new
            {
                @ref = Ref("column", t.Name, c.Name),
                name = c.Name,
                dataType = c.DataType.ToString(),
                isHidden = c.IsHidden,
                isKey = SafeBool(() => c.IsKey),
                description = NullIfEmpty(c.Description),
            }).ToList();
            var meas = t.Measures.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => new
            {
                @ref = Ref("measure", t.Name, x.Name),
                name = x.Name,
                isHidden = x.IsHidden,
                formatString = NullIfEmpty(x.FormatString),
                description = NullIfEmpty(x.Description),
                expression = Collapse(x.Expression),
            }).ToList();
            tables.Add(new
            {
                @ref = Ref("table", t.Name, null),
                name = t.Name,
                type = t.GetType().Name,
                isHidden = t.IsHidden,
                description = NullIfEmpty(t.Description),
                columns = cols,
                measures = meas,
            });
        }
        var rels = m.Relationships.OfType<SingleColumnRelationship>().Select(r => new
        {
            from = $"{r.FromTable?.Name}[{r.FromColumn?.Name}]",
            to = $"{r.ToTable?.Name}[{r.ToColumn?.Name}]",
            active = r.IsActive,
            crossFilter = r.CrossFilteringBehavior.ToString(),
        }).ToList();
        return new { model = m.Name, tables, relationships = rels };
    });
    var outObj = new { grade = card.Grade, overall = card.Overall, findings = card.Findings.Count(), dump = (object)dump };
    File.WriteAllText(args[2], JsonSerializer.Serialize(outObj, jsonOpts), new System.Text.UTF8Encoding(false));
    Console.WriteLine($"dumped {((dynamic)dump).tables.Count} tables to {args[2]}  (grade {card.Grade} / {card.Overall})");
    return 0;
}

if (mode == "apply")
{
    if (args.Length < 4) { Console.Error.WriteLine("apply needs <bim> <content.json> <outFolder>"); return 1; }
    var content = JsonDocument.Parse(File.ReadAllText(args[2])).RootElement;
    var before = await engine.AiReadinessScanAsync();
    Console.WriteLine($"BEFORE: grade {before.Grade}  overall {before.Overall}  findings {before.Findings.Count()}");

    int desc = 0, syn = 0, hid = 0, unhid = 0, ren = 0, cat = 0;
    var renames = new List<(string r, string to)>();

    if (content.TryGetProperty("edits", out var edits))
    {
        foreach (var e in edits.EnumerateArray())
        {
            var r = e.GetProperty("ref").GetString();
            try
            {
                if (e.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String && d.GetString().Length > 0)
                { if ((await engine.SetDescriptionAsync(r, d.GetString(), "agent")).Changed) desc++; }

                if (e.TryGetProperty("synonyms", out var sy) && sy.ValueKind == JsonValueKind.Array)
                {
                    var terms = sy.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                    if (terms.Length > 0) { await engine.SetSynonymsAsync(r, terms, "en-US", "agent"); syn++; }
                }

                if (e.TryGetProperty("hide", out var h) && (h.ValueKind == JsonValueKind.True || h.ValueKind == JsonValueKind.False))
                {
                    var hv = h.GetBoolean();
                    if ((await engine.SetColumnMetadataAsync(r, hv, null, null, null, "agent")).Changed) { if (hv) hid++; else unhid++; }
                }

                if (e.TryGetProperty("dataCategory", out var dc) && dc.ValueKind == JsonValueKind.String && dc.GetString().Length > 0)
                { if ((await engine.SetColumnMetadataAsync(r, null, null, dc.GetString(), null, "agent")).Changed) cat++; }

                if (e.TryGetProperty("rename", out var rn) && rn.ValueKind == JsonValueKind.String && rn.GetString().Length > 0)
                    renames.Add((r, rn.GetString()));
            }
            catch (Exception ex) { Console.Error.WriteLine($"  edit failed [{r}]: {ex.Message}"); }
        }
    }

    // Renames last (a rename invalidates the old ref for any later edit).
    foreach (var (r, to) in renames)
    {
        try { await engine.RenameObjectAsync(r, to, "agent"); ren++; }
        catch (Exception ex) { Console.Error.WriteLine($"  rename failed [{r} -> {to}]: {ex.Message}"); }
    }

    if (content.TryGetProperty("aiInstructions", out var ai) && ai.ValueKind == JsonValueKind.String && ai.GetString().Length > 0)
    {
        var res = await engine.SetAiInstructionsAsync(ai.GetString(), "en-US", "agent");
        Console.WriteLine($"  AI instructions: {res.Length} chars, changed={res.Changed}");
    }

    Console.WriteLine($"applied: {desc} descriptions, {syn} synonym sets, {hid} hidden, {unhid} unhidden, {ren} renamed, {cat} data-categories");

    Directory.CreateDirectory(args[3]);
    var tmdlFolder = Path.Combine(args[3], "definition");
    var bimOut = Path.Combine(args[3], "Contoso.optimised.bim");
    await engine.SaveAsync(tmdlFolder, "tmdl");
    await engine.SaveAsync(bimOut, "bim");

    var after = await engine.AiReadinessScanAsync();
    Console.WriteLine($"AFTER:  grade {after.Grade}  overall {after.Overall}  findings {after.Findings.Count()}");
    Console.WriteLine("-- categories (after) --");
    foreach (var c in after.Categories.OrderByDescending(c => c.Weight))
        Console.WriteLine($"   {c.Category,-16} {c.Score,6:F1}  (viol {c.Violations}/{c.Applicable})");
    Console.WriteLine($"saved: {tmdlFolder}  +  {bimOut}");
    return 0;
}

Console.Error.WriteLine("unknown mode: " + mode);
return 1;

static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
static string Collapse(string s) { if (string.IsNullOrWhiteSpace(s)) return null; s = System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim(); return s.Length > 400 ? s.Substring(0, 400) + " …" : s; }
static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }

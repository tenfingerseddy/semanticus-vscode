using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    // A published model that was saved to a file keeps living in that file. Opening it again from the service gives the service copy, which lacks
    // whatever the file gained since (UAT 1.7 Free, D3). So an open says a saved copy holds more, and a save never writes over one that does.
    public sealed partial class LocalEngine
    {
        /// <summary>What a saved copy of the open published model holds that the open model does not: new or changed measures, calculated columns and tables.</summary>
        private sealed class SavedCopyLoss
        {
            public string Path;
            public DateTime Modified;
            public List<string> Names = new List<string>();
        }

        private static string Flat(string expression) => Regex.Replace(expression ?? "", @"\s+", "");

        private static string JsonText(JsonElement e) =>
            e.ValueKind == JsonValueKind.String ? e.GetString() : e.ValueKind == JsonValueKind.Array ? string.Join("\n", e.EnumerateArray().Select(x => x.ToString())) : "";

        /// <summary>The items a .bim holds: "table:T", "measure:T/M" and "column:T/C" (calculated), each with its flattened expression. Null when it cannot be read.</summary>
        private static Dictionary<string, string> HeldByFile(string bim)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(bim));
                var held = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!doc.RootElement.TryGetProperty("model", out var model) || !model.TryGetProperty("tables", out var tables)) return null;
                foreach (var t in tables.EnumerateArray())
                {
                    var tn = t.TryGetProperty("name", out var tnp) ? tnp.GetString() : null;
                    if (tn == null) continue;
                    held["table:" + tn] = "";
                    if (t.TryGetProperty("measures", out var ms))
                        foreach (var m in ms.EnumerateArray())
                            if (m.TryGetProperty("name", out var mn)) held["measure:" + tn + "/" + mn.GetString()] = Flat(m.TryGetProperty("expression", out var ex) ? JsonText(ex) : "");
                    if (t.TryGetProperty("columns", out var cs))
                        foreach (var c in cs.EnumerateArray())
                            if (c.TryGetProperty("type", out var ty) && ty.GetString() == "calculated" && c.TryGetProperty("name", out var cn))
                                held["column:" + tn + "/" + cn.GetString()] = Flat(c.TryGetProperty("expression", out var ex2) ? JsonText(ex2) : "");
                }
                return held;
            }
            catch { return null; }
        }

        private static Task<Dictionary<string, string>> HeldBySessionAsync(Session s) => s.RunAsync(() =>
        {
            var held = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in s.Model.Tables)
            {
                held["table:" + t.Name] = "";
                foreach (var m in t.Measures) held["measure:" + t.Name + "/" + m.Name] = Flat(m.Expression);
                foreach (var c in t.Columns.OfType<CalculatedColumn>()) held["column:" + t.Name + "/" + c.Name] = Flat(c.Expression);
            }
            return held;
        });

        private static string PlainName(string key)
        {
            var rest = key.Substring(key.IndexOf(':') + 1);
            return rest.Contains('/') ? rest.Substring(rest.IndexOf('/') + 1) : rest;
        }

        /// <summary>The saved copies in the lasting folder that are files of THIS published model and hold something the open model lacks, newest first. Never throws.</summary>
        private async Task<List<SavedCopyLoss>> SavedCopiesHoldingMoreAsync(Session s)
        {
            var found = new List<SavedCopyLoss>();
            try
            {
                if (s.LiveOrigin == null || string.IsNullOrEmpty(s.SourcePath)) return found;
                var identity = PaneIdentity(s, null);
                var folder = LastingFolderFor(s.SourcePath);
                if (identity == null || !Directory.Exists(folder)) return found;
                Dictionary<string, string> mine = null;
                foreach (var file in Directory.EnumerateFiles(folder, "*.bim", SearchOption.TopDirectoryOnly))
                {
                    if (!string.Equals(ModelHome.RecordedFor(file)?.Identity, identity, StringComparison.Ordinal)) continue;
                    var theirs = HeldByFile(file);
                    if (theirs == null) continue;
                    mine ??= await HeldBySessionAsync(s).ConfigureAwait(false);
                    var more = theirs.Where(kv => !mine.TryGetValue(kv.Key, out var have) || have != kv.Value).Select(kv => kv.Key).ToList();
                    if (more.Count == 0) continue;
                    var loss = new SavedCopyLoss { Path = Path.GetFullPath(file), Modified = File.GetLastWriteTimeUtc(file) };
                    // Measures first: they are what a person recognises. Tables and calculated columns follow.
                    loss.Names.AddRange(more.OrderBy(k => k.StartsWith("measure:", StringComparison.Ordinal) ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal).Select(PlainName));
                    found.Add(loss);
                }
            }
            catch { /* without the offer the open works as it always did */ }
            return found.OrderByDescending(f => f.Modified).ToList();
        }

        /// <summary>The offer attached to a live open: the newest saved copy that holds more than the service copy just opened, or null.</summary>
        private async Task<SavedCopyOffer> SavedCopyOfferAsync(Session s)
        {
            var best = (await SavedCopiesHoldingMoreAsync(s).ConfigureAwait(false)).FirstOrDefault();
            return best == null ? null : new SavedCopyOffer
            {
                Path = best.Path,
                Name = System.IO.Path.GetFileName(best.Path),
                Extra = best.Names.Count,
                Names = best.Names.Take(5).ToArray(),
            };
        }

        internal static string SavedCopyNote(SavedCopyOffer o) =>
            $"A saved copy of this model is at {o.Path}. It holds {o.Extra} {(o.Extra == 1 ? "thing" : "things")} this copy from the service does not have ({string.Join(", ", o.Names)}{(o.Extra > o.Names.Length ? ", and more" : "")}). Open the saved copy to keep that work.";
    }
}

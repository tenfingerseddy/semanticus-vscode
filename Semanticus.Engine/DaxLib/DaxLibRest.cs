using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// Minimal, anonymous, READ-ONLY client for the DaxLib package feed (https://api.daxlib.org/v1) — the "app store
    /// for DAX UDFs". Mirrors <see cref="FabricRest"/>'s conventions (per-call <c>NewClient</c> with timeout+BaseAddress,
    /// one <c>SendAsync</c> with bounded 429/Retry-After backoff, scrubbed errors, host-pin via BaseAddress + relative
    /// URLs) — <b>but carries NO Authorization header</b>: the feed is anonymous, so this can never violate Golden Rule
    /// #1 (no credential crosses a door). All HTTP primitives are <c>internal</c> and take an injected <see cref="HttpClient"/>
    /// so the offline tests can drive search / download / parse against a scripted <see cref="HttpMessageHandler"/> with
    /// no live network. The two hosts we ever reach are pinned: <see cref="ApiBase"/> and <see cref="RawBase"/> (the
    /// GitHub raw fallback for when the API is unavailable). Nothing here mutates the model — installation lives in the
    /// engine; once installed a package's functions are ordinary TOM UDFs with no runtime DaxLib dependency.
    /// </summary>
    internal static class DaxLibRest
    {
        internal const string ApiBase = "https://api.daxlib.org/v1/";
        // The package source-of-truth repo (MIT) — the API mirrors it. Used as a fallback when the API is unreachable.
        internal const string RawBase = "https://raw.githubusercontent.com/daxlib/daxlib/main/";

        // The package feed is UNTRUSTED (anyone can publish to daxlib.org). Two caps keep a hostile package from
        // exhausting engine memory (the engine is one process hosting the single live model session for BOTH doors):
        // (1) the WIRE bytes — MaxResponseContentBufferSize on the client (matches the codebase convention at
        // LocalEngine.cs's BPA-rules download); (2) the DECOMPRESSED bytes of each ZIP entry — a tiny compressed
        // ".daxpkg" can still deflate-bomb to GBs, so we read each entry through a hard byte cap (a download cap can't
        // catch that). A real UDF package's functions.tmdl is well under a MB; 16 MB is a generous ceiling.
        internal const long MaxDownloadBytes = 16_000_000;
        internal const int MaxEntryBytes = 16_000_000;
        private const int MaxZipEntries = 2000;

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        // Test-only seam: the offline tests set this to a factory that returns an HttpClient backed by a scripted
        // HttpMessageHandler, so the FULL install path (resolve → download → mutate model) runs with no live network.
        // Production never sets it → each call news its own host-pinned, size-capped client. (A documented seam beats a
        // DI rewrite of an otherwise-untestable network boundary; the branch is free.)
        internal static Func<string, HttpClient> ClientFactoryForTests;

        private static HttpClient NewClient(string baseUrl)
            => ClientFactoryForTests?.Invoke(baseUrl)
               ?? new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(100), MaxResponseContentBufferSize = MaxDownloadBytes };

        // ---- public entry points (own the HttpClient) ----

        internal static async Task<DaxLibPackage[]> SearchAsync(string text, int skip, int take, CancellationToken ct)
        {
            using var http = NewClient(ApiBase);
            return (await SearchAsync(http, text, skip, take, ct).ConfigureAwait(false)).ToArray();
        }

        internal static async Task<string[]> VersionsAsync(string id, CancellationToken ct)
        {
            using var http = NewClient(ApiBase);
            return await VersionsAsync(http, id, ct).ConfigureAwait(false);
        }

        internal static async Task<DaxLibMetadata> MetadataAsync(string id, string version, CancellationToken ct)
        {
            using var http = NewClient(ApiBase);
            return await MetadataAsync(http, id, version, ct).ConfigureAwait(false);
        }

        /// <summary>Fetch + parse a package's content (manifest + functions). Tries the API's <c>.daxpkg</c> (OPC/ZIP)
        /// first; on any failure falls back to the GitHub raw mirror (the two text files), so browse/install survive
        /// API churn. Throws (scrubbed) only when BOTH transports fail.</summary>
        internal static async Task<DaxLibContent> GetContentAsync(string id, string version, CancellationToken ct)
        {
            try
            {
                using var http = NewClient(ApiBase);
                var bytes = await DownloadPackageAsync(http, id, version, ct).ConfigureAwait(false);
                return ExtractFromDaxpkg(bytes);
            }
            catch (Exception apiEx)
            {
                try
                {
                    using var raw = NewClient(RawBase);
                    var manifest = await GetStringAsync(raw, RawPath(id, version, "manifest.daxlib"), ct).ConfigureAwait(false);
                    var functions = await GetStringAsync(raw, RawPath(id, version, "lib/functions.tmdl"), ct).ConfigureAwait(false);
                    return new DaxLibContent { ManifestJson = manifest, FunctionsTmdl = functions };
                }
                catch (Exception rawEx)
                {
                    throw new InvalidOperationException(
                        $"Could not fetch DaxLib package '{id}' {version} from the API or the GitHub mirror. "
                        + FabricRest.Scrub(apiEx.Message) + " / " + FabricRest.Scrub(rawEx.Message));
                }
            }
        }

        // raw.githubusercontent path layout: packages/{firstletter}/{id-lowercased}/{version}/{relPath}
        private static string RawPath(string id, string version, string relPath)
        {
            var lower = (id ?? "").ToLowerInvariant();
            var first = lower.Length > 0 ? lower.Substring(0, 1) : "_";
            return $"packages/{first}/{Uri.EscapeDataString(lower)}/{Uri.EscapeDataString(version ?? "")}/{relPath}";
        }

        // ---- testable HTTP primitives (injected HttpClient) ----

        internal static async Task<List<DaxLibPackage>> SearchAsync(HttpClient http, string text, int skip, int take, CancellationToken ct)
        {
            // The filter param has been reported as BOTH q= and text= — send both; the server honours whichever it knows
            // and ignores the other (unknown query params are not error-bearing), so we needn't probe with two round-trips.
            string FilterQs() => string.IsNullOrWhiteSpace(text)
                ? ""
                : "q=" + Uri.EscapeDataString(text) + "&text=" + Uri.EscapeDataString(text) + "&";

            var items = new List<DaxLibPackage>();
            if (take > 0)   // a single bounded page (caller-driven paging)
            {
                var url = $"query?{FilterQs()}skip={Math.Max(0, skip)}&take={Math.Min(take, 100)}";
                items.AddRange(ParseSearch(await GetStringAsync(http, url, ct).ConfigureAwait(false)));
                return items;
            }
            // No explicit page → loop take=100 until a page comes back empty (no total-count field on this feed).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var page = 0; page < 50; page++)   // bound: ~5000 packages — a runaway backstop (the feed is ~dozens today)
            {
                var url = $"query?{FilterQs()}skip={Math.Max(0, skip) + page * 100}&take=100";
                var batch = ParseSearch(await GetStringAsync(http, url, ct).ConfigureAwait(false));
                if (batch.Count == 0) break;
                // De-dupe defensively (a feed that ignores skip would otherwise loop forever returning page 0).
                var added = 0;
                foreach (var p in batch) if (p.Id != null && seen.Add(p.Id + "\0" + p.Version)) { items.Add(p); added++; }
                if (added == 0) break;
                if (batch.Count < 100) break;
            }
            return items;
        }

        internal static async Task<string[]> VersionsAsync(HttpClient http, string id, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A package id is required.");
            var body = await GetStringAsync(http, $"package/{Uri.EscapeDataString(id)}/versions.json", ct).ConfigureAwait(false);
            return ParseVersions(body);
        }

        internal static async Task<DaxLibMetadata> MetadataAsync(HttpClient http, string id, string version, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A package id is required.");
            if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("A version is required.");
            var body = await GetStringAsync(http, $"package/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}/metadata.json", ct).ConfigureAwait(false);
            return ParseMetadata(body);
        }

        internal static async Task<byte[]> DownloadPackageAsync(HttpClient http, string id, string version, CancellationToken ct)
        {
            var url = $"package/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}/{id}.{version}.daxpkg";
            using var resp = await SendAsync(http, HttpMethod.Get, url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(ParseError(await SafeBody(resp, ct).ConfigureAwait(false), (int)resp.StatusCode));
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        // ---- the one HTTP send: bounded 429 backoff (Retry-After), no auth header ----
        private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string url, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                var req = new HttpRequestMessage(method, url);
                var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode == 429 && attempt < 4)
                {
                    var ra = resp.Headers.RetryAfter?.Delta?.TotalSeconds;
                    var delay = Math.Min((int)(ra ?? Math.Pow(2, attempt + 1)), 60);
                    resp.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                    continue;
                }
                return resp;
            }
        }

        private static async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken ct)
        {
            using var resp = await SendAsync(http, HttpMethod.Get, url, ct).ConfigureAwait(false);
            var body = await SafeBody(resp, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(ParseError(body, (int)resp.StatusCode));
            return body;
        }

        private static async Task<string> SafeBody(HttpResponseMessage resp, CancellationToken ct)
            => resp.Content != null ? await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : "";

        // RFC7807 problem+json on errors ({type,title,status,detail}); degrade to a capped, scrubbed body otherwise.
        internal static string ParseError(string body, int status)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var title = Str(root, "title");
                var detail = Str(root, "detail");
                var s = $"DaxLib {status}";
                if (!string.IsNullOrEmpty(title)) s += " " + title;
                if (!string.IsNullOrEmpty(detail)) s += ": " + detail;
                return FabricRest.Scrub(s) + Hint(status);
            }
            catch
            {
                var b = body ?? "";
                if (b.Length > 300) b = b.Substring(0, 300) + "…";
                return FabricRest.Scrub($"DaxLib {status}: " + b) + Hint(status);
            }
        }

        private static string Hint(int status) => status switch
        {
            404 => "  [Not found — check the package id / version (try daxlib_versions).]",
            400 => "  [Bad request — 'take' must be 1..100.]",
            _ => string.Empty,
        };

        // ---- pure parsers (testable, no network) ----

        /// <summary>Open a <c>.daxpkg</c> (OPC/ZIP) and pull out the two text parts we need + the readme. Entry lookup
        /// is case-insensitive and tolerant of a leading slash / backslash; a missing functions file is a hard error
        /// (an install would have nothing to do).</summary>
        internal static DaxLibContent ExtractFromDaxpkg(byte[] zipBytes)
        {
            if (zipBytes == null || zipBytes.Length == 0) throw new InvalidOperationException("The downloaded package was empty.");
            using var ms = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxZipEntries)
                throw new InvalidOperationException($"DaxLib package has {zip.Entries.Count} entries (cap {MaxZipEntries}) — refusing to read.");
            string Read(params string[] names)
            {
                foreach (var n in names)
                {
                    var e = zip.Entries.FirstOrDefault(z =>
                        string.Equals(z.FullName.Replace('\\', '/').TrimStart('/'), n, StringComparison.OrdinalIgnoreCase));
                    if (e != null) return ReadEntryCapped(e);
                }
                return null;
            }
            var functions = Read("lib/functions.tmdl");
            if (string.IsNullOrWhiteSpace(functions))
                throw new InvalidOperationException("The package has no lib/functions.tmdl — nothing to install.");
            return new DaxLibContent
            {
                ManifestJson = Read("manifest.daxlib"),
                FunctionsTmdl = functions,
                Readme = Read("README.md", "readme.md"),
            };
        }

        // Read one ZIP entry's DECOMPRESSED text through a hard byte cap. We do NOT trust ZipArchiveEntry.Length (a
        // crafted central directory can understate it), so the real guard is counting bytes off the on-demand
        // DeflateStream and failing past the cap — this is what stops a deflate bomb (tiny compressed → GBs inflated).
        private static string ReadEntryCapped(ZipArchiveEntry e)
        {
            if (e.Length > MaxEntryBytes)   // honest fast-path reject (the streaming cap below catches a lying Length)
                throw new InvalidOperationException($"DaxLib package entry '{e.FullName}' is {e.Length} bytes (cap {MaxEntryBytes}) — refusing to read.");
            using var s = e.Open();
            using var buf = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = s.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > MaxEntryBytes)
                    throw new InvalidOperationException($"DaxLib package entry '{e.FullName}' exceeds the {MaxEntryBytes} byte cap — refusing to read (possible decompression bomb).");
                buf.Write(chunk, 0, read);
            }
            return Encoding.UTF8.GetString(buf.ToArray());
        }

        // The query feed returns { data: [ {id, version/latestVersion, description, authors[], tags[], downloads, projectUrl} ] }.
        // Field names are not contractually fixed (no OpenAPI) → read leniently, trying the likely aliases.
        internal static List<DaxLibPackage> ParseSearch(string body)
        {
            var list = new List<DaxLibPackage>();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); } catch { return list; }
            using (doc)
            {
                var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                        : doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d
                        : doc.RootElement.TryGetProperty("packages", out var p) && p.ValueKind == JsonValueKind.Array ? p
                        : default;
                if (arr.ValueKind != JsonValueKind.Array) return list;
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    list.Add(new DaxLibPackage
                    {
                        Id = Str(e, "id") ?? Str(e, "packageId") ?? Str(e, "name"),
                        Version = Str(e, "version") ?? Str(e, "latestVersion") ?? Str(e, "latestStableVersion"),
                        Description = Str(e, "description") ?? Str(e, "summary"),
                        Authors = StrArray(e, "authors", "author"),
                        Tags = StrArray(e, "tags", "tag"),
                        Downloads = Long(e, "downloads", "totalDownloads"),
                        ProjectUrl = Str(e, "projectUrl") ?? Str(e, "projectURL"),
                    });
                }
            }
            return list;
        }

        internal static string[] ParseVersions(string body)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); } catch { return Array.Empty<string>(); }
            using (doc)
            {
                var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                        : doc.RootElement.TryGetProperty("versions", out var v) && v.ValueKind == JsonValueKind.Array ? v
                        : default;
                if (arr.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
                return arr.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()
                               : e.ValueKind == JsonValueKind.Object ? Str(e, "version")
                               : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            }
        }

        internal static DaxLibMetadata ParseMetadata(string body)
        {
            var m = new DaxLibMetadata();
            try
            {
                using var doc = JsonDocument.Parse(body);
                var r = doc.RootElement;
                m.Id = Str(r, "id");
                m.Version = Str(r, "version");
                m.Description = Str(r, "description");
                m.Authors = StrArray(r, "authors", "author");
                m.Tags = StrArray(r, "tags", "tag");
                m.ReleaseNotes = Str(r, "releaseNotes");
                m.ProjectUrl = Str(r, "projectUrl") ?? Str(r, "projectURL");
                m.RepositoryUrl = Str(r, "repositoryUrl") ?? Str(r, "repositoryURL");
                m.Published = Str(r, "published") ?? Str(r, "publishedDate");
                m.Downloads = Long(r, "downloads", "totalDownloads");
            }
            catch { /* a malformed metadata.json degrades to a near-empty DTO rather than throwing */ }
            return m;
        }

        // manifest.daxlib = JSON; the dependencies[] field is real-world but NOT in the published 1.0.0 schema
        // (additionalProperties:false) → parse it LENIENTLY (and tolerate {id,version} | "id" | "id:version" shapes).
        internal static DaxLibManifest ParseManifest(string json)
        {
            var man = new DaxLibManifest { Dependencies = Array.Empty<DaxLibDependency>() };
            if (string.IsNullOrWhiteSpace(json)) return man;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                man.Id = Str(r, "id");
                man.Version = Str(r, "version");
                man.Description = Str(r, "description");
                man.Authors = StrArray(r, "authors", "author");
                var deps = new List<DaxLibDependency>();
                if (r.TryGetProperty("dependencies", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.Object)
                        {
                            var id = Str(e, "id") ?? Str(e, "packageId") ?? Str(e, "name");
                            if (!string.IsNullOrWhiteSpace(id)) deps.Add(new DaxLibDependency { Id = id, Version = Str(e, "version") });
                        }
                        else if (e.ValueKind == JsonValueKind.String)
                        {
                            var s = e.GetString();
                            var idx = s?.IndexOf(':') ?? -1;
                            if (idx > 0) deps.Add(new DaxLibDependency { Id = s.Substring(0, idx), Version = s.Substring(idx + 1) });
                            else if (!string.IsNullOrWhiteSpace(s)) deps.Add(new DaxLibDependency { Id = s, Version = null });
                        }
                    }
                }
                man.Dependencies = deps.ToArray();
            }
            catch { /* lenient: a manifest we can't parse yields no deps, not a failure (functions.tmdl is the payload) */ }
            return man;
        }

        /// <summary>Split a flattened <c>lib/functions.tmdl</c> into its individual UDFs. The file is bare
        /// <c>function 'Dotted.Name' = (params) =&gt; &lt;DAX&gt;</c> blocks (NO <c>createOrReplace</c> wrapper), each
        /// preceded by <c>///</c> doc lines (at column 0, TMDL-style) and followed by indented <c>annotation</c> lines.
        /// We key block boundaries off a column-0 <c>function</c>/<c>///</c> (so a <c>//</c>-comment or the word inside an
        /// indented DAX body never falsely splits a block), capture the full <c>(params)=&gt;body</c> lambda VERBATIM (to
        /// pass straight to create_function), the doc (→ description), and the DAXLIB_* annotations (→ provenance).</summary>
        internal static DaxLibFunction[] SplitFunctions(string tmdl)
        {
            var result = new List<DaxLibFunction>();
            if (string.IsNullOrWhiteSpace(tmdl)) return result.ToArray();
            var lines = tmdl.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            bool IsFunctionLine(string l) => l.StartsWith("function ", StringComparison.Ordinal) || l.StartsWith("function\t", StringComparison.Ordinal);
            bool IsDocLine(string l) => l.StartsWith("///", StringComparison.Ordinal);   // column-0 only (DAX bodies are indented)

            for (var i = 0; i < lines.Length; i++)
            {
                if (!IsFunctionLine(lines[i])) continue;

                // doc = the contiguous run of column-0 /// lines immediately above this function line
                var doc = new List<string>();
                for (var j = i - 1; j >= 0 && IsDocLine(lines[j]); j--) doc.Insert(0, lines[j].Substring(3).TrimStart());

                // name + any inline expression after the first '='
                var fnLine = lines[i];
                var eq = fnLine.IndexOf('=');
                var head = eq >= 0 ? fnLine.Substring("function".Length, eq - "function".Length) : fnLine.Substring("function".Length);
                var name = head.Trim().Trim('\'').Trim();
                var exprLines = new List<string>();
                var inline = eq >= 0 ? fnLine.Substring(eq + 1) : "";
                if (!string.IsNullOrWhiteSpace(inline)) exprLines.Add(inline.Trim());

                var annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var exprDone = false;
                var k = i + 1;
                for (; k < lines.Length; k++)
                {
                    var raw = lines[k];
                    if (IsFunctionLine(raw) || IsDocLine(raw)) break;   // next block starts (its doc is column-0 ///)
                    var t = raw.TrimStart();
                    if (IsAnnotationLine(t, out var akey, out var aval))
                    {
                        exprDone = true;
                        annotations[akey] = aval;
                        continue;
                    }
                    if (!exprDone) exprLines.Add(raw);   // keep original indentation; dedented below
                }
                i = k - 1;   // resume scanning at the terminator line

                if (string.IsNullOrWhiteSpace(name)) continue;
                result.Add(new DaxLibFunction
                {
                    Name = name,
                    Expression = Dedent(exprLines),
                    Description = doc.Count > 0 ? string.Join(" ", doc).Trim() : null,
                    Annotations = annotations,
                });
            }
            return result.ToArray();
        }

        // A genuine TMDL annotation line is `annotation <Identifier> = <value>`. Requiring the identifier + '=' means a
        // DAX body line that merely begins with the bare token "annotation" (e.g. an operand `annotation IN {…}`) can't
        // be mistaken for an annotation and silently truncate the captured lambda. (Real risk is low — daxlib UDFs are
        // generic/parameterized, not hardcoding a model object named `annotation` — but this removes the failure class.)
        private static bool IsAnnotationLine(string trimmed, out string key, out string value)
        {
            key = null; value = null;
            const string kw = "annotation";
            if (!trimmed.StartsWith(kw, StringComparison.Ordinal)) return false;
            var rest = trimmed.Substring(kw.Length);
            if (rest.Length == 0 || !(rest[0] == ' ' || rest[0] == '\t')) return false;   // must be `annotation ` (keyword + space), not `annotations`/`annotation(`
            rest = rest.TrimStart();
            var eq = rest.IndexOf('=');
            if (eq <= 0) return false;
            var name = rest.Substring(0, eq).Trim();
            if (name.Length == 0 || !name.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '.')) return false;
            key = name; value = rest.Substring(eq + 1).Trim();
            return true;
        }

        // Strip the common leading-whitespace from the expression continuation lines (TMDL block indent) so the stored
        // lambda is clean; trailing blank lines are trimmed. DAX is whitespace-insensitive so this is cosmetic-safe.
        private static string Dedent(List<string> lines)
        {
            // drop leading/trailing blank lines
            int start = 0, end = lines.Count - 1;
            while (start <= end && string.IsNullOrWhiteSpace(lines[start])) start++;
            while (end >= start && string.IsNullOrWhiteSpace(lines[end])) end--;
            if (start > end) return "";
            var slice = lines.GetRange(start, end - start + 1);
            var min = slice.Where(l => !string.IsNullOrWhiteSpace(l))
                           .Select(l => l.Length - l.TrimStart().Length)
                           .DefaultIfEmpty(0).Min();
            return string.Join("\n", slice.Select(l => l.Length >= min ? l.Substring(min) : l)).TrimEnd();
        }

        // ---- small JSON helpers (mirror FabricRest.Str) ----
        private static string Str(JsonElement e, string prop)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static long Long(JsonElement e, params string[] props)
        {
            foreach (var p in props)
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
                    return n;
            return 0;
        }

        private static string[] StrArray(JsonElement e, params string[] props)
        {
            foreach (var p in props)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out var v)) continue;
                if (v.ValueKind == JsonValueKind.Array)
                    return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToArray();
                if (v.ValueKind == JsonValueKind.String)   // a comma/semicolon-separated string
                    return v.GetString().Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            return Array.Empty<string>();
        }
    }

    // ---- internal value shapes (not the door DTOs — those live in Protocol.cs) ----
    internal sealed class DaxLibContent { public string ManifestJson; public string FunctionsTmdl; public string Readme; }
    internal sealed class DaxLibMetadata
    {
        public string Id; public string Version; public string Description;
        public string[] Authors; public string[] Tags; public string ReleaseNotes;
        public string ProjectUrl; public string RepositoryUrl; public string Published; public long Downloads;
    }
    internal sealed class DaxLibManifest
    {
        public string Id; public string Version; public string Description; public string[] Authors;
        public DaxLibDependency[] Dependencies;
    }
    internal sealed class DaxLibFunction
    {
        public string Name;                                  // dotted, unquoted (e.g. Sample.Add)
        public string Expression;                            // the FULL (params) => body lambda, verbatim
        public string Description;                            // from the /// doc (may be null)
        public Dictionary<string, string> Annotations;       // DAXLIB_PackageId / DAXLIB_PackageVersion / ...
    }
}

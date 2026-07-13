using System;
using System.IO;
using System.Text;

namespace Semanticus.Engine.Evidence
{
    /// <summary>
    /// Persists an <see cref="EvidenceDoc"/> as two files side by side: the canonical JSON (the record of truth,
    /// what Verify reads) and the rendered HTML (what a human opens or prints). Both are written atomically - to a
    /// temp file, then moved into place - so a crash never leaves a half-written certificate.
    ///
    /// Verification recomputes the content hash from the JSON on disk and compares it to the hash the file carries.
    /// It needs no secret and no key (the same property the Verified Edits store has): the artifact proves its own
    /// integrity to anyone, and a single changed character is caught with a plain-language reason.
    /// </summary>
    public static class EvidenceStore
    {
        public const string JsonSuffix = ".evidence.json";
        public const string HtmlSuffix = ".evidence.html";

        /// <summary>
        /// Seal <paramref name="doc"/> (validate, then compute and stamp its content hash) and write the canonical
        /// JSON and rendered HTML into <paramref name="directory"/>, named from the document id. Returns both paths.
        /// </summary>
        public static (string jsonPath, string htmlPath) Write(EvidenceDoc doc, string directory)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("a target directory is required", nameof(directory));

            var artifact = EvidenceArtifact.Seal(doc);

            Directory.CreateDirectory(directory);
            var baseName = SafeName(doc.Id);
            var jsonPath = Path.Combine(directory, baseName + JsonSuffix);
            var htmlPath = Path.Combine(directory, baseName + HtmlSuffix);

            // Transactional commit. Stage BOTH temp files first, then move them into place - the HTML first and the
            // .evidence.json LAST. The JSON is the record of truth Verify reads, so it is the commit point: until it
            // lands, the certificate is not committed. A crash after the HTML move but before the JSON move leaves an
            // HTML with no fresh JSON beside it - Verify never trusts an HTML on its own, and if a prior JSON is
            // still there its re-render check (below) rejects the stale pair rather than passing it off as verified.
            var jsonTmp = StageTemp(jsonPath, artifact.Json);
            var htmlTmp = StageTemp(htmlPath, artifact.Html);
            File.Move(htmlTmp, htmlPath, overwrite: true);
            File.Move(jsonTmp, jsonPath, overwrite: true);
            return (jsonPath, htmlPath);
        }

        /// <summary>
        /// Authenticate the JSON record at <paramref name="jsonPath"/>. Five gates, in order, each with its own
        /// DISTINCT plain-language reason so the story is never ambiguous: (1) the raw token stream carries no
        /// duplicate property names (last-wins shadowing); (2) the file reads as JSON with a string content
        /// signature; (3) the signature matches the hash recomputed over the schema projection (strict: unknown
        /// members anywhere, unknown section kinds, and type mismatches are all unreadable); (4) the record passes
        /// the full structural <see cref="EvidenceDoc.Validate"/> - a self-hashed non-document (for example a bare
        /// <c>{"contentHash":...}</c>) fails here even though its hash "matched"; (5) if a sibling .evidence.html
        /// exists it must byte-match a fresh deterministic re-render of the verified record. An ABSENT html sibling
        /// is NOT a failure - the .evidence.json is the record of truth; only a PRESENT-but-divergent report is
        /// rejected. Never throws for a bad file - the reason carries the story.
        /// </summary>
        public static bool Verify(string jsonPath, out string reason)
        {
            if (string.IsNullOrWhiteSpace(jsonPath)) { reason = "no record path was given"; return false; }
            if (!File.Exists(jsonPath)) { reason = "no record was found at that path"; return false; }

            string json;
            try { json = File.ReadAllText(jsonPath, Encoding.UTF8); }
            catch (Exception ex) { reason = "the record could not be read: " + ex.Message; return false; }

            // The duplicate-name preflight runs FIRST, before the signature is even extracted: deserialization is
            // last-wins, so a shadow property ("id":"evil" before the real id) would verify healthy while showing
            // a human (or a first-wins reader) the shadow value. No path below this line reads unpreflighted JSON.
            try
            {
                if (EvidenceHash.ContainsDuplicateProperties(json))
                {
                    reason = "the file contains duplicate JSON properties; a duplicated name can show a reader one value while the signature covers another";
                    return false;
                }
            }
            catch { reason = "the record is not readable as an evidence document"; return false; }

            System.Text.Json.Nodes.JsonObject node;
            try { node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject; }
            catch { reason = "the record is not readable as an evidence document"; return false; }
            if (node == null) { reason = "the record is not readable as an evidence document"; return false; }

            // Pull the stored signature WITHOUT assuming its type. A wrongly typed contentHash (an object, a number)
            // must yield a named reason, never a thrown InvalidOperationException.
            var hashNode = node[EvidenceHash.HashProperty];
            if (hashNode == null) { reason = "the record carries no content signature to check against"; return false; }
            string stored;
            try { stored = hashNode.GetValue<string>(); }
            catch { reason = "the content signature is not a string"; return false; }
            if (string.IsNullOrWhiteSpace(stored)) { reason = "the record carries no content signature to check against"; return false; }

            // Strictly deserialize to a document. This is also how the hash is recomputed (canonicalize through the
            // schema), so a structurally unreadable file - unknown member, unknown section kind, type mismatch - is
            // caught right here as unreadable rather than sliding through as a "mismatch".
            EvidenceDoc doc;
            try { doc = EvidenceHash.Deserialize(json); }
            catch { reason = "the record is not readable as an evidence document"; return false; }

            string recomputed;
            try { recomputed = EvidenceHash.Compute(doc); }
            catch (Exception ex) { reason = "the record could not be checked: " + ex.Message; return false; }

            if (!string.Equals(stored, recomputed, StringComparison.Ordinal))
            {
                reason = "the content does not match its recorded signature; it may have been changed after it was produced";
                return false;
            }

            // The hash matched, but a hash proves only self-consistency. Run the full structural validation so a
            // self-hashed object that is not a real evidence document is still rejected.
            try { doc.Validate(); }
            catch (EvidenceValidationException ex) { reason = "the record is not a valid evidence document: " + ex.Message; return false; }

            // The .evidence.json is the record of truth. If its sibling report exists it must be exactly the report
            // this record renders (Render is deterministic), or someone changed the human-facing file after the fact.
            // An absent sibling is fine - there is simply no report to contradict the record.
            var htmlPath = HtmlSiblingPath(jsonPath);
            if (File.Exists(htmlPath))
            {
                byte[] onDisk;
                try { onDisk = File.ReadAllBytes(htmlPath); }
                catch (Exception ex) { reason = "the report file could not be read: " + ex.Message; return false; }

                byte[] expected;
                try { expected = new UTF8Encoding(false).GetBytes(EvidenceRenderer.Render(doc)); }
                catch (Exception ex) { reason = "the report file could not be checked: " + ex.Message; return false; }

                if (!((ReadOnlySpan<byte>)onDisk).SequenceEqual(expected))
                {
                    reason = "the report file does not match the verified record; regenerate it";
                    return false;
                }
            }

            reason = "the content matches its recorded signature";
            return true;
        }

        // The report sibling of a .evidence.json record: same base name, .evidence.html suffix.
        private static string HtmlSiblingPath(string jsonPath)
            => jsonPath.EndsWith(JsonSuffix, StringComparison.OrdinalIgnoreCase)
                ? jsonPath.Substring(0, jsonPath.Length - JsonSuffix.Length) + HtmlSuffix
                : jsonPath + HtmlSuffix;

        private static string StageTemp(string path, string contents)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            var tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
            // Explicit UTF-8 without a BOM: the bytes must match what Render/CanonicalJson produced, so verification
            // and the golden-file pin stay byte-exact.
            File.WriteAllText(tmp, contents, new UTF8Encoding(false));
            return tmp;
        }

        // Keep a file name to the id's safe characters; anything else becomes '_'. Ids are normally guids, but a
        // hand-built id must never escape the target directory or trip the file system.
        private static string SafeName(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "evidence";
            var sb = new StringBuilder(id.Length);
            foreach (var c in id)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }
    }
}

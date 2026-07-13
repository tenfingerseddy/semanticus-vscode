using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Semanticus.Engine
{
    // ============================================================================================
    // CERTIFIED TOTALS (month-end close) — a LABELED, PERSISTED, IMMUTABLE, TAMPER-EVIDENT, model-bound
    // baseline of the figures a close signed off, each at its STATED CONTEXT. The honest, buildable version
    // of "locking" a close: DETECT drift against what was certified, never prevent it. A change that moves a
    // signed-off number is named; a refresh cannot be stopped.
    //
    // Correctness invariants (each answers a review finding):
    //  • IMMUTABLE (P1-3): a re-capture of the same (ref + context) is REFUSED — an edited figure can never
    //    silently overwrite the attested one and then report HELD. Re-certify under a new label.
    //  • TAMPER-EVIDENT (P1-3 / P2-D): each baseline carries a content hash taken the Evidence/ way — strict
    //    options (unknown members REJECTED), a raw-text duplicate-property preflight, canonicalize-then-hash
    //    over VerifiedEditsStore.Sha256. A file edited without recomputing it is a LOUD refusal on read.
    //  • DURABLE IDENTITY (P1-4 / P1-B): a figure records the measure's LineageTag; the tag must resolve to
    //    EXACTLY ONE measure whose NAME still matches, else not-checkable (a clone/impostor is never HELD).
    //  • CONTEXT-BOUND (P1-5 / P2-G): the baseline stamps the model it was captured on; a LOCAL source keys
    //    on the model fingerprint (its server/database are per-session), not a volatile GUID.
    //  • ONLY EVALUATED, STABLE FIGURES ARE CERTIFIED (P1-2 / P2-F): a figure that could not evaluate, or
    //    whose context uses a LITERAL volatile function, is not certified; one whose context references a
    //    measure is certified but marked StabilityProvable=false and re-checked as not-checkable.
    //  • NO SILENT RESET (P1-C): a corrupt store is preserved aside and the capture REFUSES — never overwritten.
    // ============================================================================================

    /// <summary>One captured cell within an entry's grid. Number XOR Text carry the value; both null = BLANK
    /// (so a blank that becomes 0 is caught).</summary>
    public sealed class CertifiedCell
    {
        public string Context { get; set; }
        public double? Number { get; set; }
        public string Text { get; set; }

        [JsonIgnore]
        public object Value => Number.HasValue ? (object)Number.Value : Text;
    }

    /// <summary>The replay-context stamp (P1-5): the model + connection identity a certified baseline is bound
    /// to. Effective-user / role is NOT observable in this connection abstraction (bearer token, no
    /// impersonation surface), so it is deliberately not claimed. For a LOCAL source (Power BI Desktop) the
    /// server/database are a per-session localhost:port + GUID, so they are left unstamped and identity rests
    /// on the model fingerprint (P2-G).</summary>
    public sealed class CertifiedContext
    {
        public string Server { get; set; }
        public string Database { get; set; }
        public string ModelName { get; set; }
        public string Fingerprint { get; set; }
        public string Culture { get; set; }

        /// <summary>The first differing BLOCKING element (plain language), or null when they all match — the elements
        /// that genuinely mean "a DIFFERENT model": server, database, model name, culture. An element not stamped at
        /// capture (null/empty) is never compared (a local source's absent server/database can't manufacture a false
        /// mismatch). The model SHAPE FINGERPRINT is deliberately NOT here (P2-1): an ordinary edit — adding an
        /// unrelated measure — changes the fingerprint, and blocking on it would BLIND the drift check on the SAME
        /// model and lie about why. Per-figure identity (LineageTag + name) already survives unrelated edits, so a
        /// shape change is only a NOTE (<see cref="ShapeChangeNote"/>), never a whole-baseline refusal.</summary>
        public string DiffFrom(CertifiedContext now)
        {
            if (now == null) return "the current connection identity could not be read";
            string Cmp(string label, string a, string b) =>
                string.IsNullOrEmpty(a) || string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? null
                    : $"the {label} differs (certified on '{a}', now '{b ?? "(none)"}')";
            return Cmp("server", Server, now.Server) ?? Cmp("database", Database, now.Database)
                ?? Cmp("model", ModelName, now.ModelName) ?? Cmp("model culture", Culture, now.Culture);
        }

        /// <summary>An INFORMATIONAL note when the model's shape changed since certification (both fingerprints stamped
        /// and differ) — expected after edits, never blocking. Null when unchanged or unstamped.</summary>
        public string ShapeChangeNote(CertifiedContext now)
            => !string.IsNullOrEmpty(Fingerprint) && now != null && !string.IsNullOrEmpty(now.Fingerprint)
               && !string.Equals(Fingerprint, now.Fingerprint, StringComparison.OrdinalIgnoreCase)
                ? "NOTE: the model changed shape since certification (fingerprint differs) — expected after edits; each figure is still checked by its own identity, so this does not block the check."
                : null;
    }

    /// <summary>One certified figure: a measure evaluated at its STATED CONTEXT (the per-entry filters). Scalar
    /// by design (GroupBy empty). No Error field — a figure that could not be evaluated is never certified.</summary>
    public sealed class CertifiedEntry
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string LineageTag { get; set; }
        public string[] GroupBy { get; set; } = Array.Empty<string>();
        public string[] Filters { get; set; } = Array.Empty<string>();
        public CertifiedCell[] Cells { get; set; } = Array.Empty<CertifiedCell>();
        public bool Truncated { get; set; }
        public bool StabilityProvable { get; set; } = true;   // false = the context references a measure (indirect volatility) → not-checkable on compare
    }

    public sealed class CertifiedBaseline
    {
        public string Label { get; set; }
        public string CapturedUtc { get; set; }
        public long Revision { get; set; }
        public double Tolerance { get; set; }                  // the float-noise relative tolerance HELD is judged within (self-describing record, P2-D)
        public CertifiedContext Context { get; set; }
        public List<CertifiedEntry> Entries { get; set; } = new List<CertifiedEntry>();
        public string ContentHash { get; set; }
    }

    public sealed class CertifiedBaselineFile
    {
        public List<CertifiedBaseline> Baselines { get; set; } = new List<CertifiedBaseline>();
    }

    public sealed class CertifiedUpsertResult
    {
        public CertifiedBaseline Baseline { get; set; }
        public string Refused { get; set; }
        public int Added { get; set; }
    }

    internal static class CertifiedStore
    {
        private static readonly object _lock = new object();

        /// <summary>The float-noise relative tolerance HELD is judged within (~1e-7, DaxBench.ValuesEqual's basis).
        /// Stored in every baseline + echoed in every compare so the record is self-describing (P2-D / P1-6).</summary>
        public const double Tolerance = 1e-7;

        // Strict, Evidence-style serialization: camelCase, nulls omitted, and UNKNOWN MEMBERS REJECTED so a smuggled
        // field cannot ride along a hashed record undetected (mirrors EvidenceHash.SerializerOptions).
        private static readonly JsonSerializerOptions Strict = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        private static readonly JsonSerializerOptions StrictIndented = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };

        // ---- strict read (mirrors the Evidence/ read chokepoint: duplicate-preflight → strict schema decode) ----

        /// <summary>Load the store. Missing/empty ⇒ empty, corrupt=false. Present but unreadable (bad bytes, malformed
        /// JSON, a DUPLICATE property, or an UNKNOWN member) ⇒ empty, corrupt=true — the caller fails LOUD. A transient
        /// IO/OS failure is NOT corruption: it PROPAGATES (a narrow catch), so a locked file never resets the store.</summary>
        public static CertifiedBaselineFile Load(string file, out bool corrupt)
        {
            corrupt = false;
            if (file == null || !File.Exists(file)) return new CertifiedBaselineFile();
            byte[] bytes = File.ReadAllBytes(file);   // an IO failure here PROPAGATES — it is not "corruption"
            if (bytes.Length == 0) return new CertifiedBaselineFile();
            try
            {
                var text = StrictUtf8(bytes);                                   // bad byte → DecoderFallbackException
                if (Evidence.EvidenceHash.ContainsDuplicateProperties(text))   // last-wins duplicate would shadow the hash basis
                    throw new FormatException("the certified store contains duplicate JSON properties");
                return JsonSerializer.Deserialize<CertifiedBaselineFile>(text, Strict) ?? new CertifiedBaselineFile();
            }
            // NARROW: only genuine "this text is not a valid certified store" shapes are corruption. Anything else propagates.
            catch (Exception e) when (e is JsonException or FormatException or System.Text.DecoderFallbackException)
            {
                corrupt = true;
                return new CertifiedBaselineFile();
            }
        }

        public static CertifiedBaseline Find(CertifiedBaselineFile f, string label)
            => f?.Baselines?.FirstOrDefault(b => string.Equals(b.Label, (label ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        // ---- tamper-evident hashing (Evidence/ canonicalize-then-hash, strict options, over VerifiedEditsStore.Sha256) ----

        public static string ComputeHash(CertifiedBaseline b)
        {
            var node = (JsonObject)JsonSerializer.SerializeToNode(b, Strict);
            node.Remove("contentHash");   // camelCase under the strict options
            return VerifiedEditsStore.Sha256(Evidence.EvidenceHash.Canonicalize(node));
        }

        public static bool HashMatches(CertifiedBaseline b)
            => b != null && !string.IsNullOrEmpty(b.ContentHash) && string.Equals(b.ContentHash, ComputeHash(b), StringComparison.Ordinal);

        // ---- immutable, context-bound, no-silent-reset upsert ----

        public static CertifiedUpsertResult Upsert(string file, string label, long revision, CertifiedContext context, IEnumerable<CertifiedEntry> newEntries)
        {
            lock (_lock)
            {
                var f = Load(file, out var corrupt);
                if (corrupt)
                {
                    // P1-C: NEVER overwrite a corrupt store. Preserve it aside (propagating any failure so we ABORT
                    // rather than clobber recoverable certifications for other labels), then REFUSE loudly.
                    PreserveCorruptAside(file);   // throws on failure → Upsert aborts, original untouched
                    return new CertifiedUpsertResult { Refused = "the certified store (.semanticus/certified-baselines.json) was unreadable and has been preserved aside as a '.corrupt-*' sibling — nothing was certified. Investigate that file, then re-run the capture to certify against a fresh store." };
                }
                label = (label ?? "").Trim();
                var entries = (newEntries ?? Enumerable.Empty<CertifiedEntry>()).ToList();
                var bl = f.Baselines.FirstOrDefault(b => string.Equals(b.Label, label, StringComparison.OrdinalIgnoreCase));

                if (bl != null)
                {
                    var cdiff = bl.Context?.DiffFrom(context);
                    if (cdiff != null)
                        return new CertifiedUpsertResult { Refused = $"'{label}' was certified on a different model: {cdiff}. A certified baseline is bound to the model and context it was captured on — use a new label for this model." };
                    if (!HashMatches(bl))
                        return new CertifiedUpsertResult { Refused = $"the certified baseline '{label}' on disk has been modified since capture (hash mismatch) — refusing to extend a tampered record. Investigate .semanticus/certified-baselines.json." };
                    foreach (var e in entries)
                        if (bl.Entries.Any(x => SameFigure(x, e)))
                            return new CertifiedUpsertResult { Refused = $"'{e.Ref}' is already certified under '{label}' at that context (a certification is immutable, so it cannot be silently overwritten). To re-certify, use a new label with a revision, e.g. \"{label} r2\"." };
                    bl.Entries.AddRange(entries);
                    bl.Revision = revision;
                    bl.CapturedUtc = DateTime.UtcNow.ToString("o");
                    bl.Tolerance = Tolerance;
                    bl.ContentHash = ComputeHash(bl);
                    Write(file, f);
                    return new CertifiedUpsertResult { Baseline = bl, Added = entries.Count };
                }

                bl = new CertifiedBaseline
                {
                    Label = label, Context = context, Revision = revision, Tolerance = Tolerance,
                    CapturedUtc = DateTime.UtcNow.ToString("o"), Entries = entries,
                };
                bl.ContentHash = ComputeHash(bl);
                f.Baselines.Add(bl);
                Write(file, f);
                return new CertifiedUpsertResult { Baseline = bl, Added = entries.Count };
            }
        }

        private static bool SameFigure(CertifiedEntry a, CertifiedEntry b)
            => string.Equals(a.Ref, b.Ref, StringComparison.OrdinalIgnoreCase)
               && (a.GroupBy ?? Array.Empty<string>()).SequenceEqual(b.GroupBy ?? Array.Empty<string>(), StringComparer.Ordinal)
               && (a.Filters ?? Array.Empty<string>()).SequenceEqual(b.Filters ?? Array.Empty<string>(), StringComparer.Ordinal);

        private static void Write(string file, CertifiedBaselineFile f)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var tmp = file + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(tmp, JsonSerializer.Serialize(f, StrictIndented));
            try { File.Move(tmp, file, overwrite: true); }
            catch { try { File.Delete(tmp); } catch { } throw; }
        }

        /// <summary>Move a corrupt store to a fresh '.corrupt-&lt;hex&gt;' sibling. Mirrors the shipped WriteCorruptAside
        /// contract (LocalEngine.Workflows.cs): a NEW name (never overwrites a prior backup) and any failure PROPAGATES
        /// so the caller aborts the mutation rather than destroying recoverable data (P1-C).</summary>
        private static void PreserveCorruptAside(string file)
        {
            if (!File.Exists(file)) return;
            var aside = file + ".corrupt-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.Move(file, aside);   // target is a fresh unique name (CreateNew semantics); a failure throws → abort
        }

        private static string StrictUtf8(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new System.Text.UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
            return new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }

        // ---- volatility (P2-F): LITERAL time-volatile functions block certification; a measure reference in the
        //      context cannot be proven non-volatile, so it is certified StabilityProvable=false (not-checkable later).

        private static readonly System.Text.RegularExpressions.Regex VolatileFn =
            new System.Text.RegularExpressions.Regex(@"\b(TODAY|NOW|UTCNOW|UTCTODAY|RAND|RANDBETWEEN)\s*\(",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The first LITERAL time-volatile / non-deterministic function found in a figure's context, or null.
        /// Text-only — it cannot see indirect volatility through a measure (that is StabilityProvable's job).</summary>
        public static string VolatileContext(IEnumerable<string> filters)
        {
            foreach (var f in filters ?? Enumerable.Empty<string>())
            {
                var m = VolatileFn.Match(f ?? "");
                if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            }
            return null;
        }

        // A bracketed reference: 'Table'[Col] / Table[Col] (table-qualified → a column) or bare [Name] (a measure, or a
        // column resolved by context). Returns the bare-bracket identifiers so the engine can check them against measure names.
        private static readonly System.Text.RegularExpressions.Regex BareBracket =
            new System.Text.RegularExpressions.Regex(@"(?<!\])\[([^\]]+)\]", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex QualifiedBracket =
            new System.Text.RegularExpressions.Regex(@"(?:'[^']+'|[A-Za-z_][A-Za-z0-9_]*)\s*\[([^\]]+)\]", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The identifiers referenced by a BARE [name] (not table-qualified) across the filters — the candidates
        /// for a measure reference the engine then confirms against the model's measure names (P2-F).</summary>
        public static IReadOnlyCollection<string> BareBracketRefs(IEnumerable<string> filters)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in filters ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                var qualified = new HashSet<int>();
                foreach (System.Text.RegularExpressions.Match qm in QualifiedBracket.Matches(f))
                    qualified.Add(qm.Groups[1].Index);
                foreach (System.Text.RegularExpressions.Match bm in BareBracket.Matches(f))
                    if (!qualified.Contains(bm.Groups[1].Index)) set.Add(bm.Groups[1].Value.Trim());
            }
            return set;
        }

        // ---- value <-> cell bridging ----

        public static CertifiedCell CellOf(string context, object v)
        {
            if (v == null) return new CertifiedCell { Context = context };
            return IsNumeric(v)
                ? new CertifiedCell { Context = context, Number = Convert.ToDouble(v) }
                : new CertifiedCell { Context = context, Text = DaxBench.Fmt(v) };
        }

        public static BaselineEntryState ToEntryState(CertifiedEntry e)
        {
            var rows = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var c in e.Cells ?? Array.Empty<CertifiedCell>()) rows[c.Context] = c.Value;
            return new BaselineEntryState { Ref = e.Ref, Name = e.Name, Rows = rows, Truncated = e.Truncated };
        }

        /// <summary>Normalize a figure's context for declaration↔capture matching (P1-A) by collapsing only INCIDENTAL
        /// token spacing — whitespace INSIDE a quoted literal (<c>'…'</c> table name, <c>"…"</c> string) or a bracketed
        /// identifier (<c>[…]</c> column/measure) is PRESERVED (P2-2), because it is significant: <c>[Month No]</c> is
        /// not <c>[MonthNo]</c> and <c>"North East"</c> is not <c>"NorthEast"</c>. A delimiter-aware walk (the
        /// SqlReadOnlyGuard idiom, not its code): drop whitespace only when outside every literal/identifier span.</summary>
        public static string NormalizeContext(IEnumerable<string> filters)
        {
            var joined = string.Join("&&", (filters ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
            var sb = new System.Text.StringBuilder(joined.Length);
            char quote = '\0';   // inside a '…' or "…" literal (the char is the open/close quote)
            bool inBracket = false;
            foreach (var c in joined)
            {
                if (quote != '\0') { sb.Append(c); if (c == quote) quote = '\0'; continue; }
                if (inBracket) { sb.Append(c); if (c == ']') inBracket = false; continue; }
                if (c == '\'' || c == '"') { quote = c; sb.Append(c); continue; }
                if (c == '[') { inBracket = true; sb.Append(c); continue; }
                if (char.IsWhiteSpace(c)) continue;   // incidental spacing outside any literal/identifier → drop
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool IsNumeric(object o) =>
            o is byte || o is sbyte || o is short || o is ushort || o is int || o is uint
            || o is long || o is ulong || o is float || o is double || o is decimal;
    }
}

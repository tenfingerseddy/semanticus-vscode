using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Semanticus.Engine.Evidence
{
    /// <summary>
    /// The content hash of an <see cref="EvidenceDoc"/> and the canonical JSON it is taken over. The digest itself
    /// reuses the Verified Edits hashing idiom (<see cref="VerifiedEditsStore.Sha256"/>) so the two trails speak the
    /// same format: an evidence <c>PrevHash</c> can point straight at a Verified Edits chain head. What differs is the
    /// BASIS - Verified Edits hashes a fixed length-prefixed field list; an evidence document hashes its whole shape,
    /// so we canonicalize the JSON (recursively sorted keys, no whitespace, the hash field removed) and hash that.
    ///
    /// Canonicalization is done at the JsonNode level, over the SAME text a value serializes to, so it is identical
    /// whether the source is a live document (Compute) or a file already on disk (HashOfJsonText / Verify). That is
    /// what lets verification recompute the hash with no secret and catch any change after the fact.
    /// </summary>
    public static class EvidenceHash
    {
        public const string HashProperty = "contentHash";

        /// <summary>The one serializer configuration for evidence: camelCase names, the five words as their token
        /// strings, and nulls omitted so an absent optional field and a null one canonicalize the same way.</summary>
        public static readonly JsonSerializerOptions SerializerOptions = BuildOptions();

        private static JsonSerializerOptions BuildOptions()
        {
            var o = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false,
                // GLOBAL, not per-type, on purpose: EVERY object in the document - the root, coverage, every
                // section, every row, and any DTO added later - must reject members the schema does not know. A
                // root-only attribute left every nested object open: an appended "approved":true inside a finding
                // would be silently dropped from the hashed projection and Verify would bless a visibly modified
                // file. The option covers the whole graph, so no nested type can ever opt back in silently and a
                // new DTO cannot be forgotten. The "$type" discriminator is polymorphism METADATA, not a member,
                // so it is unaffected.
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            };
            // The strict verdict converter: ONLY the five exact case-sensitive tokens round-trip, both directions.
            // JsonStringEnumConverter is deliberately NOT used - it reads names case-insensitively (and accepts
            // comma-combined flags syntax), so "verified" or "Verified, Broken" would hash as if canonical.
            o.Converters.Add(new StrictVerdictConverter());
            return o;
        }

        /// <summary>Serialize <paramref name="doc"/> to canonical JSON, INCLUDING its content hash. This is the exact
        /// text written to the .evidence.json file - deterministic and side-by-side verifiable.</summary>
        public static string CanonicalJson(EvidenceDoc doc)
        {
            var node = (JsonObject)JsonSerializer.SerializeToNode(doc, SerializerOptions);
            return Canonicalize(node);
        }

        /// <summary>The content hash of a document: SHA-256 over its canonical JSON with the hash field removed.</summary>
        public static string Compute(EvidenceDoc doc)
        {
            var node = (JsonObject)JsonSerializer.SerializeToNode(doc, SerializerOptions);
            node.Remove(HashProperty);
            return VerifiedEditsStore.Sha256(Canonicalize(node));
        }

        /// <summary>Strictly deserialize raw JSON to an <see cref="EvidenceDoc"/> using the library's own options
        /// (camelCase, the five-token enum, unknown members and unknown section discriminators rejected). Throws on
        /// anything that is not a genuine evidence document. This is the single strict-read chokepoint shared by the
        /// hash path and <see cref="EvidenceStore.Verify"/>.</summary>
        public static EvidenceDoc Deserialize(string json)
            => JsonSerializer.Deserialize<EvidenceDoc>(json, SerializerOptions)
               ?? throw new FormatException("the record is not an evidence document");

        /// <summary>Recompute the content hash from raw JSON text (a file on disk). The text is preflighted for
        /// duplicate property names first (see <see cref="ContainsDuplicateProperties"/>), then canonicalization
        /// goes THROUGH THE SCHEMA: the text is deserialized to an <see cref="EvidenceDoc"/> and hashed by
        /// <see cref="Compute"/>, so the disk path runs through the exact same serializer the live path does. That
        /// makes the digest immune to lexical differences a raw re-serialize would keep (1 vs 1.0 vs 1e0, explicit
        /// null vs absent, escaped vs literal strings): a disk doc and the same doc in memory always hash the same.
        /// The ONE ordering the schema path is NOT immune to is a section object whose "$type" discriminator has
        /// been moved out of first position (.NET 8 requires it first) - such a file THROWS here and is rejected
        /// fail-loud by Verify, rather than being silently accepted. Our own writer never produces that (the
        /// canonicalizer sorts "$type" first). Shared by <see cref="EvidenceStore.Verify"/>.</summary>
        public static string HashOfJsonText(string json)
        {
            if (ContainsDuplicateProperties(json))
                throw new FormatException("the file contains duplicate JSON properties");
            return Compute(Deserialize(json));
        }

        /// <summary>Preflight for disk JSON: true when ANY object in the text repeats a property name. JSON
        /// deserialization is last-wins, so a duplicated property would let a file show a human (or a first-wins
        /// reader) one value while the hash is computed over another - a shadow "id":"evil" before the real id
        /// would verify healthy. Both <see cref="EvidenceStore.Verify"/> and <see cref="HashOfJsonText"/> run this
        /// over the COMPLETE token stream BEFORE the signature is extracted or anything is deserialized, so no path
        /// ever reads unpreflighted disk JSON. One name-set per object depth; ordinal comparison (the same identity
        /// the canonicalizer sorts by). Throws <see cref="JsonException"/> on malformed JSON.</summary>
        public static bool ContainsDuplicateProperties(string json)
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json ?? ""));
            var scopes = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        scopes.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (!scopes.Peek().Add(reader.GetString()))
                            return true;
                        break;
                }
            }
            return false;
        }

        /// <summary>Render a node as canonical JSON: object keys sorted by ordinal, arrays kept in order, no
        /// whitespace, leaf values emitted as the exact JSON token the serializer produced. Deterministic and
        /// independent of the in-memory property order.</summary>
        public static string Canonicalize(JsonNode node)
        {
            var sb = new StringBuilder();
            Write(node, sb);
            return sb.ToString();
        }

        private static void Write(JsonNode node, StringBuilder sb)
        {
            switch (node)
            {
                case null:
                    sb.Append("null");
                    break;
                case JsonObject obj:
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteJsonString(kv.Key, sb);
                        sb.Append(':');
                        Write(kv.Value, sb);
                    }
                    sb.Append('}');
                    break;
                case JsonArray arr:
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var item in arr)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        Write(item, sb);
                    }
                    sb.Append(']');
                    break;
                default:
                    // A leaf value. ToJsonString gives the exact compact JSON token (escaped string, normalized
                    // number, true/false) - the same text on the live path and the re-parsed disk path.
                    sb.Append(node.ToJsonString());
                    break;
            }
        }

        // Encode a property key as a JSON string using the same escaping the value path uses, so keys and values
        // stay consistent. Keys are our own camelCase names plus verdict-count words - all safe, but encode anyway.
        private static void WriteJsonString(string key, StringBuilder sb)
            => sb.Append(JsonValue.Create(key).ToJsonString());
    }

    /// <summary>The wire form of a verdict: EXACTLY one of the five canonical tokens, ordinal and case-sensitive,
    /// symmetrical in both directions. Anything else - a different case, surrounding whitespace, comma-combined
    /// flags syntax, an integer, a non-string token - is rejected, so a non-canonical spelling can never enter a
    /// record and hash as if it were canonical.</summary>
    internal sealed class StrictVerdictConverter : JsonConverter<Verdict>
    {
        public override Verdict Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("a verdict must be a string holding one of the five verdict words");
            return reader.GetString() switch
            {
                "Unknown" => Verdict.Unknown,
                "Verified" => Verdict.Verified,
                "NeedsReview" => Verdict.NeedsReview,
                "Broken" => Verdict.Broken,
                "Overridden" => Verdict.Overridden,
                _ => throw new JsonException("a verdict must be exactly one of the five verdict words"),
            };
        }

        public override void Write(Utf8JsonWriter writer, Verdict value, JsonSerializerOptions options)
            => writer.WriteStringValue(value switch
            {
                Verdict.Unknown => "Unknown",
                Verdict.Verified => "Verified",
                Verdict.NeedsReview => "NeedsReview",
                Verdict.Broken => "Broken",
                Verdict.Overridden => "Overridden",
                _ => throw new JsonException("an undefined verdict cannot be written"),
            });
    }
}

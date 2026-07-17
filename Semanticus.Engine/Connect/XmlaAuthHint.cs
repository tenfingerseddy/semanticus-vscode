using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Semanticus.Engine
{
    /// <summary>
    /// The XMLA (Power BI / Fabric / AAS) sign-in story, in ONE place, so the Compare target-connect path AND the
    /// Model Interview probes tell the SAME teaching story instead of leaking AMO's raw "Authentication failed for
    /// all authenticators / RootActivityId" (which teaches nothing). Mirrors the SQL-side FabricAuthHint precedent in
    /// LocalEngine.cs and the harness tool-result contract: a failure must say what happened, why, and the next step.
    ///
    /// SECURITY: the RAW AMO/Azure exception text is never surfaced. Every value interpolated into a message is
    /// sanitized HERE (never raw caller input) — the endpoint is scrubbed of any connection-string secret and truncated
    /// to its address, and the auth mode is mapped to a fixed label set. A teaching throw attaches NO inner exception
    /// (a consumer serializing ToString() must not re-expose the AMO text); a non-auth failure is passed through
    /// <see cref="Scrub"/> and rethrown with context, so even the fallthrough message carries no secret.
    /// </summary>
    internal static class XmlaAuthHint
    {
        // The secret-marker WORDS that name a sign-in credential inside a "key=value" pair. Matched as whole WORDS of the
        // key name (split on camelCase/snake_case/dot/hyphen/space boundaries), never as a raw substring: the round-3
        // "\b" anchor let ClientSecret= / ApiKey= escape (CRITICAL 1), but the round-4 substring match then FALSE-fired on
        // innocent names — "Turkey" contains "key", "Secretariat" contains "secret", "Monkey" contains "key" (HIGH 2). A
        // word-level test catches "ClientSecret"->[client,secret] and "ApiKey"->[api,key] while leaving Turkey/Monkey/
        // Secretariat intact. "sig" IS a marker (round-8 CRITICAL 1): it is the SAS-URI signature key (?sig=... the token
        // that GRANTS an Azure Shared Access Signature), so a "?sig=SENTINEL" tail must be redacted, never persisted. The
        // old exclusion ("sig is a substring of assign/design") was for the obsolete substring era; matching is now EXACT
        // whole-word, so "sig" hits key "sig" but never "assign"->[assign] / "design"->[design]. "signature" is one too.
        // "user id" / "uid" carry no camel boundary, so the compacted-token check below names them too.
        private static readonly HashSet<string> SecretMarkers = new(StringComparer.OrdinalIgnoreCase)
        {
            "password", "pwd", "secret", "token", "accesstoken", "key", "apikey", "signature", "sig", "sas", "bearer", "uid", "userid",
        };

        // COMPACT credential compound key names — a lowercase run with NO camelCase / separator boundary for KeyWordSplit
        // to tokenize, so the word-level test below can't see the marker inside ("clientsecret" -> ["clientsecret"], not
        // [client, secret]). Round-5's tokenizer therefore let clientsecret= / accountkey= / sharedaccesssignature= pass as
        // innocent (CRITICAL 1). Fixed with a CURATED whole-key allow-list, deliberately NOT a raw suffix/contains match:
        // "turkey" and "monkey" END with "key" and "secretariat" STARTS with "secret", so any endsWith/contains rule would
        // false-fire on them — an EXACT token-joined match cannot. Extend this list for a new compact form; never loosen it
        // to suffix matching. (Separator/camel spellings like client_secret / ClientSecret are already caught word-level.)
        //
        // Round-7 (CRITICAL 1): the STANDARD lowercase Azure / connection-string credential family was still missing, so
        // sharedaccesskey= (the lowercase form of Azure's SharedAccessKey), subscriptionkey=, primarykey=, secondarykey=
        // all passed as innocent. The list below now covers the documented Azure credential key names (Storage / Service Bus
        // / Event Hubs / Cosmos / API Management / Cognitive Services / Functions). Being GENEROUS here is safe by design:
        // these are EXACT whole-key matches, so an addition can never false-positive on a Turkey/Monkey-class name.
        private static readonly HashSet<string> CompactSecretKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "clientsecret", "accountkey", "accountkeys", "accesskey", "secretkey", "privatekey", "sharedsecret",
            "sharedaccesssignature", "refreshtoken", "idtoken", "bearertoken", "clientassertion", "appsecret",
            "connectionpassword", "userid", "userpwd",
            // Standard Azure / connection-string credential key names (round-7): the lowercase compact spellings.
            "sharedaccesskey", "sharedaccesskeyname", "subscriptionkey", "primarykey", "secondarykey", "storagekey",
            "authkey", "masterkey", "functionkey", "hostkey", "signingkey", "encryptionkey", "clientkey",
            "sastoken", "saskey", "apisecret",
        };

        // Split a connection-string key NAME into its constituent words: on separators (_ . - space) AND on camelCase
        // boundaries (a lowercase/digit followed by an uppercase, and an acronym-to-word transition "SASToken"->SAS|Token).
        // The three alternatives are disjoint zero-or-fixed-width assertions -> linear, no ReDoS.
        private static readonly Regex KeyWordSplit =
            new(@"[_.\-\s]+|(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

        // True when a KEY NAME is (contains, as a whole word) a credential marker. Word-level, not substring (HIGH 2):
        // "ClientSecret"/[client,secret] and "ApiKey"/[api,key] hit; "Turkey"/[turkey], "Monkey"/[monkey],
        // "Secretariat"/[secretariat] do NOT. "User ID"/"user_id" compact to "userid" and are named that way.
        private static bool IsSecretKeyName(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            var words = KeyWordSplit.Split(key).Where(w => w.Length > 0).ToArray();
            if (words.Any(SecretMarkers.Contains)) return true;
            // Compact compound (no boundary to tokenize): match the WHOLE token-joined name against the curated list —
            // catches "clientsecret" / "accountkey" / "sharedaccesssignature" and "user id"/"user_id" (join -> "userid")
            // while a raw suffix match on the same join would wrongly hit "turkey"/"monkey". Exact whole-key only.
            return CompactSecretKeys.Contains(string.Concat(words));
        }

        // The UNESCAPED inner content of a fully-quoted value ("…" or '…'), quotes stripped and the quote-dialect escapes
        // resolved, or null when the value is not fully quoted. A credential can hide INSIDE an innocent key's quoted value
        // (Metadata="x?access_token=…"): the outer key=value match consumes the whole quoted pair without inspecting the
        // content (CRITICAL 1). Round-6 recursed into the RAW inner, but the doubled-quote (""SENTINEL"") and backslash
        // (\"SENTINEL\") escapes then terminated the nested key=value match EARLY and leaked the value (round-7 CRITICAL 1).
        // Unescape BOTH dialects first so the nested scan sees the real value. The unescaped string is never longer than the
        // input and drops the two outer quotes, so it is strictly shorter — the recursion that scans it always terminates.
        private static string QuotedInner(string val)
        {
            if (string.IsNullOrEmpty(val) || val.Length < 2) return null;
            var q = val[0];
            if ((q != '"' && q != '\'') || val[val.Length - 1] != q) return null;
            return Unescape(val.Substring(1, val.Length - 2), q);
        }

        // Resolve a quoted value's inner escapes for BOTH dialects a credential could arrive in: the connection-string
        // doubled-quote form ("" -> ", '' -> ') and the C/JSON backslash form (\" -> ", \\ -> \, \x -> x). Scanning the raw
        // escaped text let the escapes prematurely close the nested key=value match, leaking the credential (CRITICAL 1);
        // resolving them first makes the nested scan see access_token="SENTINEL" as one value. Never lengthens the input.
        private static string Unescape(string inner, char q)
        {
            if (string.IsNullOrEmpty(inner)) return inner;
            var sb = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                var c = inner[i];
                if (c == q && i + 1 < inner.Length && inner[i + 1] == q) { sb.Append(q); i++; continue; }   // "" -> " (or '' -> ')
                if (c == '\\' && i + 1 < inner.Length) { sb.Append(inner[i + 1]); i++; continue; }           // \" -> ", \\ -> \, \x -> x
                sb.Append(c);
            }
            return sb.ToString();
        }

        // One key=value shape: a leading delimiter (start / ';' / whitespace / '?' / '&' / ','), a key name (a
        // [A-Za-z0-9_.\-] run, or the two-word "user id"), then the value. The VALUE consumes a fully QUOTED string
        // ("…" or '…', with connection-string doubled-quote escaping ""/'' AND C/JSON backslash escaping \"/\') OR an
        // unquoted run up to the next delimiter. Round-4 gaps closed: the value stopped at the FIRST space, so a quoted
        // Password value with embedded spaces leaked everything past the first space (CRITICAL 1a); and because the key is
        // now a GENERIC run (word-level secret matching happens in IsSecretKeyName), the unquoted value MUST stop at EVERY
        // key-start delimiter [;&\s?,] — else an innocent key's value would swallow a following secret. Round-7: the quoted
        // alternatives also recognise a BACKSLASH-escaped quote (\") so an innocent key whose quoted value hides a secret
        // behind \"…\" is captured whole (else the outer match ended at the first \" and leaked the tail, CRITICAL 1). The
        // three quoted-body alternatives start on disjoint characters (non-quote-non-backslash / a quote / a backslash) ->
        // linear, no ReDoS.
        private static readonly Regex KeyValueRx = new(
            @"(?i)(?<lead>^|[;\s&?,])(?<key>user\s*id|[A-Za-z0-9_.\-]+)\s*=\s*(?<val>""(?:[^""\\]|""""|\\.)*""|'(?:[^'\\]|''|\\.)*'|[^;&\s?,]*)",
            RegexOptions.Compiled);
        // A JWT: three base64url segments, split by dots OR whitespace (logs sometimes break a token across spaces),
        // starting with the ubiquitous "eyJ" header. The value char class and the [.\s] separator are disjoint, so
        // there is no ambiguous overlap -> linear, no ReDoS.
        private static readonly Regex JwtRx =
            new(@"eyJ[A-Za-z0-9_-]{5,}[.\s]+[A-Za-z0-9_-]{5,}[.\s]+[A-Za-z0-9_-]{2,}", RegexOptions.Compiled);
        // An OPAQUE bearer/token: a secret keyword, then whitespace, then a high-entropy run that is neither key=value
        // nor a JWT (e.g. "bearer token abc123def456"). Redacts the value, keeps the keyword. The value class excludes
        // whitespace, so `\s+` then the run cannot overlap -> linear, no ReDoS.
        private static readonly Regex OpaqueTokenRx =
            new(@"(?i)\b(bearer|access[_-]?token|api[_-]?key|token|secret)\s+([A-Za-z0-9._~+/=-]{12,})", RegexOptions.Compiled);

        /// <summary>Remove any secret material from an arbitrary string before it is surfaced: key=value secrets (in
        /// ';' / '&amp;' / whitespace-delimited forms, incl. composite key names like ClientSecret=/ApiKey= and QUOTED
        /// values with embedded spaces), JWTs (dot- OR whitespace-split), and opaque bearer/token values. Used to scrub
        /// any error message that escapes into a teaching throw, by <see cref="SafeEndpoint"/>, and by
        /// <see cref="ScrubStringProperties"/> at every serializer.</summary>
        public static string Scrub(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // Redact the VALUE of a secret-named key=value; leave an innocent key (Turkey=, Data Source=) untouched. For an
            // innocent key whose value is QUOTED, a secret can still hide INSIDE the quotes (Metadata="x?access_token=…") —
            // recurse into the UNESCAPED content (QuotedInner resolves ""/\" escapes) and, if anything scrubbed, redact the
            // WHOLE outer value. Round-6 re-wrapped only the scrubbed inner, but re-escaping a partially scrubbed value is
            // fragile and the escapes had already leaked the tail (round-7 CRITICAL 1); full redaction is the honest
            // fail-closed move. The unescaped inner is strictly shorter than the input, so the recursion terminates.
            s = KeyValueRx.Replace(s, m =>
            {
                var lead = m.Groups["lead"].Value;
                var key = m.Groups["key"].Value;
                var val = m.Groups["val"].Value;
                if (IsSecretKeyName(key)) return lead + key + "=***";   // keep delimiter + key name, redact the whole value
                var inner = QuotedInner(val);
                if (inner != null && !string.Equals(Scrub(inner), inner, StringComparison.Ordinal))
                    return lead + key + "=***";   // a credential hid inside the quoted value — redact the WHOLE value
                return m.Value;
            });
            s = JwtRx.Replace(s, "***");
            s = OpaqueTokenRx.Replace(s, "$1 ***");
            return s;
        }

        /// <summary>True when a string carries a sign-in secret we must never store or echo — a Password= / ClientSecret=
        /// / ApiKey= / User ID= / token= component of a pasted connection string, or a bare JWT. Word-level key matching
        /// (HIGH 2) so a legitimate "Turkey=2026" / "Monkey=Business" is NOT mistaken for a secret. Used at every
        /// persistence boundary so a credential-bearing endpoint is refused (or stripped) before it can reach
        /// connections.json / the history.</summary>
        public static bool ContainsSecret(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (Match m in KeyValueRx.Matches(s))
            {
                if (IsSecretKeyName(m.Groups["key"].Value)) return true;
                var inner = QuotedInner(m.Groups["val"].Value);   // a secret nested in an innocent key's quoted value
                if (inner != null && ContainsSecret(inner)) return true;
            }
            return JwtRx.IsMatch(s);
        }

        /// <summary>Index at which a credential-bearing key=value tail begins (at its leading delimiter), or -1 when the
        /// string carries no such tail. Word-level key matching (HIGH 2) so a catalog like "Turkey=2026" is NOT cut. Lets
        /// a dataset-name sanitizer cut ONLY a real "…;Password=x" tail while leaving a bare '&amp;' / ';' / '?' inside a
        /// legitimate name ("Sales &amp; Marketing") untouched (HIGH 3).</summary>
        public static int SuspectKeyValueIndex(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            foreach (Match m in KeyValueRx.Matches(s))
            {
                if (IsSecretKeyName(m.Groups["key"].Value)) return m.Index;
                var inner = QuotedInner(m.Groups["val"].Value);   // cut from the outer key whose quoted value hides a secret
                if (inner != null && ContainsSecret(inner)) return m.Index;
            }
            return -1;
        }

        /// <summary>Serialization-boundary chokepoint: redact any secret material from EVERY writable string property of
        /// a record/event before it is persisted, so a secret pasted into ANY field (modelName, a secret-shaped tenant,
        /// detail, or a field added later) can never reach disk verbatim. Reflection over the few small records is cheap;
        /// one scrub here beats per-field sanitization that a NEW field silently escapes (CRITICAL 1).</summary>
        public static void ScrubStringProperties(object obj)
        {
            if (obj == null) return;
            foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType != typeof(string) || !p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                var v = (string)p.GetValue(obj);
                if (string.IsNullOrEmpty(v)) continue;
                var scrubbed = Scrub(v);
                if (!string.Equals(scrubbed, v, StringComparison.Ordinal)) p.SetValue(obj, scrubbed);
            }
        }

        /// <summary>Does an error string read like an XMLA/Entra auth rejection? Deliberately NARROW: only tokens that
        /// a Power BI / Fabric / Azure AS sign-in failure emits, NOT generic words a DAX ERROR() or a query failure can
        /// carry ("unauthorized", "login failed", "token expired") — those would misclassify a genuine DAX error as a
        /// sign-in problem. Used for the INTERVIEW typed-flag classification (LiveConnection.Execute), where the input
        /// may be a DAX query error. The AMO cold reject is "…failed for all authenticators"; Entra adds AADSTS;
        /// Azure.Identity adds CredentialUnavailable / "please run az login" / "interactive authentication…".</summary>
        public static bool LooksLikeAuthFailure(string e) =>
            !string.IsNullOrEmpty(e) && (
                e.Contains("for all authenticators", StringComparison.OrdinalIgnoreCase)
                || e.Contains("AADSTS", StringComparison.OrdinalIgnoreCase)
                || e.Contains("CredentialUnavailable", StringComparison.OrdinalIgnoreCase)
                || e.Contains("interactive authentication is not supported", StringComparison.OrdinalIgnoreCase)
                || e.Contains("az login", StringComparison.OrdinalIgnoreCase)
                || e.Contains("AuthenticationFailedException", StringComparison.OrdinalIgnoreCase)
                || e.Contains("no accounts were found in the cache", StringComparison.OrdinalIgnoreCase));

        /// <summary>Does an EXCEPTION (the connection/snapshot path, never a DAX query) read like an auth failure? This
        /// is deliberately BROADER than <see cref="LooksLikeAuthFailure"/>: it decides whether to run the interactive
        /// fallback, so it must also catch a bare 401 / a lifetime expiry / a "login failed" that lacks the narrow
        /// tokens — otherwise a genuine auth failure would fall through and its raw message would leak. Safe to be broad
        /// here because the input is a connection/AMO/Azure exception, not user DAX. Walks the inner-exception chain and
        /// checks the Azure.Identity / MSAL credential exception TYPES directly.</summary>
        public static bool IsAuthFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var tn = e.GetType().Name;
                if (tn.Contains("AuthenticationFailed", StringComparison.OrdinalIgnoreCase)
                    || tn.Contains("CredentialUnavailable", StringComparison.OrdinalIgnoreCase)
                    || tn.Contains("Msal", StringComparison.OrdinalIgnoreCase)
                    || tn.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
                    return true;
                var m = e.Message ?? "";
                if (LooksLikeAuthFailure(m)
                    || m.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("login failed", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("could not login", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("401", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                    || (m.Contains("token", StringComparison.OrdinalIgnoreCase) && m.Contains("expired", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        /// <summary>True when an exception is a genuine USER cancellation of an interactive sign-in (the account picker
        /// was dismissed), as opposed to a wrong-tenant / expired-code / no-consent REJECTION that also yields no chosen
        /// account. Walks the inner chain: an <see cref="OperationCanceledException"/>, or MSAL's user-cancel shape
        /// (a Msal* exception whose text says the sign-in was canceled). Deliberately NARROW so a real auth rejection is
        /// never mislabeled "you cancelled" — the reason a forced sign-in that failed reads honestly (MED 4).</summary>
        public static bool IsUserCancellation(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is OperationCanceledException) return true;
                var m = e.Message ?? "";
                if (e.GetType().Name.Contains("Msal", StringComparison.OrdinalIgnoreCase)
                    && (m.Contains("authentication_canceled", StringComparison.OrdinalIgnoreCase)
                        || m.Contains("user_cancel", StringComparison.OrdinalIgnoreCase)
                        || m.Contains("cancel", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        /// <summary>A short, SAFE category for a sign-in that failed before an account was chosen but was NOT a user
        /// cancellation (MED 4) — surfaced in the failed-open detail so a wrong-tenant / expired-code failure is not
        /// mislabeled a cancellation. Derived from fixed buckets (never the raw exception text), so it carries no secret.</summary>
        public static string SignInFailureCategory(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message ?? "";
                if (m.Contains("AADSTS", StringComparison.OrdinalIgnoreCase)) return "the identity provider rejected the sign-in";
                if (m.Contains("consent", StringComparison.OrdinalIgnoreCase)) return "consent was not granted";
                if (m.Contains("expired", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("code_expired", StringComparison.OrdinalIgnoreCase))
                    return "the sign-in timed out or expired";
            }
            return "the sign-in did not complete";
        }

        /// <summary>The interactive sign-in a HUMAN falls back to when a cached / delegated token is missing or the
        /// endpoint rejected it. Keep the user's explicit interactive choice (devicecode stays devicecode); azcli /
        /// blank fall to a browser sign-in under the first-party Power BI client the XMLA endpoint accepts. Non-delegated
        /// modes (serviceprincipal / caller-supplied token) have NO interactive fallback — a browser can't stand in for
        /// a configured principal — so those return null (go straight to the no-interactive teaching error).</summary>
        public static string InteractiveFallbackMode(string mode) => (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "devicecode" => "devicecode",
            "serviceprincipal" or "sp" or "token" => null,
            _ => "interactive",
        };

        /// <summary>What "no explicit tenant" resolves to per mode — so the echoed hint never claims "az" for a non-az
        /// mode. Mirrors LocalEngine.DefaultTenantLabel (the SQL side); kept here so the XMLA copy is self-contained.</summary>
        public static string DefaultTenantLabel(string mode) => SafeMode(mode) switch
        {
            "azcli" => "az default",
            "interactive" or "devicecode" => "your sign-in's home tenant",
            "serviceprincipal" => "the service principal's tenant",
            _ => "default",
        };

        // ---- Sanitizers: the ONLY values that reach a message string --------------------------------------------
        /// <summary>Map any caller auth-mode string to a fixed, safe label (never echo raw caller input — a malformed
        /// authMode could carry secret material).</summary>
        public static string SafeMode(string mode) => (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "azcli" => "azcli",
            "interactive" or "entra" or "entramfa" or "mfa" => "interactive",
            "devicecode" => "devicecode",
            "serviceprincipal" or "sp" => "serviceprincipal",
            "token" => "token",
            _ => "unknown",
        };

        // A real Entra tenant is a GUID, a DNS domain (contoso.onmicrosoft.com / contoso.com), or a well-known Entra
        // alias (common / organizations / consumers). The domain form REQUIRES a dot, so a bare word or free text never
        // qualifies. Anchored ^…$ so a secret-shaped value with an '=' / space / quote can never match.
        private static readonly Regex TenantGuidRx =
            new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.Compiled);
        private static readonly Regex TenantDomainRx =
            new(@"^(?=.{1,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.Compiled);

        /// <summary>Validate a caller-supplied tenant at the intake boundary and return it (lowercased) ONLY when it is a
        /// real tenant shape — a GUID, a DNS domain, or common/organizations/consumers; otherwise null. A tenant is
        /// result-bound (it reaches <c>SessionInfo.CurrentTenant</c> via <c>LiveOrigin</c>) and in token mode is not even
        /// used for auth, so a secret-shaped tenant must be scrubbed HERE before it can surface or persist (CRITICAL 1b).
        /// Scrub-to-null (not throw): a bogus tenant simply falls back to the default-tenant sign-in, never a leak.</summary>
        public static string SafeTenant(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return null;
            var t = tenantId.Trim().ToLowerInvariant();
            if (t is "common" or "organizations" or "consumers") return t;
            return TenantGuidRx.IsMatch(t) || TenantDomainRx.IsMatch(t) ? t : null;
        }

        /// <summary>The op-INTAKE gate for an explicit tenant (HIGH 3). Tells OMITTED apart from INVALID, which
        /// <see cref="SafeTenant"/> alone cannot: it scrubs both to null, so a typo ("contoso" instead of
        /// contoso.onmicrosoft.com) silently became a DEFAULT/home-tenant sign-in — leaving the registry's stored tenant
        /// inconsistent with the account the sign-in actually used. Omitted (null/blank) still returns null (use the
        /// account's home tenant, unchanged). A NON-EMPTY value that is not a real tenant shape now REFUSES the operation
        /// with a SANITIZED message that never echoes the input (a secret-shaped tenant must not surface).</summary>
        public static string RequireTenant(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return null;   // omitted → the account's default/home tenant, unchanged
            var safe = SafeTenant(tenantId);
            if (safe == null)
                throw new ArgumentException("The tenant does not look like a tenant id or domain. Pass a directory (tenant) id or a domain like contoso.onmicrosoft.com.");
            return safe;
        }

        /// <summary>Scrub an endpoint before it is shown: drop any connection-string tail (everything from the first
        /// ';', where a Password=/token could hide) and redact any residual secret key=value. An XMLA endpoint is a
        /// bare URL with no ';', so a legitimate value is unchanged; a hostile/malformed one cannot leak a secret.</summary>
        public static string SafeEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return "(none)";
            var e = endpoint.Trim();
            var semi = e.IndexOf(';');
            if (semi >= 0) e = e.Substring(0, semi);   // drop any connection-string tail wholesale
            return Scrub(e);                           // redact any residual key=value secret or bare JWT in the URL/query
        }

        /// <summary>Now that IsAuthFailure is broad, the LiveConnection.Execute typed flag needs the NARROW classifier —
        /// exposed as this alias so the intent reads clearly at the call site (a DAX error must not set AuthFailed).</summary>
        public static bool IsQueryAuthFailure(string errorMessage)
        {
            return LooksLikeAuthFailure(errorMessage);
        }

        // ---- Teaching copy (plain language, no em dashes, no colorful emoji) ------------------------------------
        /// <summary>Surfaced to a HUMAN after an interactive sign-in WAS attempted (interactive / device-code fallback)
        /// and the target still would not authenticate. Says what happened, the most likely why (wrong tenant), and a
        /// next step the Connect panel can actually fulfil (sign in with an account that has access).</summary>
        public static string TeachingErrorAfterSignIn(string endpoint, string mode, string tenantLabel) =>
            "Not signed in to this workspace. Semanticus tried to sign you in but the sign-in did not complete. "
            + "The workspace may be in a different tenant than the account you signed in with. Run Connect and sign in "
            + "with an account that has access to this workspace, then run the compare again. "
            + $"(auth mode: {SafeMode(mode)}, tenant: {tenantLabel}, endpoint: {SafeEndpoint(endpoint)})";

        /// <summary>Surfaced to a HUMAN for a mode that does NOT sign in interactively (serviceprincipal / caller token):
        /// no browser was opened, so it must not claim one was. Names the real fix for each.</summary>
        public static string TeachingErrorNoInteractive(string endpoint, string mode, string tenantLabel)
        {
            var m = SafeMode(mode);
            var fix = m == "serviceprincipal"
                ? "Check the service principal has access to this workspace and that its tenant is correct, or switch to Interactive sign-in in Connect, "
                : "Supply a valid access token, or switch to Interactive sign-in in Connect, ";
            return "Not signed in to this workspace, and the '" + m + "' auth mode does not open an interactive sign-in. "
                + fix + "then run the compare again. "
                + $"(auth mode: {m}, tenant: {tenantLabel}, endpoint: {SafeEndpoint(endpoint)})";
        }

        /// <summary>Surfaced to an AGENT-origin caller: an interactive sign-in is a human action, so the engine never
        /// pops a browser on an agent's behalf. Names the exact op the human must run (accountable-refusal tone).</summary>
        public static string AgentRefusal(string endpoint, string mode, string tenantLabel) =>
            "Not signed in to this workspace, and a compare against a published model cannot open an interactive sign-in "
            + "on an agent's behalf. Ask the user to run Connect (connect_xmla) to sign in to " + SafeEndpoint(endpoint) + ", "
            + $"then run this compare again. (auth mode: {SafeMode(mode)}, tenant: {tenantLabel})";

        /// <summary>The concise one-liner for a Model Interview probe cell: replaces the raw AMO auth string so one
        /// sign-in story heals the Interview pane too. Reads as the "Couldn't check". No em dash (copy rule).</summary>
        public static string ProbeHint() =>
            "couldn't check: not signed in to the live model. Run Connect to sign in, then re-run this check.";
    }
}

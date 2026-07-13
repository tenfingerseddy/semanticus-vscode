using System;
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
        // Redact any secret-bearing key=value (password / token / bearer / signature / key) before a value is shown.
        // Value runs to the next connection-string (';'), query ('&'), or whitespace delimiter.
        private static readonly Regex SecretRx =
            new(@"(?i)\b(password|pwd|access[_-]?token|token|bearer|sig|signature|key|secret)\s*=\s*[^;&\s]*", RegexOptions.Compiled);
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
        /// ';' / '&amp;' / whitespace-delimited forms), JWTs (dot- OR whitespace-split), and opaque bearer/token values.
        /// Used to scrub any error message that escapes into a teaching throw, and by <see cref="SafeEndpoint"/>.</summary>
        public static string Scrub(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = SecretRx.Replace(s, "$1=***");
            s = JwtRx.Replace(s, "***");
            s = OpaqueTokenRx.Replace(s, "$1 ***");
            return s;
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

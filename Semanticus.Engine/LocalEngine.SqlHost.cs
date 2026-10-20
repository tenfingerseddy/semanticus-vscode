using System;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Asks the person one plain question about a SQL host and returns the answer: true yes, false no, null when nobody
        /// could be asked. The RPC server sets it to the attached VS Code window; a test sets it to a fake. Null means no window.</summary>
        internal Func<string, Task<bool?>> SqlHostAsker { get; set; }

        private readonly SemaphoreSlim _sqlHostAskGate = new SemaphoreSlim(1, 1);

        internal static string SqlHostQuestion(string host) => $"This model reads from {host}. Send your sign-in there?";

        /// <summary>
        /// The one rule for a sign-in token sent to a server that did not come from a saved source: a model's partitions, a plan's
        /// source, a spec. Null means go on. Otherwise the plain words to show, and no token was minted.
        /// A host passes with no question when a saved source or the person's yes already covers it, or it is a Microsoft SQL host
        /// (SqlHostTrust). A server the person typed into a call of their own passes too. Any other host asks the person once:
        /// yes saves it as a source (the store SqlHostTrust reads), no stops with a plain line and is not asked again this session.
        /// </summary>
        internal async Task<string> SqlHostRefusalAsync(string server, string database, string authMode, string tenantId, string origin, bool typedByPerson)
        {
            if (string.IsNullOrWhiteSpace(server)) return null;   // nothing to send a token to; the caller's own check says what is missing
            if (SqlHostTrust.MayReceiveToken(server)) return null;
            // A call made inside the assistant's door is the assistant's, whatever origin it carries.
            if (typedByPerson && !DeployGuard.IsAgent(origin) && !GateContext.InAssistantCall) { SqlHostTrust.TrustForSession(server); return null; }

            var host = SqlHostTrust.HostOf(server);
            var declined = $"You chose not to send your sign-in to {host}, so nothing was sent. To use it, save it as a SQL source in Connections.";
            if (SqlHostTrust.WasDeclinedThisSession(server)) return declined;

            await _sqlHostAskGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Another call may have been asked while this one waited: take its answer.
                if (SqlHostTrust.MayReceiveToken(server)) return null;
                if (SqlHostTrust.WasDeclinedThisSession(server)) return declined;
                var asker = SqlHostAsker ?? _sessions.Bus.SqlHostAsk;
                bool? answer = null;
                if (asker != null)
                {
                    try { answer = await asker(host).ConfigureAwait(false); }
                    catch { answer = null; }
                }
                if (answer == null)
                    return $"This model reads from {host}, and no sign-in was sent there because nobody could be asked. Open Semanticus and try again, or save {host} as a SQL source in Connections.";
                if (answer == false) { SqlHostTrust.DeclineForSession(server); return declined; }

                SqlHostTrust.TrustForSession(server);
                SaveTrustedHost(host, database, authMode, tenantId);
                return null;
            }
            finally { _sqlHostAskGate.Release(); }
        }

        // Yes keeps the host as a saved SQL source, so the next session needs no question. The session set already covers this one,
        // so a source that cannot be saved (the limit, an odd name) never turns a yes into a refusal.
        private static void SaveTrustedHost(string host, string database, string authMode, string tenantId)
        {
            try
            {
                var mode = string.IsNullOrWhiteSpace(authMode) ? "azcli" : authMode.Trim().ToLowerInvariant();
                if (Array.IndexOf(SqlSourceRegistry.AuthModes, mode) < 0) mode = "azcli";
                var db = string.IsNullOrWhiteSpace(database) ? "master" : database.Trim();
                try { SqlSourceRegistry.Save(null, host, host, db, mode, tenantId); }
                catch (ArgumentException) { SqlSourceRegistry.Save(null, host + " (" + db + ")", host, db, mode, tenantId); }
            }
            catch { /* the yes still stands for this session */ }
        }
    }
}

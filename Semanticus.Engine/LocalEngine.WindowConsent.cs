using System;
using System.Threading.Tasks;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine
{
    // hero19 D11: the person pressed OK on the Pro box in this window, the assistant then opened the live model, and every Ask said
    // "Allow" again. The OK is remembered for this window (this process, in memory only), and a live model the assistant opens
    // gets the same answer for the same fields, so the person is not asked a second time. The fields and the version are the
    // ones the person agreed to; nothing wider. A model the person opens themselves, a file, or a window that never had an OK are unchanged.
    public sealed partial class LocalEngine
    {
        private readonly object _windowGrantGate = new object();
        private (string[] Fields, int Version)? _windowGrant;

        private void NoteWindowGrant(string[] fields, int version)
        {
            lock (_windowGrantGate) _windowGrant = (fields == null ? Array.Empty<string>() : (string[])fields.Clone(), version);
        }

        // The person's no always wins: taking permission back ends the carried OK for the rest of this window.
        private void ForgetWindowGrant()
        {
            lock (_windowGrantGate) _windowGrant = null;
        }

        /// <summary>The assistant just opened a live model. When the person said OK in this window, say the same for this model.
        /// Never throws: a failure here leaves the Allow button, which is what it was before.</summary>
        internal async Task ExtendWindowConsentAsync(string origin)
        {
            try
            {
                (string[] Fields, int Version)? grant;
                lock (_windowGrantGate) grant = _windowGrant;
                if (grant == null || !IsAssistantOrigin(origin)) return;
                if (_sessions.Current?.LiveOrigin == null) return;   // a live model only: a file is a model the person chose
                var scope = await DecisionConsentScopeAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(scope)) return;
                var record = Decisions.ReadConsent(scope);
                if (record.WithdrawnUtc.HasValue) return;   // the person said no to this model: the carried OK never overrules it
                if (DescribeConsent(record, scope).Current) return;
                DecisionConsentStore.Grant(scope, grant.Value.Fields, grant.Value.Version, DateTimeOffset.UtcNow, "human");
                PublishConsentChanged("human", "permission given");
                TellThePlanConsentMoved();
            }
            catch { /* the Allow button is still there */ }
        }
    }
}

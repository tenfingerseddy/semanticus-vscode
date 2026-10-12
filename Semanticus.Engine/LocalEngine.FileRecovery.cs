using System.Threading.Tasks;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        public async Task<bool> ResolveFileRecoveryAsync(string expectedSession, bool restore, string origin)
        {
            var session = _sessions.Require();
            var resolved = await session.ResolveFileRecoveryAsync(expectedSession, restore, origin).ConfigureAwait(false);
            if (resolved) PublishSaveNotification(session, restore ? "recovery" : "discard_recovery", origin: origin);
            return resolved;
        }
    }
}

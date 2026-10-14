// Connect another assistant (1.5, lane connect-any): the engine side of the picker and of the connect_assistant tool.
// The file edits live in Connect/AssistantConnector.cs; this part only names the folder, the licence and the machine.

using System;
using System.Linq;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Test seam: a machine that is any OS with any home. Null on every real path.</summary>
        internal Func<ConnectHost> AssistantHostForTest;
        /// <summary>Test seam: where the user licence file is. Null on every real path (Entitlement.LicenseEntitlement.LicenseFilePath()).</summary>
        internal Func<string> LicenseFileForTest;

        private ConnectHost AssistantHost() => AssistantHostForTest?.Invoke()
            ?? ConnectHost.Real(string.IsNullOrWhiteSpace(_workspaceDir) ? System.IO.Directory.GetCurrentDirectory() : _workspaceDir);

        private string UserLicenseFile() => LicenseFileForTest?.Invoke() ?? Entitlement.LicenseEntitlement.LicenseFilePath();

        private bool AnyAssistantConnected()
        {
            try { return AssistantConnector.List(AssistantHost()).Any(a => a.State == "connected" || a.State == "outdated"); }
            catch (Exception) { return false; }
        }

        public Task<ConnectableAssistant[]> ListAssistantsAsync() => Task.FromResult(AssistantConnector.List(AssistantHost()));

        public Task<AssistantConnectResult> ConnectAssistantAsync(string assistant, string origin = "human")
        {
            try { GuardOutsideWrite(origin); } catch (Exception ex) { return Task.FromException<AssistantConnectResult>(ex); }
            var result = AssistantConnector.Connect(assistant, AssistantHost());
            if (!result.Ok) return Task.FromResult(result);
            // An engine another assistant starts, with VS Code closed, is handed no licence: it reads the user licence file.
            // So a Pro licence goes there now, and the result says so.
            switch (Entitlement.LicenseMirror.EnsureSaved(_entitlement, UserLicenseFile()))
            {
                case Entitlement.LicenseMirror.Outcome.Saved:
                    result.LicenceNote = "Your Pro licence is saved in " + UserLicenseFile() + " so " + result.Name + " runs as Pro too.";
                    break;
                case Entitlement.LicenseMirror.Outcome.Failed:
                    result.LicenceNote = "Semanticus could not save your Pro licence for " + result.Name + ", so it may start on the free tier when VS Code is closed.";
                    break;
            }
            return Task.FromResult(result);
        }
    }
}

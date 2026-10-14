using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Azure.Identity;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    // Two defects the verifier found in m15/signin-auto: a press's first sign-in must reach the silent twin, and a press must not leak into the model thread.
    [Collection("restore-root")]
    public sealed class SignInPressCarryTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _prevPersistDir;
        private readonly string _prevCacheName;

        public SignInPressCarryTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-vprobe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = _root;
            _prevPersistDir = EntraToken.PersistDirOverride;
            _prevCacheName = EntraToken.TokenCacheNameOverride;
            EntraToken.PersistDirOverride = Path.Combine(_root, "auth");
            EntraToken.TokenCacheNameOverride = "semanticus-vprobe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public void Dispose()
        {
            InteractiveSignIn.AllowedForTests(false);
            EntraToken.PersistDirOverride = _prevPersistDir;
            EntraToken.TokenCacheNameOverride = _prevCacheName;
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        // Item 3. A first sign-in inside a press (Open live, Connect) captures the account on the credential it was called on
        // (Azure.Identity sets the browser credential's Record in AuthenticateAsync). That credential is then kept in the
        // session's person slot (SeedLiveCredentialSlots) for later live reads. A later AUTOMATIC read goes to the silent twin,
        // which was built before the capture and holds no account, so it cannot renew silently and says "Press Connect".
        [Fact]
        public void Probe_a_first_sign_in_by_a_press_is_usable_by_the_silent_twin_for_later_automatic_reads()
        {
            InteractiveSignIn.AllowedForTests(true);
            Azure.Core.TokenCredential cred;
            using (SignInPress.Press()) cred = EntraToken.BuildCredential("interactive", null);   // empty store: a first sign-in
            var recordProp = cred.GetType().BaseType.GetProperty("Record", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(recordProp != null, "no Record property");
            var json = "{\"username\":\"person@contoso.example\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\"uid.utid\",\"tenantId\":\"utid\",\"clientId\":\"cid\",\"version\":\"1.0\"}";
            var captured = AuthenticationRecord.Deserialize(new MemoryStream(Encoding.UTF8.GetBytes(json)));
            recordProp.SetValue(cred, captured);   // what AuthenticateAsync does on the press credential
            var silentField = cred.GetType().GetField("_silent", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(silentField != null, "no _silent field");
            Assert.Null(recordProp.GetValue(silentField.GetValue(cred)));   // built before the capture: no account yet
            // The next automatic use syncs the twin first; the test calls the same step directly so it needs no network.
            cred.GetType().GetMethod("SyncSilentTwin", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(cred, null);
            Assert.NotNull(recordProp.GetValue(silentField.GetValue(cred)));   // the twin now holds the captured account
        }

        // Item 4 (latent). The model thread is a plain Thread started by whoever builds the session; Thread.Start copies the
        // caller's ExecutionContext, so a session built inside a press runs EVERY later work item (agent and automatic ones too)
        // with SignInPress.Pressed == true.
        [Fact]
        public async Task Probe_a_model_thread_built_inside_a_press_does_not_keep_the_press()
        {
            ModelDispatcher d;
            using (SignInPress.Press()) d = new ModelDispatcher();
            using (d)
            {
                Assert.False(SignInPress.Pressed);
                var seen = await d.RunAsync(() => SignInPress.Pressed);
                Assert.False(seen);
            }
        }
    }
}

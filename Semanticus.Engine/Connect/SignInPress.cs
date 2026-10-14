// The one gate that knows whether a person pressed a button (hero22 H1, 14 Oct).
//
// WHY. After an assistant created a new model, the person's Studio engine opened a real Microsoft sign-in window over VS Code
// though the saved destination used a service principal. Some read the page started by itself (a compare, a workspace list, a
// refresh) was built with the person's origin, and a person's origin was allowed to open a window. Origin says WHO the call is
// for. It never said WHETHER a person pressed anything.
//
// RULE. Nothing automatic opens a window. A browser sign-in may begin only while a person's press is in force: Connect, Open,
// Sign in, or a Publish that writes. Every read the page or the engine starts by itself (page loads, compares, lists,
// scans, probes, refreshes, whatever follows an assistant's action) uses the sign-in that is saved and, when that fails,
// says in plain words to press Connect.
//
// HOW. EntraToken builds every browser credential through PressGatedBrowserCredential, which asks <see cref="Pressed"/> each time
// it is used, so a credential cached by an earlier press can never open a window for a later automatic read. The person's door
// (PersonFence) opens the press for the calls that are presses. Nothing else does, so a new call is automatic until it is
// named here. The default is "not pressed" so a forgotten path fails closed, with a sentence, and never opens a window.

using System;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Semanticus.Engine
{
    public static class SignInPress
    {
        private static readonly AsyncLocal<bool> InForce = new();

        /// <summary>The wire name of the top-level request member a page's button sets (<c>pressed: true</c>). Nothing else on a request opens a press.</summary>
        public const string WireName = "pressed";

        /// <summary>True only inside a person's press. False for everything automatic.</summary>
        public static bool Pressed => InForce.Value;

        /// <summary>What a person reads when an automatic read needed a sign-in window. It names the button.</summary>
        public const string NotPressedText = "The saved sign-in did not work, and Semanticus does not open a sign-in window by itself. Press Sign in to sign in again, then try again.";

        /// <summary>End any press this context inherited (a thread started inside a press).</summary>
        internal static void Clear() => InForce.Value = false;

        /// <summary>Open a press for the code that runs inside it (and every await that follows). Dispose ends it.</summary>
        public static IDisposable Press()
        {
            var before = InForce.Value;
            InForce.Value = true;
            return new Ender(before);
        }

        private sealed class Ender : IDisposable
        {
            private readonly bool _before;
            public Ender(bool before) { _before = before; }
            public void Dispose() => InForce.Value = _before;
        }

        // The calls a person's button makes. Connect and Open sign in on purpose; Test connection signs in to a SQL source.
        private static readonly string[] NamedPresses = { "ConnectXmlaAsync", "OpenLiveAsync", "TestSqlSourceAsync" };

        /// <summary>Whether this call on the person's door is a press: a named sign-in button, or any call that writes (a bool named commit that
        /// is true: Publish, Rollback, a pipeline deploy). A preview, a compare, a list and a refresh are not.</summary>
        internal static bool IsPress(MethodInfo method, object[] args)
        {
            if (NamedPresses.Contains(method.Name, StringComparer.Ordinal)) return true;
            var ps = method.GetParameters();
            for (var i = 0; i < ps.Length && args != null && i < args.Length; i++)
                if (ps[i].ParameterType == typeof(bool) && string.Equals(ps[i].Name, "commit", StringComparison.Ordinal) && args[i] is true) return true;
            return false;
        }
    }
}

// The words a person reads when the start cannot read the source (plan statement check 4, P4). The start form printed the engine's own text:
// "Fabric SQL introspection failed: could not get a sign-in token. ... (authMode=azcli, tenant=az default) (Please run 'az login' ...)".
// Each sentence here follows docs/product-copy-style.md: what failed, what to do, and nothing about how the engine works. The raw reason is not shown;
// it stays on the exception's inner exception and in the engine's log, where a developer looks for it.

namespace Semanticus.Engine.StartPlan
{
    internal static class StartSourceCopy
    {
        private const string Nothing = " Nothing was created.";
        private const string WhatToDo = " Sign in with az login in a terminal, or choose Sign in in a browser for this source in Connections, then try again."
                                       + " Use the Microsoft account that can open this database.";

        /// <summary>No sign-in could be made at all (no one is signed in, the sign-in was closed, the account is not found).</summary>
        public static string NoSignIn() => "Semanticus could not sign in to read this database." + Nothing + WhatToDo;

        /// <summary>The database answered, and refused the sign-in (a wrong organization, an account with no access, an old sign-in).</summary>
        /// <summary>The sign-in window was closed, or nothing came back from it in time. Nothing was created, and starting again is safe.</summary>
        public static string SignInClosed() => "Sign-in was closed. Start again when ready.";

        /// <summary>The person pressed Cancel while the source was being read.</summary>
        public static string Cancelled() => "The start was cancelled." + Nothing;

        public static string SignInRefused() => "The database did not accept the sign-in." + Nothing + WhatToDo;

        /// <summary>The sign-in was fine and the read failed for another reason. The database's own sentence is kept, because it is the only honest detail there is.</summary>
        public static string CannotRead(string databaseSaid)
        {
            var said = string.IsNullOrWhiteSpace(databaseSaid) ? "" : " The database said: " + databaseSaid.Trim().TrimEnd('.') + ".";
            return "Semanticus could not read this database." + Nothing + " Check the endpoint and the database name, and that this computer is online, then try again." + said;
        }
    }
}

using System;
using System.IO;

namespace Semanticus.Engine.Entitlement
{
    /// <summary>
    /// How a Pro token reaches the engine without sitting on the process command line.
    /// The owner reads it from stdin when <c>--license-stdin</c> is set. An attaching MCP
    /// process inherits the owner's entitlement over the pipe. Env and the user license
    /// file remain fallbacks when stdin did not carry a token.
    /// </summary>
    public static class LicenseTokenDelivery
    {
        public const string StdinFlag = "--license-stdin";
        public const string LegacyArgFlag = "--license";

        /// <summary>
        /// Prefer the stdin line after <see cref="StdinFlag"/>. An older launcher may still
        /// pass <see cref="LegacyArgFlag"/>; that value is read so those processes keep
        /// working, but new launchers must not write it.
        /// </summary>
        public static string FromArgs(string[] args, TextReader stdin)
        {
            if (args != null && Array.IndexOf(args, StdinFlag) >= 0)
            {
                var line = stdin?.ReadLine();
                if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
            }
            if (args == null) return null;
            var i = Array.IndexOf(args, LegacyArgFlag);
            if (i >= 0 && i + 1 < args.Length
                && !string.IsNullOrWhiteSpace(args[i + 1])
                && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                return args[i + 1].Trim();
            return null;
        }

        public static bool CommandLineExposesToken(string[] args)
        {
            if (args == null) return false;
            var i = Array.IndexOf(args, LegacyArgFlag);
            return i >= 0 && i + 1 < args.Length
                && !string.IsNullOrWhiteSpace(args[i + 1])
                && !args[i + 1].StartsWith("-", StringComparison.Ordinal);
        }
    }
}

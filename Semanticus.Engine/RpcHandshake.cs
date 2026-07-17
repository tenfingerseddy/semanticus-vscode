using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    internal enum RpcConnectionRole
    {
        Agent,
        Human,
    }

    /// <summary>
    /// A deliberately tiny pre-JSON-RPC handshake. The owner assigns authority once, before any method target is
    /// registered; request payloads can never promote a connection. The UI challenge is held by VS Code SecretStorage.
    /// </summary>
    internal static class RpcHandshake
    {
        private const string Prefix = "SEMANTICUS-RPC/1 ";
        private const int MaxPreambleBytes = 256;
        private const string Accepted = Prefix + "accepted";
        private const string Rejected = Prefix + "rejected";

        internal static async Task WriteAsync(Stream stream, RpcConnectionRole role, string uiChallenge = null,
            CancellationToken cancellationToken = default)
        {
            var line = role == RpcConnectionRole.Agent
                ? Prefix + "agent\n"
                : Prefix + "human " + ValidateChallenge(uiChallenge) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<RpcConnectionRole> ReadAsync(Stream stream, string expectedUiChallenge,
            CancellationToken cancellationToken)
        {
            var line = await ReadLineAsync(stream, "RPC client disconnected before the role handshake.", cancellationToken)
                .ConfigureAwait(false);
            if (line == Prefix + "agent") return RpcConnectionRole.Agent;
            if (!line.StartsWith(Prefix + "human ", StringComparison.Ordinal))
                throw new InvalidDataException("RPC role handshake is invalid.");

            var supplied = line.Substring((Prefix + "human ").Length);
            if (!ChallengeMatches(expectedUiChallenge, supplied))
                throw new UnauthorizedAccessException("RPC UI authentication failed.");
            return RpcConnectionRole.Human;
        }

        /// <summary>The role is not established until the server confirms it. Clients must consume this line before
        /// handing the stream to JSON-RPC, so a rejected human proof can never masquerade as a connected UI.</summary>
        internal static async Task WriteResponseAsync(Stream stream, bool accepted,
            CancellationToken cancellationToken = default)
        {
            var bytes = Encoding.UTF8.GetBytes((accepted ? Accepted : Rejected) + "\n");
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        internal static async Task ReadAcceptedAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            var line = await ReadLineAsync(stream, "RPC server disconnected before confirming the role handshake.", cancellationToken)
                .ConfigureAwait(false);
            if (line == Accepted) return;
            if (line == Rejected) throw new UnauthorizedAccessException("RPC role handshake was rejected.");
            throw new InvalidDataException("RPC role handshake response is invalid.");
        }

        private static async Task<string> ReadLineAsync(Stream stream, string disconnectedMessage,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var bytes = new byte[MaxPreambleBytes];
            var one = new byte[1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException(disconnectedMessage);
                if (one[0] == (byte)'\n') break;
                if (one[0] == (byte)'\r') throw new InvalidDataException("RPC role handshake contains an invalid carriage return.");
                bytes[count++] = one[0];
            }
            if (count == bytes.Length) throw new InvalidDataException("RPC role handshake is too long.");
            return new UTF8Encoding(false, true).GetString(bytes, 0, count);
        }

        internal static string ValidateChallenge(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 32 || value.Length > 128)
                throw new ArgumentException("The RPC UI challenge must contain 32 to 128 characters.", nameof(value));
            return value;
        }

        private static bool ChallengeMatches(string expected, string supplied)
        {
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied)) return false;
            var left = Encoding.UTF8.GetBytes(expected);
            var right = Encoding.UTF8.GetBytes(supplied);
            return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        }
    }
}

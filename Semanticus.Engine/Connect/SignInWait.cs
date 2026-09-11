using System;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// The interactive sign-in wait, in one place: a support id, a hard ceiling, and an honest cancel.
    /// Closing the chooser used to leave the page waiting until the 10 minute webview guard. This wrapper
    /// stops at <see cref="InteractiveTimeout"/>, names cancel vs timeout vs fail, and logs a line with the
    /// id and no secret. Both doors go through here because both call <see cref="EntraToken.BuildCredentialAsync"/>.
    /// </summary>
    internal static class SignInWait
    {
        internal static readonly TimeSpan InteractiveTimeout = TimeSpan.FromSeconds(120);

        internal enum Outcome { Ok, Cancelled, TimedOut, Failed }

        // Test seam: capture the log line without reading stderr. Null in production.
        internal static Action<string> LogForTests;

        internal static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 8);

        internal static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct, TimeSpan? timeout = null)
        {
            var id = NewId();
            if (ct.IsCancellationRequested)
            {
                Log(Outcome.Cancelled, id);
                throw new SignInException(Outcome.Cancelled, id, Message(Outcome.Cancelled, id));
            }
            using var timeoutCts = new CancellationTokenSource(timeout ?? InteractiveTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                var result = await work(linked.Token).ConfigureAwait(false);
                Log(Outcome.Ok, id);
                return result;
            }
            catch (SignInException)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is TaskCanceledException)
            {
                var timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
                var outcome = timedOut ? Outcome.TimedOut : Outcome.Cancelled;
                Log(outcome, id);
                throw new SignInException(outcome, id, Message(outcome, id));
            }
            catch (Exception ex) when (XmlaAuthHint.IsUserCancellation(ex))
            {
                Log(Outcome.Cancelled, id);
                throw new SignInException(Outcome.Cancelled, id, Message(Outcome.Cancelled, id));
            }
            catch (Exception)
            {
                Log(Outcome.Failed, id);
                throw new SignInException(Outcome.Failed, id, Message(Outcome.Failed, id));
            }
        }

        internal static void Log(Outcome outcome, string id)
        {
            var word = outcome switch
            {
                Outcome.Ok => "ok",
                Outcome.Cancelled => "cancelled",
                Outcome.TimedOut => "timed out",
                _ => "failed",
            };
            var line = "[auth] sign-in " + id + " " + word;
            LogForTests?.Invoke(line);
            try { Console.Error.WriteLine(line); } catch { /* a log must never fail a sign-in */ }
        }

        internal static string Message(Outcome outcome, string id) => outcome switch
        {
            Outcome.Cancelled => XmlaAuthHint.CancelledHint(id),
            Outcome.TimedOut => XmlaAuthHint.TimedOutHint(id),
            _ => XmlaAuthHint.FailedHint(id),
        };
    }

    /// <summary>A classified sign-in stop. The message is safe to show. There is no inner exception, so a
    /// serializer cannot revive the raw AMO or MSAL text.</summary>
    internal sealed class SignInException : InvalidOperationException
    {
        public SignInWait.Outcome Outcome { get; }
        public string SupportId { get; }

        public SignInException(SignInWait.Outcome outcome, string supportId, string message) : base(message)
        {
            Outcome = outcome;
            SupportId = supportId;
        }
    }
}

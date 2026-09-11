using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// Observes <c>model/didChange</c> notifications on one JSON-RPC client.
    ///
    /// A one-shot next-event waiter is wrong on this pipe: Broadcast is fire-and-forget
    /// (<c>RpcServer</c>), so the previous mutation's notification can still be in flight after
    /// that mutation's RPC response has already returned. Arming "wait for the next event" then
    /// observes the straggler, and the edit under test arrives with no waiter. That is F-001:
    /// the paste copy is on the shared session while the paste observation misses it. Match the
    /// notification you mean; a longer timeout hides a lost notification exactly as well as it
    /// waits out a slow one.
    ///
    /// One consume-once cursor walks the stream. A returned match and an ignored straggler are
    /// both consumed, so neither can satisfy a later wait. An overlapping wait throws instead of
    /// silently orphaning the first. Internal: this is a smoke-harness helper, not engine API.
    /// </summary>
    internal sealed class DidChangeWaiter
    {
        private readonly object _gate = new object();
        private readonly List<ChangeNotification> _all = new List<ChangeNotification>();
        private int _consumed;
        private TaskCompletionSource<ChangeNotification> _next;
        private Func<ChangeNotification, bool> _match;

        public IReadOnlyList<ChangeNotification> All
        {
            get { lock (_gate) return _all.ToArray(); }
        }

        public void Observe(ChangeNotification n)
        {
            if (n == null) return;
            lock (_gate)
            {
                _all.Add(n);
                if (_next == null) return;
                if (_match != null && !_match(n))
                {
                    _consumed++;   // ignored straggler cannot satisfy a later wait
                    return;
                }
                _consumed++;
                var t = _next;
                _next = null;
                _match = null;
                t.TrySetResult(n);
            }
        }

        /// <summary>
        /// Wait for the next unconsumed matching notification. Already-received unconsumed
        /// matches are returned immediately. A null match takes the next unconsumed event.
        /// </summary>
        public Task<ChangeNotification> WaitAsync(Func<ChangeNotification, bool> match)
        {
            lock (_gate)
            {
                if (_next != null)
                    throw new InvalidOperationException("DidChangeWaiter does not allow overlapping waits; the first waiter would be orphaned.");
                while (_consumed < _all.Count)
                {
                    var n = _all[_consumed++];
                    if (match == null || match(n)) return Task.FromResult(n);
                }
                _next = new TaskCompletionSource<ChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
                _match = match;
                return _next.Task;
            }
        }
    }
}

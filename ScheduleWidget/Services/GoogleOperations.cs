using System;
using System.Threading;

namespace ScheduleWidget
{
    // Each async flow carries its own cancellation scope, including Task.Run and ConfigureAwait(false).
    // Replacing a session cancels existing scopes; a new operation never inherits an old request's token.
    internal sealed class GoogleOperations
    {
        private readonly object gate = new object();
        private readonly AsyncLocal<Scope> current = new AsyncLocal<Scope>();
        private CancellationTokenSource session = new CancellationTokenSource();

        internal CancellationToken Token => current.Value?.Token ?? CancellationToken.None;
        internal void Check() => Token.ThrowIfCancellationRequested();

        internal Scope Begin(CancellationToken cancellation = default(CancellationToken))
        {
            lock (gate)
            {
                var scope = new Scope(this, current.Value,
                    CancellationTokenSource.CreateLinkedTokenSource(session.Token, cancellation));
                current.Value = scope;
                return scope;
            }
        }

        internal void Cancel()
        {
            CancellationTokenSource previous;
            lock (gate) { previous = session; session = new CancellationTokenSource(); }
            try { previous.Cancel(); }
            finally { previous.Dispose(); }
        }

        internal sealed class Scope : IDisposable
        {
            private readonly GoogleOperations owner;
            private readonly Scope previous;
            private readonly CancellationTokenSource source;
            internal CancellationToken Token { get; }
            internal Scope(GoogleOperations owner, Scope previous, CancellationTokenSource source)
            { this.owner = owner; this.previous = previous; this.source = source; Token = source.Token; }
            public void Dispose() { owner.current.Value = previous; source.Dispose(); }
        }
    }
}

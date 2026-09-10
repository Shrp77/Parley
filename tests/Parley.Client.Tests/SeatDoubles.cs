using Grpc.Core;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// Seat doubles shared by the suites that need a broker in a state a real one cannot be held in.
/// </summary>
/// <remarks>
/// Extracted from <c>LostSeatTests</c> when a second suite needed a lost seat. Copied rather than
/// moved is how this repository already lost an afternoon: two copies of its repository-file helper
/// existed within a day of each other and had begun to differ.
/// </remarks>
internal static class SeatDoubles
{
    /// <summary>
    /// An Agents client that grants a fixed number of attachments, ends each, and refuses the rest.
    /// </summary>
    /// <param name="refuseWith">What to refuse attachments beyond its allowance with.</param>
    /// <remarks>
    /// A real broker cannot be told to acknowledge an attach and then refuse the next, and the
    /// window between a broker going away and its seat noticing is not something a test should
    /// race. This is that state, held still.
    /// </remarks>
    internal sealed class Stubborn(StatusCode refuseWith) : Wire.Agents.AgentsClient
    {
        private int _allowance = 1;
        private int _grants;
        private int _refusals;

        /// <summary>How many attachments have been acknowledged.</summary>
        public int Grants => Volatile.Read(ref _grants);

        /// <summary>How many attachments have been turned down.</summary>
        public int Refusals => Volatile.Read(ref _refusals);

        /// <summary>Permits one more attachment, as a broker coming back would.</summary>
        public void Allow() => Interlocked.Increment(ref _allowance);

        /// <inheritdoc/>
        public override AsyncServerStreamingCall<Wire.Event> Attach(
            Wire.AttachRequest request, CallOptions options)
        {
            if (Interlocked.Decrement(ref _allowance) < 0)
            {
                Interlocked.Increment(ref _allowance);
                Interlocked.Increment(ref _refusals);

                throw new RpcException(new Status(refuseWith, "no."));
            }

            Interlocked.Increment(ref _grants);

            // Acknowledged and then finished: a broker that went away with the stream open,
            // rather than one that was never there.
            return new(new Acknowledged(),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }
    }

    /// <summary>A stream that yields one attached event and then ends.</summary>
    internal sealed class Acknowledged : IAsyncStreamReader<Wire.Event>
    {
        private bool _sent;

        /// <inheritdoc/>
        public Wire.Event Current { get; private set; } = new();

        /// <inheritdoc/>
        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_sent)
            {
                return Task.FromResult(false);
            }

            _sent = true;
            Current = new Wire.Event { Attached = new Wire.Attached() };

            return Task.FromResult(true);
        }
    }
}

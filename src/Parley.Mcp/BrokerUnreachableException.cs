namespace Parley.Mcp;

/// <summary>
/// Nothing is listening where the broker was expected.
/// </summary>
/// <remarks>
/// <para>
/// The failure a deployment meets first, and the one whose message has to earn its place: the
/// person reading it has just launched an editor, not a broker, and nothing in that experience
/// suggests a second process was involved.
/// </para>
/// <para>
/// It is a distinct type rather than an <see cref="InvalidOperationException"/> so that the
/// launch path can say what to do about it, which is different from what to do about every other
/// refusal. A retired agent needs an operator; a name collision needs a configuration change;
/// this needs somebody to start the broker.
/// </para>
/// </remarks>
public sealed class BrokerUnreachableException : Exception
{
    /// <summary>Creates the exception for an address nothing answered on.</summary>
    /// <param name="endpoint">Where the broker was expected.</param>
    /// <param name="innerException">What the transport reported.</param>
    /// <remarks>
    /// Named rather than positional in spirit: there is deliberately no ordinary
    /// <c>(message, inner)</c> constructor, because it would have this signature and the two
    /// would be told apart only by which argument happened to be an address.
    /// </remarks>
    public BrokerUnreachableException(string endpoint, Exception? innerException)
        : base($"No broker is listening at {endpoint}. Parley keeps its store in one process " +
               "and everything else connects to it, so nothing works until that process is " +
               $"running: start it with `parley serve`. If it is running elsewhere, set " +
               $"{Core.BrokerEndpoint.Variable} to its address.",
               innerException) =>
        Endpoint = endpoint;

    /// <summary>Creates the exception with a default message.</summary>
    public BrokerUnreachableException()
        : base("No broker is listening. Start one with `parley serve`.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public BrokerUnreachableException(string? message)
        : base(message)
    {
    }

    /// <summary>Gets where the broker was expected, if it was named.</summary>
    public string? Endpoint { get; }
}

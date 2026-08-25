namespace Parley.Headless;

/// <summary>
/// An agent's runner could not be started.
/// </summary>
/// <remarks>
/// A distinct type because a fleet must tell two failures apart. An agent that starts and then
/// answers badly is a protocol matter; an agent that never starts is a deployment matter — a
/// command not on the path, a workspace that moved, a runner refusing to authenticate — and it
/// affects every agent configured the same way. Reported as one exception so the supervisor can
/// name the agent and carry on to the next rather than ending the pass.
/// </remarks>
public sealed class InvocationFailedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public InvocationFailedException()
        : base("An agent's runner could not be started.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public InvocationFailedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the failure underneath it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The platform failure.</param>
    public InvocationFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

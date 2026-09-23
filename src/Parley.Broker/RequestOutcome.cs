using Parley.Core;
using Parley.Store;

namespace Parley.Broker;

/// <summary>
/// What became of one Request: the exchange it opened and the message carrying it, or the refusal.
/// </summary>
/// <remarks>
/// A wrapper over <see cref="OpenOutcome"/> rather than a copy of its fields, so the refusal and what the
/// sender can act on travel together. <see cref="Request"/> is non-null exactly when the admission
/// succeeded: admission runs before the message is written, so a refused Request writes nothing at all.
/// </remarks>
public sealed record RequestOutcome
{
    private RequestOutcome(OpenOutcome admission, Message? request, string? requestGroup)
    {
        Admission = admission;
        Request = request;
        RequestGroup = requestGroup;
    }

    /// <summary>Gets the group binding this exchange to the others one Request to a role opened (§5.5).</summary>
    /// <value>The group, or <see langword="null"/> for a Request to one participant, or a refusal.</value>
    public string? RequestGroup { get; }

    /// <summary>Gets the admission decision, with everything a refusal hands back.</summary>
    /// <value>The outcome of admission.</value>
    public OpenOutcome Admission { get; }

    /// <summary>Gets the exchange opened, when one was.</summary>
    /// <value>The exchange, or <see langword="null"/> on a refusal.</value>
    public Exchange? Exchange => Admission.Exchange;

    /// <summary>Gets the Request as stored, when it was admitted.</summary>
    /// <value>The message, or <see langword="null"/> on a refusal.</value>
    public Message? Request { get; }

    /// <summary>Gets why it was refused.</summary>
    /// <value><see cref="RequestRejection.None"/> on success.</value>
    public RequestRejection Rejection => Admission.Rejection;

    /// <summary>Gets a value indicating whether the Request was admitted.</summary>
    /// <value><see langword="true"/> when an exchange was opened.</value>
    public bool Succeeded => Admission.Succeeded;

    internal static RequestOutcome Opened(OpenOutcome admission, Message request, string? requestGroup)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(request);

        if (!admission.Succeeded)
        {
            throw new ArgumentException("A refused admission carries no Request.", nameof(admission));
        }

        return new(admission, request, requestGroup);
    }

    internal static RequestOutcome Refused(OpenOutcome admission)
    {
        ArgumentNullException.ThrowIfNull(admission);

        if (admission.Succeeded)
        {
            throw new ArgumentException("A successful admission is not a refusal.", nameof(admission));
        }

        return new(admission, request: null, requestGroup: null);
    }
}

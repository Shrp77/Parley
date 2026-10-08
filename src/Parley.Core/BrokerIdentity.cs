namespace Parley.Core;

/// <summary>What a broker said about itself when it was asked what it speaks (SPEC.md §10.2 <c>implements</c>).</summary>
/// <param name="Protocol">The protocol version it speaks.</param>
/// <param name="BrokerVersion">The build serving it, or empty when it did not say.</param>
/// <param name="StoreProblems">How its store differs from the shape that build expects; empty when it matches.</param>
/// <remarks>
/// <para>
/// <b>Three things where there was one.</b> The protocol says whether a surface and a broker can talk. It cannot say
/// whether the programs on a machine are from one release, or whether the file the broker serves has what that release
/// added to it, and the owner met both as a terminal that would not send. A broker that cannot name its build or its
/// store's shape leaves both empty, which reads as "not said" and never as "fine".
/// </para>
/// </remarks>
public sealed record BrokerIdentity(string Protocol, string BrokerVersion, IReadOnlyList<string> StoreProblems);

namespace Parley.Core;

/// <summary>
/// How many exchanges reached a given clarification depth.
/// </summary>
/// <param name="Depth">Clarifications the answerer requested.</param>
/// <param name="Exchanges">How many exchanges reached exactly that depth.</param>
public sealed record ClarifyDepthBucket(int Depth, long Exchanges);

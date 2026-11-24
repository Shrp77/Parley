using Parley.Core;

namespace Parley.Web.Pages.Conversations;

/// <summary>One placed file as the transcript and the Media page draw it (<c>_Placed</c>).</summary>
/// <param name="File">The file.</param>
/// <param name="Project">The project, for its addresses.</param>
/// <param name="Conversation">The conversation it was placed in.</param>
/// <param name="InMedia">Whether it is drawn in Media, where it also leads back to its place in the conversation.</param>
public sealed record PlacedItem(SharedFile File, string? Project, string Conversation, bool InMedia = false);

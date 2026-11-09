namespace Parley.Core;

/// <summary>
/// What reading one file as the person came to (SPEC.md §9.5, §10.2 <c>read_shared_file</c>).
/// </summary>
/// <param name="File">The file the broker found, or <see langword="null"/> when it refused before naming one.</param>
/// <param name="Bytes">Its whole content, or <see langword="null"/> when refused or too large.</param>
/// <param name="TooLarge">Whether the file was larger than the limit asked for, so no bytes were taken.</param>
/// <param name="Refusal">Why the broker refused, or <see langword="null"/>.</param>
/// <remarks>
/// <b>The one type that carries a placed file's bytes</b>, and only to a surface that shows them to the person.
/// <see cref="SharedFile"/> carries no contents so that no reply ever does; this is never put in a reply, a message or
/// a log.
/// </remarks>
public sealed record SharedFileRead(SharedFile? File, ReadOnlyMemory<byte>? Bytes, bool TooLarge, FileRefusal? Refusal);

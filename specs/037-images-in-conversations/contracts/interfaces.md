# Interfaces: 037

## Wire (Operators)

```proto
rpc ReadSharedFile(ReadSharedFileRequest) returns (stream FetchFileFrame);

message ReadSharedFileRequest {
  string conversation_id = 1;   // required: a read is always of one conversation's file
  string name = 2;
  string expect_sha256 = 3;     // empty: no expectation
}
```

Frames as `FetchFile`: a header, then chunks; or a refusal instead of a header; or a refusal after the chunks when the
bytes were tampered with. Records nothing: no fetch, no notice, no delivery. Refuses an empty conversation.

## Client

```csharp
Task<SharedFileRead> ReadSharedFileAsync(string conversationId, string name, ContentHash? expected, long limitBytes,
    CancellationToken cancellationToken = default);
```

Reads the header; when its size exceeds `limitBytes`, ends the call and returns `TooLarge`. Otherwise buffers the
bytes and returns them.

## Web console

- `GET /Conversations/Media?project=P&id=C` — the Media page.
- `GET /Conversations/Media?handler=Image&project=P&id=C&name=N&sha=H[&large=1]` — the image's bytes.
  - 404: not an image name, no such file, or the hash differs (the page that linked it is stale).
  - 413: larger than 20 MB without `large=1`.
  - 415: the leading bytes are not the format the name says.
  - 200: the bytes, `Content-Type` from the extension, `Content-Disposition: inline`, `Cache-Control: private,
    max-age=31536000, immutable` (the URL names the hash, and a placed file never changes), and
    `Content-Security-Policy: default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox`.
- Transcript: `<li class="placed" id="f-<NAME KEY>">` before the turn it precedes; an image as a link to the handler
  holding a lazy `<img>`; another file as a link to the Files page narrowed to the conversation.

## Terminal

- `Alt+e` *Media in this conversation*, in Conversations (the selected one) and in the Transcript (the open one).
- Transcript line, indented: `  <placer> placed <name> (image|text, <size>) at <time>`.

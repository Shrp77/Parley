# Data model: 037

Nothing stored is added or changed.

- **SharedFile** (exists, Core): conversation, folder, name, size, sha256, placer (null for the person), placed time.
- **SharedFileRead** (new, Core): the outcome of reading one file as the person.
  - `File` — the header (a `SharedFile`) when the broker found it.
  - `Bytes` — the content, only when it was no larger than the limit asked for.
  - `TooLarge` — the header said more than the limit; no bytes were taken.
  - `Refusal` — the broker's refusal (no such file, changed, tampered, ambiguous), in words.
- **ImageSignature** (new, Core, pure): `IsImageName(name)`, `ContentType(name)`, `Matches(name, leading bytes)`.
- **Placement entry** (derived, Client): a `SharedFile` placed before a given turn, from `Placements.Before`.
- **Media** (derived, Client): a conversation's files split into images and others, each newest first.

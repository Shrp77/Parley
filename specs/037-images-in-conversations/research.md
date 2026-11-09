# Research: 037

## R1. How the console gets an image's bytes

- **Decision**: a new operator read, `Operators.ReadSharedFile`, streaming the same frames as `FetchFile` (header, chunks,
  refusal) through the same core in `FileWork`, with no agent and no fetch recorded.
- **Rationale**: the console may run on another machine than the broker (SSH tunnel), so it cannot read the location
  from disk. `FetchFile` is an agent operation that records a fetch, which drives change notices; viewing must record
  nothing (FR-014). Sharing the core keeps the read hold, the stall rule, the re-resolve under the hold and the tamper
  check in one place.
- **Alternatives**: reading the location from disk when it is local (fails over a tunnel, two code paths); adding the
  bytes to `ListSharedFiles` (a listing would carry contents, which `SharedFile`'s remarks forbid).

## R2. What answers content sniffing

- **Decision**: three things. (a) Only names ending in an image extension are served, with the content type that
  extension names. (b) The leading bytes must be that format's signature (PNG `89 50 4E 47 0D 0A 1A 0A`, JPEG
  `FF D8 FF`, GIF `GIF87a`/`GIF89a`, WebP `RIFF????WEBP`), or the console answers 415 and serves nothing. (c) The
  response carries `X-Content-Type-Options: nosniff` (already global), `Content-Security-Policy: default-src 'none';
  img-src 'self'; style-src 'unsafe-inline'; sandbox` and `Content-Disposition: inline`.
- **Rationale**: (a) and (c) already stop a browser treating the bytes as a page. (b) means a file named `.png` that
  holds HTML never reaches a browser at all, so it does not rest on one browser's sniffing rules. The check is on the
  console, after the broker hands the bytes over; the broker still never inspects them.
- **Alternatives**: decoding the image server-side to make a thumbnail (decodes another participant's bytes in Parley's
  own process, and needs an imaging package); no signature check (rests on browser behaviour alone).

## R3. Very large images

- **Decision**: 20 MB inline limit. The console stops reading when the header says the file is larger and shows a tile;
  "Show anyway" fetches with an explicit `large=1` and opens the image on its own.
- **Rationale**: bounds what one page view pulls through the broker and into the browser. Pixel size is unknown without
  decoding; a decompression bomb under 20 MB is the browser decoder's problem, recorded as what remains.

## R4. Where a placement sits in the transcript

- **Decision**: by `PlacedUtc` against each turn's `CreatedUtc`, before the first turn that is later. Placements after
  the last turn come at the end. One function in Parley.Client, used by the web and the terminal.
- **Rationale**: the placement record is the only link (FR-002). Message times come from agents' machines, so order
  across clocks is approximate, as the transcript already says.

## R5. Removed files

- **Decision**: not shown (spec FR-004 amended). The broker deletes a removed file's row and keeps no record of it.

## R6. Opening text files from the transcript and Media

- **Decision**: a link to the Files page narrowed to the conversation, where Open already lives.
- **Rationale**: Open is a POST with a token, and the followed region is compared by digest, so it may carry no form.

## R7. The terminal's use of the read

- **Decision**: when Open finds the location on another machine, the terminal reads the file through `ReadSharedFile`
  into a folder of its own under the user's temporary directory and opens that copy. Same for Media.
- **Rationale**: every operator operation is on both screens (`DriftTests`); this is a real need, since today Open
  refuses on a remote broker. Only image, `.md` and `.txt` names reach it, so opening a copy runs nothing (§13).

## R8. Keys

- **Decision**: `Alt+e` ("mEdia") in Conversations and in the Transcript. The table check confirms it is free.

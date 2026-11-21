# Lens 2: security and data integrity

Part of [the review gate](README.md).

## What is medium or above

- Another participant (a peer agent, a placed file, a message's text, a link in a message) gaining any effect on this
  agent's repository, its configuration or its standing instructions.
- An agent reaching an operator operation, or acting as another agent.
- Bytes or rows lost, corrupted, or recorded wrongly; a wrong hash recorded or served.
- A store, a file location or a write reached through a link.
- A credential persisted, logged, or offered to another machine, including by a lookup that makes Windows sign in.
- Another participant's text drawn without its control characters neutralised.
- A request another site starts reaching a page that acts.

Low: a power the unauthenticated loopback port already concedes (§16), an effect that needs another program writing in
the owner's own folders, a beacon that carries no credential.

## The defences in place

Check a change against each; a new page or a new path lookup must keep all of them.

**The web console**
- It answers only loopback host names and the host it binds (DNS rebinding).
- No page can be framed, except the conversation page the console's own roadmap page frames (`embed` and
  `Sec-Fetch-Dest: iframe`, same origin only).
- A request whose `Sec-Fetch-Site` is `cross-site` or `same-site` is refused before any page runs, because pages act on
  a GET (they take the person's seat and record delivery). A typed address or a bookmark sends `none`.
- Every POST is behind antiforgery; a POST-only bound property is `[FromForm]`, and an `OnPost` parameter names its
  source.

**Paths and links**
- No path is looked up on another machine. `LocalPath` refuses a share or device written as text; `OffMachineLinks`
  walks a path part by part, reading each link's target without opening it, and refuses a link that leads off this
  machine, a link whose target cannot be read, a target rooted without a drive, a link whose target names
  something not there, and a stream spelling (`folder::$INDEX_ALLOCATION`). Each drive letter the walk lands on, typed or
  through a link, is judged by the name Windows keeps for it, never by asking the drive (asking can open a share): a
  local disk volume is walked, a `subst` letter is walked as the folder it stands for, an undefined letter holds
  nothing to read, and anything else is refused (a letter whose name cannot be read included).
  On Windows a link is read from its own reparse data, never .NET's `LinkTarget` text: only a symbolic link flagged
  relative is relative, an absolute target is followed only as `\??\X:\…` or walked through as a bare local-disk
  `\??\Volume{GUID}\` (from its drive letter when it has one), and every other absolute target (UNC, GLOBALROOT, a
  volume path with folders, a device) and every other kind of reparse point is refused. Any part of the walked path, or of a target or `subst` folder, that ends in a dot or any whitespace is refused, and so
  is an absolute target with `.`, `..` or an empty part, because Windows reads such a part as stored while a Win32
  path would tidy it. Every surface that looks up a path an agent can influence goes through it: the web Enrol page
  and folder browser, the terminal's Add-an-agent browser and workspace check, the enrolment scan, the ownership
  write Auto Accept makes, the person's placement (`parley files place` and the terminal's place dialog, through
  `PlaceableFile`, which also refuses a file that is itself a link), and every read of a repository's launch
  configuration or instructions file (`LaunchConfiguration.TryRead`: the node's catalogue on each join and start, the
  terminal's agents list, enrolment's look at the other catalogued repositories; `RepositoryProjects.Read`, which every
  `parley` command typed in a repository runs; enrolment's read of a declined instructions file; the bulk enrolment's
  copy check and its described plan). The adapter's own read of its `.mcp.json` runs in the agent's own process.
  On Windows those lookups are one act with the walk (`LocalOpen`): each part is opened as itself relative to the
  folder already open, a link is followed only from its own handle, and the disk is opened by its device name, so an
  agent flipping its folder or file to a link mid-lookup gets nothing followed (owner, 2026-11-09: fixed in 035 after
  lens 2 round 13 won the old check-then-lookup gap within seconds). Withdrawal, the ownership write, `HoldAsync` and
  `Linked`, `LaunchConfiguration.TryRead`, `RepositoryProjects.Read`, the enrolment scan and plan, `LocalStorage.Problem`
  (so the web Enrol page and the terminal's workspace check), and the browsers' repository checks use it. Still a check
  and then a lookup: the folder browsers' listing of a folder's children (and the enrolment scan's listing of a root),
  the person's placement (`PlaceableFile`), `LocalStorage`'s question of a drive's kind and label, asked of a letter the
  walk found local, and the node: its workspace-gone judgement asks of the catalogued folder and the folders above it by
  path, and its start and restart refuse a workspace they could not open cleanly and then start the process in that
  folder by path, so a flip between the two is followed. The enrolment scan and plan (the existence, worktree, `.mcp.json` and
  instructions-file questions, and plan validation) and the catalogue-copy check go through the opener; each is held by a test
  (`LinkRaceTests`, `EnrollCommandTests`, `NothingUnderALinkToAShareIsLookedUpTests`, which puts the link above the
  last part, where `File.Exists` and `Directory.Exists` do follow it). A file `OverwriteHeldAsync` creates (a first
  `.mcp.json` or instructions file, a new catalogue) is made relative to the folder the walk opened (`LocalOpen.Create`),
  and a failed write is undone through the file's own handle (`LocalOpen.Discard`), but that does not close the race:
  with the folder flipping to a link to a share during the create, Windows itself (pid 4) still dialled the share in
  three runs of four, most likely a file-system filter resolving the new file's name again by path (2026-11-09;
  owner, 2026-11-10: recorded, not blocking; staging the file in Parley's own folder and moving it into place, with the
  other remaining check-then-lookup places, is a follow-up feature after 035). The walk refuses a relative link reached after a junction, since Windows
  resolves it from the path as reached. Each part's long name is read from its handle,
  so a short (8.3) spelling is judged as the folder it is. Off Windows a share is a
  mount and following a link to one offers no sign-in, so the walk is kept and the open is ordinary, except that a file
  about to be written is still refused when it is itself a link.
- Enrolment treats any reparse point as a link, whatever its target reads as, and never writes through one.
- **Only local storage** (the owner's rule): a repository, a deployment's root and an agent's workspace are refused on a
  synced cloud folder (OneDrive, Google Drive, Dropbox, iCloud Drive, Box), a network drive or a virtual file system
  (`LocalStorage`).

**Files between agents**
- Only `.md`, `.txt` and image files (PNG, JPEG, GIF, WebP) cross. A fetch is written only under one of those
  extensions, never under `.git`, `.claude`, `.github` or
  another agent client's settings or rules folder, and never as `CLAUDE.md`, `AGENTS.md` or their kind. The lists are
  partial on purpose; the agent is told never to put a fetched file where a tool would read it as instructions.
- A placed file is checked against its hash; a tampered one is quarantined and its fetchers and placer are told.

## How to probe

- Make the malicious case real: a link with `mklink` (Developer Mode allows it), a hostile name, a cross-site request
  from a headless browser against a loopback logger.
- For anything that might reach the network, use a fresh TEST-NET host and time it (see [the gate](README.md)).

## Settled by the owner

- **The fetch destination rule stays.** A further folder missing from its lists is low.
- **Auto Accept** making an agent's answer its ownership line is a recorded reduction in a control, with its safeguards
  (truthful provenance, a review on every surface, the agent's own words). Not to be re-raised.
- **The unauthenticated loopback port** is §16's concession; what a hand-made client on it can do is low.
- **Roadmap text has no bound**; the terminal's slowness on a huge item is recorded evidence.
- **A GET that delivers on the person's own act** (reload, a restored tab, a prerender they started) is by design.
- macOS `/net` autofs and a link the person made themselves are recorded lows.
- **A link an agent makes in its own repository is low** (owner, 2026-10-31, lens 2 run 123): a junction, a hard link
  or a symbolic link that leads a placement, the person's or the agent's own, to a file outside the repository gives
  the agent nothing it lacks, because an agent that can make the link can read the target and send its words in a
  message. What stays above low is a lookup that leaves this machine (a share path, typed or behind a link: a
  credential offered to another host) and anything reached without the agent's own act.
- The lows triaged in the ledger's 2026-10-29 entry as moot or by design stay that way.

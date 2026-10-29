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
  A folder that mounts a local disk is walked through. Every surface that looks up a path an agent can influence goes through it: the web Enrol page
  and folder browser, the terminal's Add-an-agent browser and workspace check, the enrolment scan, and the ownership
  write Auto Accept makes.
- Enrolment treats any reparse point as a link, whatever its target reads as, and never writes through one.
- **Only local storage** (the owner's rule): a repository, a deployment's root and an agent's workspace are refused on a
  synced cloud folder (OneDrive, Google Drive, Dropbox, iCloud Drive, Box), a network drive or a virtual file system
  (`LocalStorage`).

**Files between agents**
- Only `.md` and `.txt` cross. A fetch is written only as `.md` or `.txt`, never under `.git`, `.claude`, `.github` or
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
- The lows triaged in the ledger's 2026-10-29 entry as moot or by design stay that way.

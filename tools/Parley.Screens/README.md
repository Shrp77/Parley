# parley-screens

Screenshots of the web console, taken from a broker of its own and never from a live deployment.

It starts a broker and the web console in one process, over a store in a new temporary folder. It plays a
scenario into that broker over the wire, as the agents, their node and the person would. Then it opens each
page in a browser and saves a PNG at the exact size the shot list asks for. When it ends, the folder goes.

Nothing here touches the machine's own deployment. The tool clears every `PARLEY_` variable it inherited before
anything reads one, and the broker listens on a port the system picks.

## Taking a round

From the repository's root:

```
dotnet run --project tools/Parley.Screens -- check tools/Parley.Screens/rounds/0.64.48
dotnet run --project tools/Parley.Screens -- capture tools/Parley.Screens/rounds/0.64.48
```

`check` reads both files and says everything wrong with them, starting nothing. The unit tests run it on every
round, so a round that stops reading fails the push run. `capture` writes the PNGs and `sha256.txt` (one
`name hash` line each) to `artifacts/screens/<round>/` in the repository, which git ignores.

- `--out <dir>` writes somewhere else.
- `--only a.png,b.png` takes just those shots. The other lines in `sha256.txt` are kept.
- `--browser msedge` (the default), `chrome`, or `chromium`. The first two use the browser already installed.
  `chromium` needs Playwright's own build, fetched once with
  `pwsh tools/Parley.Screens/bin/Debug/net10.0/playwright.ps1 install chromium`.

To look around the sample deployment while you write a shot list:

```
dotnet run --project tools/Parley.Screens -- hold tools/Parley.Screens/rounds/0.64.48
```

It prints the console's address and every shot's page, then waits for Enter.

The exit code says how it ended: 0 done, 1 a shot failed, 64 the command line was wrong, 65 a round file is
wrong, 69 the browser could not be used, 70 the broker refused a step, 130 stopped with Ctrl+C. A second
Ctrl+C ends a run that is stuck.

## A round is two files

A round is a folder under `rounds/`. A new round of screenshots is new files, not new code.

**`scenario.json`** is the sample data. It names the project, the machine, the agents and how each one runs,
then lists steps in order. Each step is one act a broker understands:

| `do` | What it is | Needs |
|---|---|---|
| `request` | A request, from an agent or `person` | `from`, `to`, `text`; `conversation`, `label`, `expect` optional |
| `clarify` | A clarify on an exchange | `from`, `exchange` (a label or a number), `text` |
| `result` | A result that closes an exchange | `from`, `exchange`, `outcome`, `text`; `blocker` when blocked |
| `place` | A placed file | `by`, `name`, and `file` (a path) or `render` (an HTML page, with `width` and `height`, captured as a PNG); `conversation` optional |
| `propose` | A roadmap proposal | `by`, `title`, `items`, `label`; `conversation` optional |
| `confirm` | The person confirms a proposal | `proposal` (its label) |
| `freeze` | The person freezes an agent | `agent`; `keep` to outlast a broker restart |
| `turn` | The node reports a turn | `agent`, `phase` (`working`, `ended` or `background`); `tasks` for background |
| `leave` | An agent's connection closes | `agent` |

An agent's `started` is `node` (its node started it, so it can be stopped and given turns), `outside` (opened
outside Parley) or `gone` (registered, then gone). Its optional `model` is the model its messages say wrote them.

Only a request or a propose step takes a `label`, and later steps and shots use it to name what that step made.
Files a step places must be inside the round's folder.

Set `expect` only on a request whose number a shot or the site's words name. If the broker numbers it
differently, the run stops and says so, so those words stay true. Every other request can move freely.

A few things the broker decides, not the scenario. An agent on a machine whose node is connected reads as *not
running* when it is gone, never *cannot be started now*: that needs its machine's node to be away. A request
names only a conversation its sender is already part of, or a new one.

**`shots.json`** is what to capture: the theme, the device scale, the largest a PNG may be, and the shots. Each
shot has a `file` name, an exact `width` and `height`, a `path` on the console, and what it `shows` in words.
It can also have:

- `graph: true` to pin the graph panel open.
- `storage` for other choices a person's browser keeps. `{ "parley.nav": "collapsed" }` folds the navigation
  rail and `{ "parley-convo-composer": "closed" }` folds a conversation's message box. Prefer this to clicking a
  toggle, which flips whatever state the page starts in.
- `actions` done before the capture, in order: `click`, `wait`, `scroll` (a selector, or `top`), `hide` (a
  selector to hide), or `pause` (milliseconds). Selectors are Playwright's: CSS, `text=...` and the others.

A path may name `{project}`, `{agent:role}` and `{exchange:label}` from the scenario.

Prefer selectors that say what a person sees, such as `text=Hide` or `role=button[name="Send"]`, over class
names, which change with the styling. A shot on the Files page may not click: its Open writes a copy into this
machine's own application data.

Both files are refused at load if they have a member the tool does not know or a step missing what it needs,
with a line for each problem. A step the broker refuses stops the run and names the step. A shot fails on its
own line when its page does not answer, the theme did not apply, a selector matches nothing, or its PNG is the
wrong size or too heavy. A failed shot's PNG is deleted, so the folder never holds one from an earlier run.

## What a PNG holds

Each capture is decoded and written again as an opaque 8-bit RGB PNG with only `IHDR`, `IDAT` and `IEND`. It
has no text, time or EXIF chunks and nothing after its end. The same pixels always give the same bytes, so a
screen that did not change keeps its hash between runs.

## The next round

1. Copy the last round's folder under a new name.
2. Change `scenario.json` for the sample data the new shots need, and `shots.json` for the shots.
3. Run `check`, then `hold` to look at each page, then `capture`.
4. Place each PNG in the conversation that asked for it, and send one list of names and hashes.

All names in a scenario are invented. No real project, person or repository may appear in one.

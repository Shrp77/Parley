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
- `--stylesheet look.css` lays a stylesheet over every page before it is captured, so a design direction can be
  photographed on the real console without being built into it. Run the same round once per stylesheet, each with
  its own `--out`.
- `--browser msedge` (the default), `chrome`, or `chromium`. The first two use the browser already installed.
  `chromium` needs Playwright's own build, fetched once with
  `pwsh tools/Parley.Screens/bin/Debug/net10.0/playwright.ps1 install chromium`.

To look around the sample deployment while you write a shot list:

```
dotnet run --project tools/Parley.Screens -- hold tools/Parley.Screens/rounds/0.64.48
```

It prints the console's address and every shot's page, then waits for Enter.

## Checking a phone layout

```
dotnet run --project tools/Parley.Screens -- audit tools/Parley.Screens/rounds/phone-audit
```

`audit` opens each shot's page and writes no PNG. A shot narrower or shorter than 600 pixels opens as a phone's browser
opens it, with touch, honouring the viewport tag. The audit lists:

- anything wider than the window, and anything a box cuts off at its side;
- every box that scrolls sideways (a code block or a wide table is meant to; a page's column is not);
- every control that, scrolled to the middle of the window, is out of reach, cut off, or covered by something that
  stays put, such as a bar or a pinned strip. When a fixed sheet drawn at the window's centre fills four fifths of
  the window or more, only the sheet's own controls are checked.

A screenshot does not show these faults: a desktop browser clips what a page hides sideways, while a phone's browser
may let the person scroll to it. `--stylesheet` works here too. A file holding `html { font-size: 130% !important; }`
stands in for a phone set to larger text, which is where most of these faults show. The exit code is 1 when any shot
has a problem. Read each line yourself: a menu that is open on purpose covers what is under it.

An emulator lays a page out as the desktop engine does, and a real phone's browser does not always agree. To audit
in a device's own Chrome, plug it in by USB with USB debugging on, open Chrome on it, and then run:

```
adb forward tcp:9222 localabstract:chrome_devtools_remote
dotnet run --project tools/Parley.Screens -- audit tools/Parley.Screens/rounds/phone-audit --device http://127.0.0.1:9222
adb forward --remove tcp:9222
```

Each page opens in a new tab of the device's Chrome, at the device's own size, so a shot's width and height are not
used. The tool passes the console's port back to the device for the run (`adb reverse`) and removes it at the end.
With more than one device plugged in, `ANDROID_SERIAL` names the one to use. The tabs share Chrome's profile, so the
tool clears the console's storage on the device before each page. `capture` takes `--device` too, and saves the
device's own screenshots at the device's size.

With no phone to hand, the Android emulator runs real Chrome for Android. Set its screen to the phone in question
(`adb shell wm size 1344x2992` and `adb shell wm density 480` for a Pixel 8 Pro) and use it as above.

The `phone-audit` round holds what a real conversation holds: long unbroken words, inline code, a hash, links, a code
block, a table, tall and wide images, long names and a busy graph, at 360, 412 and 448 wide and in landscape.

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
| `turn` | The node reports a turn | `agent`, `phase` (`working`, `ended` or `background`); `tasks` for background, and `labels`, the runner's label for each task, which get the ids `t1`, `t2` and on |
| `describe` | An agent says what one of its background tasks is doing | `agent`, `task` (its id), `name`; `text` (a description) and `link` optional |
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

- `theme` to take this shot in `light` or `dark` when it differs from the list's, so one list can hold both.
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

# Quickstart: what changes for an operator

## Installing

Nothing new to learn on a workstation:

```powershell
.\install.ps1
```

Windows installs all four tools. Linux installs the broker, the adapter and the web UI, and not the
terminal — on a headless box a browser over an SSH tunnel is the more natural surface. Override it:

```powershell
.\install.ps1 -Surfaces none      # an agent host nobody operates
.\install.ps1 -Surfaces terminal  # operated over SSH, no web stack
.\install.ps1 -Surfaces both
```

The script now reports each tool separately, so this is visible:

```
==> Installing version 0.19.0
    parley          unchanged
    parley-mcp      unchanged
    parley-top      installed
    parley-web-ui   unchanged
```

That line is the feature. `parley unchanged` means the broker was not replaced, which means it was
not stopped, which means no agent was ended.

## Upgrading with work in flight

Before stopping a broker, the install asks it what is open:

```
==> Checking what is in flight
    2 open exchanges in north-helm: design-review, cap-question
    Stopping the broker ends every resident agent; an agent that was holding a
    delivered question will not be there to answer it.
    Re-run with -Force to go ahead.
```

If only a surface changed, this does not appear — there is nothing to stop.

## Running the surfaces

| Before | After | Also works |
|---|---|---|
| `parley top` | `parley top` | `parley-top` |
| `parley console` | `parley web-ui` | `parley-web-ui` |
| `parley serve --with-console` | `parley serve --with-web-ui` | old spelling, with a notice, for one minor version |

`parley top` and `parley web-ui` still work: the broker's tool forwards to the separate executable.
One command, as before.

## Reaching the web UI on a headless machine

It is an HTTP server and needs no display. It binds loopback, so tunnel to it — which is what §16
permits:

```bash
ssh -L 5280:127.0.0.1:5280 agent-host
```

Then `http://127.0.0.1:5280` in a browser on your own machine.

## What to check after upgrading

```powershell
parley --version          # all four tools carry the same version
parley registry --project north-helm
```

If the broker was not stopped, its agents are the same processes they were, and any open exchange is
still open.

## If a surface is missing

```
$ parley top
The terminal is not installed on this machine. It ships as `parley-top`.
Install it with:  .\install.ps1 -Surfaces terminal
```

Not "unknown command", which would be untrue.

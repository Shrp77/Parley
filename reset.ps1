<#
.SYNOPSIS
    Removes everything a pre-019 Parley left on this machine, so 0.32.0 starts from a clean layout.

.DESCRIPTION
    019 replaced `projects.json` and hand-named stores with one root holding `projects/<name>/`,
    each project owning its own `parley.db` and its own claimed port. **Nothing was ever released
    with the old layout**, so there is no migration: what exists on a developer's machine is
    scaffolding, and this removes it.

    What it does, in order:

      1. Stops the broker and removes the autostart entry, because `StoreLock` holds each store
         with no sharing and a running broker cannot have its files deleted underneath it.
      2. Reads the old `projects.json` *before* deleting it, and removes each store it names
         along with its `-wal`, `-shm`, `.lock`, `.owner`, `.files` and `.files-incoming`
         companions. Reading first is the whole reason this is a script rather than four
         commands: once the manifest is gone, nothing knows where those stores were.
      3. Deletes `projects.json` itself.

    What it deliberately KEEPS:

      * **`node.json`** — this machine's agent catalogue, keyed by identity since 018 and
        unchanged by 019. Deleting it would mean re-enrolling every repository by hand, which is
        a real cost for no benefit.
      * **`ports.json`** — if 0.32.0 has already handed ports out, those projects are the truth
        and the record is rebuilt from them anyway.
      * Anything under `projects/`, which is the new layout and is not this script's business.

    **This file is the specification for `reset.sh`, and said so while that gap was open.** CI runs
    on Windows, Linux and macOS and `install.sh` covers all three, so a developer setting Parley up
    on Linux or macOS had no way to clear a pre-019 layout but by hand.

    The counterpart was written on 2026-09-25 (T080) and exercised in a Linux container against
    built layouts rather than reasoned about: an old manifest naming two stores with their
    companions, a per-project catalogue outside the root, and three repositories including one
    whose `.mcp.json` is not JSON. `ResetScriptTests` is what keeps the two in step; behaviour is
    the same and only the shell differs.

.PARAMETER Root
    Where Parley keeps this machine's files. Defaults to the same location the tools resolve.

.PARAMETER WhatIf
    Say what would be removed and remove nothing. Run this first.

.PARAMETER Force
    Do not ask before deleting. Without it, the stores are listed and confirmation is taken.

.PARAMETER FilesOnly
    Delete files and touch nothing else: no autostart entry, no running process. **This is what a
    test or a dry run against a scratch directory must pass**, and the reason it exists is a defect
    this script had on its first run: `-Root` pointed the file half at a throwaway directory while
    `parley autostart remove` went on acting on the real machine, and removed a live logon entry.
    A parameter that sandboxes the files and not the machine is not a sandbox.

.PARAMETER Everything
    Leave nothing at all: the whole root goes, including `node.json`, `ports.json` and the new
    `projects/` layout, and the old manifest's out-of-root stores go with it.

    **Without this, the script clears the layout 019 replaced and not the one it introduced** --
    it says so in the paragraph above about what it keeps, and that was right while the only thing
    on a machine was scaffolding from before 019. It is wrong for the loop this is now used in:
    setting the new layout up, finding a defect, and starting again. A reset that cannot remove the
    thing the last run created works exactly once.

    `node.json` goes too. Keeping it saves re-enrolling and costs correctness: it is this machine's
    catalogue of which checkouts may be started, keyed by identity, and every entry in it names a
    project that no longer exists after a wipe. Enrolment writes it again.

.PARAMETER Repositories
    A directory of checkouts. Every `parley` and `parley-<project>` key is taken out of each
    repository's `.mcp.json`, and a file left with no servers is deleted.

    Here because the launch configuration is the one piece of a deployment that does not live under
    the root, so a wipe that ignored it would leave every repository pointing at a broker that is
    gone -- and, across iterations that try different project names, accumulating a key per attempt.

.EXAMPLE
    .\reset.ps1 -WhatIf

.EXAMPLE
    .\reset.ps1 -Everything -Force -Repositories C:\DevHell\Github
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Root = (Join-Path $env:APPDATA 'Parley'),
    [switch] $Force,
    [switch] $FilesOnly,
    [switch] $Everything,
    [string] $Repositories
)

$ErrorActionPreference = 'Stop'

function Write-Step { param([string] $Text) Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Kept { param([string] $Text) Write-Host "    keeping $Text" -ForegroundColor DarkGray }

Write-Step "Root: $Root"

# **Not an early exit when there are repositories to clear**, which is the second iteration of the
# loop this script exists for: the root was removed last time and the launch entries it wrote are
# still sitting in every checkout. Exiting here because the root is absent would report a clean
# machine while leaving each repository pointing at a broker that no longer exists.
if (-not (Test-Path -LiteralPath $Root) -and -not $Repositories) {
    Write-Host "Nothing to reset: $Root does not exist." -ForegroundColor Green
    exit 0
}

# ---- 1. stop the broker -------------------------------------------------------------------
#
# Before anything is read, because a broker started between the read and the delete would hold a
# store this script has already decided to remove -- and on Windows that is a refusal rather than
# a silent failure, which is the better of the two but still leaves half a reset.

# **Skipped entirely under -FilesOnly, and that is not a convenience.** `parley autostart remove`
# and `Stop-Process` act on this machine whatever -Root says, so a run pointed at a scratch
# directory would still take down a real logon entry -- which is exactly what happened the first
# time this script was run, against the machine that was writing it.

if ($FilesOnly) {
    Write-Step 'Files only: leaving the autostart entry and any running broker alone'
} else {
    Write-Step 'Stopping the broker and removing the autostart entry'

    $parley = Get-Command parley -ErrorAction SilentlyContinue

    if ($parley) {
        if ($PSCmdlet.ShouldProcess('this machine''s autostart entry', 'Remove')) {
            # Best effort: an entry that is not installed is not a failure. What matters is that
            # nothing is holding a store afterwards.
            & parley autostart remove 2>&1 |
                ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        }
    } else {
        Write-Host '    `parley` is not on PATH; skipping the autostart entry.' -ForegroundColor Yellow
    }

    # **Every tool, not just the broker.** `parley-web-ui` is started as a child the broker does not
    # wait on, so killing the broker alone leaves it holding its port -- and the next `autostart
    # install` then reports a web UI that could not bind, on a machine where nothing is using it.
    # `parley-terminal` is included because a terminal left open holds a view of a deployment that
    # is about to stop existing.
    foreach ($name in @('parley', 'parley-web-ui', 'parley-terminal')) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            if ($PSCmdlet.ShouldProcess("$name (pid $($_.Id))", 'Stop')) {
                Write-Host "    stopping $name (pid $($_.Id))" -ForegroundColor DarkGray
                $_ | Stop-Process -Force
            }
        }
    }
}

# ---- 2. read the old manifest before removing anything ------------------------------------

$manifest = Join-Path $Root 'projects.json'
$stores = @()
$catalogues = @()

if (Test-Path -LiteralPath $manifest) {
    Write-Step "Reading $manifest"

    try {
        $document = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json

        foreach ($project in @($document.projects)) {
            if ($project.store) {
                $stores += [pscustomobject]@{ Name = $project.name; Store = $project.store }
            }

            # **The per-project catalogue, which 018 deleted the meaning of and 019 the field.** An
            # old manifest can name one anywhere, and a machine set up before 018 has one per project
            # sitting outside the root -- so the root going does not take it, and the next run finds
            # a catalogue describing repositories that answer in a project that no longer exists.
            # Found on the first real wipe: a per-project catalogue outside the root survived a
            # reset that reported itself complete.
            if ($project.node_config) {
                $catalogues += $project.node_config
            }
        }
    } catch {
        # A manifest nobody can parse still gets deleted -- it is scaffolding either way -- but
        # its stores cannot be found, and saying so is better than implying the reset was whole.
        Write-Host "    could not parse it: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host '    its stores cannot be located; delete them by hand if you know where they are.' -ForegroundColor Yellow
    }
} else {
    Write-Host "    no projects.json at $Root" -ForegroundColor DarkGray
}

# ---- 3. confirm, then delete ---------------------------------------------------------------

if ($stores.Count -gt 0) {
    Write-Step "$($stores.Count) store(s) named by the old manifest"

    foreach ($entry in $stores) {
        $exists = if (Test-Path -LiteralPath $entry.Store) { '' } else { '  (already gone)' }
        Write-Host "    $($entry.Name.PadRight(16)) $($entry.Store)$exists"
    }

    if (-not $Force -and -not $WhatIfPreference) {
        Write-Host ''
        Write-Host 'These files hold every conversation in those projects. Nothing here is recoverable.' -ForegroundColor Yellow
        $answer = Read-Host 'Type the word delete to remove them'

        if ($answer -ne 'delete') {
            Write-Host 'Nothing was deleted.' -ForegroundColor Green
            exit 0
        }
    }
}

# The companions StoreFiles names, plus SQLite's own two. A store deleted without them leaves a
# write-ahead log and a lock file that the next thing to open that path inherits.
$companions = @('', '-wal', '-shm', '.lock', '.owner')
$directories = @('.files', '.files-incoming')

foreach ($entry in $stores) {
    foreach ($suffix in $companions) {
        $path = "$($entry.Store)$suffix"

        if (Test-Path -LiteralPath $path -PathType Leaf) {
            if ($PSCmdlet.ShouldProcess($path, 'Delete')) {
                Remove-Item -LiteralPath $path -Force
                Write-Host "    removed $path" -ForegroundColor DarkGray
            }
        }
    }

    foreach ($suffix in $directories) {
        $path = "$($entry.Store)$suffix"

        if (Test-Path -LiteralPath $path -PathType Container) {
            if ($PSCmdlet.ShouldProcess($path, 'Delete')) {
                Remove-Item -LiteralPath $path -Recurse -Force
                Write-Host "    removed $path" -ForegroundColor DarkGray
            }
        }
    }
}

# Only the ones outside the root, since the root is removed whole under -Everything and this would
# otherwise report removing a file that is about to go anyway.
foreach ($catalogue in ($catalogues | Sort-Object -Unique)) {
    if ((Test-Path -LiteralPath $catalogue -PathType Leaf) -and
        -not $catalogue.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) {
        if ($PSCmdlet.ShouldProcess($catalogue, 'Delete')) {
            Remove-Item -LiteralPath $catalogue -Force
            Write-Host "    removed $catalogue" -ForegroundColor DarkGray
        }
    }
}

if (Test-Path -LiteralPath $manifest) {
    if ($PSCmdlet.ShouldProcess($manifest, 'Delete')) {
        Remove-Item -LiteralPath $manifest -Force
        Write-Host "    removed $manifest" -ForegroundColor DarkGray
    }
}

# The old broker log, which named stores that no longer exist.
$log = Join-Path $Root 'parley-broker.log'

if (Test-Path -LiteralPath $log) {
    if ($PSCmdlet.ShouldProcess($log, 'Delete')) {
        Remove-Item -LiteralPath $log -Force
        Write-Host "    removed $log" -ForegroundColor DarkGray
    }
}

# ---- 4. the layout 019 introduced ----------------------------------------------------------
#
# Only under -Everything, because clearing the new layout is destroying a working deployment
# rather than removing scaffolding. Everything a project owns lives inside its own folder under
# the root -- its `parley.db`, that store's companions, and the file location beside it -- so the
# root going is the whole of it, and there is no second list of paths to keep in step.

if ($Everything) {
    Write-Step 'Everything: removing the whole root'

    if (Test-Path -LiteralPath $Root) {
        if ($PSCmdlet.ShouldProcess($Root, 'Delete')) {
            # Read before deleting, purely so the report names what went. A count is the difference
            # between "it worked" and knowing a project was there to be removed.
            $projectsDirectory = Join-Path $Root 'projects'

            if (Test-Path -LiteralPath $projectsDirectory) {
                foreach ($folder in Get-ChildItem -LiteralPath $projectsDirectory -Directory -ErrorAction SilentlyContinue) {
                    Write-Host "    removing project $($folder.Name)" -ForegroundColor DarkGray
                }
            }

            Remove-Item -LiteralPath $Root -Recurse -Force
            Write-Host "    removed $Root" -ForegroundColor DarkGray
        }
    }
} else {
    foreach ($kept in @('node.json', 'ports.json', 'projects')) {
        $path = Join-Path $Root $kept

        if (Test-Path -LiteralPath $path) {
            Write-Kept $path
        }
    }
}

# ---- 5. the launch configurations, which are the one thing outside the root ------------------

if ($Repositories) {
    Write-Step "Clearing Parley launch entries under $Repositories"

    if (-not (Test-Path -LiteralPath $Repositories)) {
        Write-Host "    $Repositories is not a directory; nothing to clear." -ForegroundColor Yellow
    } else {
        $cleared = 0

        foreach ($repository in Get-ChildItem -LiteralPath $Repositories -Directory -ErrorAction SilentlyContinue) {
            $launch = Join-Path $repository.FullName '.mcp.json'

            if (-not (Test-Path -LiteralPath $launch -PathType Leaf)) {
                continue
            }

            try {
                $document = Get-Content -LiteralPath $launch -Raw | ConvertFrom-Json
            } catch {
                # Left alone rather than rewritten. A file this cannot parse is one somebody else
                # owns the shape of, and guessing at it is how a reset eats an editor's settings.
                Write-Host "    $($repository.Name): .mcp.json is not JSON; left alone" -ForegroundColor Yellow
                continue
            }

            if (-not $document.mcpServers) { continue }

            # `parley` and `parley-<project>` are the keys this tooling owns, per LaunchKey. Nothing
            # else is touched: an .mcp.json routinely holds servers that have no connection to Parley.
            $ours = @($document.mcpServers.PSObject.Properties.Name |
                Where-Object { $_ -eq 'parley' -or $_ -like 'parley-*' })

            if ($ours.Count -eq 0) { continue }

            if ($PSCmdlet.ShouldProcess($launch, "Remove $($ours -join ', ')")) {
                foreach ($key in $ours) {
                    $document.mcpServers.PSObject.Properties.Remove($key)
                }

                if ($document.mcpServers.PSObject.Properties.Name.Count -eq 0 -and
                    $document.PSObject.Properties.Name.Count -eq 1) {
                    Remove-Item -LiteralPath $launch -Force
                    Write-Host "    $($repository.Name): removed .mcp.json (nothing else in it)" -ForegroundColor DarkGray
                } else {
                    $document | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $launch -Encoding utf8NoBOM
                    Write-Host "    $($repository.Name): removed $($ours -join ', ')" -ForegroundColor DarkGray
                }

                $cleared++
            }
        }

        Write-Host "    $cleared repositor$(if ($cleared -eq 1) { 'y' } else { 'ies' }) cleared" -ForegroundColor DarkGray
    }
}

Write-Host ''
Write-Host 'Done. This machine has no projects now.' -ForegroundColor Green
Write-Host ''
Write-Host '  parley terminal                  create one; it gets its own store and port' -ForegroundColor White
Write-Host '  parley autostart install         serve every project at logon' -ForegroundColor White

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

    **Windows only, and that is a gap rather than a decision about the design.** CI runs on Windows,
    Linux and macOS and `install.sh` covers all three, so a developer setting Parley up on Linux or
    macOS has no way to clear a pre-019 layout but by hand. A `reset.sh` is owed — see T080 in
    `specs/019-one-way-in/tasks.md`; this file is the specification for it.

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

.EXAMPLE
    .\reset.ps1 -WhatIf

.EXAMPLE
    .\reset.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Root = (Join-Path $env:APPDATA 'Parley'),
    [switch] $Force,
    [switch] $FilesOnly
)

$ErrorActionPreference = 'Stop'

function Write-Step { param([string] $Text) Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Kept { param([string] $Text) Write-Host "    keeping $Text" -ForegroundColor DarkGray }

Write-Step "Root: $Root"

if (-not (Test-Path -LiteralPath $Root)) {
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

    Get-Process -Name 'parley' -ErrorAction SilentlyContinue | ForEach-Object {
        if ($PSCmdlet.ShouldProcess("parley (pid $($_.Id))", 'Stop')) {
            Write-Host "    stopping parley (pid $($_.Id))" -ForegroundColor DarkGray
            $_ | Stop-Process -Force
        }
    }
}

# ---- 2. read the old manifest before removing anything ------------------------------------

$manifest = Join-Path $Root 'projects.json'
$stores = @()

if (Test-Path -LiteralPath $manifest) {
    Write-Step "Reading $manifest"

    try {
        $document = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json

        foreach ($project in @($document.projects)) {
            if ($project.store) {
                $stores += [pscustomobject]@{ Name = $project.name; Store = $project.store }
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

foreach ($kept in @('node.json', 'ports.json', 'projects')) {
    $path = Join-Path $Root $kept

    if (Test-Path -LiteralPath $path) {
        Write-Kept $path
    }
}

Write-Host ''
Write-Host 'Done. This machine has no projects now.' -ForegroundColor Green
Write-Host ''
Write-Host '  parley project add <name>        create one; it gets its own store and port' -ForegroundColor White
Write-Host '  parley autostart install         serve every project at logon' -ForegroundColor White

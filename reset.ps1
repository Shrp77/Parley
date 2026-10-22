<#
.SYNOPSIS
    Clears this machine's Parley so the next run starts from nothing.

.DESCRIPTION
    A deployment is one root holding `projects/<name>/`. Each project folder owns its
    `project.json`, its `parley.db` and that store's companions (`-wal`, `-shm`, `.lock`, `.owner`,
    `.files`, `.files-incoming`), and its port is recorded in `ports.json` beside them. Beside the
    projects the root holds `node.json`, this machine's catalogue of which checkouts may be started.

    What it does, in order:

      1. Stops the broker and removes the autostart entry, because `StoreLock` holds each store
         with no sharing and a running broker cannot have its files deleted underneath it.
      2. Removes `parley-broker.log` from the root.
      3. With -Everything, removes the whole root: every project, every store, `node.json` and
         `ports.json`. Nothing else in the root needs a list of paths, because everything a
         project owns is inside its folder.
      4. With -Repositories, takes the Parley launch entries out of each checkout's `.mcp.json`.

    What it KEEPS without -Everything: `node.json`, `ports.json` and `projects/`. Each is named in
    the output.

.PARAMETER Root
    Where Parley keeps this machine's files. Defaults to the same location the tools resolve.

.PARAMETER WhatIf
    Say what would be removed and remove nothing. Run this first.

.PARAMETER Force
    Do not ask before deleting.

.PARAMETER FilesOnly
    Delete files and touch nothing else: no autostart entry, no running process. **This is what a
    test or a dry run against a scratch directory must pass.** `parley autostart remove` and
    `Stop-Process` act on this machine whatever -Root says, so without it a run pointed at a
    throwaway directory still takes down a real logon entry.

.PARAMETER Everything
    Leave nothing at all: the whole root goes, including `node.json`, `ports.json` and every
    project. A reset that cannot remove what the last run created works exactly once.

    `node.json` goes too. Every entry in it names a project that no longer exists after a wipe,
    and enrolling writes it again.

.PARAMETER Repositories
    A directory of checkouts. Every `parley` and `parley-<project>` key is taken out of each
    repository's `.mcp.json`, and a file left with no servers is deleted.

    Here because the launch configuration is the one piece of a deployment that does not live under
    the root, so a wipe that ignored it would leave every repository pointing at a broker that is
    gone.

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

# **Refused before anything is removed.** The root comes from an argument, and -Everything deletes it whole, so a
# drive root or the profile directory would take the machine with it. A directory that has something in it and none of
# what a Parley root holds is somebody else's, however it was named.
$resolved = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
$profileRoot = [System.IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\', '/')

if ($resolved -eq '' -or $resolved -match '^[A-Za-z]:$' -or $resolved -ieq $profileRoot) {
    Write-Error "Refusing to reset ${Root}: that is not a Parley root."
}

if ((Test-Path -LiteralPath $Root -PathType Container) `
        -and -not (@('projects', 'node.json', 'ports.json', 'parley-broker.log') |
            Where-Object { Test-Path -LiteralPath (Join-Path $Root $_) }) `
        -and (Get-ChildItem -LiteralPath $Root -Force | Select-Object -First 1)) {
    Write-Error "Refusing to reset ${Root}: it holds none of projects, node.json, ports.json or parley-broker.log, so it does not look like one."
}

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
# Before anything is deleted, because a broker started in between would hold a
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

# ---- 2. confirm ------------------------------------------------------------------------------

$projectsDirectory = Join-Path $Root 'projects'
$projectFolders = @()

if (Test-Path -LiteralPath $projectsDirectory) {
    $projectFolders = @(Get-ChildItem -LiteralPath $projectsDirectory -Directory -ErrorAction SilentlyContinue)
}

if ($Everything -and $projectFolders.Count -gt 0) {
    Write-Step "$($projectFolders.Count) project(s) under $projectsDirectory"

    foreach ($folder in $projectFolders) {
        Write-Host "    $($folder.Name)"
    }

    if (-not $Force -and -not $WhatIfPreference) {
        Write-Host ''
        Write-Host 'These folders hold every conversation in those projects. Nothing here is recoverable.' -ForegroundColor Yellow
        $answer = Read-Host 'Type the word delete to remove them'

        if ($answer -ne 'delete') {
            Write-Host 'Nothing was deleted.' -ForegroundColor Green
            exit 0
        }
    }
}

# ---- 3. the broker log ---------------------------------------------------------------------

$log = Join-Path $Root 'parley-broker.log'

if (Test-Path -LiteralPath $log) {
    if ($PSCmdlet.ShouldProcess($log, 'Delete')) {
        Remove-Item -LiteralPath $log -Force
        Write-Host "    removed $log" -ForegroundColor DarkGray
    }
}

# ---- 4. the root ---------------------------------------------------------------------------
#
# Only under -Everything, because clearing the projects destroys a working deployment. Everything
# a project owns lives inside its own folder under the root -- its `parley.db`, that store's
# companions, and the file location beside it -- so the root going is the whole of it, and there
# is no second list of paths to keep in step.

if ($Everything) {
    Write-Step 'Everything: removing the whole root'

    if (Test-Path -LiteralPath $Root) {
        if ($PSCmdlet.ShouldProcess($Root, 'Delete')) {
            foreach ($folder in $projectFolders) {
                Write-Host "    removing project $($folder.Name)" -ForegroundColor DarkGray
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
Write-Host 'Done.' -ForegroundColor Green
Write-Host ''
Write-Host '  parley                           create one; it gets its own store and port' -ForegroundColor White
Write-Host '  parley autostart install         serve every project at logon' -ForegroundColor White

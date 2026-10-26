<#
.SYNOPSIS
    Builds Parley from this checkout and installs its tools.

.DESCRIPTION
    Parley is not on nuget.org, so installing it means building it. That is three commands per
    tool and two of them have a trap in, which is why this exists rather than a paragraph in the
    README:

      * `dotnet tool update` does nothing at all when the version has not changed. It reports
        success, at some length, and replaces nothing -- so a rebuilt tool appears to install and
        does not, on the machine most likely to be running the old one.

      * `--add-source <folder>` treats that folder as a feed, and `bin\Release` accumulates every
        version ever packed there. "I installed from a clean state" is then true only by luck.

    This clears the stale packages, packs, installs, and then checks that the version now on PATH
    is the one that was just built. That last step is the point: it is the only way to tell a
    successful install from a silent no-op.

    It then registers the broker to start when you log in, starts it, and checks that it answers.
    The broker serves every project this machine names. A machine that names none yet is served
    too, waiting for the first: this never asks where a store goes, because a project says that,
    and a project is named in the terminal (`parley-terminal`).
    Installing the tools is not the same as having Parley working: the broker is a separate
    process that owns the store, and until one is running every agent fails to attach with "No
    broker is listening" -- a message that arrives in an editor, days later, rather than here.

.PARAMETER Uninstall
    Removes every tool in the table above and the autostart entry, and stops. -Surfaces is not
    consulted: an uninstall that left a surface behind would leave a tool nothing installs and
    nothing removes. The store and any .mcp.json are left
    alone -- they are the deployment, and somebody reinstalling a tool has not asked to lose
    their history or their launch configurations.

.PARAMETER Surfaces
    Which operator surfaces to install: terminal, web-ui, both or none. Defaults to both on
    Windows and to web-ui elsewhere.

    The surfaces are separate tools so that changing one does not stop the broker: Windows
    cannot replace a running executable, and stopping the broker ends every resident agent
    on the machine along with everything each had learned. Being separate, they are also
    skippable, and a machine that only runs agents has no reason for either.

    A surface not named here is not removed. It is left at whatever version it already had,
    and this script says so on the way out -- a skipped surface going quietly is how a
    machine ends up running a terminal from three versions ago.

.PARAMETER Only
    Install exactly these tools, by command name, and leave every other one alone.
    -Only parley-terminal replaces the terminal and does not stop the broker, because
    the broker's binary is not among the files being unlinked.

    That matters more than it sounds. SPEC.md section 12 says three things end a resident agent,
    and the first is the node stopping and telling every agent it started to quit -- and the
    process is what held everything the agent had learned. Restarting a broker to change a
    terminal costs every agent's accumulated context on the machine.

.PARAMETER Force
    Ends whatever is holding Parley's files, rather than refusing and saying what to close.

    Without it this script stops the broker it registered and nothing else, on the grounds that
    ending a process somebody launched themselves is their decision rather than a script's. That
    is the right default and it is also, on a machine with three editors open, an instruction to
    go and find three windows before anything can be installed. -Force is that decision being
    made deliberately, once, on the command line.

    What it ends is derived from the tools this installs -- a broker started by hand, and the
    `parley-mcp` adapter of every editor session with Parley configured. Those sessions keep
    running and lose their Parley tools until the editor is restarted, which is the cost and is
    why this is not the default.

    It does not weaken a single check. Every process is waited for and then looked for again,
    because Windows will not unlink a binary that is still mapped and a kill that has not landed
    yet fails in exactly the way the guards exist to prevent -- so anything that survives is
    still fatal, and says so.

.PARAMETER Hidden
    Registers a logon entry that opens no console window, by passing --hidden to
    `parley autostart install`.

    The entry runs the broker in your own session, because the node's job is starting agents and
    an agent is a CLI you have already signed in to -- so a broker outside your session would
    start agents that cannot log in. Windows gives a console program in a session a console, and
    the window is the price of being in the right one. --hidden is how it is not paid: the broker
    lets go of the console and everything it would have said is in the log this script names.

    Opt-in rather than the default, so that a machine installed without it behaves exactly as it
    did before. Nothing is lost by passing it -- the window shows nothing the log does not hold --
    but changing what an existing machine does at logon is not this script's decision to make
    quietly.

.PARAMETER Shortcut
    Windows only. Puts a "Parley web console" shortcut in the Start menu and on the desktop, with Parley's icon, that
    opens the web console the broker serves (`parley web-ui --open`). When the web UI is not running it says why in a
    message box rather than doing nothing. Pin it to the taskbar from the Start menu.

    Opt-in, because putting things on somebody's desktop is not this script's decision to make quietly. Running the
    script again without it leaves a shortcut already made alone, and -Uninstall removes it.

.PARAMETER Reset
    Wipes this machine's Parley first, by running reset.ps1 -Everything -Force, and then installs.
    Every project, store, conversation, port and node.json goes, and nothing asks before it does:
    passing -Reset is the decision.

    It runs after the build and pack succeed and before anything is installed. A build that fails
    leaves the machine as it was, rather than wiped with nothing to replace it.

    Refused with -Uninstall, which already removes what it installed, and with -Only, whose point
    is leaving the broker running.

.PARAMETER Repositories
    With -Reset only: a directory of checkouts whose .mcp.json files lose their Parley entries,
    passed to reset.ps1 as its own -Repositories.

.EXAMPLE
    .\install.ps1

.EXAMPLE
    .\install.ps1 -Reset -Repositories C:\DevHell\Github

.EXAMPLE
    .\install.ps1 -Force

.EXAMPLE
    .\install.ps1 -Hidden

.EXAMPLE
    .\install.ps1 -Hidden -Shortcut

.EXAMPLE
    .\install.ps1 -Uninstall
#>

[CmdletBinding()]
param(
    [switch] $Uninstall,
    [switch] $Force,
    [switch] $Hidden,
    [switch] $Shortcut,
    [string] $Surfaces,
    [string[]] $Only,
    [switch] $Reset,
    [string] $Repositories
)

$ErrorActionPreference = 'Stop'

# **Leaves the console in a state a person can type in, before doing anything else.**
#
# A terminal left in mouse-reporting mode prints `[555;47;16M` at the prompt on every mouse
# movement. The mode belongs to the window rather than to any process, so it outlives whatever set
# it and ruins every command typed after. Sending a disable for a mode that is already off does
# nothing, so this is free on a healthy window and repairs a stuck one.
#
# Four of the six modes `ConsoleRestore` handles: the three mouse ones and bracketed paste. The
# alternate screen is left alone on purpose -- `?1049l` switches back to the main screen, and a
# script that did that to somebody running it from inside a full-screen program would throw away
# what was on their screen to fix a problem they did not have.
#
# Skipped when output is redirected, where these would land in somebody's log file as noise.
#
# Written out one mode at a time rather than built in a loop, so that the sequences are greppable:
# `install.sh` spells them literally too, and the check that both scripts send them can only read
# what is written. A loop over a list of numbers is tidier and says nothing to anybody searching
# for `?1003l`.
if (-not [Console]::IsOutputRedirected) {
    $e = [char]27

    [Console]::Write("$e[?1003l")
    [Console]::Write("$e[?1015l")
    [Console]::Write("$e[?1006l")
    [Console]::Write("$e[?2004l")
    [Console]::Write("$e[?25h")
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
# Four columns rather than three. `Surface` says which of the two operator surfaces a tool is,
# and empty means it is not one: the broker and the agent adapter are what Parley is, and every
# machine that runs Parley at all needs both.
#
# A surface is optional because the surfaces are where the cost is. A machine that only runs
# agents does not need a terminal, and a headless Linux box has no browser to point at a web UI
# -- though it can still serve one over a tunnel, which is why the default there is the web UI
# and not nothing.
$tools = @(
    @{ Package = 'Parley.Cli'; Project = 'src\Parley.Cli\Parley.Cli.csproj'; Command = 'parley';     Surface = '' }
    @{ Package = 'Parley.Mcp'; Project = 'src\Parley.Mcp\Parley.Mcp.csproj'; Command = 'parley-mcp'; Surface = '' }
    @{ Package = 'Parley.Terminal'; Project = 'src\Parley.Terminal\Parley.Terminal.csproj'; Command = 'parley-terminal'; Surface = 'terminal' }
    @{ Package = 'Parley.WebUi'; Project = 'src\Parley.WebUi\Parley.WebUi.csproj'; Command = 'parley-web-ui'; Surface = 'web-ui' }
)

# Which surfaces this machine wants, resolved once. Windows takes both; anything else takes the
# web UI alone, because a terminal surface on a machine nobody has a session on is a package
# installed to be never run.
if (-not $Surfaces) {
    $Surfaces = if ([Environment]::OSVersion.Platform -eq 'Win32NT') { 'both' } else { 'web-ui' }

    # Said only when it decides anything. -Only names the tools outright, so announcing a
    # surface set it does not consult is a line that contradicts what the run then does.
    if (-not $Only) {
        Write-Host "    surfaces: $Surfaces (this machine's default)" -ForegroundColor DarkGray
    }
}

# Checked before anything is built. A mistyped -Only would otherwise select no tool at all and
# this script would pack nothing, install nothing, and report success -- which is the exact shape
# of failure every other guard in this file exists to prevent.
if ($Only) {
    $known = @($tools | ForEach-Object { $_.Command })
    $unknown = @($Only | Where-Object { $known -notcontains $_ })

    if ($unknown.Count -gt 0) {
        Write-Error "-Only names no such tool: $($unknown -join ', '). This repository ships $($known -join ', ')."
    }
}

$wanted = switch ($Surfaces) {
    'both'     { @('terminal', 'web-ui') }
    'terminal' { @('terminal') }
    'web-ui'   { @('web-ui') }
    'none'     { @() }
    default    { Write-Error "-Surfaces takes terminal, web-ui, both or none, and was given '$Surfaces'."; @() }
}

<#
.SYNOPSIS
    Whether this machine asked for a tool.
.DESCRIPTION
    A tool with no surface is always installed: it is not optional, it is Parley. A surface tool
    is installed only when -Surfaces named it.
#>
function Wanted([hashtable] $tool) {
    # -Only is exact and overrides everything: it names the tools, so a surface set would be a
    # second answer to a question already answered.
    if ($Only) { return $Only -contains $tool.Command }

    return -not $tool.Surface -or $wanted -contains $tool.Surface
}

# What `parley autostart install` calls the scheduled task. Duplicated from AutostartPlan.Name
# rather than asked for, because the one command that could report it is the one this script
# may be in the middle of deleting.
$task = 'parley-broker'

# The command the broker runs as, so the pre-flight below can leave it out of the adapters and
# recognise it afterwards.
$broker = 'parley'

<#
.SYNOPSIS
    Which of this repository's tools are installed globally, lower-cased.
.DESCRIPTION
    Asked of `dotnet tool list` rather than inferred from an exit code. `dotnet tool uninstall`
    returns non-zero both when a tool was absent and when it could not be removed, and treating
    those two as one is what let this script announce a clean uninstall while a tool it had
    failed to delete was still on PATH.
#>
function Installed {
    $listed = & dotnet tool list -g 2>$null

    return @($tools |
        Where-Object { $listed -match ('^' + [regex]::Escape($_.Package.ToLowerInvariant()) + '\s') } |
        ForEach-Object { $_.Package.ToLowerInvariant() })
}

<#
.SYNOPSIS
    Stops the broker the autostart entry started, and waits for it to go.
.DESCRIPTION
    Needed by both paths, for the same reason: Windows will not unlink a binary that is mapped
    into a running process, so `dotnet tool uninstall` fails against a live broker.

    That failure is silent in a way worth spelling out. `dotnet tool uninstall` returns non-zero
    for a tool that could not be removed and for one that was never there; `dotnet tool install`
    then returns *zero*, prints "is already installed", and installs nothing. So the pair reports
    complete success while leaving the previous build exactly where it was -- and the version
    check afterwards cannot tell, because the version it finds is the version it expected.

    A broker somebody started by hand is left alone. Ending a process a person launched
    themselves is their decision, and the checks downstream report honestly when it blocks them.
#>
function Stop-Broker {
    if (-not (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)) { return }

    Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue

    # Waited for rather than assumed. Stopping a task asks the process to go; it goes once it has
    # closed its listener and its streams, which is quick but is not instant.
    #
    # **The broker alone here, and the web UI below.** Waiting for both at once would spend the
    # whole ten seconds on exactly the machine this exists to help: an orphaned web UI is never
    # going to exit on its own, so the wait would run to its deadline before reaching the code
    # that ends it.
    $deadline = (Get-Date).AddSeconds(10)

    while ((Get-Process -Name 'parley' -ErrorAction SilentlyContinue) -and
           (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }

    # **A web UI whose broker has gone is an orphan, and it is this script that orphaned it.**
    # Stopping a scheduled task terminates the process; it does not ask it to exit, so the broker
    # never reaches the code that ends the child it started. The child keeps running, keeps the
    # port, and keeps `parley-web-ui.exe` mapped -- which refuses the very install that stopped
    # the broker, every time, with a refusal naming a process the person never started and cannot see.
    #
    # **Told apart by its parent rather than by its name**, because a web UI somebody started by
    # hand is still theirs and this must not reach for it. An orphan has a parent id that no
    # longer resolves; that is the whole test, and it is exact.
    foreach ($stray in @(Get-CimInstance Win32_Process -Filter "Name = 'parley-web-ui.exe'" -ErrorAction SilentlyContinue)) {
        if (Get-Process -Id $stray.ParentProcessId -ErrorAction SilentlyContinue) { continue }

        Write-Note "ending the web UI the stopped broker left behind, pid $($stray.ProcessId)"

        try {
            Stop-Process -Id $stray.ProcessId -Force -ErrorAction Stop
        }
        catch {
            # Reported by the guard that looks again below, which is the one that can refuse.
        }
    }

    # **And then waited for.** `Stop-Process` returns once the kill has been asked for, not once
    # the process has gone (`Remove-Holders` says the same), and announcing an ended pid and then
    # refusing the install naming that pid would contradict itself.
    #
    # The same wait covers a web UI exiting with its broker on the ordinary path, where nothing
    # was killed at all and the child simply goes a moment after its parent.
    $deadline = (Get-Date).AddSeconds(10)

    while ((Get-Process -Name 'parley-web-ui' -ErrorAction SilentlyContinue) -and
           (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
}

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Note($text) { Write-Host "    $text" -ForegroundColor DarkGray }

# What -Shortcut makes, named once so -Uninstall removes exactly what an install made and nothing a person put there.
$shortcutName = 'Parley web console.lnk'
$shortcutHome = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Parley' } else { $null }
$opener = if ($shortcutHome) { Join-Path $shortcutHome 'open-web-console.ps1' } else { $null }
$icon = if ($shortcutHome) { Join-Path $shortcutHome 'parley.ico' } else { $null }

<#
.SYNOPSIS
    Where the shortcuts go: the Start menu, so it can be searched for and pinned, and the desktop.
#>
function Shortcut-Paths {
    return @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) $shortcutName),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) $shortcutName)
    )
}

<#
.SYNOPSIS
    Makes the Start menu and desktop shortcuts that open the web console.
.DESCRIPTION
    **The shortcut runs a script, not `parley` itself.** `parley` is a console program, so a shortcut to it flashes a
    console window every time, and when the web UI is not running the reason is printed into that window as it closes.
    The script runs hidden and calls `parley web-ui --open`, and when that fails it shows what parley said in a message
    box, so a click never does nothing without saying why.

    The icon is the web console's own, wrapped as an .ico: a shortcut cannot use a .png, and an .ico may hold one whole.
#>
function New-WebConsoleShortcut([string] $address) {
    Write-Step 'Making the web console shortcut'

    $parleyExe = Join-Path $shims 'parley.exe'

    New-Item -ItemType Directory -Force -Path $shortcutHome | Out-Null

    # Single-quoted in the script, so a quote in a path is doubled rather than ending the string.
    $quotedExe = "'" + $parleyExe.Replace("'", "''") + "'"
    $at = if ($address) { " '" + $address.Replace("'", "''") + "'" } else { '' }

    $lines = @(
        '# Written by install.ps1 -Shortcut, and removed by install.ps1 -Uninstall.',
        # Each line as its own text: Windows PowerShell wraps a native program's error output in an error record, and
        # printing the record would put its "At line 2" noise in the box around what parley said.
        ('$said = (& ' + $quotedExe + ' web-ui --open' + $at + ' 2>&1 | ForEach-Object { "$_" }) -join "`n"'),
        'if ($LASTEXITCODE -ne 0) {',
        '    Add-Type -AssemblyName PresentationFramework',
        "    [void][System.Windows.MessageBox]::Show(`$said.Trim(), 'Parley web console')",
        '}'
    )
    Set-Content -LiteralPath $opener -Value $lines -Encoding utf8

    # One image, stored whole as a PNG: a 6-byte header, one 16-byte entry, then the image.
    $png = [System.IO.File]::ReadAllBytes((Join-Path $root 'src\Parley.Web\Brand\icon.png'))
    # A PNG's width and height are big-endian at bytes 16 and 20. Only the low byte goes in the entry, where 0 means 256.
    $width = $png[19]
    $height = $png[23]
    $ico = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($ico)
    $writer.Write([uint16] 0); $writer.Write([uint16] 1); $writer.Write([uint16] 1)
    $writer.Write([byte] $width); $writer.Write([byte] $height); $writer.Write([byte] 0); $writer.Write([byte] 0)
    $writer.Write([uint16] 1); $writer.Write([uint16] 32); $writer.Write([uint32] $png.Length); $writer.Write([uint32] 22)
    $writer.Write($png)
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($icon, $ico.ToArray())
    $writer.Dispose()

    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $shell = New-Object -ComObject WScript.Shell

    foreach ($path in Shortcut-Paths) {
        $link = $shell.CreateShortcut($path)
        $link.TargetPath = $powershell
        $link.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$opener`""
        $link.IconLocation = "$icon,0"
        $link.Description = 'Open the Parley web console'
        # Minimised, so the moment before -WindowStyle Hidden takes effect shows no window either.
        $link.WindowStyle = 7
        $link.Save()

        Write-Note $path
    }

    Write-Note 'to pin it to the taskbar, right-click it in the Start menu and choose Pin to taskbar'
}

<#
.SYNOPSIS
    Removes what New-WebConsoleShortcut made, whether or not this run asked for it.
#>
function Remove-WebConsoleShortcut {
    if (-not $shortcutHome) { return }

    $made = @(Shortcut-Paths) + @($opener, $icon) | Where-Object { Test-Path -LiteralPath $_ }

    if ($made.Count -eq 0) { return }

    Write-Step 'Removing the web console shortcut'

    $made | ForEach-Object { Remove-Item -LiteralPath $_ -Force }

    # Only when nothing else is in it: the folder may hold something a person or another tool put there.
    if ((Test-Path -LiteralPath $shortcutHome) -and -not (Get-ChildItem -LiteralPath $shortcutHome -Force)) {
        Remove-Item -LiteralPath $shortcutHome -Force
    }
}

<#
.SYNOPSIS
    Ends the processes holding Parley's files, and reports which of them survived.
.DESCRIPTION
    Only ever called under -Force, and it returns rather than throws: the caller knows what it
    was guarding and can say so better than this can.

    It waits, and then it looks again. Stop-Process returns once the kill has been asked
    for, not once the process has gone, and a binary is still mapped for as long as the kernel
    is finishing with it -- so continuing straight from the kill would hit the very failure
    these guards exist to prevent, with -Force having reported success. That failure is the
    worst one this script has: `dotnet tool uninstall` fails on the locked file, `dotnet tool
    install` then exits *zero* saying "is already installed", and the version check afterwards
    cannot tell, because the version it finds is the version it expected.

    Each name is looked for rather than each process id, because a session that was starting as
    this ran is a new process holding the same files, and the question is whether anything holds
    them now rather than whether these particular ones are gone.
#>
function Remove-Holders([object[]] $running) {
    if (-not $running -or $running.Count -eq 0) { return @() }

    foreach ($process in $running) {
        Write-Note "ending $($process.ProcessName), pid $($process.Id)"

        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
        catch {
            # Gone between the listing and the kill, or not this user's to end. Neither is
            # decided here: the look below is what says whether the files are free.
        }
    }

    $names = @($running | ForEach-Object { $_.ProcessName } | Sort-Object -Unique)
    $deadline = (Get-Date).AddSeconds(10)

    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Name $names -ErrorAction SilentlyContinue)) { break }

        Start-Sleep -Milliseconds 200
    }

    return @(Get-Process -Name $names -ErrorAction SilentlyContinue)
}

# Checked before anything is stopped or built, so a combination that makes no sense costs nothing.
if ($Reset -and $Uninstall) {
    Write-Error '-Reset and -Uninstall together make no sense: -Uninstall removes what was installed and installs nothing.'
}

if ($Reset -and $Only) {
    Write-Error '-Reset wipes the whole deployment and stops the broker, and -Only exists to leave the broker running. Use one or the other.'
}

if ($Repositories -and -not $Reset) {
    Write-Error '-Repositories is only used with -Reset.'
}

if ($Shortcut -and [Environment]::OSVersion.Platform -ne 'Win32NT') {
    Write-Error '-Shortcut makes a Windows shortcut, and this is not Windows. `parley web-ui --open` opens the web console.'
}

if ($Shortcut -and $Uninstall) {
    Write-Error '-Shortcut and -Uninstall together make no sense: -Uninstall removes the shortcut along with everything else.'
}

# ---------------------------------------------------------------- uninstall --

if ($Uninstall) {
    # The broker first, before anything that deletes a file. Windows will not unlink a binary
    # that is still mapped into a running process, so `dotnet tool uninstall` fails against a
    # live broker -- and it fails the same way it fails when the tool was never there, which is
    # how this script came to print "Every tool is gone" with one of them still installed.
    Write-Step 'Stopping the broker'

    Stop-Broker

    # Anything left is a broker somebody started by hand. Ending a process a person launched
    # themselves is a decision for them, so without -Force this does not kill it -- the uninstall
    # below fails honestly and the check at the end says why. With -Force that decision has been
    # made, on the command line, by the person it belongs to.
    $byHand = @(Get-Process -Name 'parley' -ErrorAction SilentlyContinue)

    if ($byHand.Count -gt 0) {
        if ($Force) {
            $survived = Remove-Holders $byHand

            if ($survived.Count -gt 0) {
                Write-Error "A broker is still running after -Force tried to end it: $(($survived | ForEach-Object { "pid $($_.Id)" }) -join ', '). It is probably another user's, which this cannot end. Nothing has been removed."
            }
        }
        else {
            Write-Note 'a broker is still running that no autostart entry started (-Force ends it)'
        }
    }

    # Then the entry, still before the tools, because it names an executable about to be deleted.
    # The other order leaves a scheduled task pointing at a path that is no longer there, which
    # fails at logon, silently, somewhere nobody looks.
    Remove-WebConsoleShortcut

    Write-Step 'Removing the autostart entry'

    if (Get-Command parley -ErrorAction SilentlyContinue) {
        & parley autostart remove 2>&1 | Out-Null
    }
    else {
        Write-Note 'parley is not on PATH; nothing to remove it with'
    }

    $before = Installed

    foreach ($tool in $tools) {
        Write-Step "Removing $($tool.Package)"

        # Absent and unremovable are different, and reporting both as "was not installed" is what
        # turned a failed uninstall into a success message. Asked before acting, so the note
        # afterwards can say which of the two happened.
        if ($before -notcontains $tool.Package.ToLowerInvariant()) {
            Write-Note 'was not installed'
            continue
        }

        # Failure is not fatal here: stopping at the first one would leave the second installed,
        # and somebody running this to clean up wants both attempted.
        & dotnet tool uninstall -g $tool.Package 2>&1 | Out-Null
    }

    # The same check the install does, for the same reason. Every step above can report success
    # while leaving a tool exactly where it was, so the only honest closing line is one that
    # asked afterwards.
    $remaining = Installed

    if ($remaining.Count -gt 0) {
        Write-Error "Still installed: $($remaining -join ', '). A running process holds the files -- 'Get-Process parley' will show it -- and Windows will not delete a binary that is mapped. Stop it and run this again."
    }

    Write-Host ''
    Write-Host 'Every Parley tool is gone, and so are the autostart entry and any web console shortcut. The store and any .mcp.json'
    Write-Host 'are left alone: those are the deployment rather than the installation.'
    exit 0
}

# ------------------------------------------------------------ prerequisites --

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'The .NET SDK is not on PATH. Parley builds from source, so it needs one: https://dotnet.microsoft.com/download'
}

$sdk = (& dotnet --version).Trim()
Write-Step "Building with .NET SDK $sdk"

# The floor is whatever global.json pins, and dotnet itself enforces that far better than a
# string comparison here would. This is only worth saying so that a failure below reads as a
# version problem rather than a mysterious build error.
Write-Note 'global.json pins the SDK band; dotnet will say so if this one is outside it.'

# ------------------------------------------------------------------- clear ---

Write-Step 'Clearing previously packed versions'

foreach ($tool in $tools) {
    if (-not (Wanted $tool)) { continue }

    $output = Join-Path $root (Split-Path -Parent $tool.Project | Join-Path -ChildPath 'bin\Release')
    $stale = Get-ChildItem -Path $output -Filter '*.nupkg' -ErrorAction SilentlyContinue

    if ($stale) {
        Write-Note "$($stale.Count) old package(s) in $output"
        $stale | Remove-Item -Force
    }
}

# -------------------------------------------------------------------- pack ---

foreach ($tool in $tools) {
    if (-not (Wanted $tool)) {
        $why = if ($Only) { "-Only did not name $($tool.Command)" }
               else { "-Surfaces did not name $($tool.Surface)" }

        Write-Note "skipping $($tool.Package): $why"
        continue
    }

    Write-Step "Packing $($tool.Package)"

    & dotnet pack (Join-Path $root $tool.Project) -c Release --nologo -v quiet

    if ($LASTEXITCODE -ne 0) { Write-Error "Could not pack $($tool.Package)." }
}

# The version every package just took, read from the packages rather than from the project file.
# What matters is what was produced, and a build that ignored the property would be exactly the
# defect this check should catch.
# Read from whichever package was just built, rather than from Parley.Cli's: a run can pack only
# a surface, and Parley.Cli's package would then not exist.
$first = $tools | Where-Object { Wanted $_ } | Select-Object -First 1

if (-not $first) { Write-Error 'No tool was selected, so nothing would be installed.' }

$built = Get-ChildItem -Path $root -Filter "$($first.Package).*.nupkg" -Recurse |
    Where-Object { $_.FullName -like '*\bin\Release\*' } |
    ForEach-Object { $_.BaseName -replace "^$([regex]::Escape($first.Package))\.", '' } |
    Select-Object -First 1

if (-not $built) { Write-Error 'Nothing was packed. The build produced no package.' }

# ------------------------------------------------------------------- reset ---

# Here rather than first, so a build that fails leaves the machine as it was. reset.ps1 stops the
# broker and its children and removes the autostart entry itself; the steps below then find
# nothing to stop, which they already handle.
if ($Reset) {
    Write-Step 'Resetting this machine (-Reset)'

    $resetArguments = @{ Everything = $true; Force = $true }

    if ($Repositories) { $resetArguments.Repositories = $Repositories }

    & (Join-Path $root 'reset.ps1') @resetArguments
}

# ----------------------------------------------------------------- install ---

# Before anything is stopped. A running `parley-mcp` holds its own files in the tool store, so
# `dotnet tool uninstall` fails with "Access to the path ...\.store\parley.mcp\<version>' is
# denied". Checked after the broker was stopped, that would leave a machine that had been serving
# a deployment with no broker and one tool replaced and one not.
#
# The broker itself is not checked here. It is stopped a few lines below, deliberately and by
# name, which is a different thing from a session somebody has open.
Write-Step 'Checking nothing holds the tools'

# Derived from the table above rather than named again here. A third tool, or a rename, would
# otherwise leave this checking something that no longer exists -- quietly, because a guard that
# matches nothing looks exactly like a machine with nothing running.
#
# The broker's own command is excluded: it is stopped a few lines below, deliberately and by name.
# One started by hand is a different matter and is caught after that, where it can be told apart.
# **Only the tools about to be replaced.** A surface this run is skipping is not going to be
# unlinked, so a terminal somebody has open is no reason to refuse an install of the broker --
# and refusing anyway would make -Surfaces useless on the machine most likely to want it.
#
# **The web UI is excluded with it, for the same reason.** The broker starts `parley-web-ui` as a
# child and ends it when it ends, so a running web UI on a machine whose broker is about to be
# stopped is not a process anybody has to close. Counting it as a holder would refuse the install
# naming a window the person does not have open and cannot close, and tell them to use -Force to
# end something that was going to stop anyway.
#
# A web UI somebody started by hand is a different matter, and is caught after the broker has been
# stopped -- where it can be told apart, which is exactly what the broker's own exclusion does two
# lines above.
$adapters = @($tools |
    Where-Object { $_.Command -ne $broker -and (Wanted $_) } |
    ForEach-Object { $_.Command })

$webUiCommand = 'parley-web-ui'
$replacingBroker = @($tools | Where-Object { $_.Command -eq $broker -and (Wanted $_) }).Count -gt 0

if ($replacingBroker) {
    $adapters = @($adapters | Where-Object { $_ -ne $webUiCommand })
}

# **Empty is an ordinary answer.** `-Only parley` wants exactly the tool this list excludes by
# name, so nothing is left to look for -- and `Get-Process -Name @()` does not return nothing, it
# refuses the argument: "the argument is null, empty, or an element of the argument collection
# contains a null value".
# Written as if/else rather than a ternary, and that is not taste: `?` is PowerShell's alias for
# `Where-Object`, so a ternary broken across lines is read as a pipeline into it and fails with a
# message about a `Property` parameter nobody wrote.
if ($adapters.Count -eq 0) {
    $holders = @()
} else {
    $holders = @(Get-Process -Name $adapters -ErrorAction SilentlyContinue)
}

if ($holders.Count -gt 0) {
    $listed = ($holders | ForEach-Object { "      $($_.ProcessName), pid $($_.Id), started $($_.StartTime.ToString('HH:mm'))" }) -join "`n"

    # Written as a branch rather than leaning on Write-Error being fatal. It is, because
    # $ErrorActionPreference is Stop at the top of this file, but a reader should not have to
    # know that to see which of these two things happens.
    if ($Force) {
        Write-Note "$($holders.Count) adapter(s) hold the tool store; -Force is ending them"

        $survived = Remove-Holders $holders

        if ($survived.Count -gt 0) {
            Write-Error @"
$($survived.Count) adapter(s) are still running after -Force tried to end them:
$(($survived | ForEach-Object { "      $($_.ProcessName), pid $($_.Id)" }) -join "`n")
    Nothing has been changed and the broker has not been stopped. A process this user cannot
    end is the usual reason; 'Get-Process parley-mcp' will show whose it is.
"@
        }

        # Said plainly because it is the cost of the flag, and it is paid in somebody else's
        # window. Those sessions keep running; what they lose is Parley, until the editor restarts.
        Write-Note 'those editor sessions have lost their Parley tools until each editor restarts'
    }
    else {
        # Named by kind, because the two are closed differently and by different people. An
        # adapter belongs to an editor session somebody may not realise is Parley; a terminal is
        # a window they are looking at, and Ctrl+Q closes it.
        $kinds = @()

        if ($holders | Where-Object { $_.ProcessName -eq 'parley-mcp' }) {
            $kinds += 'an adapter belongs to an editor session with Parley configured; close the session'
        }

        if ($holders | Where-Object { $_.ProcessName -in 'parley-terminal' }) {
            $kinds += 'a terminal is a window somebody has open; Ctrl+Q leaves it'
        }

        # **Reached only when no broker is being replaced, or when one was started by hand.** A web
        # UI the broker started is excluded above, so anything named here is genuinely somebody's
        # own process -- and it has no window at all, which is precisely why it needs a sentence:
        # a person told to "close it" would look for something that was never on their screen.
        if ($holders | Where-Object { $_.ProcessName -eq $webUiCommand }) {
            $kinds += 'a web UI has no window; end it from the terminal it was started in, or with -Force'
        }

        # **Never empty, because the sentence is built from it.** With no kind matched it would be a
        # line containing one full stop, under a list of processes. A refusal that names what is
        # wrong and then says nothing about what to do is the half that gets read.
        if ($kinds.Count -eq 0) {
            $kinds += 'end them, or re-run with -Force'
        }

        Write-Error @"
$($holders.Count) Parley process(es) are running and hold the files this would replace:
$listed
    $($kinds -join "; ").
    Close them and run this again, or re-run with -Force to end them. Nothing has been changed
    and the broker has not been stopped.
"@
    }
}

Write-Note 'nothing is holding the tool store'

# Before anything replaces a file, and only when that file is the broker's. A live broker holds
# `parley` open, which makes the uninstall below fail and the install after it a no-op that
# reports success -- so it has to stop. But it only has to stop for its own binary.
#
# **This is the whole point of the split, and it is one `if`.** A run that replaces only the
# terminal unlinks a file the broker never opened, so there is nothing to stop: SPEC.md section 12
# has the node telling every agent it started to quit, and each of those processes is what held
# everything that agent had learned. Restarting a broker to change a terminal spends all of it.
#
# Decided above, where the holder check needed the same answer: the web UI is only excluded from
# that check because this is about to stop it. Two computations of one thing agree until one is
# edited, and these two would disagree in the direction that matters -- a web UI excluded from the
# guard and then never stopped is a locked file with nothing left to say so.
if ($replacingBroker) {
    # **What stopping it will interrupt, said before it is stopped.**
    #
    # SPEC.md section 12: the node stopping tells every agent it started to quit, and the process
    # is what held everything each of them had learned. A Request already drained into an agent's
    # inbox is marked delivered, and killing the agent that holds it leaves the exchange open and
    # owed with nothing running to answer it; nothing times out, so it waits and is flagged.
    #
    # **Asked of the broker, never of the store.** Only the broker opens the database (section 4),
    # and reading it here for a progress message would break the one boundary this whole
    # architecture is built on. `parley open` is the same conversations() reply both surfaces read.
    #
    # A warning and not a refusal. The person running this has decided to upgrade, and stopping
    # them is not this script's job.
    $parleyOnPath = Get-Command parley -ErrorAction SilentlyContinue

    # **Asked of each project, because `parley open` refuses to choose between several.** On a machine naming more than
    # one, a bare `parley open` could never answer, and those are the machines with the most in
    # flight. Names come from `parley project list`, whose first column is the name and whose
    # second is the endpoint; a machine naming none is asked once, of the broker it would default to.
    $inFlight = @()
    $unasked = @()
    $asked = $false

    if ($parleyOnPath) {
        $names = @(& parley project list 2>$null | ForEach-Object { if ("$_" -match '^([a-z][a-z0-9-]*)\s+\S+://') { $Matches[1] } })

        if ($names.Count -eq 0) {
            $inFlight = @(& parley open 2>$null)
            $asked = $LASTEXITCODE -eq 0
        }
        else {
            $asked = $true

            foreach ($name in $names) {
                $said = @(& parley --project $name open 2>$null)

                if ($LASTEXITCODE -ne 0) {
                    $unasked += $name
                    continue
                }

                $inFlight += @($said | Where-Object { $_ -is [string] -and $_.Trim() } | ForEach-Object { "${name}: $_" })
            }
        }
    }

    if (-not $asked) {
        # **Said as could-not-ask, not as nothing-in-flight.** A guard that fails open has to say
        # so: the two readings look identical from here and lead somewhere completely different.
        Write-Warning 'Could not ask the broker what is open, so this cannot say what stopping it will interrupt. That is not the same as nothing being in flight.'
    }
    else {
        if ($unasked.Count -gt 0) {
            Write-Warning "Could not ask $($unasked -join ', ') what is open, so this cannot say what stopping the broker will interrupt there. That is not the same as nothing being in flight."
        }

        $lines = @($inFlight | Where-Object { $_ -is [string] -and $_.Trim() })

        if ($lines.Count -gt 0) {
            Write-Warning "$($lines.Count) conversation(s) have an exchange still open. Stopping the broker ends every agent it started. When an agent's turn ends its open exchange stays open and is flagged to the person:"

            foreach ($line in $lines) {
                Write-Host "      $line" -ForegroundColor DarkGray
            }
        }
    }

    Write-Step 'Stopping any running broker'

    Stop-Broker
}
else {
    Write-Step 'Leaving the broker alone'
    Write-Note 'nothing being replaced is the broker''s, so no agent is ended and no exchange is interrupted'
}

# After the stop, because until now a running broker was expected. What is left is one somebody
# started by hand, which Stop-Broker deliberately leaves alone -- and which holds parley.exe just
# as firmly. Caught here rather than by the uninstall, which would report a denied path and leave
# the tools half replaced.
$replacingCommands = @($tools | Where-Object { Wanted $_ } | ForEach-Object { $_.Command })

$strays = @(Get-Process -Name $replacingCommands -ErrorAction SilentlyContinue)

if ($strays.Count -gt 0) {
    $listed = ($strays | ForEach-Object { "      $($_.ProcessName), pid $($_.Id)" }) -join "`n"

    if ($Force) {
        Write-Note "$($strays.Count) process(es) still hold the tools; -Force is ending them"

        $survived = Remove-Holders $strays

        if ($survived.Count -gt 0) {
            Write-Error @"
Something is still holding the tools after -Force tried to end it:
$(($survived | ForEach-Object { "      $($_.ProcessName), pid $($_.Id)" }) -join "`n")
    Nothing has been replaced; the autostart broker is stopped and will start again at logon, or
    now with: Start-ScheduledTask -TaskName $task
"@
        }
    }
    else {
        Write-Error @"
Something is still holding the tools after the broker was stopped:
$listed
    A broker started by hand is not stopped by this script, on purpose, and neither is a web UI
    started beside it -- the one the autostart broker starts goes with it. Stop it and run this
    again, or re-run with -Force to end it. Nothing has been replaced; the autostart broker is
    stopped and will start again at logon, or now with: Start-ScheduledTask -TaskName $task
"@
    }
}

Write-Step "Installing version $built"

# What happened to each, said one line per tool at the end. Three outcomes, and only one of them
# is "nothing happened": a surface this machine did not ask for may still be installed from
# before, at whatever version it was, and that is the case worth naming. A skipped surface going
# quietly is how a machine ends up running a terminal three versions behind its broker.
$outcomes = @()
$listedBefore = Installed

foreach ($tool in $tools) {
    if (-not (Wanted $tool)) {
        $outcomes += if ($listedBefore -contains $tool.Package.ToLowerInvariant()) {
            @{ Command = $tool.Command; Said = "left as it was, not selected by $(if ($Only) { '-Only' } else { '-Surfaces' })" }
        }
        else {
            @{ Command = $tool.Command; Said = "not installed, not selected by $(if ($Only) { '-Only' } else { '-Surfaces' })" }
        }

        continue
    }

    $source = Join-Path $root (Split-Path -Parent $tool.Project | Join-Path -ChildPath 'bin\Release')

    # Uninstall first rather than `update`, which is a no-op when the version has not moved --
    # and the version has not moved on every rebuild that fixed something without shipping new
    # behaviour. Removing first makes the install unconditional.
    & dotnet tool uninstall -g $tool.Package 2>&1 | Out-Null

    $said = & dotnet tool install -g --add-source $source $tool.Package --version $built 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Could not install $($tool.Package): $said"
    }

    # Zero does not mean installed. `dotnet tool install` exits 0 for a package that is already
    # there, prints "is already installed", and replaces nothing -- so reaching this with that
    # message means the uninstall above failed and PATH still holds the previous build. The
    # version check below cannot catch it: the version it finds is the version it expected.
    if ($said -match 'already installed') {
        Write-Error "$($tool.Package) was already installed and could not be replaced, so the build just packed is not the one on PATH. Something is holding its files -- 'Get-Process parley' will show a broker -- and it has to stop before this can work."
    }

    $outcomes += @{ Command = $tool.Command; Said = "installed $built" }
}

foreach ($outcome in $outcomes) {
    Write-Note "$($outcome.Command): $($outcome.Said)"
}

# Said loudly rather than in the list above, because it is the one outcome that leaves the
# machine inconsistent. The surfaces speak SPEC.md §10.2, which is a versioned contract, so
# a surface left behind is a client of a broker it may no longer agree with.
$stale = @($outcomes | Where-Object { $_.Said -like 'left as it was*' })

if ($stale.Count -gt 0) {
    Write-Warning "$(($stale | ForEach-Object { $_.Command }) -join ', ') is installed and was not updated to $built. Re-run with -Surfaces both to bring it up, or with -Uninstall to remove everything."
}

# ------------------------------------------------------------------ verify ---

Write-Step 'Checking what is actually on PATH'

$shims = Join-Path $env:USERPROFILE '.dotnet\tools'

if ($env:PATH -notlike "*$shims*") {
    Write-Warning "$shims is not on PATH for this session. Open a new terminal, or add it, or the commands below will not be found."
}

# Called by full path from here down, never as a bare `parley`. A first-ever tool install adds
# the shim directory to PATH for *new* processes, so the bare name does not resolve in the
# session that just did the installing -- which is this one, on the run that most needs it.
$parley = Join-Path $shims 'parley.exe'

# The check the whole script exists for. An install that silently did nothing leaves the old
# version here, and every other step reports success.
#
# **Asked of each tool that was installed, rather than of `parley`.** A run can replace only a
# surface, and asking the broker would then compare the version just built against a broker
# deliberately left alone, and fail a run that did exactly what it was asked.
$reported = @()

foreach ($tool in $tools) {
    if (-not (Wanted $tool)) { continue }

    $binary = Join-Path $shims ($tool.Command + '.exe')

    # Joined into one string, and a non-zero exit is a failure on its own: a tool that printed no version leaves an
    # empty array, `-notmatch` over an empty array is empty, and empty is false -- so the check would pass on a tool
    # that had said nothing.
    $says = (& $binary --version | Out-String).Trim()

    if ($LASTEXITCODE -ne 0 -or $says -notmatch [regex]::Escape($built)) {
        Write-Error "Installed $built but $($tool.Command) reports '$says'. Something older is still there; run this with -Uninstall and try again."
    }

    $reported += $says
}

Write-Host ''

foreach ($says in $reported) {
    Write-Host $says -ForegroundColor Green
}

# ---------------------------------------------------------------- broker ----

# **Skipped entirely when the broker is not being replaced, and this is the half that is easy to
# miss.** Not stopping the broker earlier is worthless if the script re-registers the
# autostart entry down here: `autostart install` replaces the scheduled task, which restarts the
# process, which ends every resident agent on the machine along with everything each had
# learned. The stop would have been avoided and the cost paid anyway, hundreds of lines later.
#
# Nothing here is needed for a surface: a terminal is not registered with a supervisor, does not
# listen on a port, and has no store. There is nothing to configure and nothing to prove.
if (-not $replacingBroker) {
    if ($Shortcut) { New-WebConsoleShortcut $null }

    Write-Host ''
    Write-Host 'The broker was not touched, and is still serving whatever it was serving.' -ForegroundColor Green
    Write-Host 'Your agents kept running, so nothing they were working on was lost.'

    exit 0
}

# **Every machine's entry serves the manifest**: every project this machine names, each on its own store and port, and
# a project named later started within seconds. A machine that names none yet gets the same entry, waiting for the
# first -- so this never asks where a store goes. That is a project's to say, and a project is named in `parley terminal`
# (the terminal), where everything else it needs is said at the same time.
$listed = & $parley project list 2>$null
$listedExit = $LASTEXITCODE
$manifest = $null

if ($listedExit -eq 0) {
    # The `root:` line of `project list`: the directory of projects the broker is about to serve.
    $line = $listed | Where-Object { $_ -match '^root:\s+(.+)$' } | Select-Object -Last 1

    if ($line -match '^root:\s+(.+)$') { $manifest = $Matches[1].Trim() }
}

$named = $listedExit -eq 0 -and ($listed -join "`n") -notmatch 'No projects configured'

Write-Step 'Registering the broker to start at logon'

# No elevation here, and that is not an accident: this goes through the Task Scheduler's COM
# API, which registers a task for the current user without a consent prompt. `schtasks.exe`
# refuses the same registration from the same shell.
if ($manifest) {
    Write-Host "  serving every project in $manifest"
}

# **Splatted beside the verb rather than replacing it.** Building the whole command as an array
# would take the words `autostart install` out of this file, and `InstallScriptTests` looks for
# exactly that string to check the script sets the broker up rather than printing the command for
# somebody to copy. The verb stays written where a reader and a test can both see it, and only
# what varies is a variable.
$extraArguments = @()

if ($Hidden) { $extraArguments = @('--hidden') }

# What the command says is shown as it is and read for two things: where the broker logs, and where the web UI it
# starts listens. Read rather than assumed, because both are the command's to decide.
$registered = @(& $parley autostart install @extraArguments)
$registeredExit = $LASTEXITCODE

$registered | ForEach-Object { Write-Host $_ }

if ($registeredExit -ne 0) {
    Write-Error "Could not set the broker to start automatically. The tools are installed, so running 'parley serve' by hand will say why."
}

$log = $registered | Where-Object { $_ -match '^\s*logs:\s+(.+)$' } | ForEach-Object { $Matches[1].Trim() } | Select-Object -First 1
$webUi = $registered | Where-Object { $_ -match '--web-ui\s+"?([^\s"]+)' } | ForEach-Object { $Matches[1] } | Select-Object -First 1

# ----------------------------------------------------------------- prove ----

Write-Step 'Checking that it answers'

# Asked of the broker, not of the supervisor. "The task is registered" and "an agent can attach" are different claims.
# With projects named, `parley project list` asks every project's broker and says which are reachable, and the claim
# is that all of them are. With none named there is no broker to ask yet -- the entry is waiting -- so what is checked
# is that the process it started is running, and the person is told how to name the first project.
$deadline = (Get-Date).AddSeconds(20)
$answering = $false

do {
    $listed = (& $parley project list 2>$null) -join "`n"

    if ($LASTEXITCODE -eq 0) {
        if ($listed -match 'No projects configured') {
            if (Get-Process -Name $broker -ErrorAction SilentlyContinue) {
                $answering = $true
                break
            }
        }
        # `answering`, not `reachable`: "this process connected to it" must not read as "reachable
        # from the network", which is what a person planning a firewall rule would act on. This poll
        # wants the first fact -- did the broker answer.
        elseif ($listed -match '(?m)\sanswering$' -and $listed -notmatch 'not answering') {
            $answering = $true
            $named = $true
            break
        }
    }

    Start-Sleep -Milliseconds 250
}
while ((Get-Date) -lt $deadline)

if (-not $answering) {
    Write-Error "Registered, but the broker is not answering. It writes its own log, and $log is where it says why."
}

if ($manifest) { Write-Note "serves: every project in $manifest" }
Write-Note "log:   $log"

Write-Host ''
Write-Host 'The broker is running, and starts again when you log in.' -ForegroundColor Green
Write-Host ''

# **What to do next is the terminal's flow:** make a project, then enrol agents into it from
# inside it. Doing any of it needs the terminal, and on a machine without one there is no way at
# all: the command line cannot name a project and the web UI cannot either, by the one-writer rule
# its own naming page records. So a machine without the terminal is told outright rather than
# handed steps it cannot follow.
if (Get-Command parley-terminal -ErrorAction SilentlyContinue) {
    Write-Host 'Next, run:  parley' -ForegroundColor White

    if ($named) {
        Write-Host '  1. Open a project, or create a new one with c.' -ForegroundColor White
    }
    else {
        Write-Host '  1. Create a project with c. There is none yet, so nothing is being served.' -ForegroundColor White
    }

    Write-Host '  2. In the project, choose Agent > Enroll agents from folders (f).' -ForegroundColor White
    Write-Host '  3. Pick the folder that holds your repositories, tick the ones you want, press Enter.' -ForegroundColor White
}
else {
    Write-Warning 'Projects and agents are set up in the terminal, and this machine has not installed it.'
    Write-Host '  .\install.ps1 -Only parley-terminal               installs it without stopping the broker' -ForegroundColor White
}

if ($Shortcut) {
    Write-Host ''
    New-WebConsoleShortcut $webUi
}

if ($webUi) {
    Write-Host ''
    Write-Host "The web UI: $webUi" -ForegroundColor White
}

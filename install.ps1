<#
.SYNOPSIS
    Builds Parley from this checkout and installs its two tools.

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
    Installing the tools is not the same as having Parley working: the broker is a separate
    process that owns the store, and until one is running every agent fails to attach with "No
    broker is listening" -- a message that arrives in an editor, days later, rather than here.

.PARAMETER Store
    Named Store rather than Db because `-Db` cannot exist here: CmdletBinding adds the common
    parameters, `-Debug` carries `db` as an alias, and PowerShell rejects the whole script at
    parse time for the conflict. It is also the word the broker itself uses.

    The SQLite store the broker owns. One file for the whole machine, outside every repository,
    because every agent on it reaches that file through the one broker process.

    Asked for if it is not given, defaulting to a .parley folder in your home directory. It does
    not need to exist: the broker creates it on first run.

.PARAMETER NodeConfig
    The catalogue of agents this machine may start on demand, passed to the autostart entry as
    --node-config.

    Read back from the entry being replaced when it is not given, because an entry registered
    without one starts the broker alone -- and a machine reinstalling over a working deployment
    would lose the half that starts its agents, with the loss arriving days later as a question
    that waits out its window and is ghosted.

.PARAMETER Uninstall
    Removes both tools and the autostart entry, and stops. The store and any .mcp.json are left
    alone -- they are the deployment, and somebody reinstalling a tool has not asked to lose
    their history or their launch configurations.

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

.EXAMPLE
    .\install.ps1

.EXAMPLE
    .\install.ps1 -Store D:\parley\parley.db

.EXAMPLE
    .\install.ps1 -Force

.EXAMPLE
    .\install.ps1 -Uninstall
#>

[CmdletBinding()]
param(
    [string] $Store,
    [string] $NodeConfig,
    [switch] $Uninstall,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$tools = @(
    @{ Package = 'Parley.Cli'; Project = 'src\Parley.Cli\Parley.Cli.csproj'; Command = 'parley' }
    @{ Package = 'Parley.Mcp'; Project = 'src\Parley.Mcp\Parley.Mcp.csproj'; Command = 'parley-mcp' }
)

# What `parley autostart install` calls the scheduled task. Duplicated from AutostartPlan.Name
# rather than asked for, because the one command that could report it is the one this script
# may be in the middle of deleting.
$task = 'parley-broker'

# What the entry being replaced was registered with, read before anything touches it. The only
# reason this is needed is the catalogue: `autostart install` is given the store on the command
# line and would otherwise drop this machine's agents from the arrangement, which is a loss
# nobody sees until a question goes unanswered days later.
$existingArguments = ''

try {
    $existing = Get-ScheduledTask -TaskName $task -ErrorAction Stop
    $existingArguments = ($existing.Actions | ForEach-Object { $_.Arguments }) -join ' '
}
catch {
    # No entry yet. An ordinary first install, and nothing to carry over.
}

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
    $deadline = (Get-Date).AddSeconds(10)

    while ((Get-Process -Name 'parley' -ErrorAction SilentlyContinue) -and
           (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
}

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Note($text) { Write-Host "    $text" -ForegroundColor DarkGray }

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

<#
.SYNOPSIS
    Settles where the broker's store goes.
.DESCRIPTION
    Given on the command line, asked for otherwise, and defaulted when there is nobody to ask.

    The default is under the home directory rather than beside this checkout, and that is the
    whole reason this is a question rather than a constant. The store outlives any one clone --
    it holds every exchange the deployment has ever had -- so putting it inside a repository
    means `git clean` destroys the history of twenty agents, and putting it inside *this* one
    means Parley's own working copy is load-bearing for machines that do not have it.
#>
function Resolve-Store([string] $given) {
    $default = Join-Path $env:USERPROFILE '.parley\parley.db'

    if ($given) { return [IO.Path]::GetFullPath($given) }

    # Prompting needs somebody to answer. Redirected input is a script or a CI job running this,
    # and Read-Host there returns empty at best and blocks at worst, so the default is taken and
    # said out loud instead -- a silent choice of where a database lives is worse than a loud one.
    if ([Console]::IsInputRedirected) {
        Write-Note "nothing to prompt (input is redirected); using $default"
        return $default
    }

    Write-Host ''
    Write-Host '  The broker owns one SQLite file, and every agent on this machine reaches it'
    Write-Host '  through that one process. It belongs outside every repository, because it is'
    Write-Host '  shared by all of them and outlives any one clone.'
    Write-Host ''

    $answer = Read-Host "  Store [$default]"

    if ([string]::IsNullOrWhiteSpace($answer)) { return $default }

    # Expanded here rather than passed through. A relative path would be resolved against
    # whatever directory the supervisor happens to start in at logon, which is not this one.
    return [IO.Path]::GetFullPath($answer.Trim())
}

# ---------------------------------------------------------------- uninstall --

if ($Uninstall) {
    # The broker first, before anything that deletes a file. Windows will not unlink a binary
    # that is still mapped into a running process, so `dotnet tool uninstall` fails against a
    # live broker -- and it fails the same way it fails when the tool was never there, which is
    # how this script came to print "Both tools are gone" with one of them still installed.
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
    Write-Host 'Both tools are gone, and so is the autostart entry. The store and any .mcp.json'
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
    $output = Join-Path $root (Split-Path -Parent $tool.Project | Join-Path -ChildPath 'bin\Release')
    $stale = Get-ChildItem -Path $output -Filter '*.nupkg' -ErrorAction SilentlyContinue

    if ($stale) {
        Write-Note "$($stale.Count) old package(s) in $output"
        $stale | Remove-Item -Force
    }
}

# -------------------------------------------------------------------- pack ---

foreach ($tool in $tools) {
    Write-Step "Packing $($tool.Package)"

    & dotnet pack (Join-Path $root $tool.Project) -c Release --nologo -v quiet

    if ($LASTEXITCODE -ne 0) { Write-Error "Could not pack $($tool.Package)." }
}

# The version every package just took, read from the packages rather than from the project file.
# What matters is what was produced, and a build that ignored the property would be exactly the
# defect this check should catch.
$built = Get-ChildItem -Path $root -Filter 'Parley.Cli.*.nupkg' -Recurse |
    Where-Object { $_.FullName -like '*\bin\Release\*' } |
    ForEach-Object { $_.BaseName -replace '^Parley\.Cli\.', '' } |
    Select-Object -First 1

if (-not $built) { Write-Error 'Nothing was packed. The build produced no package.' }

# ----------------------------------------------------------------- install ---

# Before anything is stopped, because the failure this catches used to arrive afterwards.
#
# A running `parley-mcp` holds its own files in the tool store, so `dotnet tool uninstall` fails
# with "Access to the path ...\.store\parley.mcp\<version>' is denied". That on its own would be
# a clear error and no great harm. The harm was the order: the broker was stopped first, so a
# machine that had been serving a deployment was left with no broker, one tool replaced and one
# not, by a script that had reported nothing but success up to that point.
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
$adapters = @($tools | Where-Object { $_.Command -ne $broker } | ForEach-Object { $_.Command })

$holders = @(Get-Process -Name $adapters -ErrorAction SilentlyContinue)

if ($holders.Count -gt 0) {
    $listed = ($holders | ForEach-Object { "      pid $($_.Id), started $($_.StartTime.ToString('HH:mm'))" }) -join "`n"

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
        Write-Error @"
$($holders.Count) Parley adapter(s) are running and hold the files this would replace:
$listed
    Each one belongs to an editor session with Parley configured. Close those sessions and run
    this again, or re-run with -Force to end them. Nothing has been changed and the broker has
    not been stopped.
"@
    }
}

Write-Note 'no adapter is holding the tool store'

# Before anything replaces a file. A live broker holds the binary open, which makes the
# uninstall below fail and the install after it a no-op that reports success.
Write-Step 'Stopping any running broker'

Stop-Broker

# After the stop, because until now a running broker was expected. What is left is one somebody
# started by hand, which Stop-Broker deliberately leaves alone -- and which holds parley.exe just
# as firmly. Caught here rather than by the uninstall, which would report a denied path and leave
# the tools half replaced.
$strays = @(Get-Process -Name ($tools | ForEach-Object { $_.Command }) -ErrorAction SilentlyContinue)

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
    A broker started by hand is not stopped by this script, on purpose. Stop it and run this
    again, or re-run with -Force to end it. Nothing has been replaced; the autostart broker is
    stopped and will start again at logon, or now with: Start-ScheduledTask -TaskName $task
"@
    }
}

Write-Step "Installing version $built"

foreach ($tool in $tools) {
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

    Write-Note "$($tool.Command) installed"
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
$reported = & $parley --version

if ($reported -notmatch [regex]::Escape($built)) {
    Write-Error "Installed $built but PATH reports '$reported'. Something older is still there; run this with -Uninstall and try again."
}

Write-Host ''
Write-Host $reported -ForegroundColor Green

# ---------------------------------------------------------------- broker ----

$store = Resolve-Store $Store

# Asked before registering anything, and only because the check at the end cannot tell one
# broker from another. Stop-Broker has already stopped the one an autostart entry started, so
# anything answering now was started by hand -- and it holds the port, which means the entry
# about to be registered will not bind until that process goes. The final check would pass
# against it and report a store that nothing is actually serving.
& $parley registry 2>&1 | Out-Null
$foreign = $LASTEXITCODE -eq 0

Write-Step 'Registering the broker to start at logon'

# No elevation here, and that is not an accident: this goes through the Task Scheduler's COM
# API, which registers a task for the current user without a consent prompt. `schtasks.exe`
# refuses the same registration from the same shell.
# Carried over rather than dropped. `autostart install` registers a broker-alone entry when it
# is given no catalogue, and a machine reinstalling over a working deployment would silently
# lose the half that starts its agents -- the loss arriving days later, on a machine nobody is
# signed into, as a question that waits out its window and is ghosted. The command says so
# loudly, but the operator ran this script and is reading its output, not that one.
#
# Read back from the entry being replaced when it was not given, because somebody reinstalling
# has not asked to reconfigure anything.
$catalogue = $NodeConfig

if (-not $catalogue -and $existingArguments -match "--node-config\s+`"?([^`"]+?)`"?(\s|$)") {
    $catalogue = $Matches[1]
    Write-Host "  keeping this machine's agent catalogue: $catalogue"
}

if ($catalogue) {
    & $parley autostart install --db $store --node-config $catalogue
}
else {
    & $parley autostart install --db $store
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "Could not set the broker to start automatically. The tools are installed, so running 'parley serve --db $store' by hand will say why."
}

# ----------------------------------------------------------------- prove ----

Write-Step 'Checking that it answers'

# Asked of the broker, not of the supervisor. "The task is registered" and "an agent can attach"
# are different claims, and the second is the one this script is making -- so the check is a
# client: `parley registry` connects over the transport and exits 69 when nothing is listening.
$log = Join-Path (Split-Path -Parent $store) 'parley-broker.log'
$deadline = (Get-Date).AddSeconds(20)
$answering = $false

do {
    & $parley registry 2>&1 | Out-Null

    if ($LASTEXITCODE -eq 0) { $answering = $true; break }

    Start-Sleep -Milliseconds 250
}
while ((Get-Date) -lt $deadline)

if (-not $answering) {
    Write-Error "Registered, but the broker is not answering. It writes its own log, and $log is where it says why."
}

Write-Note "store: $store"
Write-Note "log:   $log"

if ($foreign) {
    Write-Warning "A broker that this did not start was already listening, and it is the one answering. It is serving its own store, not $store. Stop it and log out and in, or run 'parley serve --db $store' yourself."
}

Write-Host ''
Write-Host 'The broker is running, and starts again when you log in.' -ForegroundColor Green
Write-Host ''
Write-Host 'Next, in each repository you want an agent in:   /parley:setup' -ForegroundColor White
Write-Host 'Restart the editor there, then check it arrived:  parley registry' -ForegroundColor White

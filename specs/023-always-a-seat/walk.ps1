# T056: walks specs/023-always-a-seat/quickstart.md, steps 1 to 6. Step 7 needs this repository's own live agent and is
# not attempted: the owner has not authorised it (T057).
#
# Everything runs against tools packed and installed into a scratch path, with a root, a store and a port of their own,
# so the machine's real deployment is untouched. **THE FIRST THING IT DOES IS CLEAR EVERY PARLEY_ VARIABLE**, for the
# reason 020's walk records: this machine points PARLEY_NODE_CONFIG at the live catalogue, and a walk that inherits it
# writes into the deployment somebody's agents are started from.
#
# THE AGENT IS A REAL parley-mcp PROCESS, SPOKEN TO OVER ITS OWN STDIO, as in 022's walk: this script plays the model.
# THE PERSON IS THE WEB UI, driven over HTTP. Operator facts a shell has no verb for any more (019 moved them to the
# surfaces) are read over gRPC with the shipped Parley.Transport.dll, which is what both surfaces call.
#
# WHAT THIS WALK DOES NOT MEASURE, said here rather than discovered halfway down:
#
#   * The terminal. `parley terminal` refuses a redirected stream by design, so 4.1's status line, 5.1's "nothing on the
#     terminal says the seat was released", 5.2's key press and 6's "while the terminal is open" are recorded as not
#     drivable. The web UI is the same person (step 6) and is walked in their place where it can stand in.
#   * The idle window through a test seam. SeatBook.IdleAfter is a static twenty minutes with no seam in a packed tool,
#     so this walk WAITS it out (-IdleMinutes, default 21), touching nothing on the web UI meanwhile. That wait also
#     carries the person's request past §6's fifteen-minute uncollected threshold, which is why step 3.2 is checked
#     after it rather than before: no store is edited to make a flag appear.
#   * A real model (step 7).

param(
    [string]$Tools,
    [int]$IdleMinutes = 21,
    [switch]$SkipIdle,
    [switch]$Keep
)

$ErrorActionPreference = 'Continue'

$repoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$scratch    = Join-Path $env:TEMP 'parley-023-walk'
$root       = Join-Path $scratch 'root'
$repos      = Join-Path $scratch 'repos'
$project    = 'alpha'
$brokerPort = 19183         # the live deployment holds 19100
$webPort    = 5296          # and 5280
if (-not $Tools) { $Tools = Join-Path $scratch 'tools' }

$parley = Join-Path $Tools 'parley.exe'
$mcp    = Join-Path $Tools 'parley-mcp.exe'
$webui  = Join-Path $Tools 'parley-web-ui.exe'

$script:fail  = 0
$script:pass  = 0
$script:notes = @()
$script:started = @()       # every process this walk starts, by id; cleanup stops these and nothing else
function Ok    ($t) { Write-Host "  ok    $t" -ForegroundColor DarkGreen; $script:pass++ }
function Bad   ($t) { Write-Host "  FAIL  $t" -ForegroundColor Red; $script:fail++ }
function Check ($t, $cond) { if ($cond) { Ok $t } else { Bad $t } }
function Why   ($t) { Write-Host "        $t" -ForegroundColor DarkGray }
function Note  ($t) { Write-Host "  note  $t" -ForegroundColor DarkYellow; $script:notes += $t }
function Head  ($t) { Write-Host ''; Write-Host "==> $t" -ForegroundColor Cyan }
function Track ($p) { if ($p) { $script:started += $p.Id }; $p }

# ---- environment hygiene, first -----------------------------------------------------------------
Get-ChildItem Env: | Where-Object Name -like 'PARLEY_*' | ForEach-Object {
    Remove-Item "Env:\$($_.Name)" -ErrorAction SilentlyContinue
}
$env:PARLEY_CONFIG       = $root
$env:PARLEY_NODE_CONFIG  = Join-Path $root 'node.json'
$env:PARLEY_TICK_SECONDS = '1'

if (Test-Path $scratch) {
    Get-Process 'parley*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($scratch, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Get-ChildItem $scratch -Exclude 'tools','pkg' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force $scratch, $root, $repos | Out-Null

# ---- isolation baseline -------------------------------------------------------------------------
Head 'isolation baseline'
$liveRoot = Join-Path $env:APPDATA 'Parley'
$liveNode = Join-Path $liveRoot 'node.json'
$liveMcp  = Join-Path $repoRoot '.mcp.json'
function HashOrAbsent($path) { if (Test-Path $path) { (Get-FileHash $path -Algorithm MD5).Hash } else { 'absent' } }
$nodeBefore = HashOrAbsent $liveNode
$mcpBefore  = HashOrAbsent $liveMcp
$liveProjectsBefore = @(Get-ChildItem (Join-Path $liveRoot 'projects') -Directory -ErrorAction SilentlyContinue).Count
Write-Host "    live node.json: $nodeBefore; this repository's .mcp.json: $mcpBefore; live projects: $liveProjectsBefore"

# ---- tools, packed and installed ----------------------------------------------------------------
$shipping = [regex]::Match(
    (Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw),
    '<VersionPrefix>([^<]+)</VersionPrefix>').Groups[1].Value

if (-not (Test-Path $parley)) {
    Head "packing and installing the tools into the scratch path ($shipping)"
    $pkg = Join-Path $scratch 'pkg'
    New-Item -ItemType Directory -Force $pkg | Out-Null
    foreach ($one in 'Parley.Cli','Parley.WebUi','Parley.Mcp') {
        & dotnet pack (Join-Path $repoRoot "src\$one\$one.csproj") -c Release -o $pkg 2>&1 | Select-Object -Last 1
        & dotnet tool install $one --tool-path $Tools --add-source $pkg `
            --configfile (Join-Path $repoRoot 'NuGet.config') --version $shipping 2>&1 | Select-Object -Last 1
    }
}
$built = (& $parley --version) -join ' '
Check "the walk runs against packed tools in a scratch path ($built)" ($built -match [regex]::Escape($shipping))

# ---- gRPC, through the shipped transport --------------------------------------------------------
$cliDir = Split-Path (Get-ChildItem (Join-Path $Tools '.store') -Recurse -Filter 'Parley.Transport.dll' |
    Where-Object FullName -match '\\parley\.cli\\' | Select-Object -First 1).FullName
$aspnet = (Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App' -Directory |
    Where-Object Name -match '^10\.' | Sort-Object { [version]$_.Name } | Select-Object -Last 1).FullName
Add-Type -Path (Join-Path $aspnet 'Microsoft.Extensions.Logging.Abstractions.dll')
foreach ($n in 'Google.Protobuf','Grpc.Core.Api','Grpc.Net.Common','Grpc.Net.Client','Parley.Transport') {
    Add-Type -Path (Join-Path $cliDir "$n.dll")
}
$channel = [Grpc.Net.Client.GrpcChannel]::ForAddress("http://127.0.0.1:$brokerPort")
function Client($service) {
    [type]"Parley.Transport.$service+${service}Client" |
        ForEach-Object { $_.GetConstructor([type[]]@([Grpc.Core.ChannelBase])).Invoke(@($channel)) }
}
$ops   = Client 'Operators'
$nodes = Client 'Nodes'
$call  = { [Grpc.Core.CallOptions]::new() }
function Grpc([scriptblock]$body) {
    try { [pscustomobject]@{ Reply = (& $body); Status = 'OK'; Detail = '' } }
    catch {
        $e = $_.Exception; while ($e.InnerException -and -not ($e -is [Grpc.Core.RpcException])) { $e = $e.InnerException }
        if ($e -is [Grpc.Core.RpcException]) { [pscustomobject]@{ Reply = $null; Status = "$($e.StatusCode)"; Detail = $e.Status.Detail } }
        else { [pscustomobject]@{ Reply = $null; Status = 'Error'; Detail = $e.Message } }
    }
}
function Rows() { (Grpc { $ops.DescribeRegistry([Parley.Transport.Empty]::new(), (& $call)) }).Reply.Rows }

# ---- a project and a broker ---------------------------------------------------------------------
Head 'step 1: a project and a broker, and no surface'
$folder = Join-Path $root "projects\$project"
New-Item -ItemType Directory -Force $folder | Out-Null
"{`n  `"endpoint`": `"http://127.0.0.1:$brokerPort`"`n}" |
    Set-Content -Path (Join-Path $folder 'project.json') -Encoding utf8NoBOM
Note ('1.1 the project is written as the project.json the product writes: naming one is the terminal''s since 019, ' +
      'and the terminal refuses a redirected stream. Cited to NamingTests, as 019''s and 020''s walks record.')

$brokerLog = Join-Path $scratch 'broker.log'
function StartBroker() {
    $stamp = [DateTime]::UtcNow.Ticks
    Track (Start-Process -FilePath $parley -PassThru -NoNewWindow `
        -ArgumentList @('serve', '--config', $root, '--log', $brokerLog) `
        -RedirectStandardOutput (Join-Path $scratch "broker-$stamp.out") `
        -RedirectStandardError  (Join-Path $scratch "broker-$stamp.err"))
}
function Registry() { (& $parley --config $root --project $project registry 2>&1 | Out-String) }
function WaitForBroker() {
    for ($i = 0; $i -lt 120; $i++) {
        [void](& $parley --config $root --project $project registry 2>&1)
        if ($LASTEXITCODE -eq 0) { return $true }
        Start-Sleep -Milliseconds 500
    }
    $false
}
$script:broker = StartBroker
Check '1.1 the broker answers, with no surface open' (WaitForBroker)

$machine = $env:COMPUTERNAME.ToLowerInvariant() -replace '[^a-z0-9.-]', ''
$person  = "human-user@$project"
$parleyName = "parley-facilitator@$project"

$cli = Registry
Write-Host ($cli.Trim())
$personLine = ($cli -split "`n" | Where-Object { $_ -match "^$([regex]::Escape($person))\s" }) -join ''
Check "1.2 parley registry has a row $person" ($personLine.Length -gt 0)
Check '1.2 its adapter is parley-console' ($personLine -match 'parley-console')
Check '1.2 shown as away' ($personLine -match '\baway\b')

$rows = Rows
$personRow = @($rows | Where-Object Name -eq $person)
$parleyRow = @($rows | Where-Object Name -eq $parleyName)
Check "1.2 the broker's registry holds the facilitator as $parleyName" ($parleyRow.Count -eq 1)
Note ('1.2 superseded by 0.35.0 (022): `parley registry` leaves the facilitator out by design (OperatorCommands filters ' +
      'it), so the facilitator row was read from Operators.DescribeRegistry, which is what both surfaces read.')
Check '1.2 the facilitator reads running (contract: Presence)' `
    ($parleyRow.Count -eq 1 -and "$($parleyRow[0].Presence)" -eq 'Running')
Check "1.2 the person row reads Away over the wire too ($($personRow[0].Presence))" `
    ($personRow.Count -eq 1 -and "$($personRow[0].Presence)" -eq 'Away')

Head 'step 1.3: restart the broker'
Stop-Process -Id $script:broker.Id -Force; [void]$script:broker.WaitForExit(10000)
$script:broker = StartBroker
[void](WaitForBroker)
$rows = Rows
Check '1.3 after a restart there is still exactly one person row' (@($rows | Where-Object Name -eq $person).Count -eq 1)
Check '1.3 and exactly one facilitator row' (@($rows | Where-Object Name -eq $parleyName).Count -eq 1)
Check '1.3 and no second row of either under any other spelling' `
    (@($rows | Where-Object { $_.Name -like 'human-user@*' -or $_.Name -like 'parley-facilitator@*' }).Count -eq 2)

Head 'step 1.4: no machine called alpha'
$req = [Parley.Transport.DescribeNodeRequest]::new(); $req.Machine = $project
$described = Grpc { $nodes.Describe($req, (& $call)) }
Why "$($described.Status): $($described.Detail)"
Check "1.4 Nodes.Describe('$project') answers NotFound" ($described.Status -eq 'NotFound')
Check '1.4 saying no such machine has been heard from' ($described.Detail -match 'has ever been heard from')

# ================================================================================================
Head 'step 2: nothing can take the name'
$enrolDir = Join-Path $repos 'enrol'
$named = Join-Path $enrolDir 'human-user'
New-Item -ItemType Directory -Force $named | Out-Null
& git -C $named init --quiet 2>&1 | Out-Null
$catalogueBefore = HashOrAbsent $env:PARLEY_NODE_CONFIG
$enrolled = & $parley --config $root --project $project enroll $enrolDir --all 2>&1 | Out-String
$enrolCode = $LASTEXITCODE
Why (($enrolled -replace '\s+', ' ').Trim())
Check "2.1 enrolling a repository as human-user is refused (exit $enrolCode)" `
    ($enrolled -match 'human-user' -and $enrolled -match "person's seat")
Check '2.1 nothing was written into the repository' (-not (Test-Path (Join-Path $named '.mcp.json')))
Check '2.1 nothing was written into the catalogue' ((HashOrAbsent $env:PARLEY_NODE_CONFIG) -eq $catalogueBefore)

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $mcp; $psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.WorkingDirectory = $named
foreach ($k in @($psi.Environment.Keys)) { if ($k -like 'PARLEY_*') { [void]$psi.Environment.Remove($k) } }
$psi.Environment['PARLEY_CONFIG'] = $root; $psi.Environment['PARLEY_PROJECT'] = $project
$psi.Environment['PARLEY_AGENT'] = 'human-user'; $psi.Environment['PARLEY_MACHINE'] = $machine
$claim = [System.Diagnostics.Process]::Start($psi); [void](Track $claim)
$claimErr = $claim.StandardError.ReadToEndAsync()
[void]$claim.WaitForExit(20000)
$claimSaid = if ($claimErr.Wait(2000)) { $claimErr.Result } else { '' }
Why (($claimSaid -replace '\s+', ' ').Trim())
Check "2.2 parley-mcp as human-user does not attach (exited: $($claim.HasExited), code $(if ($claim.HasExited) { $claim.ExitCode }))" `
    ($claim.HasExited -and $claim.ExitCode -ne 0)
Check '2.2 and says the name is the person''s' ($claimSaid -match "person's seat")
$rows = Rows
Check '2.2 the person row is still the console''s, alone' `
    (@($rows | Where-Object Name -like 'human-user@*').Count -eq 1 -and
     (@($rows | Where-Object Name -eq $person)[0].Adapter -eq 'parley-console'))

foreach ($verb in 'retire', 'purge') {
    $said = & $parley --config $root --project $project $verb $person 2>&1 | Out-String
    Check "2.3 superseded by 0.32.0 (019): ``parley $verb`` is not a verb any more (exit $LASTEXITCODE, sends the person to the terminal)" `
        ($LASTEXITCODE -eq 64 -and $said -match 'not a command here any more' -and $said -match 'terminal')
}
$agentReq = [Parley.Transport.AgentRequest]::new(); $agentReq.Agent = $person
$retired = Grpc { $ops.Retire($agentReq, (& $call)) }
Why "Operators.Retire: $($retired.Status) $($retired.Detail) $(if ($retired.Reply) { $retired.Reply.ToString() })"
Check '2.3 Operators.Retire on the person is refused' `
    ($retired.Status -ne 'OK' -or ($retired.Reply -and -not $retired.Reply.Changed))
Check '2.3 with a sentence saying it is the person' (($retired.Detail + "$($retired.Reply)") -match 'person')
$purged = Grpc { $ops.Purge($agentReq, (& $call)) }
Why "Operators.Purge: $($purged.Status) $($purged.Detail) $(if ($purged.Reply) { $purged.Reply.ToString() })"
Check '2.3 Operators.Purge on the person is refused' ($purged.Status -ne 'OK')
Check '2.3 with a sentence saying it is the person' ($purged.Detail -match 'person')
$rows = Rows
$still = @($rows | Where-Object Name -eq $person)
Check '2.3 the seat is still there and not retired' ($still.Count -eq 1 -and "$($still[0].Status)" -ne 'Retired')

# ================================================================================================
# An agent, played by this script over parley-mcp's own stdio (022's walk's shape)
$script:agents = @{}
function Rpc($agent, $method, $params) {
    $agent.Id++
    $id = $agent.Id
    $line = @{ jsonrpc = '2.0'; id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
    $agent.Process.StandardInput.WriteLine($line)
    $agent.Process.StandardInput.Flush()
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        $read = $agent.Process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(30000)) { break }
        $got = $read.Result
        if ($null -eq $got) { break }
        try { $reply = $got | ConvertFrom-Json } catch { continue }
        if ($reply.id -eq $id) { return $reply }
    }
    $null
}
function Tool($agent, $name, $arguments) {
    $reply = Rpc $agent 'tools/call' @{ name = $name; arguments = $arguments }
    if ($null -eq $reply) { return '' }
    ($reply.result.content | ForEach-Object { $_.text }) -join "`n"
}
function Start-Agent($role, $workspace) {
    $p = [System.Diagnostics.ProcessStartInfo]::new()
    $p.FileName = $mcp; $p.UseShellExecute = $false
    $p.RedirectStandardInput = $true; $p.RedirectStandardOutput = $true; $p.RedirectStandardError = $true
    $p.WorkingDirectory = $workspace
    foreach ($k in @($p.Environment.Keys)) { if ($k -like 'PARLEY_*') { [void]$p.Environment.Remove($k) } }
    $p.Environment['PARLEY_CONFIG'] = $root; $p.Environment['PARLEY_PROJECT'] = $project
    $p.Environment['PARLEY_AGENT'] = $role; $p.Environment['PARLEY_MACHINE'] = $machine
    $p.Environment['PARLEY_WORKSPACE'] = $workspace
    $proc = [System.Diagnostics.Process]::Start($p); [void](Track $proc)
    [void]$proc.StandardError.ReadToEndAsync()
    $agent = [pscustomobject]@{ Name = "$role@$machine"; Process = $proc; Id = 0 }
    $script:agents[$agent.Name] = $agent
    [void](Rpc $agent 'initialize' @{
        protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'walk'; version = '1' } })
    $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $proc.StandardInput.Flush()
    $agent
}
function Inbox($agent) { $t = Tool $agent 'parley_inbox' @{}; try { @(($t | ConvertFrom-Json).messages) } catch { @() } }
function Until($seconds, [scriptblock]$condition) {
    for ($i = 0; $i -lt ($seconds * 4); $i++) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 250 }
    & $condition
}

Head 'step 3: an agent asks the person while they are away'
$ordersRepo = Join-Path $repos 'orders'
New-Item -ItemType Directory -Force $ordersRepo | Out-Null
& git -C $ordersRepo init --quiet 2>&1 | Out-Null
@"
{ "mcpServers": { "parley-$project": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "$project" } } } }
"@ | Set-Content -Path (Join-Path $ordersRepo '.mcp.json') -Encoding utf8NoBOM
$orders = Start-Agent 'orders' $ordersRepo

# Parley asks every attached agent what it owns (022); answered first, so that orders holds no request and its own
# request opens a conversation of its own rather than being assigned to Parley's.
$script:q = @()
[void](Until 30 { $script:q = @(Inbox $orders | Where-Object { $_.message -match 'Question from Parley' }); $script:q.Count -gt 0 })
if ($script:q.Count) {
    [void](Tool $orders 'parley_result' @{ exchangeId = [long]$script:q[0].exchange_id; outcome = 'answered'; text = 'Orders and their totals.' })
} else { Note 'Parley''s ownership question did not arrive within 30s; the request below may be assigned to its conversation' }

$roster = Tool $orders 'parley_agents' @{}
Why (($roster -replace '\s+', ' ').Trim())
$rosterJson = try { $roster | ConvertFrom-Json } catch { $null }
Check '3.0 (0.39.0) parley_agents lists the person apart, under "person"' ($null -ne $rosterJson -and $null -ne $rosterJson.person)
Check '3.0 (0.39.0) as away' ("$($rosterJson.person | ConvertTo-Json -Compress)" -match 'away')

$asked = Tool $orders 'parley_request' @{ to = $person; ask = 'Which currency should refunds be issued in for orders placed abroad?'; conversationId = 'refund-currency' }
Why (($asked -replace '\s+', ' ').Trim())
$askedJson = try { $asked | ConvertFrom-Json } catch { $null }
$receiverIs = "$($askedJson.receiver_is)"
Check '3.1 the request is admitted' ($null -ne $askedJson -and $askedJson.exchange_id)
Check '3.1 receiver_is says the person has it and will answer' ($receiverIs -match 'with the person: they have it and will answer')
Check '3.1 and does not say unreachable' ($receiverIs -notmatch 'not reachable|unreachable')
$conversation = "$($askedJson.conversation_id)"
if (-not $conversation) { $conversation = 'refund-currency' }

$waitingCli = (& $parley --config $root --project $project waiting $person 2>&1 | Out-String).Trim()
Check "3.1 the broker holds one message for the person ($waitingCli)" ($waitingCli -match '^1\b')

$flagsNow = (Grpc { $ops.DescribeFlags([Parley.Transport.Empty]::new(), (& $call)) }).Reply
Check '3.2 (before the threshold) nothing is flagged yet, as §6 says' `
    (@($flagsNow.Flags | Where-Object WaitingOn -eq $person).Count -eq 0)

# ================================================================================================
Head 'step 4: the person opens a surface (the web UI; the terminal cannot be driven)'
$webProcess = Track (Start-Process -FilePath $webui -PassThru -NoNewWindow `
    -ArgumentList @('--listen', "http://127.0.0.1:$webPort") `
    -RedirectStandardOutput (Join-Path $scratch 'web.out') -RedirectStandardError (Join-Path $scratch 'web.err'))
Check 'the web UI answers' (Until 30 { try { [void](Invoke-WebRequest "http://127.0.0.1:$webPort/" -UseBasicParsing -TimeoutSec 2); $true } catch { $false } })
$script:session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
function GetPage($url) { Invoke-WebRequest $url -UseBasicParsing -WebSession $script:session -TimeoutSec 30 }
function Token($html) {
    if ($html.Content -match '(?s)name="__RequestVerificationToken"[^>]*value="([^"]+)"') { $Matches[1] }
    elseif ($html.Content -match '(?s)value="([^"]+)"[^>]*name="__RequestVerificationToken"') { $Matches[1] }
}
function PostForm($url, $page, $fields) {
    $body = [System.Collections.Generic.List[string]]::new()
    $body.Add('__RequestVerificationToken=' + [uri]::EscapeDataString((Token $page)))
    foreach ($pair in $fields) { $body.Add([uri]::EscapeDataString($pair[0]) + '=' + [uri]::EscapeDataString("$($pair[1])")) }
    try {
        Invoke-WebRequest $url -Method Post -Body ($body -join '&') -ContentType 'application/x-www-form-urlencoded' `
            -UseBasicParsing -WebSession $script:session -TimeoutSec 60
    } catch { [pscustomobject]@{ Content = ''; Refused = $_.Exception.Message } }
}
function Text($html) { [System.Net.WebUtility]::HtmlDecode(($html -replace '<[^>]+>', ' ')) -replace '\s+', ' ' }
function WebCount($page) {
    if ($page.Content -match '(?s)class="seat-waiting">(\d+)') { [int]$Matches[1] } else { 0 }
}

$homePage = GetPage "http://127.0.0.1:$webPort/"
$inbox = GetPage "http://127.0.0.1:$webPort/Inbox"
Check '4.1 no join step: the home page offers no Join' (-not ($homePage.Content -match 'href="/Join'))
Check "4.1 the inbox names the person seat" ((Text $inbox.Content) -match [regex]::Escape($person))
$webCount = WebCount $inbox
Check "4.1 the frame shows one message waiting ($webCount), as the broker holds" ($webCount -eq 1)
Note '4.1 the terminal''s status line is not drivable (the terminal refuses a redirected stream); the web frame stands in'
[void](Until 10 { (Registry) -match "$([regex]::Escape($person))\s.*\bhere\b" })
Check '4.3 parley registry reads here while a surface holds the seat' ((Registry) -match "$([regex]::Escape($person))\s.*\bhere\b")

# ================================================================================================
if ($SkipIdle) {
    Note '5.1 and 3.2 were not walked: -SkipIdle'
} else {
    Head "step 5.1: away (the web UI untouched for $IdleMinutes minutes; nothing else polls it)"
    $deadline = [DateTime]::UtcNow.AddMinutes($IdleMinutes)
    while ([DateTime]::UtcNow -lt $deadline) { Start-Sleep -Seconds 30 }
    [void](Until 180 { (Registry) -match "$([regex]::Escape($person))\s.*\baway\b" })
    $afterIdle = Registry
    Why ((($afterIdle -split "`n") | Where-Object { $_ -match '^human-user@' }) -join '')
    Check '5.1 after the idle window parley registry reads away' ($afterIdle -match "$([regex]::Escape($person))\s.*\baway\b")
    Check '5.1 and the seat itself is still there, one row, the console''s' `
        (@((Rows) | Where-Object Name -eq $person).Count -eq 1)
    Note '5.1''s "nothing on the terminal says the seat was released" is the terminal''s and is not drivable'

    Head 'step 3.2: the person''s flag, past §6''s fifteen-minute threshold (read before the web UI is touched again)'
    $flags = (Grpc { $ops.DescribeFlags([Parley.Transport.Empty]::new(), (& $call)) }).Reply
    $mine = @($flags.Flags | Where-Object WaitingOn -eq $person)
    Why (($mine | ForEach-Object { "$($_.Kind) waiting_on=$($_.WaitingOn) waiting=$($_.Waiting) conv=$($_.ConversationId)" }) -join '; ')
    Check '3.2 the broker flags the person''s uncollected request' ($mine.Count -ge 1)
}

Head 'step 5.2 and 3.2: back, and what the health page says'
$health = GetPage "http://127.0.0.1:$webPort/Projects/Health?project=$project"
$healthText = Text $health.Content
[void](Until 10 { (Registry) -match "$([regex]::Escape($person))\s.*\bhere\b" })
Check '5.2 one touch of the web UI and parley registry reads here again' ((Registry) -match "$([regex]::Escape($person))\s.*\bhere\b")
if (-not $SkipIdle) {
    $waitingOnYou = [regex]::Match($healthText, 'Waiting on you[^.]*\.').Value
    Why $waitingOnYou
    Check '3.2 the health page reads "Waiting on you"' ($waitingOnYou.Length -gt 0)
    Check '3.2 naming the agent that asked' ($waitingOnYou -match [regex]::Escape("orders@$machine"))
    Check '3.2 with no "ask the agent" remedy' ($healthText -notmatch '(?i)ask the agent|ask again')
    Note '3.2 the terminal''s Health dialog (Alt+y) is not drivable; the web UI''s health page stands in'
}

Head 'step 4.2: answer it from the web UI'
$inbox = GetPage "http://127.0.0.1:$webPort/Inbox"
$taken = PostForm "http://127.0.0.1:$webPort/Inbox?handler=Take" $inbox @(,@('For', $project))
$takenText = Text $taken.Content
Why ([regex]::Match($takenText, '\d+ taken[^.]*\.').Value)
Check '4.2 taking delivery needs no join' ($takenText -match '1 taken')
$exchange = if ($taken.Content -match '(?s)handler=Result.*?name="Exchange" value="(\d+)"') { $Matches[1] } else { $null }
$ids = [regex]::Matches(($taken.Content -split 'handler=Result', 2)[-1], 'name="Ids" value="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
Why "result form: exchange $exchange, items $($ids -join ',')"
$fields = [System.Collections.Generic.List[object]]::new()
$fields.Add(@('For', $project)); $fields.Add(@('Exchange', $exchange))
foreach ($id in $ids) { $fields.Add(@('Ids', $id)); $fields.Add(@('Texts', 'In the currency the order was paid in.')); $fields.Add(@('Verdicts', 'answered')) }
$answered = PostForm "http://127.0.0.1:$webPort/Inbox?handler=Result" $taken $fields
Why ([regex]::Match((Text $answered.Content), 'Your messages (.{0,200})').Groups[1].Value)

$script:got = @()
[void](Until 20 { $script:got = @(Inbox $orders | Where-Object { $_.from -eq $person }); $script:got.Count -gt 0 })
Check "4.2 the agent's next collection holds the Result, from $person" ($script:got.Count -ge 1)
Check '4.2 and it is a Result' ($script:got.Count -ge 1 -and "$($script:got[0].type)" -match '(?i)result')
$transcript = GetPage ("http://127.0.0.1:$webPort/Conversations/Detail?project=$project&id=" + [uri]::EscapeDataString($conversation))
Check '4.2 the transcript says the Result was written by human:human-user' ((Text $transcript.Content) -match 'written by human:human-user')

# ================================================================================================
Head 'step 6: the web UI is the same person'
$any = @('/', '/Inbox', "/Projects/Agents?project=$project", '/Conversations') | ForEach-Object {
    try { (GetPage "http://127.0.0.1:$webPort$_").Content } catch { '' }
}
Check '6.1 no page offers a Join link' (-not (($any -join '') -match 'href="/Join'))
$joinPage = try { (Invoke-WebRequest "http://127.0.0.1:$webPort/Join" -UseBasicParsing -WebSession $script:session -TimeoutSec 10).StatusCode } catch { $_.Exception.Response.StatusCode.value__ }
Check "6.1 and /Join is not a page any more ($joinPage)" ($joinPage -eq 404)
$waitingNow = (& $parley --config $root --project $project waiting $person 2>&1 | Out-String).Trim()
$webNow = WebCount (GetPage "http://127.0.0.1:$webPort/Inbox")
Check "6.1 the web UI's count ($webNow) is the broker's ($waitingNow)" ("$webNow" -eq ($waitingNow -split '\s+')[0])

$detail = GetPage ("http://127.0.0.1:$webPort/Conversations/Detail?project=$project&id=" + [uri]::EscapeDataString($conversation))
$sent = PostForm ("http://127.0.0.1:$webPort/Conversations/Detail?project=$project&id=" + [uri]::EscapeDataString($conversation)) $detail `
    @(@('Targets', "orders@$machine"), @('Says', 'And refunds over 500 need a second approval.'))
Why ([regex]::Match((Text $sent.Content), "orders@$machine[^.]*\.").Value)
$script:got = @()
[void](Until 20 { $script:got = @(Inbox $orders | Where-Object { $_.from -eq $person }); $script:got.Count -gt 0 })
Check "5.2 / 6.1 a message sent from the web UI arrives, attributed to $person, with no join" ($script:got.Count -ge 1)
Note '6.1''s "while the terminal is open" and 5.2''s key press are the terminal''s and not drivable'

# ---- isolation held -----------------------------------------------------------------------------
Head 'isolation'
Check 'the live node.json is untouched' ((HashOrAbsent $liveNode) -eq $nodeBefore)
Check "this repository's own .mcp.json is untouched" ((HashOrAbsent $liveMcp) -eq $mcpBefore)
Check 'the live root gained no project' `
    (@(Get-ChildItem (Join-Path $liveRoot 'projects') -Directory -ErrorAction SilentlyContinue).Count -eq $liveProjectsBefore)

# ---- down: only what this walk started, by process id -------------------------------------------
Head 'stopping what the walk started'
$channel.Dispose()
foreach ($id in $script:started) {
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($p -and $p.Path -and $p.Path.StartsWith($Tools, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }
}

# The scratch area goes too, unless -Keep: nothing the walk made is worth anything after it. Waited for briefly, because a
# process stopped a moment ago can still hold a file open.
if (-not $Keep) {
    Start-Sleep -Seconds 2
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $scratch) { Start-Sleep -Seconds 3; Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $scratch) {
        # What is left is what this process has loaded (the tools' assemblies when they live in the scratch area), which
        # only its exit releases: a remover waits for that and then finishes the job.
        Start-Process -FilePath (Get-Process -Id $PID).Path -WindowStyle Hidden -ArgumentList @('-NoProfile', '-Command',
            "Wait-Process -Id $PID -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; Remove-Item -LiteralPath '$scratch' -Recurse -Force")
        Note "the scratch area holds files this process has loaded; a remover deletes it once this process exits"
    } else { Ok 'the scratch area is gone' }
}

Write-Host ''
if ($script:notes.Count) {
    Write-Host "$($script:notes.Count) thing(s) recorded rather than checked:" -ForegroundColor DarkYellow
    $script:notes | ForEach-Object { Write-Host "  - $_" -ForegroundColor DarkYellow }
}
Write-Host ''
Write-Host "$($script:pass + $script:fail) checks, $($script:pass) passed, $($script:fail) failed"
exit $script:fail

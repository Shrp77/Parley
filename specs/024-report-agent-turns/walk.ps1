# T035: walks specs/024-report-agent-turns/quickstart.md.
#
# Everything runs against tools packed and installed into a scratch path, with a root, a store and a port of their own,
# so the machine's real deployment is untouched. **THE FIRST THING IT DOES IS CLEAR EVERY PARLEY_ VARIABLE**, for the
# reason 020's walk records: this machine points PARLEY_NODE_CONFIG at the live catalogue.
#
# THE NODE IS REAL AND SO IS THE PROTOCOL IT SPEAKS TO ITS AGENT. `parley up` runs from the scratch tools against a
# scratch catalogue whose one entry, `billing`, is a stand-in for Claude Code: a PowerShell script (written into the
# scratch area at the start) that speaks Claude Code's stream-json turn protocol on its standard streams, and plays the
# model through a real parley-mcp it launches from the repository's own .mcp.json -- carrying the node's
# PARLEY_LAUNCH_TOKEN, as Claude Code would. What it does with each turn is read from `behaviour.txt` in its
# repository, which this script writes between steps: answer after N seconds, end the turn with a refused permission,
# ask orders something back, or hang. So every turn the broker hears about is one the node actually gave and measured.
# `orders` is a parley-mcp this script attaches and plays itself, over its stdio, as 022's walk does.
#
# Surfaces: the room is the terminal's, and the terminal refuses a redirected stream, so the web UI's conversation page
# -- which 024 T031 draws from the same Activities derivation, mark and words -- stands in for it, read over HTTP. The
# raw facts are also read over gRPC (Operators.DescribeRegistry, DescribeFlags) with the shipped Parley.Transport.dll.
#
# WHAT THIS WALK DOES NOT MEASURE:
#   * The terminal's room itself (cited to RoomActivityTests and ActivityOnTheWebTests, which hold the two surfaces to
#     one sentence).
#   * A real model refusing a real command. The stand-in reports one permission denial in the shape Claude Code's
#     result event carries; whether Claude Code reports a refusal that way is outside a walk's reach.
#   * Step 4's "allow the command in billing's repository settings": the stand-in has no settings. Its behaviour is
#     switched to answering, which is what allowing the command would let it do.

param(
    [string]$Tools,
    [string]$OldParley,
    [switch]$Keep
)

$ErrorActionPreference = 'Continue'

$repoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$scratch    = Join-Path $env:TEMP 'parley-024-walk'
$root       = Join-Path $scratch 'root'
$repos      = Join-Path $scratch 'repos'
$logs       = Join-Path $scratch 'logs'
$project    = 'shop'
$brokerPort = 19184         # the live deployment holds 19100
$webPort    = 5297          # and 5280
if (-not $Tools) { $Tools = Join-Path $scratch 'tools' }

$parley = Join-Path $Tools 'parley.exe'
$mcp    = Join-Path $Tools 'parley-mcp.exe'
$webui  = Join-Path $Tools 'parley-web-ui.exe'
$pwsh   = (Get-Process -Id $PID).Path

$script:fail  = 0
$script:pass  = 0
$script:notes = @()
$script:started = @()
function Ok    ($t) { Write-Host "  ok    $t" -ForegroundColor DarkGreen; $script:pass++ }
function Bad   ($t) { Write-Host "  FAIL  $t" -ForegroundColor Red; $script:fail++ }
function Check ($t, $cond) { if ($cond) { Ok $t } else { Bad $t } }
function Why   ($t) { Write-Host "        $t" -ForegroundColor DarkGray }
function Note  ($t) { Write-Host "  note  $t" -ForegroundColor DarkYellow; $script:notes += $t }
function Head  ($t) { Write-Host ''; Write-Host "==> $t" -ForegroundColor Cyan }
function Track ($p) { if ($p) { $script:started += $p.Id }; $p }
function Until($seconds, [scriptblock]$condition) {
    for ($i = 0; $i -lt ($seconds * 4); $i++) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 250 }
    [bool](& $condition)
}

# ---- environment hygiene, first -----------------------------------------------------------------
Get-ChildItem Env: | Where-Object Name -like 'PARLEY_*' | ForEach-Object {
    Remove-Item "Env:\$($_.Name)" -ErrorAction SilentlyContinue
}
$env:PARLEY_CONFIG       = $root
$env:PARLEY_NODE_CONFIG  = Join-Path $root 'node.json'
$env:PARLEY_TICK_SECONDS = '1'

function StandIns() {
    Get-CimInstance Win32_Process -Filter "Name = 'pwsh.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($scratch) }
}
if (Test-Path $scratch) {
    StandIns | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    # Only what lives inside this walk's own scratch area: the tools may be shared with another walk (-Tools).
    Get-Process 'parley*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($scratch, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Get-ChildItem $scratch -Exclude 'tools','pkg','old' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force $scratch, $root, $repos, $logs | Out-Null

# ---- isolation baseline -------------------------------------------------------------------------
Head 'isolation baseline'
$liveRoot = Join-Path $env:APPDATA 'Parley'
$liveNode = Join-Path $liveRoot 'node.json'
$liveMcp  = Join-Path $repoRoot '.mcp.json'
function HashOrAbsent($path) { if (Test-Path $path) { (Get-FileHash $path -Algorithm MD5).Hash } else { 'absent' } }
$nodeBefore = HashOrAbsent $liveNode
$mcpBefore  = HashOrAbsent $liveMcp
$liveProjectsBefore = @(Get-ChildItem (Join-Path $liveRoot 'projects') -Directory -ErrorAction SilentlyContinue).Count

# ---- tools, packed and installed; and the previous version's node, built from its commit --------
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

if (-not $OldParley) {
    # The release before 024 (0.38.0): its node joins without reports_turns. Built from an archive of its commit --
    # `git archive` reads the object store and touches neither the index nor the working tree.
    $old = Join-Path $scratch 'old'
    $OldParley = Join-Path $old 'bin\parley.exe'
    if (-not (Test-Path $OldParley)) {
        Head 'building the previous version''s node (0.37.9) from its commit'
        $commit = (& git -C $repoRoot log -1 --format=%H --fixed-strings --grep='(0.37.9)').Trim()
        New-Item -ItemType Directory -Force (Join-Path $old 'src') | Out-Null
        & git -C $repoRoot archive $commit -o (Join-Path $old 'src.tar')
        & tar -xf (Join-Path $old 'src.tar') -C (Join-Path $old 'src')
        & dotnet build (Join-Path $old 'src\src\Parley.Cli\Parley.Cli.csproj') -c Release -o (Join-Path $old 'bin') 2>&1 | Select-Object -Last 1
    }
}
$oldBuilt = (& $OldParley --version) -join ' '
Check "the previous version's node is built ($oldBuilt)" ($oldBuilt -match '0\.37\.9')

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
    ([type]"Parley.Transport.$service+${service}Client").GetConstructor([type[]]@([Grpc.Core.ChannelBase])).Invoke(@($channel))
}
$ops   = Client 'Operators'
$nodes = Client 'Nodes'
function Opts() { [Grpc.Core.CallOptions]::new() }
function Row($name) {
    try { @($ops.DescribeRegistry([Parley.Transport.Empty]::new(), (Opts)).Rows | Where-Object Name -eq $name)[0] } catch { $null }
}
function Flags() {
    try { @($ops.DescribeFlags([Parley.Transport.Empty]::new(), (Opts)).Flags) } catch { @() }
}
function NodeJoined($machine) {
    $r = [Parley.Transport.DescribeNodeRequest]::new(); $r.Machine = $machine
    try { $nodes.Describe($r, (Opts)).Connected } catch { $false }
}

# ---- the store, read on a copy so this walk is never a second writer ----------------------------
$sqliteFrom = Join-Path $repoRoot 'tests\Parley.Store.Tests\bin\Debug\net10.0'
# Loaded in place: a copy in the scratch area would be held open by this process and outlive the cleanup.
$nativeDir = Join-Path $sqliteFrom 'runtimes\win-x64\native'
$env:PATH = "$nativeDir;$env:PATH"
foreach ($one in 'Microsoft.Data.Sqlite.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.e_sqlite3.dll','SQLitePCLRaw.batteries_v2.dll') {
    Add-Type -Path (Join-Path $sqliteFrom $one)
}
[SQLitePCL.Batteries_V2]::Init()
function Read-Store($sql) {
    $store = Join-Path $root "projects\$project\parley.db"
    $copy = Join-Path $scratch "copy-$([Guid]::NewGuid().ToString('n')).db"
    Copy-Item $store $copy -Force
    foreach ($c in '-wal', '-shm') { if (Test-Path "$store$c") { Copy-Item "$store$c" "$copy$c" -Force } }
    $db = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$copy;Pooling=False")
    $db.Open()
    try {
        $cmd = $db.CreateCommand(); $cmd.CommandText = $sql
        $r = $cmd.ExecuteReader(); $out = @()
        while ($r.Read()) { $out += ,@(0..($r.FieldCount - 1) | ForEach-Object { $r.GetValue($_) }) }
        $r.Close()
        return , $out
    } finally { $db.Close(); Remove-Item "$copy*" -Force -ErrorAction SilentlyContinue }
}

# ---- the stand-in for Claude Code ---------------------------------------------------------------
$standIn = Join-Path $scratch 'standin.ps1'
@'
# A stand-in for Claude Code, for 024's walk only. Speaks the stream-json turn protocol a node drives (one "user" line
# in, one "result" line out per turn) and plays the model through the repository's own parley-mcp.
$ErrorActionPreference = 'Stop'
$here = (Get-Location).Path
$role = Split-Path -Leaf $here
$log  = Join-Path $env:WALK_LOGS "standin-$role-$PID.log"
function Log($t) { Add-Content -Path $log -Value ('{0:o} {1}' -f [DateTime]::UtcNow, $t) }
Log "started; launch token present: $([bool]$env:PARLEY_LAUNCH_TOKEN); args: $args"

$config = Get-Content (Join-Path $here '.mcp.json') -Raw | ConvertFrom-Json
$entry  = @($config.mcpServers.PSObject.Properties | Where-Object Name -like 'parley-*')[0].Value
$psi = [System.Diagnostics.ProcessStartInfo]::new($env:WALK_MCP)
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.WorkingDirectory = $here
foreach ($p in $entry.env.PSObject.Properties) { $psi.Environment[$p.Name] = "$($p.Value)" }
$psi.Environment['PARLEY_WORKSPACE'] = $here
$server = [System.Diagnostics.Process]::Start($psi)
[void]$server.StandardError.ReadToEndAsync()
$script:id = 0
function Rpc($method, $params) {
    $script:id++
    $mine = $script:id
    $server.StandardInput.WriteLine((@{ jsonrpc = '2.0'; id = $mine; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress))
    $server.StandardInput.Flush()
    while ($true) {
        $read = $server.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(60000)) { return $null }
        if ($null -eq $read.Result) { return $null }
        try { $reply = $read.Result | ConvertFrom-Json } catch { continue }
        if ($reply.id -eq $mine) { return $reply }
    }
}
function Tool($name, $arguments) {
    $reply = Rpc 'tools/call' @{ name = $name; arguments = $arguments }
    if ($null -eq $reply) { return '' }
    ($reply.result.content | ForEach-Object { $_.text }) -join "`n"
}
[void](Rpc 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'standin'; version = '1' } })
$server.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}'); $server.StandardInput.Flush()

$owed = [System.Collections.Generic.List[object]]::new()
$asked = $false
try {
    while ($null -ne ($line = [Console]::In.ReadLine())) {
        try { $evt = $line | ConvertFrom-Json } catch { continue }
        if ($evt.type -ne 'user') { continue }
        $behaviour = if (Test-Path (Join-Path $here 'behaviour.txt')) { (Get-Content (Join-Path $here 'behaviour.txt') -Raw).Trim() } else { 'answer:1' }
        Log "turn begins ($behaviour)"
        $inbox = Tool 'parley_inbox' @{}
        $messages = try { @(($inbox | ConvertFrom-Json).messages) } catch { @() }
        foreach ($m in $messages) {
            Log "inbox: $($m.type) from $($m.from), exchange $($m.exchange_id)"
            if ($m.from -like 'parley-facilitator@*') {
                if ($m.type -match '(?i)request') {
                    $r = Tool 'parley_result' @{ exchangeId = [long]$m.exchange_id; outcome = 'answered'; text = 'Invoices, tax and refunds. Not order totals.' }
                    Log "answered Parley: $r"
                }
                continue
            }
            if ($m.type -match '(?i)request') { $owed.Add($m) }
        }
        $denials = 0
        if ($owed.Count -gt 0) {
            switch -regex ($behaviour) {
                '^answer:(\d+)$' {
                    Start-Sleep -Seconds ([int]$Matches[1])
                    foreach ($o in @($owed)) {
                        $r = Tool 'parley_result' @{ exchangeId = [long]$o.exchange_id; outcome = 'answered'; text = 'Refunds are issued in euros.' }
                        Log "answered $($o.exchange_id): $r"
                    }
                    $owed.Clear()
                }
                '^refuse:(\d+)$' { Start-Sleep -Seconds ([int]$Matches[1]); $denials = 1; Log 'a command was refused for want of permission' }
                '^askback:([a-z-]+)$' {
                    if (-not $asked) {
                        $r = Tool 'parley_request' @{ to = "$($Matches[1])@$($env:WALK_MACHINE)"; ask = 'Which currency do you settle refunds in?' }
                        Log "asked back: $r"; $asked = $true
                    }
                }
                '^hang$' { Log 'hanging'; Start-Sleep -Seconds 100000 }
            }
        }
        $refused = @()
        for ($i = 0; $i -lt $denials; $i++) { $refused += @{ tool_name = 'Bash'; tool_use_id = "toolu_$i"; tool_input = @{ command = 'dotnet test' } } }
        $result = [ordered]@{ type = 'result'; subtype = 'success'; is_error = $false; num_turns = 1; total_cost_usd = 0
                              permission_denials = $refused; result = 'turn over' }
        [Console]::Out.WriteLine(($result | ConvertTo-Json -Depth 6 -Compress))
        [Console]::Out.Flush()
        Log "turn ends; denials $denials; still owed $($owed.Count)"
    }
} finally {
    Log 'standard input closed; exiting'
    if (-not $server.HasExited) { $server.Kill() }
}
'@ | Set-Content -Path $standIn -Encoding utf8NoBOM

$machine = $env:COMPUTERNAME.ToLowerInvariant() -replace '[^a-z0-9.-]', ''
$env:WALK_MCP     = $mcp
$env:WALK_LOGS    = $logs
$env:WALK_MACHINE = $machine
$billing = "billing@$machine"
$ordersName = "orders@$machine"

# ---- a project, two repositories, a catalogue, a broker, the web UI and a node -------------------
Head 'a project, a broker, the web UI and a node'
$folder = Join-Path $root "projects\$project"
New-Item -ItemType Directory -Force $folder | Out-Null
"{`n  `"endpoint`": `"http://127.0.0.1:$brokerPort`"`n}" | Set-Content -Path (Join-Path $folder 'project.json') -Encoding utf8NoBOM
Note 'the project is written as the project.json the product writes (naming one is the terminal''s since 019)'

$billingId = [Guid]::NewGuid().ToString()
function Repo($role, $id) {
    $dir = Join-Path $repos $role
    New-Item -ItemType Directory -Force $dir | Out-Null
    & git -C $dir init --quiet 2>&1 | Out-Null
    $json = [ordered]@{ mcpServers = [ordered]@{ "parley-$project" = [ordered]@{ command = 'parley-mcp'; env = [ordered]@{ PARLEY_AGENT = $role; PARLEY_PROJECT = $project } } } }
    if ($id) { $json['parleyAgentId'] = $id }
    $json | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $dir '.mcp.json') -Encoding utf8NoBOM
    $dir
}
$ordersRepo  = Repo 'orders' $null
$billingRepo = Repo 'billing' $billingId
function Behave($what) { Set-Content -Path (Join-Path $billingRepo 'behaviour.txt') -Value $what -Encoding utf8NoBOM }
Behave 'answer:12'
@{ agents = @{ $billingId = @{ command = "`"$pwsh`" -NoProfile -NonInteractive -File $standIn --input-format stream-json"; workspace = $billingRepo } } } |
    ConvertTo-Json -Depth 5 | Set-Content -Path $env:PARLEY_NODE_CONFIG -Encoding utf8NoBOM

$script:broker = Track (Start-Process -FilePath $parley -PassThru -NoNewWindow `
    -ArgumentList @('serve', '--config', $root, '--log', (Join-Path $logs 'broker.log')) `
    -RedirectStandardOutput (Join-Path $logs 'broker.out') -RedirectStandardError (Join-Path $logs 'broker.err'))
Check 'the broker answers' (Until 60 { [void](& $parley --config $root --project $project registry 2>&1); $LASTEXITCODE -eq 0 })

$auto = [Parley.Transport.SetAutoAcceptRequest]::new(); $auto.On = $false
try { [void]$ops.SetAutoAccept($auto, (Opts)) } catch { }
Note ('Auto Accept is turned off first, so that Parley accepting billing''s ownership answer does not have the node ' +
      'restart billing mid-walk (022); what Parley asks billing is answered by the stand-in in whatever turn it arrives')

$webProcess = Track (Start-Process -FilePath $webui -PassThru -NoNewWindow `
    -ArgumentList @('--listen', "http://127.0.0.1:$webPort") `
    -RedirectStandardOutput (Join-Path $logs 'web.out') -RedirectStandardError (Join-Path $logs 'web.err'))
Check 'the web UI answers' (Until 30 { try { [void](Invoke-WebRequest "http://127.0.0.1:$webPort/" -UseBasicParsing -TimeoutSec 2); $true } catch { $false } })

function StartNode($exe, $name) {
    Track (Start-Process -FilePath $exe -PassThru -NoNewWindow -ArgumentList @('--config', $root, 'up') `
        -RedirectStandardOutput (Join-Path $logs "$name.out") -RedirectStandardError (Join-Path $logs "$name.err"))
}
$script:node = StartNode $parley 'node'
Check "a $shipping node joins for this machine" (Until 30 { NodeJoined $machine })

# A never-registered agent can be sent nothing (012, NeverRegisteredTests), so billing is started once the way enrolment
# starts what it catalogues, and registers itself on attaching. Parley then asks it what it owns, which costs it a turn
# the stand-in spends answering; that is let finish before anything is measured.
$first = [Parley.Transport.StartRequest]::new(); $first.Agent = $billing; $first.Because = 'registering, as enrolment does'
try { [void]$ops.Start($first, (Opts)) } catch { Why "start: $($_.Exception.Message)" }
Check 'billing registers itself once the node has started it' (Until 30 { $null -ne (Row $billing) })
[void](Until 30 { @(Get-ChildItem $logs -Filter 'standin-billing-*.log' | Get-Content | Where-Object { $_ -match 'answered Parley' }).Count -ge 1 })
[void](Until 10 { $r = Row $billing; $r -and -not $r.TurnRunningSinceUtc })

# ---- orders, played by this script over parley-mcp's own stdio ----------------------------------
function Rpc($agent, $method, $params) {
    $agent.Id++
    $id = $agent.Id
    $agent.Process.StandardInput.WriteLine((@{ jsonrpc = '2.0'; id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress))
    $agent.Process.StandardInput.Flush()
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        $read = $agent.Process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(30000)) { break }
        if ($null -eq $read.Result) { break }
        try { $reply = $read.Result | ConvertFrom-Json } catch { continue }
        if ($reply.id -eq $id) { return $reply }
    }
    $null
}
function Tool($agent, $name, $arguments) {
    $reply = Rpc $agent 'tools/call' @{ name = $name; arguments = $arguments }
    if ($null -eq $reply) { return '' }
    ($reply.result.content | ForEach-Object { $_.text }) -join "`n"
}
function Inbox($agent) { $t = Tool $agent 'parley_inbox' @{}; try { @(($t | ConvertFrom-Json).messages) } catch { @() } }
$p = [System.Diagnostics.ProcessStartInfo]::new()
$p.FileName = $mcp; $p.UseShellExecute = $false
$p.RedirectStandardInput = $true; $p.RedirectStandardOutput = $true; $p.RedirectStandardError = $true
$p.WorkingDirectory = $ordersRepo
foreach ($k in @($p.Environment.Keys)) { if ($k -like 'PARLEY_*') { [void]$p.Environment.Remove($k) } }
$p.Environment['PARLEY_CONFIG'] = $root; $p.Environment['PARLEY_PROJECT'] = $project
$p.Environment['PARLEY_AGENT'] = 'orders'; $p.Environment['PARLEY_MACHINE'] = $machine
$p.Environment['PARLEY_WORKSPACE'] = $ordersRepo
$ordersProc = Track ([System.Diagnostics.Process]::Start($p))
[void]$ordersProc.StandardError.ReadToEndAsync()
$orders = [pscustomobject]@{ Name = $ordersName; Process = $ordersProc; Id = 0 }
[void](Rpc $orders 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'walk'; version = '1' } })
$ordersProc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}'); $ordersProc.StandardInput.Flush()
$script:q = @()
[void](Until 30 { $script:q = @(Inbox $orders | Where-Object { $_.message -match 'Question from Parley' }); $script:q.Count -gt 0 })
if ($script:q.Count) { [void](Tool $orders 'parley_result' @{ exchangeId = [long]$script:q[0].exchange_id; outcome = 'answered'; text = 'Orders and their totals.' }) }
# Everything orders collects is kept, because collecting is draining: a filter applied at the drain would lose the rest.
$script:mail = [System.Collections.Generic.List[object]]::new()
function Collect() { foreach ($m in @(Inbox $orders)) { $script:mail.Add($m) } }
function FromBilling($type) { @($script:mail | Where-Object { $_.from -eq $billing -and (-not $type -or "$($_.type)" -match $type) }) }

# ---- the room, as the web UI's conversation page draws it ---------------------------------------
$conversation = 'refunds'
function RoomLine($who) {
    try {
        $page = Invoke-WebRequest ("http://127.0.0.1:$webPort/Conversations/Detail?project=$project&id=$conversation") -UseBasicParsing -TimeoutSec 15
    } catch { return '' }
    $aside = [regex]::Match($page.Content, '(?s)<aside class="roster" aria-label="In this conversation">(.*?)</aside>').Groups[1].Value
    $m = [regex]::Match($aside, "(?s)>$([regex]::Escape($who))</a>.*?<span class=""state"">(.*?)</span>")
    if ($m.Success) { [System.Net.WebUtility]::HtmlDecode($m.Groups[1].Value).Trim() } else { '' }
}
function Ask($text) {
    $said = Tool $orders 'parley_request' @{ to = $billing; ask = $text; conversationId = $conversation }
    $reply = try { $said | ConvertFrom-Json } catch { $null }
    if (-not $reply -or -not $reply.ok) { Why "parley_request was not admitted: $(($said -replace '\s+', ' ').Trim())" }
    if ($reply) { $reply } else { [pscustomobject]@{ raw = $said } }
}

# ================================================================================================
Head 'step 1: working is a turn'
$sent = Ask 'Which currency are refunds issued in?'
Why "receiver_is: $($sent.receiver_is)"
$script:line = ''
Check '1.1 while billing''s turn runs, the room shows ◆ billing — working' `
    (Until 40 { $script:line = RoomLine $billing; $script:line -match '^◆ working' })
Why "room: $billing -> '$($script:line)'"
$row = Row $billing
Check '1.1 the registry carries the running turn (turn_running_since_utc set, turns reported)' `
    ($row -and $row.TurnsReported -and $row.TurnRunningSinceUtc)
Check '1.1 and billing is started by Parley, not open outside' ($row -and -not $row.OpenOutside)
[void](Until 40 { Collect; (FromBilling 'result').Count -ge 1 })
Check '1.2 billing answers' ((FromBilling 'result').Count -ge 1)
Check '1.2 then the mark goes back to running' (Until 20 { $script:line = RoomLine $billing; $script:line -notmatch 'working' -and $script:line -match 'running' })
Why "room: $billing -> '$($script:line)'"

# ================================================================================================
Head 'step 2: stopped is a turn that ended owing'
Behave 'refuse:2'
$sent = Ask 'Please rerun the refund tests and tell me whether they pass.'
[void](Until 40 { $r = Row $billing; $r -and $r.LastTurnPermissionDenials -eq 1 -and -not $r.TurnRunningSinceUtc })
$row = Row $billing
Why "registry: running='$($row.TurnRunningSinceUtc)' last ended='$($row.LastTurnEndedUtc)' clean=$($row.LastTurnClean) denials=$($row.LastTurnPermissionDenials)"
$endedAt = [DateTime]::UtcNow
Check '2.1 the turn ended with one permission refused, and none is running' ($row -and $row.LastTurnPermissionDenials -eq 1 -and -not $row.TurnRunningSinceUtc)
Check '2.1 within one refresh the room shows ■ billing — stopped <1m — 1 permission refused' `
    (Until 10 { $script:line = RoomLine $billing; $script:line -eq '■ stopped <1m — 1 permission refused' })
Why "room: $billing -> '$($script:line)'"
Check '2.1 never working' ($script:line -notmatch 'working')

# ================================================================================================
Head 'step 3: told at once'
$refusedFlag = $null
[void](Until 10 { $script:rf = @(Flags | Where-Object { "$($_.Kind)" -eq 'PermissionRefused' -and $_.WaitingOn -eq $billing }); $script:rf.Count -ge 1 })
$refusedFlag = @($script:rf)[0]
Why "flag: $($refusedFlag.Kind) waiting_on=$($refusedFlag.WaitingOn) waiting=$($refusedFlag.Waiting) conv=$($refusedFlag.ConversationId), $([int]([DateTime]::UtcNow - $endedAt).TotalSeconds)s after the turn ended"
Check '3.1 a permission-refused flag names billing' ($null -ne $refusedFlag)
Check '3.1 and the conversation' ($refusedFlag -and $refusedFlag.ConversationId -eq $conversation)
$health = Invoke-WebRequest "http://127.0.0.1:$webPort/Projects/Health?project=$project" -UseBasicParsing -TimeoutSec 15
$healthText = [System.Net.WebUtility]::HtmlDecode(($health.Content -replace '<[^>]+>', ' ')) -replace '\s+', ' '
$said = [regex]::Match($healthText, "$([regex]::Escape($billing)) stopped on .{0,160}?: a tool call it needed was refused for want of permission[^.]*\.").Value
Why "health page: $said"
Check '3.1 the web UI''s health page says it, naming billing and the asker' ($said -match [regex]::Escape($ordersName))
Note '3.1 the terminal''s Health dialog is not drivable; the web UI''s health page and Operators.DescribeFlags stand in'

# ================================================================================================
Head 'step 4: it clears'
Behave 'answer:1'
$start = [Parley.Transport.StartRequest]::new(); $start.Agent = $billing; $start.Because = 'the walk allowed the command'
$given = try { $ops.Start($start, (Opts)) } catch { $_.Exception.InnerException.Status.Detail }
Why "Operators.Start: $given"
[void](Until 40 { Collect; (FromBilling 'result').Count -ge 2 })
Check '4.1 given a turn, billing answers' ((FromBilling 'result').Count -ge 2)
Check '4.1 the room''s stopped mark goes' (Until 15 { $script:line = RoomLine $billing; $script:line -notmatch 'stopped' })
Why "room: $billing -> '$($script:line)'"
Check '4.1 and the flag goes' (Until 15 { @(Flags | Where-Object { "$($_.Kind)" -eq 'PermissionRefused' -and $_.WaitingOn -eq $billing }).Count -eq 0 })
Note '4.1 "allow the command in billing''s repository settings" is the stand-in switching to answering: it has no settings'

# ================================================================================================
Head 'step 5: waiting is not stopped'
Behave 'askback:orders'
$sent = Ask 'Can refunds go out before the order ships?'
$script:back = @()
[void](Until 40 { Collect; $script:back = @(FromBilling 'request'); $script:back.Count -ge 1 })
Check '5.1 billing asks orders something back and ends its turn' ($script:back.Count -ge 1)
[void](Until 20 { $r = Row $billing; $r -and -not $r.TurnRunningSinceUtc })
Check '5.1 the room shows billing — waiting on orders' (Until 15 { $script:line = RoomLine $billing; $script:line -match 'waiting on orders' })
Why "room: $billing -> '$($script:line)'"
Check '5.1 not stopped' ($script:line -notmatch 'stopped')

# ================================================================================================
Head 'step 6: a machine that goes away'
Behave 'hang'
if ($script:back.Count) {
    [void](Tool $orders 'parley_result' @{ exchangeId = [long]$script:back[0].exchange_id; outcome = 'answered'; text = 'We settle in euros.' })
}
Check '6.0 orders answers; billing''s next turn runs and hangs, and the room shows working' `
    (Until 40 { $script:line = RoomLine $billing; $script:line -match '^◆ working' })
$children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($script:node.Id)" -ErrorAction SilentlyContinue)
$grand = @($children | ForEach-Object { Get-CimInstance Win32_Process -Filter "ParentProcessId = $($_.ProcessId)" -ErrorAction SilentlyContinue })
$script:started += @($children + $grand | ForEach-Object { [int]$_.ProcessId })
Stop-Process -Id $script:node.Id -Force
$stoppedAt = [DateTime]::UtcNow
Check '6.1 within one refresh of the node going, the registry claims no running turn' `
    (Until 10 { $r = Row $billing; $r -and -not $r.TurnRunningSinceUtc })
Check '6.1 and the room stops claiming working' (Until 10 { $script:line = RoomLine $billing; $script:line -notmatch 'working' })
Why "room: $billing -> '$($script:line)', $([int]([DateTime]::UtcNow - $stoppedAt).TotalSeconds)s after the node was stopped"
$row = Row $billing
Why "registry: presence=$($row.Presence) turns_reported=$($row.TurnsReported) open_outside=$($row.OpenOutside)"
Note ('6.1 the node was killed, not asked to stop, so it could not tell the agent it started to quit: the stand-in and ' +
      'its parley-mcp were left running and are stopped by the walk by process id')
foreach ($c in @($grand + $children)) { Stop-Process -Id $c.ProcessId -Force -ErrorAction SilentlyContinue }
[void](Until 20 { $r = Row $billing; $r -and "$($r.Presence)" -ne 'Running' })

# ================================================================================================
Head 'step 7: an older node (0.37.9)'
Behave 'answer:12'
$script:oldNode = StartNode $OldParley 'old-node'
Check '7.0 the 0.37.9 node joins' (Until 30 { NodeJoined $machine })
$row = Row $billing
Check '7.1 before anything runs, billing''s turns read as not reported' ($row -and -not $row.TurnsReported)
$sent = Ask 'One more: do refunds include shipping?'
Why "receiver_is: $($sent.receiver_is)"
$seen = @()
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while ([DateTime]::UtcNow -lt $deadline) { $seen += (RoomLine $billing); Start-Sleep -Milliseconds 700 }
$seen = @($seen | Where-Object { $_ } | Select-Object -Unique)
Why "room over 30s: $($seen -join ' | ')"
Check '7.1 its agent is never shown working' (@($seen | Where-Object { $_ -match 'working' }).Count -eq 0)
Check '7.1 nor stopped' (@($seen | Where-Object { $_ -match 'stopped' }).Count -eq 0)
$row = Row $billing
Why "registry: presence=$($row.Presence) turns_reported=$($row.TurnsReported) open_outside=$($row.OpenOutside)"
Check '7.1 superseded by 0.40.0: what the room shows is "open outside Parley", not presence alone' `
    (@($seen | Where-Object { $_ -match 'open outside Parley' }).Count -ge 1)
$before7 = (FromBilling 'result').Count
[void](Until 30 { Collect; (FromBilling 'result').Count -gt $before7 })
$script:afterOld = @((FromBilling 'result') | Select-Object -Skip $before7)
Why "billing answered under the 0.37.9 node: $($script:afterOld.Count) result(s)"
$again = Ask 'And do they include import duties?'
Why "a second request under the 0.37.9 node, receiver_is: $($again.receiver_is)"
$before7b = (FromBilling 'result').Count
[void](Until 30 { Collect; (FromBilling 'result').Count -gt $before7b })
$script:secondTurn = @((FromBilling 'result') | Select-Object -Skip $before7b)
$collected = if ($again.exchange_id) {
    Read-Store "SELECT coalesce(delivered_utc, 'not collected') FROM messages WHERE exchange_id = $([long]$again.exchange_id) ORDER BY id LIMIT 1"
} else { @() }
Why ("the second request (exchange $($again.exchange_id)) after 30s: answered $(if ($script:secondTurn.Count) { 'yes' } else { 'no' }); " +
     "delivered: $(if ($collected.Count) { $collected[0][0] } else { '?' })")
Note ("7 FINDING: an agent started by a node older than 0.40.0 gets the one turn its start carries and no other. Its " +
      "adapter attaches with no launch token, so it reads open outside Parley (§11.7) and every later wake asks nobody: " +
      "the second request was told '$($again.receiver_is)' and was $(if ($script:secondTurn.Count) { '' } else { 'not ' })answered " +
      "while the 0.37.9 node that started it was joined. Neither the quickstart (presence only) nor 024 R5/R9 nor SPEC §18's " +
      "0.40 entry says an older node stops being able to give turns.")

# ================================================================================================
Head 'step 8: agents cannot read it'
$roster = Tool $orders 'parley_agents' @{}
Why (($roster -replace '\s+', ' ').Trim())
Check '8.1 parley_agents names billing (so the absence below means something)' ($roster -match [regex]::Escape($billing))
Check '8.1 and says nothing about anybody''s turns' ($roster -notmatch '(?i)turn|working|stopped|permission|denial|running_since')
$summary = [Parley.Transport.AgentSummary].GetProperties() | Select-Object -ExpandProperty Name
Why "AgentSummary (what ListAgents returns): $($summary -join ', ')"
Check '8.1 the Agents service''s roster row carries no turn field' (@($summary | Where-Object { $_ -match '(?i)turn|permission|outside' }).Count -eq 0)

# ---- SC-005: every turn a node runs is in the turn record -------------------------------------
Head 'the turn record'
$turns = Read-Store "SELECT started_utc, elapsed_ms, permission_denials FROM invocations WHERE agent = '$billing' ORDER BY id"
foreach ($t in $turns) { Why ("invocation: " + (($t | ForEach-Object { "$_" }) -join ' | ')) }
Check "SC-005 the node's ended turns are recorded in invocations ($($turns.Count))" ($turns.Count -ge 4)
Check 'SC-005 including the one with a permission refused' (@($turns | Where-Object { [int]$_[2] -eq 1 }).Count -ge 1)

# ---- isolation held -----------------------------------------------------------------------------
Head 'isolation'
Check 'the live node.json is untouched' ((HashOrAbsent $liveNode) -eq $nodeBefore)
Check "this repository's own .mcp.json is untouched" ((HashOrAbsent $liveMcp) -eq $mcpBefore)
Check 'the live root gained no project' `
    (@(Get-ChildItem (Join-Path $liveRoot 'projects') -Directory -ErrorAction SilentlyContinue).Count -eq $liveProjectsBefore)

# ---- down: only what this walk started, by process id -------------------------------------------
Head 'stopping what the walk started'
$channel.Dispose()
foreach ($c in @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($script:oldNode.Id)" -ErrorAction SilentlyContinue)) {
    $script:started += [int]$c.ProcessId
    Get-CimInstance Win32_Process -Filter "ParentProcessId = $($c.ProcessId)" -ErrorAction SilentlyContinue | ForEach-Object { $script:started += [int]$_.ProcessId }
}
foreach ($s in @(StandIns)) {
    $script:started += [int]$s.ProcessId
    Get-CimInstance Win32_Process -Filter "ParentProcessId = $($s.ProcessId)" -ErrorAction SilentlyContinue | ForEach-Object { $script:started += [int]$_.ProcessId }
}
foreach ($id in ($script:started | Select-Object -Unique)) {
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($p -and $p.Path -and ($p.Path.StartsWith($Tools, [StringComparison]::OrdinalIgnoreCase) -or
                              $p.Path.StartsWith((Split-Path $OldParley), [StringComparison]::OrdinalIgnoreCase) -or
                              $p.Path -eq $pwsh)) {
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

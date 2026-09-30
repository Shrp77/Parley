# T050: walks specs/022-parley-facilitator/quickstart.md.
#
# Everything runs against tools packed and installed into a scratch path, with a root, a store and a port of their
# own, so the machine's real deployment is untouched. Every PARLEY_ variable is cleared first, for the reason 020's
# walk records: this machine points PARLEY_NODE_CONFIG at the live catalogue.
#
# THE AGENTS ARE REAL parley-mcp PROCESSES, SPOKEN TO OVER THEIR OWN STDIO. Each one is attached with the environment
# its launch configuration would give it, and this script plays the model: it calls parley_inbox, reads Parley's
# question, and answers with parley_result or parley_clarify -- the calls a model would make. So what the broker reads
# back is what an adapter actually sends, rather than a row this script wrote in the shape it expected.
#
# WHAT THIS WALK DOES NOT MEASURE, said here rather than discovered halfway down:
#
#   * The refresh. An agent attached by this script was not started by a node, and a node refreshes only an agent it
#     started (SPEC.md §11.1), so the refresh is never sent here. RefreshWaitsForTheTurnTests holds the turn half with a
#     real process; NodeRefreshTests the node's refusal; FacilitatorAsksTests the first-line-only rule.
#   * The terminal. Its review and switch are on a screen no shell can drive; ParleyIsShownApartTests presses keys.
#   * A real model's answer, which is SPEC.md §17 question 10's business.

param([switch]$Keep)

$ErrorActionPreference = 'Continue'

$repoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$scratch    = Join-Path $env:TEMP 'parley-022-walk'
$root       = Join-Path $scratch 'root'
$tools      = Join-Path $scratch 'tools'
$repos      = Join-Path $scratch 'repos'
$project    = 'shop'
$brokerPort = 19181
$webPort    = 5298

$parley = Join-Path $tools 'parley.exe'
$mcp    = Join-Path $tools 'parley-mcp.exe'
$webui  = Join-Path $tools 'parley-web-ui.exe'

$script:fail  = 0
$script:notes = @()
function Ok    ($t) { Write-Host "  ok    $t" -ForegroundColor DarkGreen }
function Bad   ($t) { Write-Host "  FAIL  $t" -ForegroundColor Red; $script:fail++ }
function Check ($t, $cond) { if ($cond) { Ok $t } else { Bad $t } }
function Why   ($t) { Write-Host "        $t" -ForegroundColor DarkGray }
function Note  ($t) { Write-Host "  note  $t" -ForegroundColor DarkYellow; $script:notes += $t }
function Head  ($t) { Write-Host ''; Write-Host "==> $t" -ForegroundColor Cyan }

if (-not $Keep -and (Test-Path $scratch)) {
    Get-Process 'parley*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($tools, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force $scratch, $root, $repos | Out-Null

# ---- environment hygiene, first -----------------------------------------------------------------
Get-ChildItem Env: | Where-Object Name -like 'PARLEY_*' | ForEach-Object {
    Remove-Item "Env:\$($_.Name)" -ErrorAction SilentlyContinue
}
$env:PARLEY_CONFIG       = $root
$env:PARLEY_NODE_CONFIG  = Join-Path $root 'node.json'
# One-second passes, so the walk waits seconds rather than the shipped five per pass.
$env:PARLEY_TICK_SECONDS = '1'

# ---- isolation baseline -------------------------------------------------------------------------
Head 'isolation baseline'
$liveRoot = Join-Path $env:APPDATA 'Parley'
$liveNode = Join-Path $liveRoot 'node.json'
$liveMcp  = Join-Path $repoRoot '.mcp.json'
function HashOrAbsent($path) { if (Test-Path $path) { (Get-FileHash $path -Algorithm MD5).Hash } else { 'absent' } }
$nodeBefore = HashOrAbsent $liveNode
$mcpBefore  = HashOrAbsent $liveMcp
$liveProjectsBefore = @(Get-ChildItem (Join-Path $liveRoot 'projects') -Directory -ErrorAction SilentlyContinue).Count

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
        & dotnet tool install $one --tool-path $tools --add-source $pkg `
            --configfile (Join-Path $repoRoot 'NuGet.config') --version $shipping 2>&1 | Select-Object -Last 1
    }
}
$built = (& $parley --version) -join ' '
Check "the walk runs against packed tools in a scratch path ($built)" ($built -match [regex]::Escape($shipping))

# ---- a project, repositories, and a broker ------------------------------------------------------
Head 'a project and a broker'
$folder = Join-Path $root "projects\$project"
New-Item -ItemType Directory -Force $folder | Out-Null
"{`n  `"endpoint`": `"http://127.0.0.1:$brokerPort`"`n}" |
    Set-Content -Path (Join-Path $folder 'project.json') -Encoding utf8NoBOM

function Repo($role) {
    $dir = Join-Path $repos $role
    New-Item -ItemType Directory -Force $dir | Out-Null
    & git -C $dir init --quiet 2>&1 | Out-Null
    @"
{
  "mcpServers": {
    "parley-$project": {
      "command": "parley-mcp",
      "env": { "PARLEY_AGENT": "$role", "PARLEY_PROJECT": "$project" }
    }
  }
}
"@ | Set-Content -Path (Join-Path $dir '.mcp.json') -Encoding utf8NoBOM
    $dir
}

$ordersRepo  = Repo 'orders'
$billingRepo = Repo 'billing'
$remoteRepo  = Repo 'remote'

$brokerLog = Join-Path $scratch 'broker.log'
function StartBroker() {
    Start-Process -FilePath $parley -PassThru -NoNewWindow `
        -ArgumentList @('serve', '--config', $root, '--log', $brokerLog) `
        -RedirectStandardOutput (Join-Path $scratch "broker-$([DateTime]::UtcNow.Ticks).out") `
        -RedirectStandardError  (Join-Path $scratch "broker-$([DateTime]::UtcNow.Ticks).err")
}
function WaitForBroker() {
    for ($i = 0; $i -lt 120; $i++) {
        $said = (& $parley --config $root --project $project registry 2>&1 | Out-String)
        if ($LASTEXITCODE -eq 0) { return $true }
        Start-Sleep -Milliseconds 500
    }
    $false
}
$script:broker = StartBroker
Check 'the broker answers' (WaitForBroker)
$store = Join-Path $folder 'parley.db'

# ---- the store, read on a copy so this walk is never a second writer ----------------------------
$sqliteFrom = Join-Path $repoRoot 'tests\Parley.Store.Tests\bin\Debug\net10.0'
$nativeDir = Join-Path $scratch 'native'
New-Item -ItemType Directory -Force $nativeDir | Out-Null
Copy-Item (Join-Path $sqliteFrom 'runtimes\win-x64\native\e_sqlite3.dll') $nativeDir -Force
$env:PATH = "$nativeDir;$env:PATH"
foreach ($one in 'Microsoft.Data.Sqlite.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.e_sqlite3.dll','SQLitePCLRaw.batteries_v2.dll') {
    Add-Type -Path (Join-Path $sqliteFrom $one)
}
[SQLitePCL.Batteries_V2]::Init()

function Read-Store($sql) {
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
function Questions($role) {
    Read-Store "SELECT state, answer, flattened, waiting_because, conversation_id FROM ownership_questions WHERE agent LIKE '$role@%' ORDER BY id"
}
function Accepted($role) {
    Read-Store "SELECT statement, source, proposed_length FROM agent_ownership_accepted WHERE agent LIKE '$role@%'"
}
function Until($seconds, [scriptblock]$condition) {
    for ($i = 0; $i -lt ($seconds * 4); $i++) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 250 }
    & $condition
}

# ---- an agent, played by this script over parley-mcp's own stdio --------------------------------
$script:agents = @{}
function Start-Agent($role, $machine, $workspace) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $mcp
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $workspace
    foreach ($k in @($psi.Environment.Keys)) { if ($k -like 'PARLEY_*') { [void]$psi.Environment.Remove($k) } }
    $psi.Environment['PARLEY_CONFIG']    = $root
    $psi.Environment['PARLEY_PROJECT']   = $project
    $psi.Environment['PARLEY_AGENT']     = $role
    $psi.Environment['PARLEY_MACHINE']   = $machine
    $psi.Environment['PARLEY_WORKSPACE'] = $workspace
    $p = [System.Diagnostics.Process]::Start($psi)
    [void]$p.StandardError.ReadToEndAsync()
    $agent = [pscustomobject]@{ Name = "$role@$machine"; Process = $p; Id = 0 }
    $script:agents[$agent.Name] = $agent
    [void](Rpc $agent 'initialize' @{
        protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'walk'; version = '1' } })
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $p.StandardInput.Flush()
    $agent
}
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
function Inbox($agent) {
    $text = Tool $agent 'parley_inbox' @{}
    try { ($text | ConvertFrom-Json).messages } catch { @() }
}
function QuestionFor($agent, $seconds = 30) {
    $found = $null
    [void](Until $seconds { $script:q = @(Inbox $agent | Where-Object { $_.message -match 'Question from Parley' }); $script:q.Count -gt 0 })
    if ($script:q.Count -gt 0) { $found = $script:q[0] }
    $found
}

$machine = $env:COMPUTERNAME.ToLowerInvariant() -replace '[^a-z0-9.-]', ''
$answer  = "1. Orders: taking them, pricing them, their totals.`n`n2. Not tax, which billing owns."
$line    = '1. Orders: taking them, pricing them, their totals. 2. Not tax, which billing owns.'

# ================================================================================================
Head 'step 1: a new agent is asked, with nobody asking'
$orders = Start-Agent 'orders' $machine $ordersRepo
$question = QuestionFor $orders 30
Check '1.1 within a few passes the agent receives one question from Parley' ($null -ne $question)
if ($question) {
    Check '1.1 the envelope names Parley and says it is not a peer asking for work' `
        ($question.message -match 'not a peer asking for work')
    Check '1.1 with Auto Accept on, it says the answer becomes the line as written' `
        ($question.message -match 'becomes your ownership line as you write it')
    [void](Tool $orders 'parley_result' @{ exchangeId = [long]$question.exchange_id; outcome = 'answered'; text = $answer })
}
[void](Until 20 { $a = Accepted 'orders'; $a.Count -eq 1 })
$accepted = Accepted 'orders'
Check '1.2 the answer is accepted, with its line breaks collapsed' ($accepted.Count -eq 1 -and $accepted[0][0] -eq $line)
Check '1.2 recorded as automatic' ($accepted.Count -eq 1 -and $accepted[0][1] -eq 'automatic')
Check "1.2 the length recorded is the agent's own message ($($accepted[0][2]))" `
    ($accepted.Count -eq 1 -and [int]$accepted[0][2] -eq $answer.Length)
$written = (Get-Content (Join-Path $ordersRepo '.mcp.json') -Raw | ConvertFrom-Json).mcpServers."parley-$project".env.PARLEY_OWNS
Check '1.3 and it is written into the launch configuration, where peers will read it' ($written -eq $line)
Note '1.4 the refresh is not walked: this agent was attached by the walk, not started by a node (see the header)'

# ================================================================================================
Head 'step 2: nothing is asked twice'
$orders.Process.Kill(); Start-Sleep -Milliseconds 500
$orders = Start-Agent 'orders' $machine $ordersRepo
Start-Sleep -Seconds 4
Check '2.1 restarting the agent asks nothing more' ((Questions 'orders').Count -eq 1)
Stop-Process -Id $script:broker.Id -Force; Start-Sleep -Seconds 1
$script:broker = StartBroker
[void](WaitForBroker)
$orders.Process.Kill(); Start-Sleep -Milliseconds 500
$orders = Start-Agent 'orders' $machine $ordersRepo
Start-Sleep -Seconds 4
Check '2.2 restarting the broker asks nothing more' ((Questions 'orders').Count -eq 1)

# ================================================================================================
Head 'step 3: an agent that was running before'
$billing = Start-Agent 'billing' $machine $billingRepo
$bq = QuestionFor $billing 30
Check '3.1 an agent already attached when the broker starts serving is asked, without restarting' ($null -ne $bq)
if ($bq) { [void](Tool $billing 'parley_result' @{ exchangeId = [long]$bq.exchange_id; outcome = 'answered'; text = 'Invoicing and tax. Not order totals.' }) }
Note '3.1 walked as "attached, never asked" rather than by editing a stopped store: nothing is released, so no store holds agents from before 022'

# ================================================================================================
Head 'the web UI'
$webProcess = Start-Process -FilePath $webui -PassThru -NoNewWindow `
    -ArgumentList @('--listen', "http://127.0.0.1:$webPort") `
    -RedirectStandardOutput (Join-Path $scratch 'web.out') -RedirectStandardError (Join-Path $scratch 'web.err')
Check 'the web UI answers' (Until 30 { try { [void](Invoke-WebRequest "http://127.0.0.1:$webPort/" -UseBasicParsing -TimeoutSec 2); $true } catch { $false } })

$script:session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
function ActUrl($action, $subject) {
    "http://127.0.0.1:$webPort/Projects/Act?project=$project&action=$action&subject=" + [uri]::EscapeDataString($subject)
}
function Token($html) {
    if ($html.Content -match '(?s)__RequestVerificationToken.*?value="([^"]+)"') { $Matches[1] }
    elseif ($html.Content -match '(?s)value="([^"]+)"[^>]*name="__RequestVerificationToken"') { $Matches[1] }
}
function GetPage($url) { Invoke-WebRequest $url -UseBasicParsing -WebSession $script:session -TimeoutSec 30 }
function PostPage($url, [hashtable]$fields) {
    $got = GetPage $url
    $body = @{ '__RequestVerificationToken' = (Token $got) }
    foreach ($k in $fields.Keys) { $body[$k] = $fields[$k] }
    try { Invoke-WebRequest $url -Method Post -Body $body -UseBasicParsing -WebSession $script:session -TimeoutSec 60 }
    catch { [pscustomobject]@{ Content = '' } }
}
function Outcome($response) {
    if ($response.Content -match '(?s)role="status"><strong>(.*?)</strong>') {
        [System.Net.WebUtility]::HtmlDecode($Matches[1]) -replace '\s+', ' '
    } else { '' }
}

$review = GetPage "http://127.0.0.1:$webPort/Projects/Ownership?project=$project"
Check 'the ownership review lists orders as accepted automatically' `
    ($review.Content -match 'orders@' -and $review.Content -match 'accepted automatically')

# ================================================================================================
Head 'step 4: Auto Accept off'
Check '4.1 turned off from the web UI' ((Outcome (PostPage (ActUrl 'autoaccept' 'off') @{})) -match 'Auto Accept is off')
$said = Outcome (PostPage (ActUrl 'askownership' "orders@$machine") @{})
Why $said
Check '4.2 asking again needs no seat' ($said -match 'Parley asked')
$q = QuestionFor $orders 30
Check '4.2 the question keeps 020 promise: a person reads it first' ($q -and $q.message -match 'A person will read your answer')
if ($q) { [void](Tool $orders 'parley_result' @{ exchangeId = [long]$q.exchange_id; outcome = 'answered'; text = 'Orders and returns.' }) }
[void](Until 20 { $x = Questions 'orders'; $x.Count -ge 2 -and $x[-1][0] -eq 'waiting' })
$latest = (Questions 'orders')[-1]
Check '4.3 the answer waits' ($latest[0] -eq 'waiting')
Check '4.3 and says why' ($latest[3] -match 'Auto Accept is off')
Check '4.3 nothing was written' ((Get-Content (Join-Path $ordersRepo '.mcp.json') -Raw) -match [regex]::Escape($line))
$said = Outcome (PostPage (ActUrl 'acceptanswer' "orders@$machine") @{ Text = 'Orders and returns.' })
Why $said
Check "4.4 accepting it unchanged records the agent's words accepted by a person (proposed)" `
    ((Accepted 'orders')[0][1] -eq 'proposed')
Check '4.4 and settles the question' ((Questions 'orders')[-1][0] -eq 'accepted')
Check '4.5 turned back on' ((Outcome (PostPage (ActUrl 'autoaccept' 'on') @{})) -match 'Auto Accept is on')

# ================================================================================================
Head 'step 5: an agent that asks back'
$said = Outcome (PostPage (ActUrl 'askownership' "billing@$machine") @{})
$bq = QuestionFor $billing 30
if ($bq) { [void](Tool $billing 'parley_clarify' @{ exchangeId = [long]$bq.exchange_id; text = 'Do you mean this repository or the billing service?' }) }
[void](Until 20 { $x = Questions 'billing'; $x[-1][0] -eq 'asked_back' })
Check '5.1 the question shows as asked back' ((Questions 'billing')[-1][0] -eq 'asked_back')
$said = Outcome (PostPage (ActUrl 'replyaskedback' "billing@$machine") @{ Text = 'This repository.' })
Why $said
Check '5.2 a reply is sent from the review, with no seat' ($said -match 'Sent through Parley')
$reply = @(Inbox $billing | Where-Object { $_.message -match "A person's reply, sent through Parley" })
Check '5.3 the agent is told these are a person''s words, not Parley''s' ($reply.Count -eq 1)
$attr = Read-Store "SELECT responder_model FROM messages WHERE type = 'clarify' AND from_agent LIKE 'parley-facilitator@%'"
Check '5.3 and the store attributes it to a person' ($attr.Count -eq 1 -and $attr[0][0] -eq 'parley:person')

# ================================================================================================
Head 'step 6: the facilitator cannot be claimed'
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $mcp; $psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
foreach ($k in @($psi.Environment.Keys)) { if ($k -like 'PARLEY_*') { [void]$psi.Environment.Remove($k) } }
$psi.Environment['PARLEY_CONFIG'] = $root; $psi.Environment['PARLEY_PROJECT'] = $project
$psi.Environment['PARLEY_AGENT'] = 'parley-facilitator'; $psi.Environment['PARLEY_MACHINE'] = $machine
$claim = [System.Diagnostics.Process]::Start($psi)
$claimErr = $claim.StandardError.ReadToEndAsync()
[void]$claim.WaitForExit(20000)
Check "6.1 an adapter started as parley-facilitator does not attach (exit $($claim.ExitCode))" `
    ($claim.HasExited -and $claim.ExitCode -ne 0)
$registry = (& $parley --config $root --project $project registry 2>&1 | Out-String)
Check '6.2 parley registry does not list Parley as an agent' (-not ($registry -match '^parley-facilitator@'))
$refused = Tool $orders 'parley_request' @{ to = "parley-facilitator@$machine"; ask = 'Please do my work.' }
Check '6.3 work sent to Parley is refused, saying why' ($refused -match 'does no work itself')
$roster = Tool $orders 'parley_agents' @{}
Check '6.4 no agent finds Parley on its roster' (-not ($roster -match 'parley-facilitator'))

# ================================================================================================
Head 'step 7: an agent on another machine'
$remote = Start-Agent 'remote' 'otherbox' $remoteRepo
$rq = QuestionFor $remote 30
Check '7.1 it is asked' ($null -ne $rq)
if ($rq) { [void](Tool $remote 'parley_result' @{ exchangeId = [long]$rq.exchange_id; outcome = 'answered'; text = 'Remote things.' }) }
[void](Until 20 { $x = Questions 'remote'; $x.Count -ge 1 -and $x[-1][0] -eq 'waiting' })
$rlast = (Questions 'remote')[-1]
Check '7.2 its answer waits whatever Auto Accept says' ($rlast[0] -eq 'waiting')
Check '7.2 and the reason names the machine' ($rlast[3] -match 'otherbox')

# ---- isolation held -----------------------------------------------------------------------------
Head 'isolation'
Check 'the live node.json is untouched' ((HashOrAbsent $liveNode) -eq $nodeBefore)
Check "this repository's own .mcp.json is untouched" ((HashOrAbsent $liveMcp) -eq $mcpBefore)
Check 'the live root gained no project' `
    (@(Get-ChildItem (Join-Path $liveRoot 'projects') -Directory -ErrorAction SilentlyContinue).Count -eq $liveProjectsBefore)

# ---- down ---------------------------------------------------------------------------------------
Head 'stopping what the walk started'
foreach ($a in $script:agents.Values) { if (-not $a.Process.HasExited) { $a.Process.Kill() } }
foreach ($p in @($webProcess, $script:broker)) {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
if ($script:notes.Count) {
    Write-Host "$($script:notes.Count) thing(s) recorded rather than checked:" -ForegroundColor DarkYellow
    $script:notes | ForEach-Object { Write-Host "  - $_" -ForegroundColor DarkYellow }
}
Write-Host ''
if ($script:fail -eq 0) { Write-Host 'ALL WALKED CHECKS PASSED' -ForegroundColor Green }
else { Write-Host "$script:fail CHECK(S) FAILED" -ForegroundColor Red }
exit $script:fail

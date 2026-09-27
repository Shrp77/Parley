# T062: walks specs/020-what-an-agent-owns/quickstart.md.
#
# Everything runs against tools packed and installed into a scratch path, with a root, a store and a
# port of their own, so the machine's real deployment is untouched.
#
# **THE FIRST THING IT DOES IS CLEAR EVERY PARLEY_ VARIABLE, AND THAT IS NOT TIDINESS.** The first
# attempt at this walk set PARLEY_CONFIG and left the rest alone; this machine sets
# PARLEY_NODE_CONFIG to the live catalogue, so the enrolment obeyed it -- correctly -- and wrote two
# entries for temporary repositories into the deployment somebody's agents are started from. 018's
# incident, reached from a shell rather than from a surface. The product was right and the walk was
# not isolated.
#
# WHAT THIS WALK DOES NOT MEASURE, said here rather than discovered halfway down:
#
#   * Naming a project. Since 019 that is the terminal's alone and `parley terminal` refuses a
#     redirected stream by design, so the project this walk needs is written as the `project.json`
#     the product writes. Cited to NamingTests, as 019's walk recorded the same gap.
#   * Every review dialog. The terminal's ask, the proposal, the edit, the pending mark and the
#     restart offer are on a screen no shell can drive. Cited to AskingWhatItOwnsTests,
#     SettlingItByDiscussingTests, AskingEveryoneTests and TheNeighboursAreShownTests, which press
#     keys rather than call seams.
#   * A real model's answer. A scripted agent cannot say whether the ask produces a *useful* line,
#     which is the whole value of the feature. That is SPEC.md §17 question 10's business.
#
# WHAT IT FOUND, on 2026-09-27, so the next reader knows it has earned its keep: three defects, two
# of them high. All three ownership actions were performed by the console's page model and linked
# from no page at all; the acceptance asked for the unnamed launch key, so every repository enrolled
# into a named project was refused; and `parley registry` showed the live line while saying nothing
# about one already accepted. Fixed at 0.34.1. REVIEW-LEDGER.md carries the probe for each.

param([switch]$Keep)

$ErrorActionPreference = 'Continue'

# Derived rather than written down, unlike 019's walk: this script sits two directories under the
# root, so it can find it, and a hardcoded path is a walk that only runs on one machine.
$repoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$scratch    = Join-Path $env:TEMP 'parley-020-walk'
$root       = Join-Path $scratch 'root'
$tools      = Join-Path $scratch 'tools'
$repos      = Join-Path $scratch 'repos'
$project    = 'orders'
$brokerPort = 19180
$webPort    = 5299          # the live deployment holds 5280

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

# ---- environment hygiene, first -----------------------------------------------------------------
Get-ChildItem Env: | Where-Object Name -like 'PARLEY_*' | ForEach-Object {
    Remove-Item "Env:\$($_.Name)" -ErrorAction SilentlyContinue
}
$env:PARLEY_CONFIG      = $root
$env:PARLEY_NODE_CONFIG = Join-Path $root 'node.json'

# ---- isolation, before anything else ------------------------------------------------------------
Head 'isolation baseline'
$liveRoot = Join-Path $env:APPDATA 'Parley'
$liveNode = Join-Path $liveRoot 'node.json'
$liveMcp  = Join-Path $repoRoot '.mcp.json'
function HashOrAbsent($path) {
    if (Test-Path $path) { (Get-FileHash $path -Algorithm MD5).Hash } else { 'absent' }
}
$nodeBefore = HashOrAbsent $liveNode
$mcpBefore  = HashOrAbsent $liveMcp
$liveProjectsBefore = @(Get-ChildItem (Join-Path $liveRoot 'projects') -Directory -ErrorAction SilentlyContinue).Count
Write-Host "    live node.json:      $nodeBefore"
Write-Host "    live repo .mcp.json: $mcpBefore"
Write-Host "    live projects:       $liveProjectsBefore"

# ---- tools, packed and installed ----------------------------------------------------------------
# Read rather than written down. A literal here was wrong within the hour: the walk found two defects,
# the fix moved the version, and the install then asked a local feed for a package that no longer
# existed -- which fails as "not found in NuGet feeds" and leaves every check below measuring nothing.
$shipping = [regex]::Match(
    (Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw),
    '<VersionPrefix>([^<]+)</VersionPrefix>').Groups[1].Value

if (-not (Test-Path $parley)) {
    Head "packing and installing the tools into the scratch path ($shipping)"
    $pkg = Join-Path $scratch 'pkg'
    New-Item -ItemType Directory -Force $pkg | Out-Null
    foreach ($one in 'Parley.Cli','Parley.Terminal','Parley.WebUi','Parley.Mcp') {
        & dotnet pack (Join-Path $repoRoot "src\$one\$one.csproj") -c Release -o $pkg 2>&1 | Select-Object -Last 1
        # --configfile, because a user-level NuGet.config carrying a source mapping makes
        # --add-source a hard error; this repository's own config clears the mapping.
        & dotnet tool install $one --tool-path $tools --add-source $pkg `
            --configfile (Join-Path $repoRoot 'NuGet.config') --version $shipping 2>&1 |
            Select-Object -Last 1
    }
}
$built = (& $parley --version) -join ' '
Check "the walk runs against packed tools in a scratch path ($built)" `
    ($built -match [regex]::Escape($shipping))

# ---- a project, and a broker on its own store and port ------------------------------------------
Head 'a project and a broker (naming one is the terminal''s; see the header)'
$folder = Join-Path $root "projects\$project"
New-Item -ItemType Directory -Force $folder, $repos | Out-Null
"{`n  `"endpoint`": `"http://127.0.0.1:$brokerPort`"`n}" |
    Set-Content -Path (Join-Path $folder 'project.json') -Encoding utf8NoBOM

$ordersRepo  = Join-Path $repos 'orders'
$billingRepo = Join-Path $repos 'billing'
$legacyRepo  = Join-Path $repos 'legacy'
New-Item -ItemType Directory -Force $ordersRepo, $billingRepo, $legacyRepo | Out-Null
foreach ($one in $ordersRepo, $billingRepo, $legacyRepo) { & git -C $one init --quiet 2>&1 | Out-Null }

$brokerLog = Join-Path $scratch 'broker.log'
$broker = Start-Process -FilePath $parley -PassThru -NoNewWindow `
    -ArgumentList @('serve', '--config', $root, '--log', $brokerLog) `
    -RedirectStandardOutput (Join-Path $scratch 'broker.out') `
    -RedirectStandardError  (Join-Path $scratch 'broker.err')

$store = Join-Path $folder 'parley.db'
$waited = 0
while (-not (Test-Path $store) -and $waited -lt 60) { Start-Sleep -Milliseconds 500; $waited++ }
Check 'the broker opened a store of its own inside the project folder' (Test-Path $store)

# ================================================================================================
# Claim 1 -- a line without composing one
# ================================================================================================

Head 'step 1: enrolment asks for no ownership line'
$enrolled = & $parley --config $root --project $project enroll $repos --all 2>&1 | Out-String
$flat = ($enrolled -replace '\s+', ' ')

Check '1.1 enrolment never asks a person to write the line' `
    (-not ($flat -match 'owns line for|what does it own\?|enter .{0,20}owns'))
Check '1.1 it says the line is obtained by asking the agent' `
    ($flat -match 'Ask the agent what it owns')
Check '1.1 and that accepting the answer is what writes it' `
    ($flat -match 'accepting the answer writes it here')
Check '1.1 the entry is written with no owns line at all' ($flat -match 'no owns line')

$legacyMcp = Join-Path $legacyRepo '.mcp.json'
# The 009 shape, on purpose: a repository whose entry is under the unnamed `parley` key. It is
# hand-written because enrolment into a named project writes `parley-<project>`, and the unnamed key
# is what a machine enrolled before 009 still has -- which CLAUDE.md says is read for ever.
@"
{
  "mcpServers": {
    "parley": {
      "command": "parley-mcp",
      "env": {
        "PARLEY_AGENT": "legacy",
        "PARLEY_PROJECT": "$project"
      }
    }
  }
}
"@ | Set-Content -Path $legacyMcp -Encoding utf8NoBOM

# ---- the agents attach, which is the only way a line reaches the registry (§12.3) ----------------
Head 'step 1: the agents register, with no line at all'
$script:attached = @{}
function Attach($role, $machine, $workspace, $owns) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $mcp
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.WorkingDirectory = $workspace
    $psi.Environment['PARLEY_CONFIG']    = $root
    $psi.Environment['PARLEY_PROJECT']   = $project
    $psi.Environment['PARLEY_AGENT']     = $role
    $psi.Environment['PARLEY_MACHINE']   = $machine
    $psi.Environment['PARLEY_WORKSPACE'] = $workspace
    if ($owns) { $psi.Environment['PARLEY_OWNS'] = $owns }
    else { [void]$psi.Environment.Remove('PARLEY_OWNS') }

    $p = [System.Diagnostics.Process]::Start($psi)
    # Drained, or a full pipe buffer stops the child mid-attach.
    [void]$p.StandardOutput.ReadToEndAsync()
    [void]$p.StandardError.ReadToEndAsync()
    $script:attached["$role@$machine"] = $p
    $p
}

function Registry() { (& $parley --config $root --project $project registry 2>&1 | Out-String) }

$here = (& $parley --config $root --project $project registry 2>&1) | Out-Null
$machine = $env:COMPUTERNAME.ToLowerInvariant()

[void](Attach 'orders'  $machine  $ordersRepo  $null)
[void](Attach 'billing' $machine  $billingRepo $null)
[void](Attach 'legacy'  $machine  $legacyRepo  $null)
# On another machine on purpose, for step 9: the machine rule guards a write to a file, and the file
# is not on this machine when the agent is not.
[void](Attach 'remote'  'otherbox' $ordersRepo $null)

$waited = 0
while ($waited -lt 60) {
    $roster = Registry
    if (($roster -split "`n" | Where-Object { $_ -match '^(orders|billing|legacy|remote)@' }).Count -ge 4) { break }
    Start-Sleep -Milliseconds 500; $waited++
}
$roster = Registry
Write-Host ($roster.Trim())

Check '1.2 all four agents registered themselves by attaching' `
    ((($roster -split "`n") | Where-Object { $_ -match '^(orders|billing|legacy|remote)@' }).Count -ge 4)
Check '1.2 the roster shows them with no line at all' ($roster -match 'owns: \(unset')
Check '1.3 nothing calls the deployment misconfigured' `
    (-not ($roster -match 'misconfigur|error|invalid|broken'))
Check '1.3 and the absence names its own remedy' ($roster -match 'PARLEY_OWNS')

# ---- the web UI, which is the operator surface a shell can reach ---------------------------------
Head 'the web UI'
$web = $null
$webProcess = Start-Process -FilePath $webui -PassThru -NoNewWindow `
    -ArgumentList @('--listen', "http://127.0.0.1:$webPort") `
    -RedirectStandardOutput (Join-Path $scratch 'web.out') `
    -RedirectStandardError  (Join-Path $scratch 'web.err')

$waited = 0
while ($waited -lt 60) {
    try { [void](Invoke-WebRequest "http://127.0.0.1:$webPort/" -UseBasicParsing -TimeoutSec 2); break }
    catch { Start-Sleep -Milliseconds 500; $waited++ }
}
Check 'the web UI answers' ($waited -lt 60)

function ActUrl($action, $subject) {
    "http://127.0.0.1:$webPort/Projects/Act?project=$project&action=$action&subject=" +
        [uri]::EscapeDataString($subject)
}

function Token($html) {
    if ($html.Content -match '(?s)__RequestVerificationToken.*?value="([^"]+)"') { $Matches[1] }
    elseif ($html.Content -match '(?s)value="([^"]+)"[^>]*name="__RequestVerificationToken"') { $Matches[1] }
    else { $null }
}

function GetPage($url) {
    Invoke-WebRequest $url -UseBasicParsing -WebSession $script:session -TimeoutSec 30
}

function PostPage($url, [hashtable]$fields) {
    $got = GetPage $url
    $body = @{ '__RequestVerificationToken' = (Token $got) }
    foreach ($k in $fields.Keys) { $body[$k] = $fields[$k] }
    try {
        Invoke-WebRequest $url -Method Post -Body $body -UseBasicParsing `
            -WebSession $script:session -TimeoutSec 60
    } catch {
        # Returned rather than thrown, because one case below is a value the host refuses before
        # any page sees it, and that is an answer about where the refusal happened.
        [pscustomobject]@{ Content = ''; Refused = $_.Exception.Message }
    }
}

# One session, so the seat cookie is carried the way a browser carries it.
$script:session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
[void](GetPage "http://127.0.0.1:$webPort/")

function Outcome($response) {
    if ($response.Content -match '(?s)role="status"><strong>(.*?)</strong>') {
        [System.Net.WebUtility]::HtmlDecode($Matches[1]) -replace '\s+', ' '
    } else { '' }
}
function Warning($response) {
    if ($response.Content -match '(?s)</h1>\s*<p[^>]*>(.*?)</p>') {
        [System.Net.WebUtility]::HtmlDecode($Matches[1]) -replace '\s+', ' '
    } else { '' }
}

# ---- step 2: the ask ----------------------------------------------------------------------------
Head 'step 2: asking, before a seat is held'
$asked = PostPage (ActUrl 'askownership' "orders@$machine") @{}
$said = Outcome $asked
Why $said
Check '2.0 with no seat the ask is refused in words, not sent as an instruction' `
    ($said -match 'Nothing was asked' -and $said -match 'no seat')
Check '2.0 and it says where to take one' ($said -match 'inbox page')

Head 'step 2: a seat, then the ask'
$joined = PostPage "http://127.0.0.1:$webPort/Join" @{ Person = 'walker' }
Check 'a seat is held' ($joined.Content -match 'Seated as walker')

$mcpBeforeAsk = HashOrAbsent (Join-Path $ordersRepo '.mcp.json')
$ownsBeforeAsk = Registry

$asked = PostPage (ActUrl 'askownership' "orders@$machine") @{}
$said = Outcome $asked
Why $said
Check '2.1 a conversation named owns-<role>-<timestamp> was opened' `
    ($said -match "owns-orders-\d{8}-\d{6}")
if ($said -match "(owns-orders-\d{8}-\d{6})") { $conversation = $Matches[1] } else { $conversation = $null }
Check '2.1 and the surface says where the answer arrives' ($said -match 'which is where it is read and accepted')

$open = & $parley --config $root --project $project open 2>&1 | Out-String
Check '2.1 the broker holds that conversation open' `
    ($null -ne $conversation -and $open -match [regex]::Escape($conversation))

Check '2.4 asking wrote nothing into the launch configuration' `
    ((HashOrAbsent (Join-Path $ordersRepo '.mcp.json')) -eq $mcpBeforeAsk)
Check '2.4 asking changed no line in the registry' ((Registry) -eq $ownsBeforeAsk)

# The words the agent was told (FR-003), read back out of the transcript rather than out of the
# constant: a promise about what was said is only kept if what was sent carries it.
# Scoped to <ol class="transcript">, which is what the messages are rendered into. The page's own
# heading and links name the conversation, so a marker check over the whole page would be a probe
# that could only fail -- the mirror of one that cannot.
$transcript = ''
if ($conversation) {
    try {
        $page = GetPage ("http://127.0.0.1:$webPort/Conversations/Detail?project=$project&id=" +
            [uri]::EscapeDataString($conversation))
        if ($page.Content -match '(?s)<ol class="transcript">(.*?)</ol>') {
            $transcript = [System.Net.WebUtility]::HtmlDecode(($Matches[1] -replace '<[^>]+>', ' '))
        } else {
            Note 'the transcript element was not found on the conversation page'
        }
    } catch { Note "the conversation page could not be read: $($_.Exception.Message)" }
}
if ($transcript) {
    Check '2.2 the Request asks both halves -- what it owns and what it does not' `
        ($transcript -match 'What do you own in this deployment, and what do you not')
    Check '2.2 it says every agent reads the line whenever the roster is listed' `
        ($transcript -match 'reads that line whenever anybody lists the roster')
    Check '2.2 it says the second half is the one nobody else can supply' `
        ($transcript -match 'the half nobody else can supply')
    Check '2.2 it says nothing is written by answering' `
        ($transcript -match 'changes any configuration by itself')
    Check '2.3 the payload carries no marker a surface could parse' `
        (-not ($transcript -match 'owns-orders-\d{8}'))
} else {
    Note '2.2 and 2.3 were not walked: the transcript could not be read over HTTP'
}

# ---- step 5: the bulk ask -----------------------------------------------------------------------
Head 'step 5: the bulk ask states its cost first'
$before = GetPage (ActUrl 'askeveryownership' $project)
$cost = Warning $before
Why $cost
Check '5.1 the cost is stated before anything is sent' `
    ($cost -match 'costs each of them a turn' -or $cost -match 'costs each')
Check '5.1 and that nothing is written by asking' ($cost -match 'Nothing is written by asking')
Check '5.2 it says one conversation per agent, named for the agent it is about' `
    ($cost -match 'conversation of its own')

$openBefore = (& $parley --config $root --project $project open 2>&1 | Out-String)
$countBefore = ([regex]::Matches($openBefore, 'owns-')).Count
$every = PostPage (ActUrl 'askeveryownership' $project) @{}
$said = Outcome $every
Why $said
$openAfter = (& $parley --config $root --project $project open 2>&1 | Out-String)
$countAfter = ([regex]::Matches($openAfter, 'owns-')).Count
Check "5.2 one conversation per agent, no one-to-many operation (owns- conversations $countBefore -> $countAfter)" `
    ($countAfter -gt $countBefore)

# ================================================================================================
# Claim 2 -- no line reaches the registry that a person did not accept
# ================================================================================================

Head 'step 6: the agent cannot set its own line'
# Read out of the shipped assemblies rather than over stdio. The stdio handshake could not be driven
# from PowerShell -- the transport reads standard input to EOF and completes before the host has
# started, so the replies it computes are written to a stream that has gone -- and that is recorded
# rather than worked around. What matters here is the surface the tool ships, and these are the
# bytes that ship.
$mcpAssembly = Get-ChildItem (Join-Path $tools '.store') -Recurse -Filter 'parley-mcp.dll' |
    Select-Object -First 1 -ExpandProperty FullName
Note ('6.1 is read out of the shipped parley-mcp.dll rather than from a live tools/list: the stdio ' +
      'transport reaches end of input before the host starts when standard input is a file or a ' +
      'briefly-written pipe, so nothing comes back. ChannelNudgeTests drives the real server.')
if ($mcpAssembly) {
    $shipped = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($mcpAssembly))
    $toolNames = [regex]::Matches($shipped, 'parley_[a-z_]{2,30}') |
        ForEach-Object { $_.Value } | Sort-Object -Unique
    Write-Host "    tools: $($toolNames -join ', ')"
    Check '6.1 the adapter ships tools at all (so the absence below means something)' `
        ($toolNames.Count -ge 8)
    Check '6.1 none of them names ownership or a line to set' `
        (@($toolNames | Where-Object { $_ -match '(?i)own' }).Count -eq 0)
} else {
    Note '6.1 was not walked: parley-mcp.dll was not found under the tool store'
}

# The shipped wire, reflected over rather than read out of proto/parley.proto: the document is the
# authority and the assembly is what answers a caller. This is AgentsCannotSetTheirOwnLineTests'
# check applied to the binary that ships -- and it matches the words `Ownership` and `Owns` rather
# than `Own`, because `Agents.RemoveOwnFiles` contains `Own` and is about files.
$transport = Get-ChildItem (Join-Path $tools '.store') -Recurse -Filter 'Parley.Transport.dll' |
    Select-Object -First 1 -ExpandProperty FullName
if ($transport) {
    $wire = [System.Reflection.Assembly]::LoadFrom($transport)
    function ServiceMethods($service) {
        $type = $wire.GetType("Parley.Transport.$service+${service}Client")
        if ($null -eq $type) { @() } else {
            @($type.GetMethods() |
                Where-Object { $_.DeclaringType.Name -eq "${service}Client" } |
                Select-Object -ExpandProperty Name -Unique | Sort-Object -Unique)
        }
    }
    $operators = ServiceMethods 'Operators'
    $agents    = ServiceMethods 'Agents'
    $nodes     = ServiceMethods 'Nodes'
    Check '6.3 RecordOwnership is on Operators' `
        (@($operators | Where-Object { $_ -eq 'RecordOwnership' }).Count -eq 1)
    Check '6.2 no method on Agents records or sets a line' `
        (@($agents | Where-Object { $_ -match '(?i)ownership|owns' }).Count -eq 0)
    Check '6.3 and it is on no other service' `
        (@($agents + $nodes | Where-Object { $_ -match '(?i)ownership' }).Count -eq 0)
    Why ("Agents does carry RemoveOwnFiles, which is about files: " +
         (($agents | Where-Object { $_ -match '(?i)own' }) -join ', '))
} else {
    Note '6.2 and 6.3 were not walked: Parley.Transport.dll was not found under the tool store'
}

Head 'step 7 and 8: acceptance, and the refusals an operator meets'
# The legacy repository is the one the console can write to; see step 7.5 for why that is a finding.
$legacyHash = HashOrAbsent $legacyMcp
$fourHundred = 'the ordering service and its refunds, not the ledger, ' + ('x' * 360)
Check "the 400-character statement is 400 characters ($($fourHundred.Length))" ($fourHundred.Length -ge 400)

$accepted = PostPage (ActUrl 'ownership' "legacy@$machine") @{ text = $fourHundred }
$said = Outcome $accepted
Why $said
Check '8.1 a 400-character statement is accepted -- the bound is off' `
    ($said -notmatch 'too long|characters at most|Not recorded')

$legacyAfter = Get-Content $legacyMcp -Raw
Check '4.4 the launch configuration now carries it under PARLEY_OWNS' `
    ($legacyAfter -match 'PARLEY_OWNS')
Check '4.4 and it carries the whole statement, not a shortened one' `
    ($legacyAfter -match ([regex]::Escape($fourHundred.Substring(0, 60))) -and $legacyAfter -match 'xxxxxxxxxx')

$refusals = [ordered]@{
    '8.2 U+202E (reorders what follows)' = 'orders' + [char]0x202E + ' and refunds'
    '8.3 U+2028 (a line separator)'      = 'orders' + [char]0x2028 + ' and refunds'
    '8.3 U+00AD (a soft hyphen)'         = 'orders' + [char]0x00AD + ' and refunds'
    '8.3 U+FEFF (invisible)'             = 'orders' + [char]0xFEFF + ' and refunds'
    '8.3 U+E0020 (a tag character)'      = 'orders' + [char]::ConvertFromUtf32(0xE0020) + ' and refunds'
    '8.4 an embedded NUL'                = 'orders' + [char]0x0000 + ' and refunds'
}
$beforeRefusals = Get-Content $legacyMcp -Raw
foreach ($case in $refusals.Keys) {
    $response = PostPage (ActUrl 'ownership' "legacy@$machine") @{ text = $refusals[$case] }
    if ($response.PSObject.Properties.Name -contains 'Refused') {
        # The value never reached a page: the host refused the request. That is still a refusal an
        # operator sees, but it is not the surface's, so it is said as what it is.
        Note "$case never reaches the surface -- the host refuses the request itself ($($response.Refused))"
        continue
    }
    $says = Outcome $response
    Check "$case is refused" ($says -match 'Not recorded')
    Check "$case names the position" ($says -match 'at position \d+')
    if ($says -notmatch 'Not recorded') { Why $says }
}
Check '8.5 nothing was silently shortened: the file is byte-for-byte what it was' `
    ((Get-Content $legacyMcp -Raw) -eq $beforeRefusals)
Note ('8.6 is not drivable from a shell and is cited rather than claimed: the cap is a compile-time ' +
      'constant and a literal inside a CHECK, so reinstating one is a rebuild. OwnershipStatement.Bounded ' +
      'and the bound tests hold it.')

Head 'step 9: the machine check'
$remoteBefore = HashOrAbsent (Join-Path $ordersRepo '.mcp.json')
$elsewhere = PostPage (ActUrl 'ownership' 'remote@otherbox') @{ text = 'the remote side of it' }
$says = Outcome $elsewhere
Why $says
Check '9.2 the web UI refuses an agent whose machine is not this one' ($says -match 'Not recorded')
Check '9.2 and names the machine it would have to be done on' ($says -match 'otherbox')
Check '9.4 nothing was written' `
    ((HashOrAbsent (Join-Path $ordersRepo '.mcp.json')) -eq $remoteBefore)

# 9.3: a launch configuration naming a different agent under the key, which is 005's finding from the
# other side -- a withdrawal once deleted a *different* agent's launch configuration, because the
# workspace comes from the registry and the registry is what the agent said about itself.
#
# Walked through the ordinary project key. Until 0.34.1 it could not be: the console asked for the
# unnamed key, so it refused every project-enrolled repository before it ever read a name back.
$wrongTargetBefore = HashOrAbsent (Join-Path $billingRepo '.mcp.json')
$wrongTarget = PostPage (ActUrl 'ownership' "billing@$machine") @{ text = 'somebody else''s work' }
$says = Outcome $wrongTarget
Why $says
Check '9.3 the write is aimed at the repository the registry names' `
    ($says -notmatch 'has no entry for this agent')
Check '9.3 and the entry it finds there is the ordinary project key' `
    ($says -notmatch "no entry at 'parley'")

$legacyRoot = Get-Content $legacyMcp -Raw
# Rewrite the legacy entry to name a different agent, leaving everything else alone.
($legacyRoot -replace '"PARLEY_AGENT": "legacy"', '"PARLEY_AGENT": "somebody-else"') |
    Set-Content -Path $legacyMcp -Encoding utf8NoBOM
$mismatchBefore = HashOrAbsent $legacyMcp
$mismatch = PostPage (ActUrl 'ownership' "legacy@$machine") @{ text = 'something else entirely' }
$says = Outcome $mismatch
Why $says
Check '9.3b a launch configuration naming a different agent is refused' ($says -match 'Not recorded')
Check '9.3b the refusal names what the file configures instead' ($says -match "somebody-else")
Check '9.3b and nothing was written' ((HashOrAbsent $legacyMcp) -eq $mismatchBefore)
# Put it back, so the restart below reads the line that was accepted.
($legacyRoot) | Set-Content -Path $legacyMcp -Encoding utf8NoBOM

Head 'step 7.4: agents.owns is written by nothing but an attach'
$beforeRestart = Registry
Check '7.4 the accepted line is not in the registry yet' `
    (-not ($beforeRestart -match [regex]::Escape($fourHundred.Substring(0, 40))))
Why 'the registry is filled from the launch environment on attach (§12.3), so the line is pending'

# The restart, performed the way a client performs one: stop the adapter and start it again with the
# launch environment the file now holds. Nothing else may put the line there.
$legacyProcess = $script:attached["legacy@$machine"]
if ($legacyProcess -and -not $legacyProcess.HasExited) { $legacyProcess.Kill(); $legacyProcess.WaitForExit(5000) }
$ownsFromFile = ''
try {
    $json = Get-Content $legacyMcp -Raw | ConvertFrom-Json
    $ownsFromFile = $json.mcpServers.parley.env.PARLEY_OWNS
} catch { Note "the launch configuration could not be re-read: $($_.Exception.Message)" }
Check '7.4 the line a client would launch with is the accepted one' `
    ($ownsFromFile -eq $fourHundred)
[void](Attach 'legacy' $machine $legacyRepo $ownsFromFile)

$waited = 0
while ($waited -lt 60) {
    $after = Registry
    if ($after -match [regex]::Escape($fourHundred.Substring(0, 40))) { break }
    Start-Sleep -Milliseconds 500; $waited++
}
$after = Registry
Check '4.7 after the restart the roster shows the accepted line' `
    ($after -match [regex]::Escape($fourHundred.Substring(0, 40)))

Head 'step 7.5 and 9.5: what the web UI can be asked to do, and what it offers'
$agentPage = GetPage "http://127.0.0.1:$webPort/Projects/Agent?project=$project&agent=orders@$machine"
$actions = if ($agentPage.Content -match '(?s)aria-label="Actions">(.*?)</nav>') { $Matches[1] } else { '' }
Why ("the agent page offers: " + (([regex]::Matches($actions, 'action=([a-z]+)') |
    ForEach-Object { $_.Groups[1].Value }) -join ', '))
Check '9.5 the web UI offers the ask somewhere a person can reach it' ($actions -match 'askownership')
Check '9.5 and offers the acceptance somewhere a person can reach it' ($actions -match 'action=ownership')

$ownershipGet = GetPage (ActUrl 'ownership' "legacy@$machine")
Check '9.5 the acceptance describes itself before it happens, as every other action does' `
    ((Warning $ownershipGet) -notmatch 'not something the console does')
Check '9.5 and offers a field for the line' ($ownershipGet.Content -match 'name="text"')

$projectEnrolled = PostPage (ActUrl 'ownership' "orders@$machine") @{ text = 'the ordering service' }
$says = Outcome $projectEnrolled
Why $says
Check '7.5 the acceptance writes for an agent enrolled into a named project' `
    ($says -notmatch 'Not recorded')

# ---- step 10: the schema ------------------------------------------------------------------------
Head 'step 10: the store the installed broker built'
$sqliteFrom = Join-Path $repoRoot 'tests\Parley.Store.Tests\bin\Debug\net10.0'
$nativeDir = Join-Path $scratch 'native'
New-Item -ItemType Directory -Force $nativeDir | Out-Null
Copy-Item (Join-Path $sqliteFrom 'runtimes\win-x64\native\e_sqlite3.dll') $nativeDir -Force
$env:PATH = "$nativeDir;$env:PATH"
foreach ($one in 'Microsoft.Data.Sqlite.dll','SQLitePCLRaw.core.dll',
                 'SQLitePCLRaw.provider.e_sqlite3.dll','SQLitePCLRaw.batteries_v2.dll') {
    Add-Type -Path (Join-Path $sqliteFrom $one)
}
[SQLitePCL.Batteries_V2]::Init()

# On a copy, because the broker holds the store and this walk must not be a second writer -- and the
# copy takes the write-ahead log with it. Without it the file is four kilobytes of header and every
# table is missing, which is a reading that looks like a broken store.
$copy = Join-Path $scratch 'read-only-copy.db'
Copy-Item $store $copy -Force
foreach ($companion in '-wal', '-shm') {
    if (Test-Path "$store$companion") { Copy-Item "$store$companion" "$copy$companion" -Force }
}
$db = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$copy")
$db.Open()
function Scalar($sql) { $c = $db.CreateCommand(); $c.CommandText = $sql; $c.ExecuteScalar() }
# `, $out` rather than `$out`, because a function returning an array of one array has the outer one
# unrolled by the pipeline -- so a single row came back as its own first column, and three checks
# indexed into a string and failed while the store was perfectly correct.
function Rows($sql) {
    $c = $db.CreateCommand(); $c.CommandText = $sql
    $r = $c.ExecuteReader(); $out = @()
    while ($r.Read()) { $out += ,@(0..($r.FieldCount - 1) | ForEach-Object { $r.GetValue($_) }) }
    $r.Close()
    return , $out
}

Check "10.1 user_version is 1 (read: $(Scalar 'PRAGMA user_version'))" ((Scalar 'PRAGMA user_version') -eq 1)
Check '10.2 foreign_key_check returns no rows' ((Rows 'PRAGMA foreign_key_check').Count -eq 0)
Check "10.3 integrity_check says ok" ((Scalar 'PRAGMA integrity_check') -eq 'ok')
$triggers = Rows "SELECT name, tbl_name FROM sqlite_master WHERE type = 'trigger' ORDER BY name"
$onAgents = @($triggers | Where-Object { $_[1] -eq 'agents' })
Check "10.4 fourteen triggers are present (read: $($triggers.Count))" ($triggers.Count -eq 14)
Check ("10.4 including the ones naming agents from another table " +
       "($($triggers.Count - $onAgents.Count) of them are on other tables)") `
    (($triggers.Count - $onAgents.Count) -ge 2)
$generated = Rows "SELECT name, role, machine FROM agents WHERE name LIKE 'legacy@%'"
Check '10.5 role and machine still generate from name' `
    ($generated.Count -ge 1 -and $generated[0][1] -eq 'legacy' -and $generated[0][2] -eq $machine)

# 10.6: what the table refuses, asked of the table rather than of the code above it. On the same
# connection, because closing it and opening another left every query after this one reporting that
# the connection was shut -- which reads exactly like a store with no tables in it.
function TryOwns($value) {
    $c = $db.CreateCommand()
    $c.CommandText = "UPDATE agents SET owns = `$v WHERE name LIKE 'legacy@%'"
    [void]$c.Parameters.AddWithValue('$v', $value)
    try { [void]$c.ExecuteNonQuery(); $null } catch { $_.Exception.Message }
}
Check '10.6 a 400-character statement is accepted by the table' ($null -eq (TryOwns $fourHundred))
$stillRefused = @()
foreach ($case in $refusals.Keys) {
    if ($null -eq (TryOwns $refusals[$case])) { $stillRefused += $case }
}
Check "10.6 and every character of 8.2-8.4 is still refused by the table" ($stillRefused.Count -eq 0)
if ($stillRefused.Count) { Why ("admitted: " + ($stillRefused -join '; ')) }

$acceptedTable = Rows @"
SELECT name FROM pragma_table_info('agent_ownership_accepted')
"@
Check '10.7 agent_ownership_accepted exists' ($acceptedTable.Count -gt 0)
$fk = Rows "SELECT `"table`", `"from`" FROM pragma_foreign_key_list('agent_ownership_accepted')"
Check '10.7 conversation_id is a real foreign key' `
    (@($fk | Where-Object { $_[1] -eq 'conversation_id' }).Count -eq 1)

# 7.3: what a composed acceptance recorded.
$recorded = Rows @"
SELECT agent, source, proposed_length, conversation_id FROM agent_ownership_accepted ORDER BY rowid
"@
foreach ($row in $recorded) { Why ("recorded: " + (($row | ForEach-Object { "$_" }) -join ' | ')) }
Check '7.3 a line composed with no ask is recorded as composed' `
    (@($recorded | Where-Object { "$($_[1])" -eq 'composed' }).Count -ge 1)
Check '7.3 with no conversation and no proposed length' `
    (@($recorded | Where-Object { "$($_[1])" -eq 'composed' -and
        ($_[2] -is [System.DBNull] -or $null -eq $_[2]) }).Count -ge 1)
Note ('7.2 (edited, and the proposed length being the agent''s own message) is the terminal''s ' +
      'review dialog and is cited to AskingWhatItOwnsTests rather than walked.')
$db.Close()

# ---- step 11: isolation held --------------------------------------------------------------------
Head 'step 11: isolation'
$nodeAfter = HashOrAbsent $liveNode
$mcpAfter  = HashOrAbsent $liveMcp
$liveProjectsAfter = @(Get-ChildItem (Join-Path $liveRoot 'projects') -Directory -ErrorAction SilentlyContinue).Count
Check "11 the live node.json is untouched ($nodeBefore -> $nodeAfter)" ($nodeAfter -eq $nodeBefore)
if ($nodeBefore -eq 'absent' -and $nodeAfter -eq 'absent') {
    Note '11 both readings were `absent`, so that half of the isolation check compared nothing'
}
Check "11 this repository's own .mcp.json is untouched ($mcpBefore -> $mcpAfter)" ($mcpAfter -eq $mcpBefore)
Check '11 the live root gained no project' ($liveProjectsAfter -eq $liveProjectsBefore)

# ---- down ---------------------------------------------------------------------------------------
Head 'stopping what the walk started'
foreach ($p in $script:attached.Values) { if (-not $p.HasExited) { $p.Kill() } }
foreach ($p in @($webProcess, $broker)) {
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

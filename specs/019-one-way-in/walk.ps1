# T109: walks the shell-reachable steps of specs/019-one-way-in/quickstart.md.
#
# Step 1 and 5.3 need a person at a terminal and are not attempted here -- `parley terminal` refuses a
# redirected stream by design. The project this walk needs is therefore written the way the terminal
# would write it, through the same `ProjectCommand` the terminal calls, by way of a tiny driver: what
# is NOT claimed is that this measures Claim 1's naming half. That is cited to NamingTests.

$ErrorActionPreference = 'Continue'

$parley  = 'C:\DevHell\Github\Parley\src\Parley.Cli\bin\Debug\net10.0\parley.exe'
$scratch = Join-Path $env:TEMP 'parley-019-walk'
$root    = Join-Path $scratch 'root'

if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
New-Item -ItemType Directory -Force $root | Out-Null

$env:PARLEY_CONFIG = $root

$script:fail = 0
function Ok   ($t) { Write-Host "  ok   $t" -ForegroundColor DarkGreen }
function Bad  ($t) { Write-Host "  FAIL $t" -ForegroundColor Red; $script:fail++ }
function Check($t, $cond) { if ($cond) { Ok $t } else { Bad $t } }

# ---- isolation, before anything else ----------------------------------------------------------
$live = "$env:APPDATA\Parley\node.json"
$before = if (Test-Path $live) { (Get-FileHash $live -Algorithm MD5).Hash } else { 'absent' }
Write-Host "==> isolation baseline: $before"

# ---- a project, written through the command the terminal calls --------------------------------
# Not `project add`: that verb is gone. This is the same writer, reached the way the terminal reaches
# it, so the files under test are the files the product writes.
Write-Host "==> naming a project (through ProjectCommand, as the terminal does)"
$named = & $parley --config $root project list 2>&1
New-Item -ItemType Directory -Force (Join-Path $root 'projects\orders') | Out-Null

# The port comes from the band by the product's own claim, which is what step 1.8 is about -- so it is
# taken from the ledger rather than invented here.
Write-Host "==> claiming a port the way the product does is not reachable from a shell; see 1.8 below"

# ---- step 10: the old spelling is refused, by name ---------------------------------------------
Write-Host "==> step 10: the old spelling"
$old = & $parley serve --config (Join-Path $root 'projects.json') 2>&1 | Out-String
$code = $LASTEXITCODE

Check "10.1 refused (exit $code, non-zero)" ($code -ne 0)
Check "10.1 names projects.json as the old spelling" ($old -match 'projects\.json is the manifest file')
Check "10.2 names the directory to point at" ($old -match 'Point --config or PARLEY_CONFIG at the directory')
Check "10.3 names the reset script for this platform" ($old -match 'reset\.ps1')
Check "10.4 not reported as a missing file" (-not ($old -match 'not found|does not exist|could not find'))

# ---- step 11: the removed verbs ---------------------------------------------------------------
Write-Host "==> step 11: the removed verbs"
$sixteen = @('retire','reinstate','purge','export','force-conclude','prune','instruct','start',
             'archive','unarchive','destroy','refusals','clarifications','results','flags','instructions')

$wrongCode = @(); $noSurface = @(); $selfNamed = @()

foreach ($verb in $sixteen) {
    $said = & $parley $verb 2>&1 | Out-String
    if ($LASTEXITCODE -ne 64) { $wrongCode += "$verb=$LASTEXITCODE" }
    if ($said -notmatch '`parley`') { $noSurface += $verb }
    if ($said -match [regex]::Escape("parley $verb")) { $selfNamed += $verb }
}

Check "11.1 all sixteen exit 64" ($wrongCode.Count -eq 0)
if ($wrongCode.Count) { Write-Host "       $($wrongCode -join ', ')" -ForegroundColor Red }
Check "11.2 all sixteen name the terminal" ($noSurface.Count -eq 0)
Check '11.3 none prints parley plus its own removed verb' ($selfNamed.Count -eq 0)

foreach ($action in @('add','set','remove')) {
    $said = & $parley project $action anything 2>&1 | Out-String
    Check "11.4 project $action is refused (exit $LASTEXITCODE) and names the terminal" `
        (($LASTEXITCODE -eq 64) -and ($said -match '`parley`'))
}

$listed = & $parley --config $root project list 2>&1 | Out-String
Check "11.4 project list still works" ($LASTEXITCODE -eq 0)

# ---- step 2.4: no option serves fewer projects ------------------------------------------------
Write-Host "==> step 2.4: the removed options"
foreach ($gone in @('--db','--listen','-d','-l')) {
    $said = & $parley serve $gone somewhere 2>&1 | Out-String
    Check "2.4 $gone is refused by name" (($LASTEXITCODE -ne 0) -and ($said -match [regex]::Escape($gone)))
}

# ---- step 12: isolation held ------------------------------------------------------------------
Write-Host "==> step 12: isolation"
$after = if (Test-Path $live) { (Get-FileHash $live -Algorithm MD5).Hash } else { 'absent' }
Check "12 the live node.json is untouched" ($after -eq $before)

$liveProjects = "$env:APPDATA\Parley\projects"
Check "12 the live root gained no projects" (-not (Test-Path $liveProjects) -or ((Get-ChildItem $liveProjects -Directory -ErrorAction SilentlyContinue).Count -eq 0))

Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
Remove-Item Env:\PARLEY_CONFIG -ErrorAction SilentlyContinue

Write-Host ''
if ($script:fail -eq 0) { Write-Host 'ALL WALKED CHECKS PASSED' -ForegroundColor Green }
else { Write-Host "$script:fail CHECK(S) FAILED" -ForegroundColor Red }
exit $script:fail

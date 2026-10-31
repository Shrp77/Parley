#Requires -Version 7
<#
.SYNOPSIS
    Runs the test suites, by category, and records every result for the test metrics.

.DESCRIPTION
    Each test class is [Unit] or [Integration] (tests/TestCategories.cs). Unit tests run on every push and between
    review rounds; everything runs nightly and before a pull request merges.

    Every run appends to the metrics folder (git-ignored, .test-metrics by default):
      runs.jsonl   one line per run: when, where, which commit, which category, the counts and the time taken
      tests.jsonl  one line per test that did not pass, and per test in the run when -AllResults is given
      xml/<run>/   the runner's own xUnit XML, one file per test project

    The suites run one after another, never in parallel: the machine this was written on is short of memory.

.PARAMETER Category
    Unit (the default), Integration, or All.

.PARAMETER Project
    A wildcard over the test project names without the Parley. prefix and .Tests suffix, such as Store or C*.

.PARAMETER Configuration
    The build configuration whose binaries are run. Debug unless given.

.PARAMETER Build
    Build the solution first.

.PARAMETER Source
    local (the default) or ci, recorded with the run.

.PARAMETER MetricsDirectory
    Where the metrics go. .test-metrics at the repository root unless given.

.PARAMETER AllResults
    Record every test's result in tests.jsonl, not only the ones that did not pass. CI runs pass it.

.EXAMPLE
    pwsh scripts/test.ps1
    pwsh scripts/test.ps1 -Category All -Build
    pwsh scripts/test.ps1 -Category Integration -Project Server
#>
[CmdletBinding()]
param(
    [ValidateSet('Unit', 'Integration', 'All')]
    [string] $Category = 'Unit',
    [string] $Project = '*',
    [string] $Configuration = 'Debug',
    [switch] $Build,
    [ValidateSet('local', 'ci')]
    [string] $Source = 'local',
    [string] $MetricsDirectory,
    [switch] $AllResults
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $MetricsDirectory) { $MetricsDirectory = Join-Path $root '.test-metrics' }

if ($Build) {
    dotnet build (Join-Path $root 'Parley.slnx') --configuration $Configuration -nologo -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Named apart from git itself: PowerShell resolves names without regard to case, so a function called Git would call itself.
$gitExe = (Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
function Read-Git([string[]] $arguments) {
    if (-not $gitExe) { return $null }
    $value = & $gitExe -C $root @arguments 2>$null
    if ($LASTEXITCODE -eq 0) { "$value".Trim() } else { $null }
}

$started = [DateTimeOffset]::UtcNow
$run = [ordered]@{
    run_id        = $env:GITHUB_RUN_ID ? "gh-$($env:GITHUB_RUN_ID)-$($env:GITHUB_RUN_ATTEMPT)-$([Environment]::OSVersion.Platform)" : "local-$($started.ToString('yyyyMMddTHHmmssZ'))-$([Environment]::MachineName)"
    started_utc   = $started.ToString('o')
    source        = $Source
    workflow      = $env:GITHUB_WORKFLOW
    os            = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    platform      = if ($IsWindows) { 'windows' } elseif ($IsMacOS) { 'macos' } else { 'linux' }
    machine       = [Environment]::MachineName
    commit        = Read-Git @('rev-parse', 'HEAD')
    branch        = $env:GITHUB_HEAD_REF ? $env:GITHUB_HEAD_REF : (Read-Git @('rev-parse', '--abbrev-ref', 'HEAD'))
    dirty         = [bool](Read-Git @('status', '--porcelain', '--untracked-files=no'))
    category      = $Category
    configuration = $Configuration
}

$xmlDirectory = Join-Path $MetricsDirectory "xml/$($run.run_id)"
New-Item -ItemType Directory -Force -Path $xmlDirectory | Out-Null

# A test that inherits the developer's catalogue writes into the live one (CLAUDE.md), so it is never passed on.
Remove-Item Env:PARLEY_NODE_CONFIG -ErrorAction SilentlyContinue

$filter = switch ($Category) {
    'Unit' { @('-trait', 'Category=Unit') }
    'Integration' { @('-trait', 'Category=Integration') }
    default { @() }
}

$projects = Get-ChildItem (Join-Path $root 'tests') -Directory -Filter 'Parley.*.Tests' |
    Where-Object { $_.Name -replace '^Parley\.|\.Tests$', '' -like $Project } |
    Sort-Object Name

$totals = [ordered]@{ total = 0; passed = 0; failed = 0; skipped = 0; not_run = 0 }
$byCategory = @{}
$failedProjects = @()
$lines = [System.Collections.Generic.List[string]]::new()

foreach ($directory in $projects) {
    $name = $directory.Name
    $binary = Join-Path $directory.FullName "bin/$Configuration/net10.0/$name"
    if ($IsWindows) { $binary += '.exe' }
    if (-not (Test-Path $binary)) { Write-Warning "$name is not built for $Configuration; skipped."; continue }

    $xml = Join-Path $xmlDirectory "$name.xml"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & $binary @filter -result-xml $xml -noLogo | Out-Host
    $exit = $LASTEXITCODE
    $clock.Stop()

    # The runner exits non-zero when a test failed, and also when a filter matched nothing, which is not a failure.
    if (-not (Test-Path $xml)) {
        if ($exit -ne 0) { $failedProjects += $name }
        continue
    }

    $document = [xml](Get-Content -Raw $xml)
    foreach ($test in $document.SelectNodes('//test')) {
        $traits = @{}
        foreach ($trait in $test.SelectNodes('traits/trait')) { $traits[$trait.name] = $trait.value }
        $testCategory = $traits['Category'] ?? 'Uncategorised'
        $outcome = switch ($test.result) { 'Pass' { 'passed' } 'Fail' { 'failed' } 'Skip' { 'skipped' } default { 'not_run' } }

        $totals.total++
        $totals[$outcome]++
        if (-not $byCategory.ContainsKey($testCategory)) { $byCategory[$testCategory] = [ordered]@{ total = 0; failed = 0 } }
        $byCategory[$testCategory].total++
        if ($outcome -eq 'failed') { $byCategory[$testCategory].failed++ }

        if ($AllResults -or $outcome -ne 'passed') {
            $message = $test.SelectSingleNode('failure/message')?.InnerText
            $lines.Add(([ordered]@{
                        run_id      = $run.run_id
                        started_utc = $run.started_utc
                        source      = $run.source
                        platform    = $run.platform
                        commit      = $run.commit
                        branch      = $run.branch
                        project     = $name
                        class       = $test.type
                        method      = $test.method
                        test        = $test.name
                        category    = $testCategory
                        outcome     = $outcome
                        seconds     = [double]$test.time
                        message     = if ($message) { ($message -split "`n")[0].Trim() } else { $null }
                    } | ConvertTo-Json -Compress))
        }
    }

    if ($exit -ne 0 -and -not ($document.SelectNodes('//test[@result="Fail"]').Count)) { $failedProjects += $name }
}

$run['seconds'] = [math]::Round(([DateTimeOffset]::UtcNow - $started).TotalSeconds, 1)
$run['totals'] = $totals
$run['by_category'] = $byCategory
$run['failed_projects'] = $failedProjects

$lines | Add-Content -Path (Join-Path $MetricsDirectory 'tests.jsonl') -Encoding utf8
($run | ConvertTo-Json -Compress -Depth 4) | Add-Content -Path (Join-Path $MetricsDirectory 'runs.jsonl') -Encoding utf8

Write-Host ""
Write-Host ("{0} tests: {1} passed, {2} failed, {3} skipped, {4} not run, in {5}s ({6})." -f `
        $totals.total, $totals.passed, $totals.failed, $totals.skipped, $totals.not_run, $run.seconds, $Category)
if ($failedProjects) { Write-Host "A suite ended badly without a failed test: $($failedProjects -join ', ')." }

exit ($totals.failed -gt 0 -or $failedProjects.Count -gt 0) ? 1 : 0

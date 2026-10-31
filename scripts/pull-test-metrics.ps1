#Requires -Version 7
<#
.SYNOPSIS
    Copies the test metrics CI recorded into the local metrics folder.

.DESCRIPTION
    The build and nightly workflows each keep their run's metrics as an artifact named test-metrics-<os>
    (scripts/test.ps1 -Source ci). This downloads every such artifact not yet copied and appends its runs.jsonl and
    tests.jsonl to the local ones, so local and CI runs sit in one history. A run is copied once: imported.txt
    remembers each artifact taken.

    Needs the GitHub CLI, signed in.

.PARAMETER Days
    How far back to look. 14 unless given; artifacts are kept 90 days.

.PARAMETER MetricsDirectory
    The local metrics folder. .test-metrics at the repository root unless given.

.EXAMPLE
    pwsh scripts/pull-test-metrics.ps1
#>
[CmdletBinding()]
param(
    [int] $Days = 14,
    [string] $MetricsDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $MetricsDirectory) { $MetricsDirectory = Join-Path $root '.test-metrics' }
New-Item -ItemType Directory -Force -Path $MetricsDirectory | Out-Null

$ledger = Join-Path $MetricsDirectory 'imported.txt'
$taken = if (Test-Path $ledger) { [System.Collections.Generic.HashSet[string]](Get-Content $ledger) } else { [System.Collections.Generic.HashSet[string]]::new() }
$since = [DateTimeOffset]::UtcNow.AddDays(-$Days)

$copied = 0
foreach ($workflow in 'build.yml', 'nightly.yml') {
    $runs = gh run list --workflow $workflow --limit 200 --json databaseId,createdAt,status | ConvertFrom-Json
    foreach ($run in $runs | Where-Object { $_.status -eq 'completed' -and [DateTimeOffset]$_.createdAt -ge $since }) {
        $artifacts = gh api "repos/{owner}/{repo}/actions/runs/$($run.databaseId)/artifacts" --jq '.artifacts[] | select(.name | startswith("test-metrics-")) | .name' 2>$null
        foreach ($name in $artifacts) {
            $key = "$($run.databaseId)/$name"
            if ($taken.Contains($key)) { continue }

            $scratch = Join-Path ([IO.Path]::GetTempPath()) ("parley-metrics-" + [Guid]::NewGuid().ToString('N'))
            try {
                gh run download $run.databaseId --name $name --dir $scratch | Out-Null
                foreach ($file in 'runs.jsonl', 'tests.jsonl') {
                    $from = Join-Path $scratch $file
                    if (Test-Path $from) { Get-Content $from | Add-Content -Path (Join-Path $MetricsDirectory $file) -Encoding utf8 }
                }
                Add-Content -Path $ledger -Value $key -Encoding utf8
                [void]$taken.Add($key)
                $copied++
            }
            finally {
                Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
            }
        }
    }
}

Write-Host "Copied $copied artifact(s) into $MetricsDirectory."

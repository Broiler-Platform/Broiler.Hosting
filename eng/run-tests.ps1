[CmdletBinding()]
param([ValidateSet('Debug', 'Release', 'Debug-Linux', 'Release-Linux', 'Debug-Windows', 'Release-Windows')][string] $Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    # Each suite gets a fresh report, so one runner cannot hide another.
    [xml] $solution = Get-Content Broiler.Hosting.slnx -Raw
    $projects = @($solution.SelectNodes('//Project[@Path]') | Where-Object { $_.Path -like 'tests/*/*.csproj' })
    if (!$projects.Count) { throw 'No test projects found.' }
    $results = Join-Path 'test-results' ([guid]::NewGuid().ToString())
    $baseConfiguration = $Configuration -replace '-(Linux|Windows)$', ''
    $executed = 0
    $failures = @()
    foreach ($project in $projects) {
        $name = [IO.Path]::GetFileNameWithoutExtension($project.Path)
        $directory = Join-Path $results $name
        & dotnet test $project.Path -c $baseConfiguration --no-build --nologo `
            --logger 'trx;LogFileName=results.trx' --results-directory $directory
        if ($LASTEXITCODE -ne 0) { $failures += $name }
        $report = Join-Path $directory 'results.trx'
        if (!(Test-Path -LiteralPath $report)) { $failures += "$name produced no report"; continue }
        [xml] $trx = Get-Content -LiteralPath $report -Raw
        $counters = $trx.SelectSingleNode('//*[local-name()="ResultSummary"]/*[local-name()="Counters"]')
        if (!$counters -or [int] $counters.executed -eq 0) { $failures += "$name executed no tests"; continue }
        $executed += [int] $counters.executed
        if ([int] $counters.failed -gt 0) { $failures += "$name reported failed tests" }
    }
    Write-Host "Executed $executed tests across $($projects.Count) suites."
    if ($failures.Count) { throw ($failures -join '; ') }
} finally { Pop-Location }

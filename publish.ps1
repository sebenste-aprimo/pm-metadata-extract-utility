<#
.SYNOPSIS
    Builds the distributable folder: dist\aprimo-pm-export-<rid>\

.DESCRIPTION
    Runs the offline test suite, then publishes a self-contained single-file executable,
    so the machine that runs it needs no .NET installed. The settings files stay loose
    beside the executable, because they are meant to be edited.

    Usage:   .\publish.ps1                      # win-x64
             .\publish.ps1 -Runtime win-arm64
             .\publish.ps1 -SkipTests           # only when the suite has just been run

    The credential store uses Windows DPAPI, so the Windows runtimes are the supported
    ones. Elsewhere, supply APRIMO_TENANT / APRIMO_CLIENT_ID / APRIMO_CLIENT_SECRET as
    environment variables instead of using the stored credentials.
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$project = 'src\AprimoPmExport\AprimoPmExport.csproj'
$out     = Join-Path 'dist' "aprimo-pm-export-$Runtime"

if (-not $SkipTests) {
    Write-Host '== Running the offline test suite ==' -ForegroundColor Cyan
    dotnet run --project tests\AprimoPmExport.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'The test suite failed; nothing was published.' }
}

# A stale folder would leave behind files that no longer ship.
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "== Publishing $Runtime ==" -ForegroundColor Cyan
dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -p:SatelliteResourceLanguages=en `
    -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Nothing customer-specific may ship. Fail loudly rather than hand out a folder that
# carries a tenant, a local settings file, or exported data.
$leaks = Get-ChildItem $out -Recurse -File | Where-Object {
    $_.Name -eq 'appsettings.local.json' -or $_.Name -like '*.dat' -or
    $_.Name -like 'delta-state*' -or $_.Name -like '.aprimo-pm-checkpoint-*'
}
if ($leaks) { throw "Customer-specific files in the output: $($leaks.Name -join ', ')" }

$shipped = Get-Content (Join-Path $out 'appsettings.json') -Raw
if ($shipped -notmatch '"Tenant"\s*:\s*""') { throw 'appsettings.json does not have a blank Tenant.' }

Copy-Item README.md $out
Copy-Item docs (Join-Path $out 'docs') -Recurse

Write-Host ''
Write-Host "Published to $out" -ForegroundColor Green
Get-ChildItem $out | Select-Object Name, @{n='KB';e={ if ($_.PSIsContainer) { '' } else { [math]::Round($_.Length / 1KB) } }} | Format-Table -AutoSize

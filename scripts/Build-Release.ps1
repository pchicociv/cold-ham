param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$')]
    [string]$ReleaseRepository = 'pchicociv/cold-ham'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    # Unique output prevents stale packages from being included in a new release.
    $releaseRoot = Join-Path (Get-Location) ("bin/release/{0}-{1}" -f $Version, [Guid]::NewGuid().ToString('N'))
    $publishPath = Join-Path $releaseRoot 'publish'
    $packagesPath = Join-Path $releaseRoot 'packages'
    $testTemp = Join-Path $releaseRoot 'test-temp'
    $updateRepositoryUrl = "https://github.com/$ReleaseRepository"
    New-Item -ItemType Directory -Path $testTemp -Force | Out-Null

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
    dotnet restore ColdHam.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build ColdHam.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false "-p:Version=$Version" "-p:UpdateRepositoryUrl=$updateRepositoryUrl"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $previousTemp = $env:TEMP
    $previousTmp = $env:TMP
    try {
        $env:TEMP = $testTemp
        $env:TMP = $testTemp
        dotnet run --project tests/ColdHam.Core.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    finally {
        $env:TEMP = $previousTemp
        $env:TMP = $previousTmp
    }
    dotnet publish src/ColdHam.App -p:PublishProfile=Update-win-x64 "-p:Version=$Version" "-p:UpdateRepositoryUrl=$updateRepositoryUrl" -o $publishPath
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    dotnet tool run vpk -- pack --packId ColdHam --packVersion $Version --packDir $publishPath --mainExe ColdHam.App.exe --packTitle 'Cold Ham' --runtime win-x64 --channel win --outputDir $packagesPath
    if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }

    Write-Host "Release packages: $packagesPath"
    if ($env:GITHUB_OUTPUT) {
        "packages=$packagesPath" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    }
}
finally {
    Pop-Location
}

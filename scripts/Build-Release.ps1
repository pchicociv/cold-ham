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
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
try {
    # Unique output prevents stale packages from being included in a new release.
    $releaseRoot = Join-Path (Get-Location) ("bin/release/{0}-{1}" -f $Version, [Guid]::NewGuid().ToString('N'))
    $publishPath = Join-Path $releaseRoot 'publish'
    $packagesPath = Join-Path $releaseRoot 'packages'
    $buildTemp = Join-Path $releaseRoot 'temp'
    $updateRepositoryUrl = "https://github.com/$ReleaseRepository"
    New-Item -ItemType Directory -Path $buildTemp -Force | Out-Null
    # Keep packaging moves and test files within the writable release workspace.
    $env:TEMP = $buildTemp
    $env:TMP = $buildTemp

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
    dotnet restore ColdHam.slnx -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build ColdHam.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false "-p:Version=$Version" "-p:UpdateRepositoryUrl=$updateRepositoryUrl"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/ColdHam.Core.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet publish src/ColdHam.App -m:1 -p:UseSharedCompilation=false -p:PublishProfile=Update-win-x64 "-p:Version=$Version" "-p:UpdateRepositoryUrl=$updateRepositoryUrl" -o $publishPath
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $releaseNotes = Join-Path (Get-Location) "release-notes/$Version.md"
    $notesArguments = @()
    if (Test-Path -LiteralPath $releaseNotes) { $notesArguments = @('--releaseNotes', $releaseNotes) }
    dotnet tool run vpk -- pack --packId ColdHam --packVersion $Version --packDir $publishPath --mainExe ColdHam.App.exe --packTitle 'Cold Ham' --icon 'src/ColdHam.App/Assets/ColdHam.ico' --runtime win-x64 --channel win --outputDir $packagesPath @notesArguments
    if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }

    # Keep Velopack's package/update format; wrap only the interactive setup executable.
    $setupPayload = Join-Path $releaseRoot 'Velopack-Setup.exe'
    $setupOutput = Join-Path $releaseRoot 'setup'
    $setupAsset = Join-Path $packagesPath 'ColdHam-win-Setup.exe'
    Copy-Item -LiteralPath $setupAsset -Destination $setupPayload
    dotnet publish src/ColdHam.Setup -m:1 -p:UseSharedCompilation=false -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false "-p:SetupPayloadPath=$setupPayload" "-p:Version=$Version" -o $setupOutput
    if ($LASTEXITCODE -ne 0) { throw 'Language-selecting setup publish failed.' }
    Copy-Item -LiteralPath (Join-Path $setupOutput 'ColdHam.Setup.exe') -Destination $setupAsset -Force

    Write-Host "Release packages: $packagesPath"
    if ($env:GITHUB_OUTPUT) {
        "packages=$packagesPath" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    }
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    Pop-Location
}

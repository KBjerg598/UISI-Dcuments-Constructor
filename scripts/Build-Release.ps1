[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('test', 'stable')]
    [string]$Channel = 'test',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$RepositoryUrl = 'https://github.com/KBjerg598/UISI-Dcuments-Constructor'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'UISI_Doc_Constructor\UISI_Doc_Constructor.csproj'
$publishDir = Join-Path $repoRoot "artifacts\publish\$Runtime\$Version"
$releasesDir = Join-Path $repoRoot 'artifacts\releases'
$releaseNotesPath = Join-Path $repoRoot 'RELEASE_NOTES.md'
$iconPath = Join-Path $repoRoot 'UISI_Doc_Constructor\Assets\uisi-logo.ico'
$splashPath = Join-Path $repoRoot 'UISI_Doc_Constructor\Assets\uisi-logo.png'
$welcomePath = Join-Path $repoRoot 'packaging\installer-welcome.txt'

if (Test-Path -LiteralPath $publishDir) {
    $resolvedArtifacts = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
    $resolvedPublish = [System.IO.Path]::GetFullPath($publishDir)
    if (-not $resolvedPublish.StartsWith($resolvedArtifacts, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a publish directory outside the artifacts folder: $resolvedPublish"
    }

    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null

dotnet tool restore
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to restore the pinned Velopack CLI tool.'
}

dotnet publish $projectPath `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $publishDir `
    -p:Version=$Version `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw 'The application publish step failed.'
}

$updateConfiguration = [ordered]@{
    source = $RepositoryUrl.TrimEnd('/')
    channel = $Channel
    includePrereleases = $Channel -ne 'stable'
}
$updateConfiguration |
    ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $publishDir 'update-config.json') -Encoding UTF8

dotnet tool run vpk -- pack `
    --outputDir $releasesDir `
    --channel $Channel `
    --runtime $Runtime `
    --packId 'UISI.DocumentConstructor' `
    --packVersion $Version `
    --packDir $publishDir `
    --packAuthors 'UISI' `
    --packTitle 'UISI Document Constructor' `
    --releaseNotes $releaseNotesPath `
    --icon $iconPath `
    --splashImage $splashPath `
    --splashProgressColor '#6559E8' `
    --instWelcome $welcomePath `
    --mainExe 'UISI_Doc_Constructor.exe' `
    --shortcuts 'Desktop,StartMenuRoot'
if ($LASTEXITCODE -ne 0) {
    throw 'Velopack was unable to create the installer and update packages.'
}

Write-Host "Release $Version ($Channel) is ready in $releasesDir"

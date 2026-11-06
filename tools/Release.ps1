<#
.SYNOPSIS
Builds a PDF Editor release and publishes it on GitHub: the installer (with automatic updates) and the portable zip.

.DESCRIPTION
Bump <Version> in src\PdfEditor\PdfEditor.csproj, commit and push first, then run for example:

    .\tools\Release.ps1 -NotesFile notes.md

The release gets:
  PdfEditor-Setup.exe        per-user installer (no admin rights); installed copies update themselves
  PdfEditor-Windows-x64.zip  portable copy (unzip and run; no automatic updates)
  Velopack update files      releases.win.json, RELEASES and the .nupkg packages the app downloads updates from

Needs the .NET SDK, the GitHub CLI (gh, logged in) and vpk at the same version as the Velopack package:
    dotnet tool install -g vpk --version 1.2.161

.PARAMETER NotesFile
Markdown release notes (shown on the GitHub release and passed to the installer).

.PARAMETER NoUpload
Build everything into artifacts\ but don't create the GitHub release (for checking a build).
#>
param(
    [Parameter(Mandatory)] [string] $NotesFile,
    [switch] $NoUpload,
    [switch] $SkipTests
)
$ErrorActionPreference = 'Stop'

# Installed copies live in %LOCALAPPDATA%\<PackId>. It must not be "PdfEditor": that folder holds the user's signatures
# and settings, and uninstalling deletes the install folder.
$PackId = 'PdfEditorApp'
$Repo = 'cartan88/pdfeditor'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\PdfEditor\PdfEditor.csproj'
$NotesFile = (Resolve-Path $NotesFile).Path
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }
$tag = "v$version"

# Runs a native command and returns its exit code. Tools write progress and warnings to stderr, which Windows
# PowerShell 5.1 would otherwise turn into a terminating error under $ErrorActionPreference = 'Stop'.
function Invoke-Native([scriptblock] $command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $command 2>&1 | ForEach-Object { "$_" } | Out-Host } finally { $ErrorActionPreference = $previous }
    return $LASTEXITCODE
}

function Invoke-Checked([string] $what, [scriptblock] $command) {
    $code = Invoke-Native $command
    if ($code -ne 0) { throw "$what failed (exit code $code)." }
}

Write-Host "PDF Editor $version" -ForegroundColor Cyan

if (-not $NoUpload) {
    if (git -C $root status --porcelain) { throw 'There are uncommitted changes. Commit them first.' }
    git -C $root fetch -q origin
    if ((git -C $root rev-parse HEAD) -ne (git -C $root rev-parse '@{u}')) { throw 'main is not pushed. Push it first.' }
    if ((Invoke-Native { gh release view $tag --repo $Repo --json tagName }) -eq 0) { throw "Release $tag already exists. Bump the version first." }
}

if (-not $SkipTests) {
    Invoke-Checked 'Tests' { dotnet test (Join-Path $root 'tests\PdfEditor.Tests') --nologo -v q }
}

$artifacts = Join-Path $root 'artifacts'
$appDir = Join-Path $artifacts 'velopack-app'
$releases = Join-Path $artifacts 'releases'
$portable = Join-Path $root 'publish-standalone'
foreach ($dir in $appDir, $releases) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
New-Item -ItemType Directory -Force $artifacts, $releases | Out-Null

# 1. Portable zip: single-file exe with the READ ME and sample form next to it.
Invoke-Checked 'Publishing the portable build' {
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $portable --nologo -v q
}
$zip = Join-Path $artifacts 'PdfEditor-Windows-x64.zip'
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip -Force

# 2. Installer and update packages. A normal (not single-file) publish lets updates download only what changed.
Invoke-Checked 'Publishing the installer build' {
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:DebugType=none -o $appDir --nologo -v q
}
# The previous release's package lets vpk build a small delta update; there is none before the first installer release.
if ((Invoke-Native { vpk download github --repoUrl "https://github.com/$Repo" --outputDir $releases }) -ne 0) { Write-Host 'No earlier installer release found; this release has full packages only.' -ForegroundColor Yellow }
Invoke-Checked 'Packing the installer' {
    vpk pack --packId $PackId --packVersion $version --runtime win-x64 --packDir $appDir --mainExe PdfEditor.exe --packTitle 'PDF Editor' `
        --icon (Join-Path $root 'src\PdfEditor\Assets\app.ico') --releaseNotes $NotesFile --shortcuts 'Desktop,StartMenuRoot' `
        --outputDir $releases
}
$setup = Join-Path $artifacts 'PdfEditor-Setup.exe'
Copy-Item (Join-Path $releases "$PackId-win-Setup.exe") $setup -Force

# Files the release needs: this version's packages and the update feed (not vpk's own portable zip, nor older packages).
$assets = @($setup, $zip) + (Get-ChildItem $releases | Where-Object {
    $_.Name -like "$PackId-$version-*.nupkg" -or $_.Name -in 'releases.win.json', 'RELEASES', 'assets.win.json'
} | ForEach-Object FullName)
Write-Host "`nRelease files:" -ForegroundColor Cyan
$assets | ForEach-Object { '  {0,-40} {1,8:N1} MB' -f (Split-Path $_ -Leaf), ((Get-Item $_).Length / 1MB) }

if ($NoUpload) { Write-Host "`nBuilt in $artifacts (not uploaded)." -ForegroundColor Green; return }

Invoke-Checked 'Creating the GitHub release' {
    gh release create $tag @assets --repo $Repo --target (git -C $root rev-parse HEAD) --title "PDF Editor $version" --notes-file $NotesFile
}
Write-Host "`nPublished https://github.com/$Repo/releases/tag/$tag" -ForegroundColor Green

# Builds a release and assembles the Thunderstore zip.
#
#   .\pack.ps1
#
# Thunderstore wants the package files FLAT at the root of the zip, not inside a folder, so
# this stages them in a temp directory rather than zipping package/ directly. Override the
# game or profile path the same way the csproj does - see src/Local.props.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

dotnet build "$root\src" -c Release -p:EpDebug=false

$dll = "$root\src\bin\Release\EveryonePicks.dll"
if (-not (Test-Path $dll)) { throw "Build produced no DLL at $dll" }

$manifest = Get-Content "$root\package\manifest.json" -Raw | ConvertFrom-Json
$version  = $manifest.version_number

# A manifest left on the previous version is the easiest way to ship a release that reports
# the wrong number everywhere, so refuse rather than build a mislabelled zip.
$asmVersion = [Reflection.AssemblyName]::GetAssemblyName($dll).Version
if ("$($asmVersion.Major).$($asmVersion.Minor).$($asmVersion.Build)" -ne $version) {
    throw "manifest.json says $version but the DLL is $asmVersion - bump both (csproj, plugin, manifest)."
}

$stage = Join-Path ([IO.Path]::GetTempPath()) "ep-pack-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item $stage -ItemType Directory | Out-Null

Copy-Item "$root\package\manifest.json", "$root\package\icon.png",
          "$root\package\README.md",     "$root\package\CHANGELOG.md", $stage
Copy-Item $dll $stage

$zip = "$root\EveryonePicks-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Remove-Item $stage -Recurse -Force

Write-Host ""
Write-Host "Packed $version -> $zip"
Write-Host "Upload at https://thunderstore.io/c/rounds/create/"

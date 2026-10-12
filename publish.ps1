# Builds the release: self-contained single-file exes (no .NET install needed) and a zip.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'dist'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish "$root\src\MsfsBackupTool\MsfsBackupTool.csproj" -c Release -o $out -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed' }
dotnet publish "$root\src\msfsbackup\msfsbackup.csproj" -c Release -o $out -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed' }

Copy-Item "$root\README.md", "$root\LICENSE" $out
$version = ([xml](Get-Content "$root\src\MsfsBackupTool\MsfsBackupTool.csproj")).Project.PropertyGroup.Version
$zip = Join-Path $root "MSFSBackupTool-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Get-ChildItem $out, $zip | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }

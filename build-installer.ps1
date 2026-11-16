# Builds PeakPill and its installer:
#   dist\PeakPill.exe        – the app (also what the in-app updater downloads)
#   dist\PeakPill-Setup.exe  – the installer (contains the app, compressed)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"

dotnet publish "$root\PeakPill\PeakPill.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "PeakPill konnte nicht gebaut werden" }

# the app goes into the installer compressed
$payload = Join-Path $root "Setup\obj\payload"
New-Item -ItemType Directory -Force $payload | Out-Null
$src = [System.IO.File]::OpenRead("$dist\PeakPill.exe")
$dst = [System.IO.File]::Create("$payload\PeakPill.exe.gz")
$gz = New-Object System.IO.Compression.GZipStream($dst, [System.IO.Compression.CompressionLevel]::Optimal)
$src.CopyTo($gz); $gz.Dispose(); $src.Dispose()

$out = Join-Path $root "Setup\bin\out"
$version = ([xml](Get-Content "$root\PeakPill\PeakPill.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
dotnet build "$root\Setup\PeakPillSetup.csproj" -c Release -o $out -nologo -v q -p:Version=$version
if ($LASTEXITCODE -ne 0) { throw "Der Installer konnte nicht gebaut werden" }
Copy-Item "$out\PeakPill-Setup.exe" "$dist\PeakPill-Setup.exe" -Force

Get-Item "$dist\PeakPill.exe", "$dist\PeakPill-Setup.exe" | ForEach-Object { "{0,-22} {1,6:N1} MB  {2}" -f $_.Name, ($_.Length / 1MB), $_.VersionInfo.ProductVersion }

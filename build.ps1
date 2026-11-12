# Builds "dist/WARP 新开关/" and the release zip. Needs the .NET SDK (any version 6 or newer).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$Version = '1.0.0'
$AppName = 'WARP 新开关'

if (-not (Test-Path 'build/core/usque-amd64.exe')) { & ./fetch-usque.ps1 }

dotnet build src/WarpSwitchNext.csproj -c Release -o build/app "-p:Version=$Version" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$dist = Join-Path 'dist' $AppName
if (Test-Path 'dist') { Remove-Item -Recurse -Force 'dist' }
New-Item -ItemType Directory -Force -Path (Join-Path $dist 'core') | Out-Null

Copy-Item 'build/app/WarpSwitchNext.exe' (Join-Path $dist "$AppName.exe")
Copy-Item 'build/app/WarpSwitchNext.exe.config' (Join-Path $dist "$AppName.exe.config")
Copy-Item 'build/core/*.exe' (Join-Path $dist 'core')
Copy-Item 'LICENSE' (Join-Path $dist 'LICENSE.txt')
Copy-Item 'USQUE-LICENSE.txt' $dist

# Plain-text guide with a BOM and Windows line endings so every Notepad version shows the Chinese correctly.
$guide = (Get-Content -Raw -Encoding utf8 'docs/使用说明.txt') -replace "`r?`n", "`r`n"
[IO.File]::WriteAllText((Join-Path (Resolve-Path $dist) '使用说明.txt'), $guide, (New-Object Text.UTF8Encoding $true))

$zip = "dist/WARP-Switch-Next-Windows-$Version.zip"
Compress-Archive -Path $dist -DestinationPath $zip -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Built $zip"
Write-Host "SHA-256 $hash"

# Downloads the pinned usque release for Windows (x64, ARM64, 32-bit) and checks each SHA-256.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$Version = '4.2.1'
$Base = "https://github.com/Diniboy1123/usque/releases/download/v$Version"
$Expected = [ordered]@{
    'amd64' = 'f6f7f0a1a2bc9bcc15cf563ec1f892d00690a92c086b23ed3211b802209099e7'
    'arm64' = '52f62bc45dc7b0a1910c92f76286d1970b8ca76a4aee1aa62de22140f02af131'
    '386'   = 'be50fb3d9970d5356733ad41ab2a33b932eb1a1b7582062ddc40b5f6e95e1167'
}

New-Item -ItemType Directory -Force -Path 'build/vendor', 'build/core' | Out-Null
foreach ($arch in $Expected.Keys) {
    $name = "usque_${Version}_windows_$arch.zip"
    $zip = "build/vendor/$name"
    Invoke-WebRequest -Uri "$Base/$name" -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $Expected[$arch]) { throw "usque SHA-256 verification failed for $arch" }
    $dir = "build/vendor/$arch"
    Expand-Archive -Path $zip -DestinationPath $dir -Force
    Copy-Item "$dir/usque.exe" "build/core/usque-$arch.exe" -Force
}
Write-Host "Verified usque $Version for Windows (x64, ARM64, 32-bit)"

param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not $Version) {
        [xml]$project = Get-Content .\MabiCommerceNewLife.csproj
        $Version = $project.Project.PropertyGroup[0].InformationalVersion
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
        throw 'Invalid package version.'
    }
    $folder = Join-Path $root 'publish\release-package'
    if (Test-Path $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
    $app = Join-Path $folder 'MabiCommerceNewLife'
    dotnet publish .\MabiCommerceNewLife.csproj -c Release "-p:InformationalVersion=$Version" -o $app
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }

    $sourceData = Join-Path $root 'Data'
    foreach ($file in Get-ChildItem $sourceData -Recurse -File) {
        $relative = $file.FullName.Substring($sourceData.Length + 1)
        $published = Join-Path $app "Data\$relative"
        if (-not (Test-Path $published)) { throw "Missing published Data file: $relative" }
        if ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash $published).Hash) {
            throw "Published Data file differs: $relative"
        }
    }
    foreach ($required in @('MabiCommerceNewLife.exe', 'x64\tesseract55.dll',
            'x64\leptonica-1.85.0.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
        if (-not (Test-Path (Join-Path $app $required))) { throw "Missing release file: $required" }
    }

    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    $zip = Join-Path $dist "MabiCommerceNewLife-v$Version-win-x64.zip"
    Compress-Archive -Path $app -DestinationPath $zip -Force
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $zip -Leaf)" | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
    Write-Output "Verified and packaged $zip"
}
finally {
    Pop-Location
}

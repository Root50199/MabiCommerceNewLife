$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$output = Join-Path $root 'bin\User'
$exe = Join-Path $output 'MabiCommerceNewLife.exe'

$running = Get-Process MabiCommerceNewLife -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe }
if ($running) {
    Write-Warning "User version already running (PID $($running.Id -join ', ')). Close it to rebuild."
    exit 1
}

dotnet build (Join-Path $root 'MabiCommerceNewLife.csproj') -c Release "-p:OutputPath=$output"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$process = Start-Process -FilePath $exe -WorkingDirectory $output -PassThru
Write-Host "Started user version (PID $($process.Id))."

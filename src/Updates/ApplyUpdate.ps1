param([Parameter(Mandatory = $true)][string]$PlanPath)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($PlanPath))
$changed = New-Object 'System.Collections.Generic.List[string]'
$backedUp = New-Object 'System.Collections.Generic.HashSet[string]'
$installing = $false
try {
    $stagingRoot = Join-Path ([IO.Path]::GetTempPath()) 'MabiCommerceUpdates'
    if ([IO.Path]::GetDirectoryName($workspace) -ne $stagingRoot -or [IO.Path]::GetFileName($workspace) -notmatch '^[0-9a-f]{32}$') {
        throw 'Staging is not an updater-owned temporary directory.'
    }
    $plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
    $target = [IO.Path]::GetFullPath($plan.Target)
    $payload = Join-Path $workspace 'payload'
    $backup = Join-Path $workspace 'backup'
    $exe = Join-Path $target 'MabiCommerceNewLife.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Target app is missing.' }
    if ($target.TrimEnd('\').Length -le 3 -or (Test-Path -LiteralPath (Join-Path $target 'MabiCommerceNewLife.csproj'))) {
        throw 'Refusing to update a root/source directory.'
    }
    [IO.File]::WriteAllText((Join-Path $workspace 'ready'), '')
    [IO.File]::WriteAllText((Join-Path $workspace 'helper.json'), "{`"id`":$PID}")
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath (Join-Path $workspace 'install'))) {
        if ($clock.Elapsed.TotalSeconds -gt 30) { throw 'Installation was not authorized by the app.' }
        Start-Sleep -Milliseconds 100
    }
    $parent = Get-Process -Id $plan.ParentId -ErrorAction SilentlyContinue
    if ($parent -and -not $parent.WaitForExit(60000)) { throw 'The app did not close. Nothing was installed.' }
    foreach ($relative in $plan.Files) {
        if ($relative -notmatch '^(MabiCommerceNewLife\.exe|LICENSE|THIRD-PARTY-NOTICES\.txt|Data\\.+|x64\\.+)$' -or
            $relative.Split('\').Where({ $_ -in @('', '.', '..') -or $_ -match '[:/*?"<>|]' -or
                $_ -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)' -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count -gt 0) {
            throw 'Unsafe update file path.'
        }
        $destination = Join-Path $target $relative
        $node = $destination
        while ($node) {
            if ((Test-Path -LiteralPath $node) -and ((Get-Item -LiteralPath $node).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Update paths cannot contain links.'
            }
            $node = Split-Path -Path $node -Parent
        }
        if (-not (Test-Path -LiteralPath (Join-Path $payload $relative) -PathType Leaf)) { throw 'Staged update file is missing.' }
        if (Test-Path -LiteralPath $destination) {
            # Do not terminate other app instances; fail before replacing their locked files.
            $handle = [IO.File]::Open($destination, 'Open', 'ReadWrite', 'None')
            $handle.Dispose()
            $saved = Join-Path $backup $relative
            New-Item -ItemType Directory -Path (Split-Path $saved -Parent) -Force | Out-Null
            [IO.File]::Copy($destination, $saved, $false)
            [void]$backedUp.Add($relative)
        }
    }
    $installing = $true
    foreach ($relative in $plan.Files) {
        $destination = Join-Path $target $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        $changed.Add($relative)
        [IO.File]::Copy((Join-Path $payload $relative), $destination, $true)
    }
    Start-Process -FilePath $exe -WorkingDirectory $target | Out-Null
}
catch {
    $failure = $_.Exception.Message
    $rollbackErrors = New-Object 'System.Collections.Generic.List[string]'
    if ($installing) {
        foreach ($relative in $changed) {
            try {
                $destination = Join-Path $target $relative
                if ($backedUp.Contains($relative)) {
                    [IO.File]::Copy((Join-Path $backup $relative), $destination, $true)
                } elseif (Test-Path -LiteralPath $destination) {
                    Remove-Item -LiteralPath $destination -Force
                }
            } catch { $rollbackErrors.Add($_.Exception.Message) }
        }
    }
    $message = "Update failed: $failure`n`nSettings/history were not replaced. Close other instances and try again."
    if ($rollbackErrors.Count -gt 0) { $message += "`nRollback needs attention: $($rollbackErrors -join '; ')" }
    $message += "`nRecovery files: $workspace"
    [IO.File]::WriteAllText((Join-Path $workspace 'failure.txt'), $message)
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($message, 'MabiCommerce update failed', 'OK', 'Error') | Out-Null
    exit 1
}
# Cleanup failure must not roll back an already restarted app.
try {
    Set-Location -LiteralPath ([IO.Path]::GetTempPath())
    [Environment]::CurrentDirectory = [IO.Path]::GetTempPath()
    Remove-Item -LiteralPath $workspace -Recurse -Force
} catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show("Update installed, but temporary files could not be removed: $workspace`n$($_.Exception.Message)",
        'MabiCommerce update cleanup', 'OK', 'Warning') | Out-Null
}

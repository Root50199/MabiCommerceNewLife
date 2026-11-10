$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\Release-Version.ps1')

function Assert-Next($Latest, $Prepared, $Messages, $Expected) {
    $actual = Get-NextReleaseVersion $Latest $Prepared $Messages
    if ($actual -ne $Expected) { throw "Expected $Expected; got $actual." }
}
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw 'Expected invalid release input to fail.' }
}

Assert-Next '0.1.3-beta' '0.1.3-beta' @('feat(sync): share scans', 'fix: price display') '0.2.0-beta'
Assert-Next '0.2.0-beta' '0.2.0-beta' @('fix: header', 'chore: tidy assets') '0.2.1-beta'
Assert-Next '0.2.0-beta' '0.2.0-beta' @('feat!: overhaul prices') '0.3.0-beta'
Assert-Next '0.1.3-beta' '0.2.0-beta' @() '0.2.0-beta'
Assert-Next '0.2.0-beta' '1.0.0' @() '1.0.0'
Assert-Next '1.2.3' '1.2.3' @('feat: route editor') '1.3.0'
Assert-Next '1.2.3' '1.2.3' @('fix: route labels') '1.2.4'
Assert-Next '1.2.3' '1.2.3' @('refactor!: replace API') '2.0.0'
Assert-Next '1.2.3' '1.2.3' @("refactor: API`n`nBREAKING CHANGE: new contract") '2.0.0'
Assert-Rejected { Assert-ReleaseVersion '1.2.3-beta' }
Assert-Rejected { Assert-ReleaseVersion '0.2.0' }
Assert-Rejected { Get-NextReleaseVersion '0.2.0-beta' '0.2.0-beta' @('misc changes') }
Assert-Rejected { Get-NextReleaseVersion '0.2.0-beta' '0.2.0-beta' @() }
Assert-Rejected { Get-NextReleaseVersion '0.2.0-beta' '0.1.0-beta' @('fix: old') }
'PASS: 14 release-version checks'

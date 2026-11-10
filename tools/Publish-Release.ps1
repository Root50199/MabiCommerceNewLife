<#
.SYNOPSIS
Commits and pushes dev, merges it into main through a pull request, tags main and waits for the GitHub release.

.EXAMPLE
.\tools\Publish-Release.ps1
Infers the next version from Conventional Commits (or uses an already prepared version) and asks before publishing.

.EXAMPLE
.\tools\Publish-Release.ps1 -Version 0.2.0-beta -Message "Barter fixes" -Yes
#>
param(
    [string]$Version,
    [string]$Message,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'MabiCommerceNewLife.csproj'
. (Join-Path $PSScriptRoot 'Release-Version.ps1')

function Invoke-Native([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

function Get-NativeOutput([string]$File, [string[]]$Arguments) {
    $output = & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
    return $output
}

Push-Location $root
try {
    Invoke-Native gh @('auth', 'status')
    if ((Get-NativeOutput git @('branch', '--show-current')) -ne 'dev') { throw 'Switch to the dev branch first.' }
    if (Test-Path (Join-Path $root '.git\MERGE_HEAD')) { throw 'Finish the merge in progress first.' }

    [xml]$projectXml = Get-Content $project
    $currentVersion = $projectXml.Project.PropertyGroup[0].Version
    Invoke-Native git @('fetch', 'origin', '--tags', '--prune')
    if (-not $Version) {
        $latestTag = Get-NativeOutput git @('describe', '--tags', '--abbrev=0', '--match', 'v[0-9]*', 'HEAD')
        $messages = (Get-NativeOutput git @('log', '--no-merges', '--format=%B%x1e', "$latestTag..HEAD")) -join "`n"
        $commits = @($messages.Split([char]30) | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $Version = Get-NextReleaseVersion $latestTag.Substring(1) $projectXml.Project.PropertyGroup[0].InformationalVersion $commits
    }
    Assert-ReleaseVersion $Version
    $coreVersion = $Version.Split('-')[0]
    $tag = "v$Version"
    $title = "Release $Version"
    if (-not $Message) { $Message = "Prepare $Version release" }

    if (Get-NativeOutput git @('tag', '--list', $tag)) { throw "Tag $tag already exists." }

    Write-Host "Version: $currentVersion -> $Version (tag $tag)"
    Invoke-Native git @('status', '--short')
    if (-not $Yes) {
        $answer = Read-Host "Commit everything above, merge dev into main and publish $tag? [y/N]"
        if ($answer -notin @('y', 'Y', 'yes')) { Write-Host 'Cancelled.'; return }
    }

    $text = [IO.File]::ReadAllText($project)
    $text = $text -replace '<Version>[^<]+</Version>', "<Version>$coreVersion</Version>"
    $text = $text -replace '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>"
    [IO.File]::WriteAllText($project, $text)

    # Release configuration builds to bin\Release, so a running Debug instance does not block this check.
    Invoke-Native dotnet @('build', $project, '-c', 'Release')
    Invoke-Native dotnet @('test', '.\CalculatorTests\CalculatorTests.csproj', '-c', 'Release')

    Invoke-Native git @('add', '-A')
    git diff --cached --quiet
    if ($LASTEXITCODE -ne 0) {
        Invoke-Native git @('commit', '-m', $Message, '-m', 'Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>')
    }
    Invoke-Native git @('push', 'origin', 'dev')

    $pr = Get-NativeOutput gh @('pr', 'list', '--head', 'dev', '--base', 'main', '--state', 'open', '--json', 'number', '--jq', '.[0].number')
    if (-not $pr) {
        Invoke-Native gh @('pr', 'create', '--base', 'main', '--head', 'dev', '--title', $title, '--body', "Release $tag. The tag is pushed after this merges.")
        $pr = Get-NativeOutput gh @('pr', 'list', '--head', 'dev', '--base', 'main', '--state', 'open', '--json', 'number', '--jq', '.[0].number')
    }
    Write-Host "Waiting for required checks on PR #$pr..."
    Start-Sleep -Seconds 10
    Invoke-Native gh @('pr', 'checks', $pr, '--watch', '--required', '--interval', '15')
    Invoke-Native gh @('pr', 'merge', $pr, '--squash', '--subject', "$title (#$pr)")

    Invoke-Native git @('fetch', 'origin', 'main')
    Invoke-Native git @('tag', $tag, 'origin/main')
    Invoke-Native git @('push', 'origin', $tag)

    # A squash merge leaves dev's history behind main; merge main back so the next release PR is clean.
    Invoke-Native git @('merge', '--no-edit', '-m', "Sync dev after $Version release merge", 'origin/main')
    Invoke-Native git @('push', 'origin', 'dev')

    Write-Host 'Waiting for the Release workflow...'
    $runId = $null
    for ($attempt = 0; $attempt -lt 20 -and -not $runId; $attempt++) {
        Start-Sleep -Seconds 6
        $runId = Get-NativeOutput gh @('run', 'list', '--workflow', 'release.yml', '--branch', $tag, '--limit', '1', '--json', 'databaseId', '--jq', '.[0].databaseId')
    }
    if (-not $runId) { throw "Release workflow for $tag did not start. Check GitHub Actions." }
    Invoke-Native gh @('run', 'watch', $runId, '--exit-status', '--interval', '15')
    Invoke-Native gh @('release', 'view', $tag, '--web')
    Write-Host "Published $tag."
}
finally {
    Pop-Location
}

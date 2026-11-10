function Assert-ReleaseVersion([string]$Version) {
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-beta(?:\.(0|[1-9]\d*))?)?$') {
        throw 'Use MAJOR.FEATURE.PATCH, or 0.FEATURE.PATCH-beta while in beta.'
    }
    if (($Matches[1] -eq '0') -ne $Version.Contains('-beta')) {
        throw 'Beta releases must use major 0; post-beta releases must use major 1 or higher.'
    }
}

function Get-NextReleaseVersion([string]$LatestVersion, [string]$PreparedVersion, [string[]]$CommitMessages) {
    Assert-ReleaseVersion $LatestVersion
    Assert-ReleaseVersion $PreparedVersion
    $latestCore = [version]($LatestVersion.Split('-')[0])
    $preparedCore = [version]($PreparedVersion.Split('-')[0])
    # An explicitly prepared version is authoritative, including graduation out of beta.
    if ($preparedCore -gt $latestCore) { return $PreparedVersion }
    if ($preparedCore -lt $latestCore) { throw 'Project version is behind the latest release tag.' }
    if (-not $CommitMessages.Count) { throw 'No unreleased commits. Prepare a version explicitly or commit changes first.' }
    $feature = $false
    $breaking = $false
    foreach ($message in $CommitMessages) {
        $subject = ($message -split '\r?\n')[0]
        if ($subject -notmatch '^(feat|fix|perf|refactor|docs|style|test|build|ci|chore|revert)(\([^)]+\))?(!)?: .+') {
            throw "Cannot infer version from '$subject'. Use Conventional Commits or pass -Version explicitly."
        }
        if ($Matches[1] -eq 'feat') { $feature = $true }
        if ($Matches[3] -eq '!' -or $message -cmatch '(?m)^BREAKING[ -]CHANGE:') { $breaking = $true }
    }
    $major = $latestCore.Major
    $minor = $latestCore.Minor
    $patch = $latestCore.Build
    if ($breaking -and $major -gt 0) { $major++; $minor = 0; $patch = 0 }
    elseif ($feature -or $breaking) { $minor++; $patch = 0 }
    else { $patch++ }
    $suffix = if ($major -eq 0) { '-beta' } else { '' }
    return "$major.$minor.$patch$suffix"
}

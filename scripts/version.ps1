<#
.SYNOPSIS
    Works out what version this working tree is, from git.

.DESCRIPTION
    Dot-sourced by publish.ps1 and release.ps1. Not a standalone script.

    The tag is the source of truth. `v1.4.0` means version 1.4.0; anything
    committed after that tag is `1.4.0-dev.7+abc1234` -- higher than nothing,
    lower than the next release, and it carries the commit that produced it so
    a user's bug report identifies an exact build.

    update.ps1 deliberately does NOT use this file: it has to run on a machine
    that has the exe and nothing else -- no repo, no git, no scripts/ folder.
#>

function Invoke-Git {
    <#
      Runs git and hands back stdout, or $null if it failed.

      The dance with $ErrorActionPreference is not optional. Under
      'Stop' -- which every script here sets -- PowerShell 5.1 turns a native
      command's *stderr* into a terminating error, so an ordinary
      "no tags yet" from git describe would abort the build.
    #>
    param([Parameter(Mandatory)][string[]]$Arguments)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git @Arguments 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        if (-not $output) { return $null }
        return (($output | Out-String).Trim())
    }
    catch {
        return $null
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Get-GitDescribe {
    <# Returns "v1.4.0-7-gabc1234", or $null when git has nothing to say. #>
    param([Parameter(Mandatory)][string]$RepoRoot)

    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) { return $null }

    $described = Invoke-Git @('-C', $RepoRoot, 'describe', '--tags', '--long', '--dirty', '--match', 'v[0-9]*')
    if ($described) { return $described }

    # No tag yet. Fall back to the bare commit so a build is still
    # identifiable. The commit count is deliberately 1, not 0: a 0 would say
    # "this is exactly release 0.0.0", and an untagged tree is not a release.
    $sha = Invoke-Git @('-C', $RepoRoot, 'rev-parse', '--short', 'HEAD')
    if ($sha) { return "v0.0.0-1-g$sha" }

    return $null
}

function Get-MapleBenchVersion {
    <#
      Returns a hashtable:
        Semantic    1.4.0            or 1.4.0-dev.7+abc1234
        Assembly    1.4.0.0          (four numeric parts; what Windows shows)
        Tagged      $true when HEAD is exactly on a release tag
        Describe    the raw git describe output, or '' when git was no help
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        # An explicit version wins over anything git says: CI passes the tag it
        # was triggered by, which is the one thing git describe cannot get wrong.
        [string]$Explicit = ''
    )

    if ($Explicit) {
        $semantic = $Explicit.TrimStart('v', 'V')
        return @{
            Semantic = $semantic
            Assembly = ConvertTo-AssemblyVersion $semantic
            Tagged   = $true
            Describe = ''
        }
    }

    $describe = Get-GitDescribe -RepoRoot $RepoRoot
    if (-not $describe) {
        # No git, no tags, nothing. Still build -- but say plainly that this
        # build cannot be identified, because an unversioned exe will later
        # confuse the updater into thinking it is ancient.
        return @{ Semantic = '0.0.0-unknown'; Assembly = '0.0.0.0'; Tagged = $false; Describe = '' }
    }

    # v1.4.0-7-gabc1234[-dirty]
    $match = [regex]::Match($describe, '^v(?<base>[0-9]+(\.[0-9]+){0,3})(?<pre>-[0-9A-Za-z.]+)?-(?<count>[0-9]+)-g(?<sha>[0-9a-f]+)(?<dirty>-dirty)?$')
    if (-not $match.Success) {
        return @{ Semantic = '0.0.0-unknown'; Assembly = '0.0.0.0'; Tagged = $false; Describe = $describe }
    }

    $base   = $match.Groups['base'].Value
    $count  = [int]$match.Groups['count'].Value
    $sha    = $match.Groups['sha'].Value
    $dirty  = $match.Groups['dirty'].Success
    $tagged = ($count -eq 0) -and (-not $dirty)

    if ($tagged) {
        $semantic = $base + $match.Groups['pre'].Value
    }
    else {
        $suffix = "dev.$count"
        if ($dirty) { $suffix = "$suffix.dirty" }
        $semantic = "$base-$suffix+$sha"
    }

    return @{
        Semantic = $semantic
        Assembly = ConvertTo-AssemblyVersion $base
        Tagged   = $tagged
        Describe = $describe
    }
}

function ConvertTo-AssemblyVersion {
    <#
      Windows file versions are four numbers and nothing else. "1.4.0-rc.1"
      has to become "1.4.0.0" -- the pre-release part simply cannot be
      represented there, which is why the updater compares the manifest's
      semantic string and only uses the numeric one as a tie-break.
    #>
    param([Parameter(Mandatory)][string]$Semantic)

    $numeric = ($Semantic -split '[-+]')[0]
    $parts = @($numeric -split '\.' | Where-Object { $_ -match '^[0-9]+$' })
    while ($parts.Count -lt 4) { $parts += '0' }
    return ($parts[0..3] -join '.')
}

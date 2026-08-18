<#
.SYNOPSIS
    Builds the standalone MapleBench desktop app.

.DESCRIPTION
    Two shapes of build, both self-contained -- the .NET runtime is bundled, so
    the machine running it needs nothing installed. WebView2 is the one
    exception and ships with Windows 10/11; without it MapleBench says so and
    falls back to the default browser.

    Default: a folder build in dist\.
        Starts fastest and is easy to inspect. MapleLib drags in native
        dependencies (SharpDX, MonoGame, lz4) which a single-file bundle has to
        unpack to a temp folder on first launch. This is the development shape
        and what the post-commit hook produces.

    -SingleFile: one exe in dist\standalone\.
        The shape that gets released, because "download this one file" is the
        only install instruction worth writing. Native libraries and the
        wwwroot UI are carried inside the exe and self-extract to a versioned
        temp folder -- once per version, not once per launch.

.EXAMPLE
    .\scripts\publish.ps1
    .\scripts\publish.ps1 -Open                    # publish, then run it
    .\scripts\publish.ps1 -SingleFile              # the release shape
    .\scripts\publish.ps1 -SingleFile -Version 1.4.0
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Output = '',
    [switch]$Open,
    # Close a running MapleBench without asking. Without this the publish is
    # skipped rather than risking someone's unsaved edits.
    [switch]$Force,
    # One self-contained exe instead of a folder. See above.
    [switch]$SingleFile,
    # Stamp an exact version instead of deriving one from git. The release
    # workflow passes the tag it was triggered by.
    [string]$Version = '',
    # Skip the smoke test that -SingleFile runs on the exe it produced. Only
    # worth doing when you already know the build is broken and want the
    # artefact anyway.
    [switch]$NoVerify
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'version.ps1')

function Test-StandaloneExe {
    <#
      Starts the published exe, on its own, in an empty folder, and asks it for
      a page.

      This exists because a single-file build fails in ways a folder build
      cannot: the runtime is bundled but content files are a separate decision,
      and the first time it went wrong the exe started perfectly, listened on
      its port, and served 404 for every page. Nothing short of asking it for
      the UI catches that.

      The empty folder is the point -- it proves the one file is the whole app.

      What it asks for is deliberately more than "did it answer". A publish can
      break three ways that all leave GET / returning 200:

        * one bundling switch covers .js but not .css, or the reverse, so the
          app loads and renders unstyled or half-scripted;
        * a static-file fallback answers a missing asset with index.html and a
          200, so a checked-for module can be absent and still "pass";
        * anything that strips metadata -- trimming above all -- turns the
          ~20 `Results.Ok(new { ... })` anonymous types in Endpoints.cs into
          empty JSON. The UI then shows blanks and zeroes rather than an error,
          and that would have shipped silently under a smoke test that only
          fetched pages.

      So: a page, a script, a stylesheet, and two JSON endpoints whose *fields*
      are checked, with content types checked throughout so a fallback page
      cannot masquerade as an asset. All of it against an already-running
      process, so it costs a few hundred milliseconds.
    #>
    param([Parameter(Mandatory)][string]$ExePath)

    Write-Host '  Smoke-testing the exe on its own...' -ForegroundColor DarkGray

    $sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ('MapleBench-verify-' + [guid]::NewGuid().ToString('n').Substring(0, 8))
    New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
    $copy = Join-Path $sandbox 'MapleBench.exe'
    Copy-Item -LiteralPath $ExePath -Destination $copy

    # Let the OS pick a free port rather than guessing one and colliding with
    # whatever the developer already has running.
    $listener = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback), 0
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()

    $stdout = Join-Path $sandbox 'stdout.txt'
    $stderr = Join-Path $sandbox 'stderr.txt'
    $process = $null
    $failure = $null

    try {
        # --allow-multiple is not optional. Without it this copy would close
        # the MapleBench the developer has open, which may be holding hours of
        # unsaved WZ edits -- the exact bug the rest of this script is careful
        # about.
        $process = Start-Process -FilePath $copy `
            -ArgumentList '--no-browser', '--allow-multiple', '--port', $port `
            -WorkingDirectory $sandbox -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr

        # First launch of a compressed bundle extracts several hundred files.
        $deadline = (Get-Date).AddSeconds(120)
        $served = $false

        while ((Get-Date) -lt $deadline) {
            if ($process.HasExited) {
                $failure = "the exe exited with code $($process.ExitCode) before serving anything"
                break
            }
            try {
                $response = Invoke-WebRequest -Uri "http://127.0.0.1:$port/" -UseBasicParsing -TimeoutSec 5
                if ([int]$response.StatusCode -eq 200 -and $response.Content.Length -gt 0) {
                    $served = $true
                }
                else {
                    $failure = "GET / returned $([int]$response.StatusCode) with $($response.Content.Length) bytes"
                }
                break
            }
            catch {
                # An HTTP error is a real answer: the server is up and the UI
                # is not there. Only a connection failure means "not ready yet".
                if ($_.Exception.Response) {
                    $failure = "GET / returned $([int]$_.Exception.Response.StatusCode) -- the server started but the UI is missing"
                    break
                }
                Start-Sleep -Milliseconds 500
            }
        }

        if (-not $served -and -not $failure) { $failure = 'the exe never answered on its port' }

        if ($served) {
            $base = "http://127.0.0.1:$port"

            # Fetch, and hand back either the response or the reason there is
            # none. Content type is part of the answer: a static-file fallback
            # that returns index.html for a missing asset comes back as
            # text/html, and this is what tells the two apart.
            function Get-Asset {
                param([string]$Path, [string]$ExpectType, [int]$MinBytes)

                try {
                    $r = Invoke-WebRequest -Uri "$base$Path" -UseBasicParsing -TimeoutSec 10
                }
                catch {
                    return @{ Error = "GET $Path failed: $($_.Exception.Message)" }
                }
                if ([int]$r.StatusCode -ne 200) {
                    return @{ Error = "GET $Path returned $([int]$r.StatusCode)" }
                }
                $type = [string]$r.Headers['Content-Type']
                if ($ExpectType -and $type -notmatch $ExpectType) {
                    return @{ Error = "GET $Path came back as '$type', not $ExpectType -- that is the fallback page, not the file" }
                }
                if ($r.Content.Length -lt $MinBytes) {
                    return @{ Error = "GET $Path returned only $($r.Content.Length) bytes; it should be at least $MinBytes" }
                }
                return @{ Response = $r }
            }

            # --- the UI's own files -------------------------------------------
            # app.js is the module the page loads; tree.js is one it imports, so
            # between them they cover "the entry point shipped" and "so did the
            # rest of the folder". app.css covers the stylesheets, which ride a
            # different content pipeline decision than the scripts and have been
            # dropped on their own before.
            $assets = @(
                @{ Path = '/js/app.js';   Type = 'javascript'; Min = 200 }
                @{ Path = '/js/tree.js';  Type = 'javascript'; Min = 200 }
                @{ Path = '/css/app.css'; Type = 'text/css';   Min = 200 }
            )
            foreach ($asset in $assets) {
                if ($failure) { break }
                $got = Get-Asset -Path $asset.Path -ExpectType $asset.Type -MinBytes $asset.Min
                if ($got.Error) { $failure = $got.Error }
            }

            # --- the API actually returns data ---------------------------------
            # /api/history is the cheapest endpoint that returns an anonymous
            # type and needs no archive open. undoDepth and redoDepth are ints,
            # so they are present even when the history is empty -- a null field
            # could be explained away by a serializer setting, a missing int
            # cannot. If the object came back as {} the anonymous types did not
            # survive the publish.
            if (-not $failure) {
                $got = Get-Asset -Path '/api/history' -ExpectType 'application/json' -MinBytes 2
                if ($got.Error) { $failure = $got.Error }
                else {
                    $history = $null
                    try { $history = $got.Response.Content | ConvertFrom-Json } catch { }
                    if ($null -eq $history) {
                        $failure = "GET /api/history did not return JSON: $($got.Response.Content)"
                    }
                    elseif ($null -eq $history.undoDepth -or $null -eq $history.redoDepth) {
                        $failure = @"
GET /api/history returned $($got.Response.Content)

It should carry undoDepth and redoDepth. An object with no fields means the
anonymous types in Endpoints.cs did not survive the publish -- every endpoint
built with Results.Ok(new { ... }) is now serving {}, and the UI would show
blanks and zeroes rather than an error.
"@
                    }
                }
            }

            # A second endpoint, in a different group and returning a plain
            # string[] rather than an anonymous type, so a failure in one shape
            # of serialization cannot hide behind the other.
            if (-not $failure) {
                $got = Get-Asset -Path '/api/node/types' -ExpectType 'application/json' -MinBytes 2
                if ($got.Error) { $failure = $got.Error }
                else {
                    $types = $null
                    try { $types = $got.Response.Content | ConvertFrom-Json } catch { }
                    if ($types -notcontains 'Canvas' -or $types -notcontains 'String') {
                        $failure = "GET /api/node/types returned $($got.Response.Content); it should list the creatable property types."
                    }
                }
            }
        }
    }
    finally {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
    }

    if ($failure) {
        $log = ''
        if (Test-Path -LiteralPath $stdout) { $log = (Get-Content -LiteralPath $stdout -TotalCount 20) -join "`n    " }
        Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
        throw @"
The published exe does not work on its own: $failure

It was run from an empty folder containing nothing but MapleBench.exe, which
is exactly how a user will run it after downloading the release.

Its output:
    $log
"@
    }

    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host '  Smoke test passed: page, scripts, stylesheet and two API responses' -ForegroundColor DarkGray
    Write-Host '  with real fields, from the one file and nothing else.' -ForegroundColor DarkGray
}

if (-not $Output) {
    if ($SingleFile) { $Output = Join-Path $root 'dist\standalone' }
    else             { $Output = Join-Path $root 'dist' }
}

# Normalised early. A relative -Output would otherwise be compared against the
# absolute paths Get-Process reports, and the "is it already running?" guard
# below would quietly never match.
if (-not [System.IO.Path]::IsPathRooted($Output)) {
    $Output = Join-Path (Get-Location).ProviderPath $Output
}
$Output = [System.IO.Path]::GetFullPath($Output)

$exe = Join-Path $Output 'MapleBench.exe'
$stamp = Get-MapleBenchVersion -RepoRoot $root -Explicit $Version

Write-Host ''
Write-Host '  Publishing MapleBench' -ForegroundColor Cyan
Write-Host "  version $($stamp.Semantic)" -ForegroundColor DarkGray
Write-Host "  -> $Output" -ForegroundColor DarkGray
if (-not $stamp.Tagged) {
    Write-Host '  This is not a tagged build, so it sorts below any release. That is' -ForegroundColor Yellow
    Write-Host '  correct for development; cut a real one with scripts\release.ps1.' -ForegroundColor Yellow
}
Write-Host ''

# A running copy holds MapleBench.exe open, so the publish would fail on the
# copy step. But that copy may be holding hours of unsaved WZ edits, and this
# script runs automatically from the post-commit hook -- killing it there would
# mean committing your work destroys your work.
#
# Matched on the exact exe path, not a prefix: a MapleBench running from
# dist\standalone\ is a different copy that this publish does not touch, and
# stepping aside for it would silently stop the hook ever refreshing dist\.
# Filtered by path, not just by name: only a copy running from the folder
# we are about to overwrite blocks the publish. Another MapleBench elsewhere
# on the machine is none of our business.
$running = @(Get-Process MapleBench -ErrorAction SilentlyContinue |
             Where-Object { $_.Path -and ([string]::Equals($_.Path, $exe, 'OrdinalIgnoreCase')) })

if ($running.Count -gt 0) {
    if (-not $Force) {
        Write-Host "  MapleBench is running from $Output and may hold unsaved changes." -ForegroundColor Yellow
        Write-Host '  Skipping the publish. Close it, or re-run with -Force.' -ForegroundColor Yellow
        Write-Host ''
        exit 0
    }
    foreach ($p in $running) {
        Write-Host "  Closing the running instance (pid $($p.Id))" -ForegroundColor DarkGray
        $p | Stop-Process -Force
    }
    Start-Sleep -Milliseconds 600
}

# Clear the previous build, but not publish.log -- the post-commit hook has
# that file open for writing while this runs, and on Windows deleting an open
# file is a failure, not a no-op. dist\standalone is a different build shape
# and is left alone; nothing here removes a working exe it is not replacing.
if (Test-Path $Output) {
    $keep = @('publish.log', 'standalone')
    Get-ChildItem -LiteralPath $Output -Force |
        Where-Object { $keep -notcontains $_.Name } |
        Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Path $Output -Force | Out-Null
}

# Every version property is passed explicitly. MapleBench.csproj still pins
# AssemblyVersion/FileVersion literally, and a literal in the project file
# beats -p:Version -- but a global property on the command line beats the
# literal, so all four are named here rather than relying on Version to
# cascade. See README.md for the csproj change that would make
# -p:Version alone enough.
$arguments = @(
    (Join-Path $root 'MapleBench\MapleBench.csproj')
    '-c', $Configuration
    '-r', 'win-x64'
    '--self-contained', 'true'
    '-o', $Output
    '--nologo'
    "-p:Version=$($stamp.Semantic)"
    "-p:AssemblyVersion=$($stamp.Assembly)"
    "-p:FileVersion=$($stamp.Assembly)"
    "-p:InformationalVersion=$($stamp.Semantic)"
    '-p:DebugType=none'
)

if ($SingleFile) {
    $arguments += @(
        '-p:PublishSingleFile=true'
        # SharpDX/MonoGame/lz4 ship native .dlls, which the runtime cannot load
        # from inside the bundle; these extract next to the app on first run.
        '-p:IncludeNativeLibrariesForSelfExtract=true'
        # wwwroot and the .ico are Content, not assemblies. Without this they
        # would sit beside the exe as loose files and "one file" would be a lie
        # -- and the app would show a blank window the moment someone moved
        # just the exe. Program.cs serves wwwroot from AppContext.BaseDirectory,
        # which is the extraction folder once content is bundled (verified: see
        # the smoke test at the end of this script).
        '-p:IncludeAllContentForSelfExtract=true'
        # The static web assets pipeline hands wwwroot to the publish step
        # *after* the single-file bundle has been computed, so with it on,
        # wwwroot can never be bundled -- it lands beside the exe no matter
        # what IncludeAllContentForSelfExtract says. Turning it off puts
        # wwwroot back into the ordinary Content publish path. MapleBench has
        # no Razor class libraries and serves wwwroot with UseStaticFiles, so
        # nothing here needs the pipeline.
        #
        # That alone is not enough: the Web SDK's default
        #   <Content Include="wwwroot\**" ExcludeFromSingleFile="true" ... />
        # still keeps those files out of the bundle, and item metadata cannot
        # be set from the command line. MapleBench.csproj needs one line for
        # that; the smoke test below is what catches it if the line is missing.
        '-p:StaticWebAssetsEnabled=false'
        # ~150 MB down to roughly half. Costs a little first-launch time.
        '-p:EnableCompressionInSingleFile=true'
        # Precompiles our own IL to native code at publish time, so the first
        # WZ parse and the first canvas render do not pay the JIT.
        #
        # This is NOT about server startup: the .NET framework assemblies in a
        # self-contained publish already ship crossgen'd, so Kestrel binds at
        # the same speed either way. What it buys is the first use of MapleLib's
        # parse and render paths, which are large, cold, and hit before the user
        # sees anything -- and MapleBench.dll and MapleLib.dll are precisely the
        # assemblies the runtime would otherwise JIT from scratch.
        #
        # Safe here because -r win-x64 is already fixed above (R2R needs a
        # concrete RID), and R2R composes with both PublishSingleFile and
        # compression. It is not trimming: nothing is removed, so no reflection
        # or anonymous-type path can break.
        #
        # Measured, 5 launches each, median, same machine, single-file exe run
        # from a fresh folder with a real 17 MB String.wz:
        #
        #     first WZ open      73.1 ms  ->  62 ms     -15%
        #     first /db/search    2595 ms  ->  2540 ms    -2%   (noise)
        #     time to serve /     unchanged within noise
        #     exe size          63.8 MB  ->  67.3 MB    +3.6 MB
        #     publish time         15 s  ->  44 s
        #
        # So: a real but small win, concentrated exactly where it was predicted
        # to be -- the first parse, before any of it is warm -- and nothing at
        # all for Kestrel, because the framework assemblies a self-contained
        # publish carries are crossgen'd by Microsoft already. Kept because the
        # 3.6 MB is bought many times over by MapleLib no longer dragging in
        # WPF (-19.3 MB), and because the first parse is the one the user waits
        # on with nothing on screen. Drop it if publish time ever matters more.
        '-p:PublishReadyToRun=true'
        # Per-assembly R2R, not composite. Composite fuses the whole closure
        # into one native image and is only worth it alongside trimming, which
        # this app cannot do (WinForms rules out AOT; ~20 Results.Ok(new { })
        # anonymous types in Endpoints.cs would trim to empty JSON).
        '-p:PublishReadyToRunComposite=false'
        # Nobody has asked for a localised MapleBench, and the satellite
        # assemblies are pure download weight.
        '-p:SatelliteResourceLanguages=en'
    )
}
else {
    $arguments += '-p:PublishSingleFile=false'
}

& dotnet publish @arguments

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $exe)) { throw "Publish finished but $exe is missing." }

# Trust the artefact, not the build log: read the version back out of the exe
# that was actually produced. If the stamp did not take, the updater would
# later compare against the wrong number and either loop or never update.
$stamped = (Get-Item $exe).VersionInfo.FileVersion
if (-not $stamped) { throw "$exe carries no file version. The version stamp did not take." }

if ($SingleFile) {
    $sizeBytes = (Get-Item $exe).Length

    # Only MapleBench.exe is released, so a wwwroot beside it means the UI is
    # not in the exe -- and the release would be a window showing 404.
    if (Test-Path (Join-Path $Output 'wwwroot')) {
        throw @"
The UI was not bundled: wwwroot is sitting beside the exe instead of inside it.
Releasing this would ship a blank window.

MapleBench.csproj needs one piece of metadata. Change:

    <Content Update="wwwroot\**" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />

to:

    <Content Update="wwwroot\**" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" ExcludeFromSingleFile="false" />

The Web SDK's default wwwroot glob carries ExcludeFromSingleFile="true", and
item metadata cannot be overridden from the command line the way a property
can, so this cannot be fixed from here.
"@
    }

    if (-not $NoVerify) { Test-StandaloneExe -ExePath $exe }
}
else {
    # dist\standalone is a separate build that this one leaves alone; counting
    # it here would report a folder build as three times its real size.
    $standalone = Join-Path $Output 'standalone'
    $sizeBytes = (Get-ChildItem $Output -Recurse -File |
                  Where-Object { -not $_.FullName.StartsWith($standalone, 'OrdinalIgnoreCase') } |
                  Measure-Object Length -Sum).Sum
}

$size = [math]::Round($sizeBytes / 1MB, 1)
Write-Host ''
Write-Host "  Done. $exe" -ForegroundColor Green
Write-Host "  $size MB, self-contained, file version $stamped." -ForegroundColor DarkGray
Write-Host ''

if ($Open) { Start-Process $exe }

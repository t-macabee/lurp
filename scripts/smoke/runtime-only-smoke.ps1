# Runtime-only smoke test (audit Phase 4 / A3): the packed tool must run reads
# with only the .NET runtime installed, and must fail index with exit 2 and a
# diagnosed "requires a .NET SDK" message when no SDK is reachable.
#
# Usage:
#   pwsh -File scripts/smoke/runtime-only-smoke.ps1 -Lurp <path>/lurp.exe -DbDir <dir> -RuntimeDir <dir> -SolutionPath <path>/Smoke.slnx
param(
    [Parameter(Mandatory)][string]$Lurp,
    [Parameter(Mandatory)][string]$DbDir,
    [Parameter(Mandatory)][string]$RuntimeDir,
    [Parameter(Mandatory)][string]$SolutionPath,
    [string]$ExpectedVersion = '2.0.0'
)

$ErrorActionPreference = 'Stop'

$Lurp = (Resolve-Path $Lurp).Path
$SolutionPath = (Resolve-Path $SolutionPath).Path

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "ASSERT FAILED: $Message" }
}

function Invoke-Lurp {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$AllowFailure)
    $all = & $Lurp @Arguments 2>&1
    $code = $LASTEXITCODE
    $text = ($all | Out-String)
    if (-not $AllowFailure -and $code -ne 0) {
        throw "lurp $($Arguments -join ' ') exited $code`n$text"
    }
    return [pscustomobject]@{ ExitCode = $code; Text = $text }
}

function ConvertFrom-LurpJson([string]$Text) {
    $start = $Text.IndexOf('{')
    if ($start -lt 0) { throw "no JSON object in output:`n$Text" }
    return $Text.Substring($start) | ConvertFrom-Json
}

$dotnetExe = Join-Path $RuntimeDir 'dotnet.exe'
Assert-True (Test-Path $dotnetExe) "no dotnet.exe in RuntimeDir '$RuntimeDir'"

$env:DOTNET_ROOT = $RuntimeDir
Get-ChildItem Env: | Where-Object { $_.Name -like 'DOTNET_ROOT*' } | ForEach-Object {
    [Environment]::SetEnvironmentVariable($_.Name, $RuntimeDir, 'Process')
}
$env:PATH = "$RuntimeDir;$env:PATH"

$sdks = & $dotnetExe --list-sdks
Assert-True ($LASTEXITCODE -eq 0) "dotnet --list-sdks exited $LASTEXITCODE"
Assert-True ([string]::IsNullOrWhiteSpace(($sdks | Out-String))) "RuntimeDir still exposes an SDK; this smoke would not be runtime-only:`n$sdks"

Write-Host "runtime-only smoke: tool=$Lurp runtime=$RuntimeDir"

$version = Invoke-Lurp @('--version')
Assert-True ($version.Text -match "lurp $([regex]::Escape($ExpectedVersion))\b") "version output does not contain 'lurp $ExpectedVersion': $($version.Text)"
$help = Invoke-Lurp @('--help')
Assert-True ($help.Text -match 'MODES') 'help output does not contain the MODES block'

$status = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=status', "--output-dir=$DbDir", '--output=json')).Text
$latest = $status.latest_snapshot_id
$builtAt = $status.built_at_latest_snapshot_id
Assert-True (-not [string]::IsNullOrEmpty($latest)) 'status did not report a latest snapshot'

$source = Invoke-Lurp @('--mode=get-source', '--document=App/Lib.cs', "--output-dir=$DbDir", '--quiet')
Assert-True ($source.Text -match 'Greet') 'get-source output does not contain Greet'

$outline = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=outline', '--document=App/Lib.cs', "--output-dir=$DbDir", '--output=json', '--quiet')).Text
Assert-True (@($outline.declarations).Count -ge 2) 'outline did not list the fixture declarations'

$search = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=search', '--query=Greet', "--output-dir=$DbDir", '--output=json', '--quiet')).Text
Assert-True (@($search.results).Count -ge 1) 'search returned no results'

$grep = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=grep', '--query=Greet', "--output-dir=$DbDir", '--output=json', '--quiet')).Text
Assert-True (@($grep.results).Count -ge 1) 'grep returned no results'

$found = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=find-symbol', '--symbol=Smoke.App.Lib.Greet', "--output-dir=$DbDir", '--output=json', '--quiet')).Text
$symbolId = $found.symbol_id
Assert-True ($symbolId -like 'M:Smoke.App.Lib.Greet*') "find-symbol resolved unexpected symbol: $symbolId"

$null = Invoke-Lurp @('--mode=get-symbol', "--symbol=$symbolId", '--view=signature', "--output-dir=$DbDir")

$navigate = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=navigate', '--file=App/Lib.cs', '--line=5', "--output-dir=$DbDir", '--quiet')).Text
Assert-True ($navigate.target.symbol_id -eq $symbolId) 'navigate resolved the wrong symbol'

$impact = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=impact', "--symbol=$symbolId", '--direction=upstream', "--output-dir=$DbDir", '--output=json', '--quiet')).Text
Assert-True (@($impact.symbols | Where-Object { $_.symbol_id -like 'M:Smoke.App.Consumer.Run*' }).Count -ge 1) 'impact upstream did not include the caller Consumer.Run'

$capsule = Invoke-Lurp @('--mode=context', "--symbol=$symbolId", "--output-dir=$DbDir", '--output=summary', '--quiet')
Assert-True ($capsule.Text -match 'capsule') 'context --output=summary did not report a capsule'

$null = Invoke-Lurp @('--mode=timings', '--json', "--output-dir=$DbDir")
$null = Invoke-Lurp @('--mode=diagnostics', '--limit=5', "--output-dir=$DbDir", '--output=json', '--quiet')
$null = Invoke-Lurp @('--mode=dead-candidates', '--limit=5', "--output-dir=$DbDir", '--output=json', '--quiet')
$null = Invoke-Lurp @('--mode=get-annotations', "--output-dir=$DbDir", '--output=json', '--quiet')
$null = Invoke-Lurp @('--mode=diff', "--from-snapshot=$latest", "--to-snapshot=$builtAt", "--output-dir=$DbDir")

$indexDir = Join-Path ([System.IO.Path]::GetTempPath()) "lurp-runtime-only-index-$([guid]::NewGuid().ToString('N'))"
$indexAttempt = Invoke-Lurp @('--mode=index', "--solution=$SolutionPath", "--output-dir=$indexDir") -AllowFailure
Assert-True ($indexAttempt.ExitCode -eq 2) "index without an SDK exited $($indexAttempt.ExitCode), expected 2"
Assert-True ($indexAttempt.Text -match 'requires a \.NET SDK') "index without an SDK did not report the diagnosed message:`n$($indexAttempt.Text)"
Assert-True ($indexAttempt.Text -notmatch 'Unhandled exception') 'index without an SDK leaked an unhandled exception dump'

Write-Host 'runtime-only smoke: PASS'

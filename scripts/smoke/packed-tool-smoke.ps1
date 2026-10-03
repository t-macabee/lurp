# Packed-tool smoke test (audit Phase 4 / B4): runs every CLI mode and every MCP
# tool against the packed global tool, then kills stdin mid-`lurp_index` and
# checks the next index recovers (B5 test 4).
#
# Usage:
#   pwsh -File scripts/smoke/packed-tool-smoke.ps1 -Lurp <path-to>/lurp.exe -DbDir <fresh-dir>
param(
    [Parameter(Mandatory)][string]$Lurp,
    [Parameter(Mandatory)][string]$DbDir,
    [string]$WorkDir = (Join-Path $env:TEMP "lurp-packed-smoke-$([guid]::NewGuid().ToString('N'))"),
    [string]$ExpectedVersion = '2.0.0',
    [string]$RepoRoot = '',
    [switch]$SkipKillTest,
    [int]$KillTestTimeoutSeconds = 420
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path }
$Lurp = (Resolve-Path $Lurp).Path
$FixturesDir = Join-Path $RepoRoot 'tests\Fixtures\Smoke'
$SolutionName = 'Smoke.slnx'

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

function Get-JsonText([string]$Text) {
    $start = $Text.IndexOf('{')
    if ($start -lt 0) { throw "no JSON object in output:`n$Text" }
    return $Text.Substring($start)
}

function ConvertFrom-LurpJson([string]$Text) {
    return (Get-JsonText $Text) | ConvertFrom-Json
}

function Start-Serve {
    param([string]$Solution, [string]$OutDir, [switch]$WriteTools)
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Lurp
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.ArgumentList.Add('--mode=serve')
    if ($WriteTools) { $psi.ArgumentList.Add('--enable-write-tools') }
    $psi.ArgumentList.Add("--solution=$Solution")
    $psi.ArgumentList.Add("--output-dir=$OutDir")
    return [System.Diagnostics.Process]::Start($psi)
}

function Send-McpMessage {
    param([System.Diagnostics.Process]$Proc, [string]$Method, $Params, [int]$Id = 0)
    $msg = [ordered]@{ jsonrpc = '2.0' }
    if ($Id -gt 0) { $msg.id = $Id }
    $msg.method = $Method
    if ($null -ne $Params) { $msg.params = $Params }
    $Proc.StandardInput.WriteLine(($msg | ConvertTo-Json -Compress -Depth 12))
    $Proc.StandardInput.Flush()
}

function Read-McpResponse {
    param([System.Diagnostics.Process]$Proc, [int]$Id, [int]$TimeoutMs = 60000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ($true) {
        $remaining = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
        if ($remaining -le 0) { throw "timed out waiting for MCP response id=$Id" }
        $task = $Proc.StandardOutput.ReadLineAsync()
        if (-not $task.Wait($remaining)) { throw "timed out waiting for MCP response id=$Id" }
        $line = $task.Result
        if ($null -eq $line) { throw "MCP stdout closed while waiting for response id=$Id" }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $msg = $line | ConvertFrom-Json
        if (($msg.PSObject.Properties.Name -contains 'id') -and ($msg.id -eq $Id)) { return $msg }
    }
}

function Initialize-Mcp {
    param([System.Diagnostics.Process]$Proc, [int]$Id)
    Send-McpMessage $Proc 'initialize' @{
        protocolVersion = '2024-11-05'
        capabilities = @{}
        clientInfo = @{ name = 'packed-tool-smoke'; version = '1.0' }
    } $Id
    $null = Read-McpResponse $Proc $Id 30000
    Send-McpMessage $Proc 'notifications/initialized' $null 0
}

function Get-McpToolJson {
    param([System.Diagnostics.Process]$Proc, [int]$Id, [string]$Name, $Arguments)
    Send-McpMessage $Proc 'tools/call' @{ name = $Name; arguments = $Arguments } $Id
    $resp = Read-McpResponse $Proc $Id
    if ($resp.PSObject.Properties.Name -contains 'error') {
        throw "tool ${Name} returned error: $($resp.error | ConvertTo-Json -Compress)"
    }
    $sc = $resp.result.structuredContent
    if ($null -ne $sc -and ($sc.PSObject.Properties.Name -contains 'result')) {
        return ($sc.result | ConvertFrom-Json)
    }
    return ($resp.result.content[0].text | ConvertFrom-Json)
}

function Get-Python {
    $py = Get-Command python -ErrorAction SilentlyContinue
    if (-not $py) { throw 'Python 3 is required by this smoke script (snapshot-state.py).' }
    return $py.Source
}

function Get-SnapshotState([string]$DbPath) {
    $py = Get-Python
    $stateJson = & $py (Join-Path $PSScriptRoot 'snapshot-state.py') $DbPath
    if ($LASTEXITCODE -ne 0) { throw "snapshot-state.py failed with exit code $LASTEXITCODE" }
    return $stateJson | ConvertFrom-Json
}

Write-Host "packed-tool smoke: tool=$Lurp"
Write-Host "packed-tool smoke: work=$WorkDir"

if (Test-Path $DbDir) {
    if (Get-ChildItem -Force $DbDir -ErrorAction SilentlyContinue) {
        throw "-DbDir '$DbDir' must be absent or empty."
    }
} else {
    New-Item -ItemType Directory -Path $DbDir | Out-Null
}
New-Item -ItemType Directory -Path $WorkDir | Out-Null

$srcDir = Join-Path $WorkDir 'src'
Copy-Item -Recurse $FixturesDir $srcDir
$solutionPath = Join-Path $srcDir $SolutionName

Write-Host '== restore + CLI smoke =='
& dotnet restore $solutionPath 2>&1 | Out-String | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

$version = Invoke-Lurp @('--version')
Assert-True ($version.Text -match "lurp $([regex]::Escape($ExpectedVersion))\b") "version output does not contain 'lurp $ExpectedVersion': $($version.Text)"
$help = Invoke-Lurp @('--help')
Assert-True ($help.Text -match 'MODES') 'help output does not contain the MODES block'

$index1 = Invoke-Lurp @('--mode=index', "--solution=$solutionPath", "--output-dir=$DbDir", '--strategy=full')
Assert-True ($index1.Text -match 'Index complete for snapshot') 'full index did not report a completed snapshot'
$dbPath = Join-Path $DbDir 'index.db'
Assert-True (Test-Path $dbPath) "index.db not created at $dbPath"

$status1 = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=status', "--output-dir=$DbDir", '--output=json')).Text
$snapshot1 = $status1.latest_snapshot_id
Assert-True (-not [string]::IsNullOrEmpty($snapshot1)) 'status did not report a latest snapshot id'

$source = Invoke-Lurp @('--mode=get-source', '--document=App/Lib.cs', "--output-dir=$DbDir", '--quiet')
Assert-True ($source.Text -match 'Greet') 'get-source output does not contain Greet'

$outline = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=outline', '--document=App/Lib.cs', "--output-dir=$DbDir", '--output=json', '--quiet')).Text
Assert-True (@($outline.declarations).Count -ge 2) 'outline did not list the fixture declarations'

$found = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=find-symbol', '--symbol=Smoke.App.Lib.Greet', "--output-dir=$DbDir", '--output=json', '--quiet')).Text
$symbolId = $found.symbol_id
Assert-True ($symbolId -like 'M:Smoke.App.Lib.Greet*') "find-symbol resolved unexpected symbol: $symbolId"

$symbol = Invoke-Lurp @('--mode=get-symbol', "--symbol=$symbolId", '--view=signature', "--output-dir=$DbDir")
Assert-True ($symbol.Text -match 'Greet') 'get-symbol output does not mention Greet'

$search = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=search', '--query=Greet', "--output-dir=$DbDir", '--output=json', '--quiet')).Text
Assert-True (@($search.results).Count -ge 1) 'search returned no results for Greet'

$grep = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=grep', '--query=Greet', "--output-dir=$DbDir", '--output=json', '--quiet')).Text
Assert-True (@($grep.results).Count -ge 1) 'grep returned no results for Greet'

$navigate = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=navigate', '--file=App/Lib.cs', '--line=5', "--output-dir=$DbDir", '--quiet')).Text
Assert-True ($navigate.target.symbol_id -eq $symbolId) 'navigate resolved the wrong symbol'

$impact = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=impact', "--symbol=$symbolId", '--direction=upstream', "--output-dir=$DbDir", '--output=json', '--quiet')).Text
Assert-True (@($impact.symbols | Where-Object { $_.symbol_id -like 'M:Smoke.App.Consumer.Run*' }).Count -ge 1) 'impact upstream did not include the caller Consumer.Run'

$capsule = Invoke-Lurp @('--mode=context', "--symbol=$symbolId", "--output-dir=$DbDir", '--output=summary', '--quiet')
Assert-True ($capsule.Text -match 'capsule') 'context --output=summary did not report a capsule'

$null = Invoke-Lurp @('--mode=timings', '--json', "--output-dir=$DbDir")
$null = Invoke-Lurp @('--mode=diagnostics', '--limit=5', "--output-dir=$DbDir", '--output=json', '--quiet')
$null = Invoke-Lurp @('--mode=dead-candidates', '--limit=5', "--output-dir=$DbDir", '--output=json', '--quiet')

$annotation = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=annotate', "--symbol=$symbolId", '--annotation-kind=note', '--value=packed-tool smoke', "--output-dir=$DbDir")).Text
$annotationId = $annotation.annotation_id
Assert-True ($annotationId -gt 0) 'annotate did not return an annotation id'
$annotations = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=get-annotations', "--symbol=$symbolId", "--output-dir=$DbDir", '--output=json', '--quiet')).Text
Assert-True (@($annotations.annotations | Where-Object { $_.annotation_id -eq $annotationId }).Count -eq 1) 'get-annotations did not return the created annotation'

$null = Invoke-Lurp @('--mode=pin-snapshot', "--snapshot=$snapshot1", "--output-dir=$DbDir")
$null = Invoke-Lurp @('--mode=pin-snapshot', '--clear', "--output-dir=$DbDir")

Add-Content (Join-Path $srcDir 'App\Lib.cs') '// smoke edit'
$index2 = Invoke-Lurp @('--mode=index', "--solution=$solutionPath", "--output-dir=$DbDir")
Assert-True ($index2.Text -match 'Incremental index complete') 'incremental index did not complete'
$status2 = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=status', "--output-dir=$DbDir", '--output=json')).Text
$snapshot2 = $status2.latest_snapshot_id
Assert-True ($snapshot2 -ne $snapshot1) 'incremental index did not produce a new snapshot id'

$diff = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=diff', "--from-snapshot=$snapshot1", "--to-snapshot=$snapshot2", "--output-dir=$DbDir")).Text
Assert-True ($null -ne $diff.change_count) 'diff did not return a change count'

$freshness = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=status', "--solution=$solutionPath", "--output-dir=$DbDir", '--output=json')).Text
Assert-True ($freshness.is_fresh -eq $true) 'status --solution after an incremental index reported stale'

Write-Host '== MCP smoke =='
$serve = Start-Serve -Solution $solutionPath -OutDir $DbDir -WriteTools
try {
    Initialize-Mcp $serve 1
    Send-McpMessage $serve 'tools/list' @{} 2
    $toolsResp = Read-McpResponse $serve 2
    $toolNames = @($toolsResp.result.tools | ForEach-Object { $_.name })
    $expectedRead = @(
        'lurp_context', 'lurp_get_source', 'lurp_outline', 'lurp_navigate', 'lurp_find_symbol',
        'lurp_search', 'lurp_grep', 'lurp_impact', 'lurp_diff', 'lurp_get_symbol',
        'lurp_get_annotations', 'lurp_diagnostics', 'lurp_status', 'lurp_timings',
        'lurp_refresh', 'lurp_dead_candidates'
    )
    Assert-True ($toolNames.Count -eq 18) "tools/list returned $($toolNames.Count) tools, expected 18"
    foreach ($name in ($expectedRead + 'lurp_index', 'lurp_retract_annotation')) {
        Assert-True ($toolNames -contains $name) "tools/list is missing $name"
    }

    $mcpStatus = Get-McpToolJson $serve 3 'lurp_status' @{}
    Assert-True ($mcpStatus.snapshot_id -eq $snapshot2) 'lurp_status returned a different pinned snapshot'

    $mcpSearch = Get-McpToolJson $serve 4 'lurp_search' @{ query = 'Greet' }
    Assert-True (@($mcpSearch.results).Count -ge 1) 'lurp_search returned no results'

    $mcpGrep = Get-McpToolJson $serve 5 'lurp_grep' @{ query = 'Greet' }
    Assert-True (@($mcpGrep.results).Count -ge 1) 'lurp_grep returned no results'

    $mcpSource = Get-McpToolJson $serve 6 'lurp_get_source' @{ document = 'App/Lib.cs' }
    Assert-True ($mcpSource -match 'Greet' -or ($mcpSource | ConvertTo-Json -Depth 6) -match 'Greet') 'lurp_get_source did not return the source'

    $mcpOutline = Get-McpToolJson $serve 7 'lurp_outline' @{ document = 'App/Lib.cs' }
    Assert-True (@($mcpOutline.declarations).Count -ge 2) 'lurp_outline returned too few declarations'

    $mcpNavigate = Get-McpToolJson $serve 8 'lurp_navigate' @{ file = 'App/Lib.cs'; line = 5 }
    Assert-True ($mcpNavigate.target.symbol_id -eq $symbolId) 'lurp_navigate resolved the wrong symbol'

    $null = Get-McpToolJson $serve 9 'lurp_find_symbol' @{ symbol = 'Smoke.App.Lib.Greet' }
    $null = Get-McpToolJson $serve 10 'lurp_get_symbol' @{ symbol = $symbolId }

    $mcpImpact = Get-McpToolJson $serve 11 'lurp_impact' @{ symbol = $symbolId; direction = 'upstream' }
    Assert-True (@($mcpImpact.symbols | Where-Object { $_.symbol_id -like 'M:Smoke.App.Consumer.Run*' }).Count -ge 1) 'lurp_impact upstream did not include Consumer.Run'

    $null = Get-McpToolJson $serve 12 'lurp_context' @{ symbol = $symbolId }
    $null = Get-McpToolJson $serve 13 'lurp_diff' @{ from_snapshot = $snapshot1; to_snapshot = $snapshot2 }
    $null = Get-McpToolJson $serve 14 'lurp_timings' @{}
    $null = Get-McpToolJson $serve 15 'lurp_diagnostics' @{ limit = 5 }
    $null = Get-McpToolJson $serve 16 'lurp_dead_candidates' @{ limit = 5 }
    $null = Get-McpToolJson $serve 17 'lurp_refresh' @{}

    $mcpAnnotation = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=annotate', "--symbol=$symbolId", '--annotation-kind=note', '--value=mcp retract smoke', "--output-dir=$DbDir")).Text
    $mcpAnnotationId = $mcpAnnotation.annotation_id
    $mcpAnnotations = Get-McpToolJson $serve 18 'lurp_get_annotations' @{ symbol = $symbolId }
    Assert-True (@($mcpAnnotations.annotations | Where-Object { $_.annotation_id -eq $mcpAnnotationId }).Count -eq 1) 'lurp_get_annotations did not return the created annotation'
    $null = Get-McpToolJson $serve 19 'lurp_retract_annotation' @{ annotation_id = $mcpAnnotationId }
    $afterRetract = Get-McpToolJson $serve 20 'lurp_get_annotations' @{ symbol = $symbolId }
    Assert-True (@($afterRetract.annotations | Where-Object { $_.annotation_id -eq $mcpAnnotationId }).Count -eq 0) 'lurp_retract_annotation did not remove the annotation'
} finally {
    try { $serve.StandardInput.Close() } catch { }
    try { if (-not $serve.WaitForExit(60000)) { $serve.Kill($true) } } catch { }
    $serve.Dispose()
}

if ($SkipKillTest) {
    Write-Host '== kill-stdin-mid-lurp_index: skipped =='
} else {
    Write-Host '== kill-stdin-mid-lurp_index (B5 test 4) =='
    $killDbDir = Join-Path $WorkDir 'kill-db'
    New-Item -ItemType Directory -Path $killDbDir | Out-Null
    $killSolution = Join-Path $RepoRoot 'Lurp.slnx'

    & dotnet restore $killSolution 2>&1 | Out-String | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore $killSolution failed with exit code $LASTEXITCODE" }

    $killIndex = Invoke-Lurp @('--mode=index', "--solution=$killSolution", "--output-dir=$killDbDir", '--strategy=full')
    Assert-True ($killIndex.Text -match 'Index complete for snapshot') 'kill-test baseline index did not complete'

    $killServe = Start-Serve -Solution $killSolution -OutDir $killDbDir -WriteTools
    try {
        Initialize-Mcp $killServe 101
        $start = Get-McpToolJson $killServe 102 'lurp_index' @{ strategy = 'full'; force = $true }
        $operationId = $start.operation_id
        Assert-True (-not [string]::IsNullOrEmpty($operationId)) 'lurp_index did not return an operation id'
        Assert-True ($start.status -eq 'running') "lurp_index did not start running: $($start.status)"

        $sawExtraction = $false
        $deadline = [DateTime]::UtcNow.AddSeconds($KillTestTimeoutSeconds)
        $pollId = 103
        while ([DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 250
            $poll = Get-McpToolJson $killServe $pollId 'lurp_index' @{ operation_id = $operationId }
            $pollId++
            if ($poll.status -ne 'running') { throw "index finished before stdin kill (status=$($poll.status))" }
            if (@($poll.progress | Where-Object { $_ -match '^\s*\[' }).Count -ge 1) { $sawExtraction = $true; break }
        }
        Assert-True $sawExtraction 'never observed extraction progress; cannot claim a mid-run kill'

        $killServe.StandardInput.Close()
        $exited = $killServe.WaitForExit($KillTestTimeoutSeconds * 1000)
        Assert-True $exited 'server did not exit after stdin close'
        Assert-True ($killServe.ExitCode -eq 0) "server exited $($killServe.ExitCode) after stdin close"
    } finally {
        try { $killServe.StandardInput.Close() } catch { }
        try { if (-not $killServe.HasExited) { $killServe.Kill($true) } } catch { }
        $killServe.Dispose()
    }

    $killDbPath = Join-Path $killDbDir 'index.db'
    $stateAfterKill = Get-SnapshotState $killDbPath
    Write-Host "  after kill: failed=$($stateAfterKill.failed) incomplete_unpruned=$($stateAfterKill.incomplete_unpruned)"
    Assert-True (($stateAfterKill.failed + $stateAfterKill.incomplete_unpruned) -ge 1) "cancelled run left no trace in the snapshots table (state: $($stateAfterKill | ConvertTo-Json -Compress))"

    $recover = Invoke-Lurp @('--mode=index', "--solution=$killSolution", "--output-dir=$killDbDir")
    Assert-True ($recover.ExitCode -eq 0) 'index after the stdin kill failed'
    $stateAfterRecover = Get-SnapshotState $killDbPath
    Assert-True ($stateAfterRecover.incomplete_unpruned -eq 0) "unpruned incomplete snapshots remain (state: $($stateAfterRecover | ConvertTo-Json -Compress))"

    $recoverStatus = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=status', "--output-dir=$killDbDir", '--output=json')).Text
    Assert-True (-not [string]::IsNullOrEmpty($recoverStatus.latest_snapshot_id)) 'status after recovery reports no complete snapshot'
    Write-Host "  recovery ok: incomplete_unpruned=0 latest=$($recoverStatus.latest_snapshot_id)"
}

Write-Host 'packed-tool smoke: PASS'

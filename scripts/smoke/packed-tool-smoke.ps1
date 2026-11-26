# Packed-tool smoke test (audit Phase 4 / B4): runs every CLI mode and every MCP
# tool against the packed global tool, then kills stdin mid-`lurp_index` and
# checks the next index recovers (B5 test 4).
#
# Usage:
#   pwsh -File scripts/smoke/packed-tool-smoke.ps1 -Lurp <path-to>/lurp (`lurp.exe` on Windows) -ExpectedVersion <version> -DbDir <fresh-dir>
param(
    [Parameter(Mandatory)][string]$Lurp,
    [Parameter(Mandatory)][string]$DbDir,
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) "lurp-packed-smoke-$([guid]::NewGuid().ToString('N'))"),
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [string]$RepoRoot = '',
    [switch]$SkipKillTest,
    [int]$KillTestTimeoutSeconds = 420,
    # Bounded waits for the WR3/WR4 reproductions: a blocked pipe or a locked
    # output directory must fail its check, not hold the CI job until its timeout.
    [int]$WindowsRestartTimeoutSeconds = 150,
    [int]$StepTimeoutSeconds = 30,
    [int]$StderrResponseTimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path }
$Lurp = (Resolve-Path $Lurp).Path
Import-Module (Join-Path $PSScriptRoot 'LurpSmoke.psm1') -Force -ArgumentList $Lurp
$FixturesDir = Join-Path $RepoRoot 'tests' 'Fixtures' 'Smoke'
$SolutionName = 'Smoke.slnx'

function Start-Serve {
    param([string]$Solution, [string]$OutDir, [switch]$WriteTools)
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Lurp
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    # The MCP stdio transport uses the raw stdin/stdout byte streams as UTF-8
    # (MCP C# SDK 2.2.0 StdioServerTransport), so the redirected streams of the
    # client must be no-BOM UTF-8 too. Drain stderr from the start: an undrained
    # pipe fills on Windows and blocks the server's console logger.
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    $psi.StandardInputEncoding = $utf8NoBom
    $psi.StandardOutputEncoding = $utf8NoBom
    $psi.StandardErrorEncoding = $utf8NoBom
    $psi.ArgumentList.Add('--mode=serve')
    if ($WriteTools) { $psi.ArgumentList.Add('--enable-write-tools') }
    $psi.ArgumentList.Add("--solution=$Solution")
    $psi.ArgumentList.Add("--output-dir=$OutDir")
    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc | Add-Member -NotePropertyName StderrDrain -NotePropertyValue $proc.StandardError.ReadToEndAsync()
    return $proc
}

function Stop-Serve {
    param([System.Diagnostics.Process]$Proc, [int]$TimeoutMs = 60000)
    try { $Proc.StandardInput.Close() } catch { }
    try { if (-not $Proc.WaitForExit($TimeoutMs)) { $Proc.Kill($true) } } catch { }
    $stderr = ''
    try { $stderr = $Proc.StderrDrain.GetAwaiter().GetResult() } catch { }
    $Proc.Dispose()
    return $stderr
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
    param([System.Diagnostics.Process]$Proc, [int]$Id, [int]$TimeoutMs = 60000, [switch]$Raw)
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
        if (($msg.PSObject.Properties.Name -contains 'id') -and ($msg.id -eq $Id)) {
            if ($Raw) { return $line }
            return $msg
        }
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
    param([System.Diagnostics.Process]$Proc, [int]$Id, [string]$Name, $Arguments, [int]$TimeoutMs = 60000)
    Send-McpMessage $Proc 'tools/call' @{ name = $Name; arguments = $Arguments } $Id
    $rawResponse = Read-McpResponse $Proc $Id $TimeoutMs -Raw
    $resp = $rawResponse | ConvertFrom-Json
    if ($resp.PSObject.Properties.Name -contains 'error') {
        throw "tool ${Name} returned error: $($resp.error | ConvertTo-Json -Compress)"
    }
    if (($resp.result.PSObject.Properties.Name -contains 'isError') -and ($resp.result.isError -eq $true)) {
        $errText = if ($resp.result.content -and @($resp.result.content).Count -gt 0) { $resp.result.content[0].text } else { '' }
        throw "tool ${Name} returned isError: $errText"
    }
    $structured = [System.Text.Json.Nodes.JsonNode]::Parse($rawResponse)['result']['structuredContent']
    if ($structured -isnot [System.Text.Json.Nodes.JsonObject]) {
        throw "tool ${Name} did not return a structuredContent object"
    }
    if ($structured.ContainsKey('result')) {
        throw "tool ${Name} returned a legacy result wrapper in structuredContent"
    }
    $text = $resp.result.content[0].text
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "tool ${Name} did not return content[0].text"
    }
    $textPayload = [System.Text.Json.Nodes.JsonNode]::Parse($text)
    if (-not [System.Text.Json.Nodes.JsonNode]::DeepEquals($structured, $textPayload)) {
        throw "tool ${Name} structuredContent differs from content[0].text"
    }
    return $resp.result.structuredContent
}

function Get-Python {
    $py = Get-Command python3 -ErrorAction SilentlyContinue
    if (-not $py) { $py = Get-Command python -ErrorAction SilentlyContinue }
    if (-not $py) { throw 'Python 3 is required by this smoke script.' }
    return $py.Source
}

function Get-SnapshotState([string]$DbPath) {
    $py = Get-Python
    $stateJson = & $py (Join-Path $PSScriptRoot 'snapshot-state.py') $DbPath
    if ($LASTEXITCODE -ne 0) { throw "snapshot-state.py failed with exit code $LASTEXITCODE" }
    return $stateJson | ConvertFrom-Json
}

# WR3 reproduction. Runs only on Windows: a stopped serve must be restartable
# at once and must not leave a process holding the output directory. Every step
# is bounded, so a locked file or a hung child fails the check instead of the job.
function Test-WindowsServeRestart {
    if (-not $IsWindows) {
        Write-Host '== WR3 (Windows only): skipped on this runner =='
        return
    }
    Write-Host '== WR3 (Windows only): stop serve mid-index, restart, delete output dir =='
    $wr3Db = Join-Path $WorkDir 'wr3-db'
    New-Item -ItemType Directory -Path $wr3Db | Out-Null
    $wr3Src = Join-Path $WorkDir 'wr3-src'
    Copy-Item -Recurse $FixturesDir $wr3Src
    $wr3Solution = Join-Path $wr3Src $SolutionName

    & dotnet restore $wr3Solution 2>&1 | Out-String | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) "WR3: dotnet restore failed with exit code $LASTEXITCODE"
    $baseline = Invoke-Lurp @('--mode=index', "--solution=$wr3Solution", "--output-dir=$wr3Db", '--strategy=full')
    Assert-True ($baseline.Text -match 'Index complete for snapshot') 'WR3: baseline index did not complete'

    $first = Start-Serve -Solution $wr3Solution -OutDir $wr3Db -WriteTools
    try {
        Initialize-Mcp $first 401
        $start = Get-McpToolJson $first 402 'lurp_index' @{ strategy = 'full'; force = $true }
        Assert-True (-not [string]::IsNullOrEmpty($start.operation_id)) 'WR3: lurp_index did not return an operation id'

        $sawExtraction = $false
        $deadline = [DateTime]::UtcNow.AddSeconds($WindowsRestartTimeoutSeconds)
        $pollId = 403
        while ([DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 250
            $poll = Get-McpToolJson $first $pollId 'lurp_index' @{ operation_id = $start.operation_id } ($StepTimeoutSeconds * 1000)
            $pollId++
            if ($poll.status -ne 'running') { throw "WR3: index finished before the serve stop (status=$($poll.status))" }
            if (@($poll.progress | Where-Object { $_ -match '^\s*\[' }).Count -ge 1) { $sawExtraction = $true; break }
        }
        Assert-True $sawExtraction 'WR3: never observed extraction progress; cannot claim a mid-run stop'

        $stopWatch = [Diagnostics.Stopwatch]::StartNew()
        try { $first.Kill($true) } catch { }
        Assert-True $first.WaitForExit($StepTimeoutSeconds * 1000) "WR3: serve did not stop within $StepTimeoutSeconds s after Kill"
        Write-Host "  stop: $($stopWatch.ElapsedMilliseconds) ms"
    } finally {
        try { if (-not $first.HasExited) { $first.Kill($true) } } catch { }
        $null = Stop-Serve $first -TimeoutMs ($StepTimeoutSeconds * 1000)
    }

    $restart = Start-Serve -Solution $wr3Solution -OutDir $wr3Db -WriteTools
    try {
        $restartWatch = [Diagnostics.Stopwatch]::StartNew()
        Initialize-Mcp $restart 411
        $restartStatus = Get-McpToolJson $restart 412 'lurp_status' @{} ($StepTimeoutSeconds * 1000)
        Assert-True (-not [string]::IsNullOrEmpty($restartStatus.snapshot_id)) 'WR3: restarted serve did not report a snapshot'
        Write-Host "  restart: $($restartWatch.ElapsedMilliseconds) ms"
    } finally {
        $null = Stop-Serve $restart -TimeoutMs ($StepTimeoutSeconds * 1000)
    }

    # A surviving writer leaves index.db (or a lock file) open on Windows: the
    # delete then fails or hangs. Run it in a job so the step has a time limit.
    $deleteWatch = [Diagnostics.Stopwatch]::StartNew()
    $deleteJob = Start-Job -ScriptBlock { param($path) [IO.Directory]::Delete($path, $true) } -ArgumentList $wr3Db
    try {
        if (-not (Wait-Job $deleteJob -Timeout $StepTimeoutSeconds)) {
            throw "deleting the output directory took more than $StepTimeoutSeconds s"
        }
        $null = Receive-Job $deleteJob -ErrorAction Stop
    } catch {
        throw "WR3: the output directory could not be deleted: $($_.Exception.Message)"
    } finally {
        Remove-Job $deleteJob -Force -ErrorAction SilentlyContinue
    }
    Assert-True (-not (Test-Path $wr3Db)) 'WR3: the output directory still exists after delete'
    Write-Host "  delete: $($deleteWatch.ElapsedMilliseconds) ms"
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

Write-Host '== model-view smoke =='
$python = Get-Python
$generator = Join-Path $RepoRoot 'scripts' 'model-view' 'generate.py'
$modelViewInput = Join-Path $PSScriptRoot 'model-view-input.py'
$indexLog = Join-Path $WorkDir 'index-run.log'
$modelView = Join-Path $WorkDir 'model-view.html'
Set-Content -LiteralPath $indexLog -Value $index1.Text -Encoding utf8NoBOM
& $python $generator --db $dbPath --index-log $indexLog --out $modelView
Assert-True ($LASTEXITCODE -eq 0) "model-view render expected exit 0, got $LASTEXITCODE"
$page = Get-Content -LiteralPath $modelView -Raw -Encoding utf8
$edgeCount = & $python $modelViewInput edge-count $dbPath $snapshot1
Assert-True ($LASTEXITCODE -eq 0) "edge-count expected exit 0, got $LASTEXITCODE"
$edgeTotalPattern = '<tr class="total-row"><td>Total typed edges</td><td class="num">([\d,]+)</td>'
$pageEdgeCount = [regex]::Match($page, $edgeTotalPattern).Groups[1].Value.Replace(',', '')
Assert-True ($pageEdgeCount -ceq $edgeCount) "model-view edge total expected '$edgeCount', got '$pageEdgeCount'"
$pageSnapshot = [regex]::Match($page, 'Snapshot <code>([^<]+)</code>').Groups[1].Value
Assert-True ($pageSnapshot -ceq $snapshot1) "model-view snapshot expected '$snapshot1', got '$pageSnapshot'"
$projectHeader = [regex]::Match($page, '<th class="num">Edges extracted</th>').Value
Assert-True ($projectHeader -ne '') "model-view expected a per-project table, got no 'Edges extracted' column"

$noEdgesDb = Join-Path $WorkDir 'no-edges.db'
$noEdgesView = Join-Path $WorkDir 'model-view-no-edges.html'
& $python $modelViewInput no-edges $dbPath $noEdgesDb $snapshot1
Assert-True ($LASTEXITCODE -eq 0) "no-edges copy expected exit 0, got $LASTEXITCODE"
& $python $generator --db $noEdgesDb --out $noEdgesView
Assert-True ($LASTEXITCODE -eq 0) "no-edges render expected exit 0, got $LASTEXITCODE"
$emptyPage = Get-Content -LiteralPath $noEdgesView -Raw -Encoding utf8
$emptyTotal = [regex]::Match($emptyPage, $edgeTotalPattern).Groups[1].Value
Assert-True ($emptyTotal -ceq '0') "no-edges model-view total expected '0', got '$emptyTotal'"
$evidenceBody = [regex]::Match($emptyPage, '<th>Evidence tier</th>.*?<tbody>(.*?)</tbody>', 'Singleline').Groups[1].Value
$shares = @([regex]::Matches($evidenceBody, '<tr>.*?<td class="num">([^<]*)</td></tr>') | ForEach-Object { $_.Groups[1].Value })
$tierCount = [regex]::Matches($page, '<div class="ladder-tier ').Count
Assert-True ($tierCount -gt 0 -and $shares.Count -eq $tierCount) "no-edges model-view expected $tierCount share cells (one per tier), got $($shares.Count)"
foreach ($share in $shares) {
    Assert-True ($share -ceq '0.0%') "no-edges model-view share expected '0.0%', got '$share'"
}

$externalDb = Join-Path $WorkDir 'external-file.db'
$externalView = Join-Path $WorkDir 'model-view-external-file.html'
& $python $modelViewInput external-file $dbPath $externalDb $snapshot1 "$('../' * 40)home/someone/packages/External.cs"
Assert-True ($LASTEXITCODE -eq 0) "external-file copy expected exit 0, got $LASTEXITCODE"
& $python $generator --db $externalDb --out $externalView
Assert-True ($LASTEXITCODE -eq 0) "external-file render expected exit 0, got $LASTEXITCODE"
$externalPage = Get-Content -LiteralPath $externalView -Raw -Encoding utf8
Assert-True ($externalPage.Contains('&lt;external&gt;/External.cs')) 'model-view expected a file outside the repository as <external>/External.cs'
Assert-True (-not $externalPage.Contains('home/someone')) 'model-view shows the local folder of a file outside the repository'

$provenanceHelp = [regex]::Match($help.Text, '(?m)^ {4}--provenance=[^\r\n]*(?:\r?\n {5,}[^\r\n]*)*').Value
$expectedLevels = [regex]::Match($provenanceHelp, 'Values:\s+([^.]+)\.').Groups[1].Value -replace '\s+', ''
$pageLevels = [regex]::Matches($page, '<div class="ladder-tier tier-([a-z_]+)[ "]') |
    ForEach-Object { $_.Groups[1].Value }
$actualLevels = $pageLevels -join ','
Assert-True ($expectedLevels -ne '' -and $actualLevels -ceq $expectedLevels) "model-view ladder expected '$expectedLevels' from --help, got '$actualLevels'"
$capsuleGrade = [regex]::Match($page, '<p class="capsule-grade">\s*<code>([^<]+)</code> is a capsule grade, never an edge').Groups[1].Value
Assert-True ($capsuleGrade -ceq 'global_implementation_relation') "model-view capsule grade expected 'global_implementation_relation', got '$capsuleGrade'"

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

$null = Invoke-Lurp @('--mode=retract-annotation', "--annotation-id=$annotationId", "--output-dir=$DbDir")
$annotationsAfterRetract = ConvertFrom-LurpJson (Invoke-Lurp @('--mode=get-annotations', "--symbol=$symbolId", "--output-dir=$DbDir", '--output=json', '--quiet')).Text
Assert-True (@($annotationsAfterRetract.annotations | Where-Object { $_.annotation_id -eq $annotationId }).Count -eq 0) 'retract-annotation did not remove the annotation'

$null = Invoke-Lurp @('--mode=pin-snapshot', "--snapshot=$snapshot1", "--output-dir=$DbDir")
$null = Invoke-Lurp @('--mode=pin-snapshot', '--clear', "--output-dir=$DbDir")

Add-Content (Join-Path $srcDir 'App' 'Lib.cs') '// smoke edit'
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
    $null = Stop-Serve $serve
}

# Run both reproductions even when one fails: the CI log must show the result
# of WR3 as well as WR4 before any fix is applied.
$reproFailures = @()
try {
    Write-Host '== WR4: Unicode and a full stderr pipe over MCP =='
    $pipeServe = Start-Serve -Solution $solutionPath -OutDir $DbDir -WriteTools
    $pipeStderr = ''
    try {
        Initialize-Mcp $pipeServe 201

        # Both directions carry non-ASCII text: the symbol argument is written to
        # stdin and the source line comes back on stdout. With the present default
        # redirected-stream encoding on Windows neither round trip is exact.
        $mcpMarker = Get-McpToolJson $pipeServe 202 'lurp_find_symbol' @{ symbol = 'Smoke.App.Café.Marker' }
        Assert-True ($mcpMarker.symbol_id -clike 'F:Smoke.App.Café.Marker*') "find-symbol over MCP lost the Unicode symbol: $($mcpMarker.symbol_id)"
        $mcpMarkerSource = Get-McpToolJson $pipeServe 203 'lurp_get_source' @{ document = 'App/Unicode.cs' }
        $markerLine = @($mcpMarkerSource.source -split "`r?`n" | Where-Object { $_.Contains('public const string Marker') })
        Assert-True ($markerLine.Count -eq 1 -and $markerLine[0] -ceq '    public const string Marker = "Café — Δ";') "get-source over MCP did not return the exact text Café — Δ: $($mcpMarkerSource.source)"

        # The MCP host logs every tool call to stderr at Trace level. Start-Serve
        # drains that pipe asynchronously; with the old undrained pipe the logger
        # blocked after about 4 KB on Windows. Every response has a bounded wait.
        $floodCalls = 60
        $floodId = 210
        for ($i = 0; $i -lt $floodCalls; $i++) {
            Send-McpMessage $pipeServe 'tools/call' @{ name = 'lurp_get_symbol'; arguments = @{} } $floodId
            $null = Read-McpResponse $pipeServe $floodId ($StderrResponseTimeoutSeconds * 1000)
            $floodId++
        }
        Send-McpMessage $pipeServe 'tools/list' @{} $floodId
        $null = Read-McpResponse $pipeServe $floodId ($StderrResponseTimeoutSeconds * 1000)
    } finally {
        $pipeStderr = Stop-Serve $pipeServe -TimeoutMs ($StepTimeoutSeconds * 1000)
    }
    $stderrBytes = [Text.Encoding]::UTF8.GetByteCount($pipeStderr)
    Write-Host "  stderr from serve: $stderrBytes byte(s)"
    Assert-True ($stderrBytes -gt 16384) "serve wrote $stderrBytes byte(s) of stderr, expected more than 16384"
} catch {
    $reproFailures += "WR4: $($_.Exception.Message)"
}
try {
    Test-WindowsServeRestart
} catch {
    $reproFailures += "WR3: $($_.Exception.Message)"
}
if ($reproFailures.Count -gt 0) {
    throw "Windows reproduction(s) failed:`n$($reproFailures -join "`n")"
}

$runModes = @((Get-LurpModesRun) + 'serve' | Sort-Object -Unique)
$helpModes = @(Get-LurpHelpModes | Sort-Object -Unique)
$missingModes = @($helpModes | Where-Object { $runModes -notcontains $_ })
$extraModes = @($runModes | Where-Object { $helpModes -notcontains $_ })
Assert-True ($missingModes.Count -eq 0 -and $extraModes.Count -eq 0) "CLI modes covered mismatch: missing=[$($missingModes -join ', ')] extra=[$($extraModes -join ', ')]"

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
        try { if (-not $killServe.HasExited) { $killServe.Kill($true) } } catch { }
        $null = Stop-Serve $killServe -TimeoutMs ($KillTestTimeoutSeconds * 1000)
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

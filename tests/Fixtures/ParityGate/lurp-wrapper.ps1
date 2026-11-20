$ErrorActionPreference = 'Stop'

# PowerShell splits an argument such as --symbol=T:Name at the colon.
# Read the original arguments so the wrapper passes each tool argument as received.
$argv = [Environment]::GetCommandLineArgs()
$scriptIndex = [Array]::IndexOf($argv, '-File') + 1
$commandIndex = [Array]::IndexOf($argv, '-Tool', $scriptIndex + 1)
$failMode = $null
$replaceFile = $null
for ($i = $scriptIndex + 1; $i -lt $commandIndex; $i += 2) {
    switch ($argv[$i]) {
        '-FailMode' { $failMode = $argv[$i + 1] }
        '-ReplaceFile' { $replaceFile = $argv[$i + 1] }
        default { throw "Unknown wrapper option $($argv[$i])" }
    }
}

$tool = $argv[$commandIndex + 1]
$toolArgs = @($argv | Select-Object -Skip ($commandIndex + 2))
if ($failMode -and $toolArgs -contains "--mode=$failMode") {
    [Console]::Error.WriteLine('forced failure')
    exit 1
}

$scratch = $null
try {
    if ($replaceFile -and $toolArgs -contains '--mode=index') {
        $relativePath, $replacement = $replaceFile -split '=', 2
        $solutionArg = $toolArgs | Where-Object { $_.StartsWith('--solution=') } | Select-Object -First 1
        $solutionIndex = [Array]::IndexOf($toolArgs, $solutionArg)
        $solution = $solutionArg.Substring('--solution='.Length)
        $scratch = Join-Path ([IO.Path]::GetTempPath()) ('lurp-recall-fixture-' + [Guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath (Split-Path -Parent $solution) -Destination $scratch -Recurse
        Copy-Item -LiteralPath $replacement -Destination (Join-Path $scratch $relativePath) -Force
        $toolArgs[$solutionIndex] = '--solution=' + (Join-Path $scratch (Split-Path -Leaf $solution))
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $tool
    $startInfo.UseShellExecute = $false
    foreach ($arg in $toolArgs) {
        $startInfo.ArgumentList.Add($arg)
    }
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $process.WaitForExit()
        exit $process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}
finally {
    if ($scratch -and (Test-Path -LiteralPath $scratch)) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}

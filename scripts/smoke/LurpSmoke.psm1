# scripts/smoke/LurpSmoke.psm1
#
# Shared smoke-test helper. The scripts import it with
#   Import-Module (Join-Path $PSScriptRoot 'LurpSmoke.psm1') -ArgumentList $Lurp
# because a module function runs in the module's session state and cannot see
# the caller's $Lurp; -ArgumentList binds the tool path to the module's param.
param([string]$Lurp)

$script:ModesRun = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "ASSERT FAILED: $Message" }
}

function Get-JsonText([string]$Text) {
    $start = $Text.IndexOf('{')
    if ($start -lt 0) { throw "no JSON object in output:`n$Text" }
    return $Text.Substring($start)
}

function ConvertFrom-LurpJson([string]$Text) {
    return (Get-JsonText $Text) | ConvertFrom-Json
}

function Invoke-Lurp {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$AllowFailure)
    $all = & $Lurp @Arguments 2>&1
    $code = $LASTEXITCODE
    $text = ($all | Out-String)
    if (-not $AllowFailure -and $code -ne 0) {
        throw "lurp $($Arguments -join ' ') exited $code`n$text"
    }
    if ($code -eq 0) {
        foreach ($arg in $Arguments) {
            if ($arg -match '^--mode=(.+)$') {
                [void]$script:ModesRun.Add($Matches[1])
            }
        }
    }
    return [pscustomobject]@{ ExitCode = $code; Text = $text }
}

function Get-LurpModesRun {
    return @($script:ModesRun | Sort-Object -Unique)
}

function Get-LurpHelpModes {
    $help = Invoke-Lurp @('--help')
    $modes = @()
    $inModesBlock = $false
    foreach ($rawLine in ($help.Text -split "`n")) {
        $line = $rawLine.TrimEnd("`r")
        if ($line.Trim() -eq 'MODES') {
            $inModesBlock = $true
            continue
        }
        if (-not $inModesBlock) { continue }
        if ($line.Trim() -eq 'OPTIONS BY MODE') { break }
        $trimmed = $line.TrimStart()
        if (-not $trimmed.StartsWith('--mode=', [System.StringComparison]::Ordinal)) { continue }
        $name = $trimmed.Substring('--mode='.Length)
        $space = $name.IndexOf(' ')
        if ($space -ge 0) { $name = $name.Substring(0, $space) }
        $modes += $name
    }
    return @($modes)
}

Export-ModuleMember -Function Invoke-Lurp, Assert-True, ConvertFrom-LurpJson, Get-LurpModesRun, Get-LurpHelpModes

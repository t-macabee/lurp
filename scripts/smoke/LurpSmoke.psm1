# scripts/smoke/LurpSmoke.psm1
#
# Shared smoke-test helper. The scripts import it with
#   Import-Module (Join-Path $PSScriptRoot 'LurpSmoke.psm1') -ArgumentList $Lurp
# because a module function runs in the module's session state and cannot see
# the caller's $Lurp; -ArgumentList binds the tool path to the module's param.
param([string]$Lurp)

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

Export-ModuleMember -Function Invoke-Lurp

# Tests for scripts/ci/LurpCi.psm1.
#
# A plain script (Pester 3.4 in this environment cannot run modern syntax), not
# a Pester suite: it runs every case, prints PASS/FAIL, and exits 1 if any case
# failed. Covered are the module's pure functions: the vulnerable-package
# parser, the nupkg digest check and the commit-SHA validator.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LurpCi.psm1') -Force

$script:Passed = 0
$script:Failed = 0

function Test-Case([string]$Name, [scriptblock]$Body) {
  try {
    & $Body
    Write-Host "PASS $Name"
    $script:Passed++
  } catch {
    Write-Host "FAIL ${Name}: $($_.Exception.Message)"
    $script:Failed++
  }
}

function Assert-Equal($Expected, $Actual, $What) {
  if ($Expected -ne $Actual) {
    throw "${What}: expected '$Expected', got '$Actual'"
  }
}

function Assert-Throws([scriptblock]$Body, [string]$MessagePart) {
  $threw = $false
  $message = ''
  try {
    & $Body
  } catch {
    $threw = $true
    $message = $_.Exception.Message
  }
  if (-not $threw) {
    throw "expected an exception containing '$MessagePart', but none was thrown"
  }
  if ($message -notlike "*$MessagePart*") {
    throw "expected message containing '$MessagePart', got '$message'"
  }
}

function New-TempDirectory {
  $dir = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $dir | Out-Null
  return $dir
}

function Write-FakeNupkg([string]$Directory, [string]$Name = 'lurp.9.9.9.nupkg') {
  [byte[]]$bytes = @(1, 2, 3)
  $path = Join-Path $Directory $Name
  [IO.File]::WriteAllBytes($path, $bytes)
  return (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

Test-Case 'Get-VulnerablePackageHits reports the vulnerable transitive package' {
  $dir = New-TempDirectory
  try {
    $json = '{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"Good","resolvedVersion":"2.0.0"}],"transitivePackages":[{"id":"Bad.Package","resolvedVersion":"1.0.0","vulnerabilities":[{"severity":"High"}]}]}]}]}'
    $hits = @(Get-VulnerablePackageHits $json)
    Assert-Equal 'Bad.Package 1.0.0' ($hits -join '|') 'hit list'
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Get-VulnerablePackageHits tolerates a missing transitivePackages property' {
  $dir = New-TempDirectory
  try {
    $json = '{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"Good","resolvedVersion":"2.0.0"}]}]}]}'
    $hits = @(Get-VulnerablePackageHits $json)
    Assert-Equal 0 $hits.Count 'hit count'
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Assert-NupkgDigest returns Path, Name and Digest for a matching nupkg' {
  $dir = New-TempDirectory
  try {
    $hash = Write-FakeNupkg $dir
    Set-Content -Path (Join-Path $dir 'SHA256SUMS.txt') -Value "$hash  lurp.9.9.9.nupkg" -Encoding ascii
    $pkg = Assert-NupkgDigest $dir
    Assert-Equal 'lurp.9.9.9.nupkg' $pkg.Name 'name'
    Assert-Equal $hash $pkg.Digest 'digest'
    Assert-Equal (Join-Path $dir 'lurp.9.9.9.nupkg') $pkg.Path 'path'
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Assert-NupkgDigest throws on a digest mismatch' {
  $dir = New-TempDirectory
  try {
    $null = Write-FakeNupkg $dir
    Set-Content -Path (Join-Path $dir 'SHA256SUMS.txt') -Value "$('0' * 64)  lurp.9.9.9.nupkg" -Encoding ascii
    Assert-Throws { Assert-NupkgDigest $dir } 'SHA-256 mismatch'
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Assert-NupkgDigest throws on a name mismatch' {
  $dir = New-TempDirectory
  try {
    $hash = Write-FakeNupkg $dir
    Set-Content -Path (Join-Path $dir 'SHA256SUMS.txt') -Value "$hash  lurp.9.9.8.nupkg" -Encoding ascii
    Assert-Throws { Assert-NupkgDigest $dir } 'name mismatch'
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Assert-NupkgDigest throws when there is more than one nupkg' {
  $dir = New-TempDirectory
  try {
    $hash = Write-FakeNupkg $dir
    Set-Content -Path (Join-Path $dir 'SHA256SUMS.txt') -Value "$hash  lurp.9.9.9.nupkg" -Encoding ascii
    $null = Write-FakeNupkg $dir 'lurp.9.9.8.nupkg'
    Assert-Throws { Assert-NupkgDigest $dir } 'exactly one'
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Assert-NupkgDigest throws when SHA256SUMS.txt is missing' {
  $dir = New-TempDirectory
  try {
    $null = Write-FakeNupkg $dir
    Assert-Throws { Assert-NupkgDigest $dir } 'SHA256SUMS.txt'
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Test-CommitSha accepts a full SHA and rejects everything else' {
  Assert-Equal $true (Test-CommitSha ('a' * 40)) '40 lowercase hex'
  Assert-Equal $false (Test-CommitSha 'abc') 'too short'
  Assert-Equal $false (Test-CommitSha (('a' * 39) + 'g')) 'non-hex character'
  Assert-Equal $false (Test-CommitSha $null) 'null'
}

$script:RealCatalogJson = '{"id":"lurp","version":"2.0.0","repository":{"type":"git","url":"https://github.com/t-macabee/lurp","commit":"7188578283a0a1848cbb7a01b4c46af5cd6fae5c"}}'
$script:RealCommit = '7188578283a0a1848cbb7a01b4c46af5cd6fae5c'
$script:TagCommit = 'b63a4ca895b317025330febfe002b35f17403ac1'

Test-Case 'Get-CatalogCommitDecision matches the catalog commit' {
  Assert-Equal 'match' (Get-CatalogCommitDecision -CatalogJson $script:RealCatalogJson -ExpectedCommit $script:RealCommit) 'decision'
}

Test-Case 'Get-CatalogCommitDecision reports the tag commit as a mismatch (B89)' {
  Assert-Equal 'mismatch' (Get-CatalogCommitDecision -CatalogJson $script:RealCatalogJson -ExpectedCommit $script:TagCommit) 'decision'
}

Test-Case 'Get-CatalogCommitDecision reports a catalog entry without repository as a mismatch' {
  Assert-Equal 'mismatch' (Get-CatalogCommitDecision -CatalogJson '{"id":"lurp","version":"2.0.0"}' -ExpectedCommit $script:RealCommit) 'decision'
}

Test-Case 'Get-HttpRetryDecision classifies status codes' {
  Assert-Equal 'ok' (Get-HttpRetryDecision 200) '200'
  foreach ($code in @(404, 408, 429, 500, 503, 0)) {
    Assert-Equal 'retry' (Get-HttpRetryDecision $code) "$code"
  }
  foreach ($code in @(400, 401, 403)) {
    Assert-Equal 'error' (Get-HttpRetryDecision $code) "$code"
  }
}

Test-Case 'Test-ParityPass accepts a success status with the same digest only' {
  $digest = 'a' * 64
  $status = [pscustomobject]@{ state = 'success'; description = "nupkg sha256=$digest" }
  Assert-Equal $true (Test-ParityPass $status $digest) 'same digest'
  Assert-Equal $false (Test-ParityPass $status ('b' * 64)) 'other digest'
  Assert-Equal $false (Test-ParityPass ([pscustomobject]@{ state = 'failure'; description = "nupkg sha256=$digest" }) $digest) 'failure state'
  Assert-Equal $false (Test-ParityPass $null $digest) 'no status'
}

Test-Case 'Select-ParityStatus returns the newest real-parity status' {
  $digest = 'a' * 64
  $json = '[{"context":"other","state":"success","description":"x"},{"context":"real-parity","state":"failure","description":"real-parity failed; see the run"},{"context":"real-parity","state":"success","description":"nupkg sha256=' + $digest + '"}]'
  Assert-Equal 'failure' (Select-ParityStatus $json).state 'newest state'
  Assert-Equal $null (Select-ParityStatus '[]') 'empty list'
}

Test-Case 'Test-VersionListed matches a listed version and is case-insensitive' {
  Assert-Equal $true (Test-VersionListed -IndexJson '{"versions":["1.1.0","1.4.0","2.0.0"]}' -Version '2.0.0') 'listed'
  Assert-Equal $false (Test-VersionListed -IndexJson '{"versions":["1.1.0","1.4.0","2.0.0"]}' -Version '3.0.0') 'not listed'
  Assert-Equal $true (Test-VersionListed -IndexJson '{"versions":["2.0.0-rc"]}' -Version '2.0.0-RC') 'case'
}

Test-Case 'Get-NuspecCommit reads repository.commit from the nuspec in the nupkg' {
  $dir = New-TempDirectory
  try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = Join-Path $dir 'lurp.1.0.0.nupkg'
    $archive = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
      $entry = $archive.CreateEntry('lurp.nuspec')
      $writer = New-Object IO.StreamWriter($entry.Open())
      try {
        $writer.Write('<package><metadata><repository type="git" commit="abc" /></metadata></package>')
      } finally {
        $writer.Dispose()
      }
    } finally {
      $archive.Dispose()
    }
    Assert-Equal 'abc' (Get-NuspecCommit $zipPath) 'commit'
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Resolve-ReleaseTarget takes the tag from a push' {
  $target = Resolve-ReleaseTarget -EventName 'push' -RefName 'v3.0.0' -RefInput '' -DryRunInput ''
  Assert-Equal 'v3.0.0' $target.Tag 'tag'
  Assert-Equal 'v3.0.0' $target.Ref 'ref'
  Assert-Equal $false $target.DryRun 'dry run'
}

Test-Case 'Resolve-ReleaseTarget takes a tag and dry run from a dispatch' {
  $target = Resolve-ReleaseTarget -EventName 'workflow_dispatch' -RefName '' -RefInput 'v3.0.0' -DryRunInput 'true'
  Assert-Equal 'v3.0.0' $target.Tag 'tag'
  Assert-Equal $true $target.DryRun 'dry run'
}

Test-Case 'Resolve-ReleaseTarget allows a dry run on a commit with no tag' {
  $target = Resolve-ReleaseTarget -EventName 'workflow_dispatch' -RefName '' -RefInput $script:RealCommit -DryRunInput 'true'
  Assert-Equal '' $target.Tag 'tag'
  Assert-Equal $true $target.DryRun 'dry run'
}

Test-Case 'Resolve-ReleaseTarget refuses a real publish on a commit with no tag' {
  Assert-Throws { Resolve-ReleaseTarget -EventName 'workflow_dispatch' -RefName '' -RefInput $script:RealCommit -DryRunInput 'false' } 'needs a tag'
}

Test-Case 'Resolve-ReleaseTarget refuses a ref that is neither a tag nor a SHA' {
  Assert-Throws { Resolve-ReleaseTarget -EventName 'workflow_dispatch' -RefName '' -RefInput 'main' -DryRunInput 'true' } 'vX.Y.Z or a full commit SHA'
}

$script:Queue = @()
$script:CallIndex = 0
$script:CallUrls = New-Object System.Collections.ArrayList

# Each call takes the next queued response; the last one repeats.
$script:FakeRequest = {
  param($Url)
  [void]$script:CallUrls.Add($Url)
  $index = [Math]::Min($script:CallIndex, $script:Queue.Count - 1)
  $script:CallIndex++
  return $script:Queue[$index]
}

function Set-FakeResponses([object[]]$Responses) {
  $script:Queue = @($Responses)
  $script:CallIndex = 0
  $script:CallUrls.Clear()
}

function New-FakeResponse([int]$Status, [string]$Content = '') {
  return [pscustomobject]@{ Status = $Status; Content = $Content }
}

$script:LeafJson = '{"catalogEntry":"https://example.test/catalog.json"}'

Test-Case 'Wait-CatalogCommit returns on a matching catalog entry' {
  Set-FakeResponses @((New-FakeResponse 200 $script:LeafJson), (New-FakeResponse 200 $script:RealCatalogJson))
  Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -IntervalSeconds 0 -Request $script:FakeRequest
  Assert-Equal 2 $script:CallUrls.Count 'calls'
}

Test-Case 'Wait-CatalogCommit throws at once on a commit mismatch and does not retry (B89)' {
  Set-FakeResponses @((New-FakeResponse 200 $script:LeafJson), (New-FakeResponse 200 $script:RealCatalogJson))
  Assert-Throws { Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:TagCommit -TimeoutMinutes 0 -IntervalSeconds 0 -Request $script:FakeRequest } 'does not match'
  Assert-Equal 2 $script:CallUrls.Count 'calls'
}

Test-Case 'Wait-CatalogCommit retries a 404 and then matches' {
  Set-FakeResponses @((New-FakeResponse 404), (New-FakeResponse 200 $script:LeafJson), (New-FakeResponse 200 $script:RealCatalogJson))
  Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -IntervalSeconds 0 -Request $script:FakeRequest
  Assert-Equal 3 $script:CallUrls.Count 'calls'
}

Test-Case 'Wait-CatalogCommit gives up after the timeout when the entry never appears' {
  Set-FakeResponses @((New-FakeResponse 404))
  Assert-Throws { Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -TimeoutMinutes 0 -IntervalSeconds 0 -Request $script:FakeRequest } 'not available after'
}

Test-Case 'Wait-CatalogCommit fails at once on a 403' {
  Set-FakeResponses @((New-FakeResponse 403))
  Assert-Throws { Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -IntervalSeconds 0 -Request $script:FakeRequest } 'nuget.org request failed'
  Assert-Equal 1 $script:CallUrls.Count 'calls'
}

Write-Host "$($script:Passed) passed, $($script:Failed) failed"
if ($script:Failed -gt 0) { exit 1 }
exit 0

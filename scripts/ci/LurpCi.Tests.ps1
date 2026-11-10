# Tests for scripts/ci/LurpCi.psm1.
#
# A plain script (Pester 3.4 in this environment cannot run modern syntax), not
# a Pester suite: it runs every case, prints PASS/FAIL, and exits 1 if any case
# failed. Covers the module's logic. The file and zip functions run against real
# temp files; Wait-CatalogCommit and Test-NuGetVersionListed run against a fake
# request and a fake clock; Invoke-NuGetRequest runs against a local HTTP server.
# The functions that call gh or dotnet are not tested.
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
  if ($null -eq $Expected -or $null -eq $Actual) {
    if ($null -ne $Expected -or $null -ne $Actual) {
      throw "${What}: expected '$Expected', got '$Actual'"
    }
    return
  }
  if ($Expected.GetType() -ne $Actual.GetType()) {
    throw "${What}: expected [$($Expected.GetType().Name)] '$Expected', got [$($Actual.GetType().Name)] '$Actual'"
  }
  if ($Expected -cne $Actual) {
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
  if (-not $message.Contains($MessagePart)) {
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
  # SHA-256 of bytes 1, 2, 3.
  return '039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81'
}

function New-FakeNupkgWithEntry([string]$Directory, [string]$EntryName, [string]$Content) {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zipPath = Join-Path $Directory 'lurp.1.0.0.nupkg'
  $archive = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
  try {
    $entry = $archive.CreateEntry($EntryName)
    $writer = New-Object IO.StreamWriter($entry.Open())
    try {
      $writer.Write($Content)
    } finally {
      $writer.Dispose()
    }
  } finally {
    $archive.Dispose()
  }
  return $zipPath
}

Test-Case 'Get-VulnerablePackageHits reports the vulnerable transitive package' {
  $json = '{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"Good","resolvedVersion":"2.0.0"}],"transitivePackages":[{"id":"Bad.Package","resolvedVersion":"1.0.0","vulnerabilities":[{"severity":"High"}]}]}]}]}'
  $hits = @(Get-VulnerablePackageHits $json)
  Assert-Equal 'Bad.Package 1.0.0' ($hits -join '|') 'hit list'
}

Test-Case 'Get-VulnerablePackageHits tolerates a missing transitivePackages property' {
  $json = '{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"Good","resolvedVersion":"2.0.0"}]}]}]}'
  $hits = @(Get-VulnerablePackageHits $json)
  Assert-Equal 0 $hits.Count 'hit count'
}

Test-Case 'Get-VulnerablePackageHits returns zero hits for a clean result without frameworks' {
  $json = '{"version":1,"projects":[{"path":"a.csproj"},{"path":"b.csproj"}]}'
  $hits = @(Get-VulnerablePackageHits $json)
  Assert-Equal 0 $hits.Count 'hit count'
}

Test-Case 'Get-VulnerablePackageHits throws when there is no projects property' {
  Assert-Throws { Get-VulnerablePackageHits '{}' } 'dotnet package list output has no projects'
}

Test-Case 'Get-VulnerablePackageHits throws when the projects list is empty' {
  Assert-Throws { Get-VulnerablePackageHits '{"version":1,"projects":[]}' } 'dotnet package list output has no projects'
}

Test-Case 'Get-VulnerablePackageHits throws when the input is empty' {
  Assert-Throws { Get-VulnerablePackageHits '' } 'dotnet package list returned no output'
}

Test-Case 'Get-VulnerablePackageHits throws when the input is whitespace' {
  Assert-Throws { Get-VulnerablePackageHits '   ' } 'dotnet package list returned no output'
}

Test-Case 'Get-VulnerablePackageHits throws when the input is not JSON' {
  Assert-Throws { Get-VulnerablePackageHits '<html>' } 'dotnet package list output is not JSON'
}

Test-Case 'Get-VulnerablePackageHits reports a vulnerable top-level package' {
  $json = '{"version":1,"projects":[{"path":"a.csproj","frameworks":[{"framework":"net10.0","topLevelPackages":[{"id":"X","resolvedVersion":"1.0.0","vulnerabilities":[{"severity":"High"}]}]}]}]}'
  $hits = @(Get-VulnerablePackageHits $json)
  Assert-Equal 'X 1.0.0' ($hits -join '|') 'hit list'
}

Test-Case 'Get-VulnerablePackageHits ignores a package with an empty vulnerabilities array' {
  $json = '{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"Clean","resolvedVersion":"1.0.0","vulnerabilities":[]}]}]}]}'
  $hits = @(Get-VulnerablePackageHits $json)
  Assert-Equal 0 $hits.Count 'hit count'
}

Test-Case 'Get-VulnerablePackageHits handles multi-line dotnet output' {
  $jsonLines = @(
    '{',
    '  "projects": [',
    '    {',
    '      "frameworks": [',
    '        {',
    '          "transitivePackages": [',
    '            { "id": "Bad.Package", "resolvedVersion": "1.0.0", "vulnerabilities": [ { "severity": "High" } ] }',
    '          ]',
    '        }',
    '      ]',
    '    }',
    '  ]',
    '}'
  )
  $hits = @(Get-VulnerablePackageHits $jsonLines)
  Assert-Equal 'Bad.Package 1.0.0' ($hits -join '|') 'hit list'
}

Test-Case 'Get-VulnerablePackageHits collects hits from every project and framework in order' {
  $json = '{"projects":[{"path":"a.csproj","frameworks":[{"framework":"net10.0","topLevelPackages":[{"id":"A","resolvedVersion":"1.0.0","vulnerabilities":[{"severity":"High"}]}]},{"framework":"net8.0","topLevelPackages":[{"id":"B","resolvedVersion":"2.0.0","vulnerabilities":[{"severity":"High"}]}]}]},{"path":"b.csproj","frameworks":[{"framework":"net10.0","topLevelPackages":[{"id":"C","resolvedVersion":"3.0.0","vulnerabilities":[{"severity":"High"}]}]},{"framework":"net8.0","topLevelPackages":[{"id":"D","resolvedVersion":"4.0.0","vulnerabilities":[{"severity":"High"}]}]}]}]}'
  $hits = @(Get-VulnerablePackageHits $json)
  Assert-Equal 'A 1.0.0|B 2.0.0|C 3.0.0|D 4.0.0' ($hits -join '|') 'hit list'
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
    $hash = Write-FakeNupkg $dir
    Set-Content -Path (Join-Path $dir 'SHA256SUMS.txt') -Value "$('0' * 64)  lurp.9.9.9.nupkg" -Encoding ascii
    Assert-Throws { Assert-NupkgDigest $dir } "SHA-256 mismatch: expected $('0' * 64), got $hash"
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Assert-NupkgDigest throws on a name mismatch' {
  $dir = New-TempDirectory
  try {
    $hash = Write-FakeNupkg $dir
    Set-Content -Path (Join-Path $dir 'SHA256SUMS.txt') -Value "$hash  lurp.9.9.8.nupkg" -Encoding ascii
    Assert-Throws { Assert-NupkgDigest $dir } 'artifact name mismatch: lurp.9.9.9.nupkg vs lurp.9.9.8.nupkg'
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
    Assert-Throws { Assert-NupkgDigest $dir } "expected exactly one nupkg in $dir, found 2"
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Assert-NupkgDigest throws when there is no nupkg' {
  $dir = New-TempDirectory
  try {
    Assert-Throws { Assert-NupkgDigest $dir } "exactly one nupkg in $dir, found 0"
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Assert-NupkgDigest throws when SHA256SUMS.txt is missing' {
  $dir = New-TempDirectory
  try {
    $null = Write-FakeNupkg $dir
    Assert-Throws { Assert-NupkgDigest $dir } "SHA256SUMS.txt missing in $dir"
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Test-CommitSha accepts 40 lowercase hex characters' {
  Assert-Equal $true (Test-CommitSha ('a' * 40)) '40 lowercase hex'
}

Test-Case 'Test-CommitSha accepts 40 uppercase hex characters' {
  Assert-Equal $true (Test-CommitSha ('A' * 40)) '40 uppercase hex'
}

Test-Case 'Test-CommitSha rejects a trailing newline' {
  Assert-Equal $false (Test-CommitSha (('a' * 40) + "`n")) 'trailing newline'
}

Test-Case 'Test-CommitSha rejects 41 hex characters' {
  Assert-Equal $false (Test-CommitSha ('a' * 41)) '41 hex characters'
}

Test-Case 'Test-CommitSha rejects an empty string' {
  Assert-Equal $false (Test-CommitSha '') 'empty string'
}

Test-Case 'Test-CommitSha rejects a too-short SHA' {
  Assert-Equal $false (Test-CommitSha 'abc') 'too short'
}

Test-Case 'Test-CommitSha rejects a non-hex character' {
  Assert-Equal $false (Test-CommitSha (('a' * 39) + 'g')) 'non-hex character'
}

Test-Case 'Test-CommitSha rejects null' {
  Assert-Equal $false (Test-CommitSha $null) 'null'
}

$script:RealCatalogJson = '{"id":"lurp","version":"2.0.0","repository":{"type":"git","url":"https://github.com/t-macabee/lurp","commit":"7188578283a0a1848cbb7a01b4c46af5cd6fae5c"}}'
$script:RealCommit = '7188578283a0a1848cbb7a01b4c46af5cd6fae5c'
$script:TagCommit = 'b63a4ca895b317025330febfe002b35f17403ac1'

Test-Case 'Get-CatalogCommitDecision matches the catalog commit' {
  Assert-Equal 'match' (Get-CatalogCommitDecision -CatalogJson $script:RealCatalogJson -ExpectedCommit $script:RealCommit) 'decision'
}

Test-Case 'Get-CatalogCommitDecision matches when ExpectedCommit is uppercase' {
  Assert-Equal 'match' (Get-CatalogCommitDecision -CatalogJson $script:RealCatalogJson -ExpectedCommit $script:RealCommit.ToUpperInvariant()) 'decision'
}

Test-Case 'Get-CatalogCommitDecision reports a catalog entry with repository but no commit as a mismatch' {
  Assert-Equal 'mismatch' (Get-CatalogCommitDecision -CatalogJson '{"id":"lurp","version":"2.0.0","repository":{"type":"git","url":"https://github.com/t-macabee/lurp"}}' -ExpectedCommit $script:RealCommit) 'decision'
}

Test-Case 'Get-CatalogCommitDecision reports the tag commit as a mismatch (B89)' {
  Assert-Equal 'mismatch' (Get-CatalogCommitDecision -CatalogJson $script:RealCatalogJson -ExpectedCommit $script:TagCommit) 'decision'
}

Test-Case 'Get-CatalogCommitDecision reports a catalog entry without repository as a mismatch' {
  Assert-Equal 'mismatch' (Get-CatalogCommitDecision -CatalogJson '{"id":"lurp","version":"2.0.0"}' -ExpectedCommit $script:RealCommit) 'decision'
}

Test-Case 'Get-CatalogCommitDecision reports an empty ExpectedCommit as a mismatch when the catalog has no commit' {
  Assert-Equal 'mismatch' (Get-CatalogCommitDecision -CatalogJson '{"id":"lurp","version":"2.0.0","repository":{"type":"git","url":"https://github.com/t-macabee/lurp"}}' -ExpectedCommit '') 'decision'
}

Test-Case 'Get-HttpRetryDecision treats 199 as an error' {
  Assert-Equal 'error' (Get-HttpRetryDecision 199) '199'
}

Test-Case 'Get-HttpRetryDecision treats 200 and 299 as ok' {
  Assert-Equal 'ok' (Get-HttpRetryDecision 200) '200'
  Assert-Equal 'ok' (Get-HttpRetryDecision 299) '299'
}

Test-Case 'Get-HttpRetryDecision treats 300 and 499 as an error' {
  Assert-Equal 'error' (Get-HttpRetryDecision 300) '300'
  Assert-Equal 'error' (Get-HttpRetryDecision 499) '499'
}

Test-Case 'Get-HttpRetryDecision treats 500 as retry' {
  Assert-Equal 'retry' (Get-HttpRetryDecision 500) '500'
}

Test-Case 'Get-HttpRetryDecision treats 404, 408, 429 and 503 as retry' {
  Assert-Equal 'retry' (Get-HttpRetryDecision 404) '404'
  Assert-Equal 'retry' (Get-HttpRetryDecision 408) '408'
  Assert-Equal 'retry' (Get-HttpRetryDecision 429) '429'
  Assert-Equal 'retry' (Get-HttpRetryDecision 503) '503'
}

Test-Case 'Get-HttpRetryDecision treats 0 (network failure) as retry' {
  Assert-Equal 'retry' (Get-HttpRetryDecision 0) '0'
}

Test-Case 'Get-HttpRetryDecision treats 400, 401 and 403 as an error' {
  Assert-Equal 'error' (Get-HttpRetryDecision 400) '400'
  Assert-Equal 'error' (Get-HttpRetryDecision 401) '401'
  Assert-Equal 'error' (Get-HttpRetryDecision 403) '403'
}

Test-Case 'Test-ParityPass accepts a success status with the same digest' {
  $digest = 'a' * 64
  $status = [pscustomobject]@{ state = 'success'; description = "nupkg sha256=$digest" }
  Assert-Equal $true (Test-ParityPass $status $digest) 'same digest'
}

Test-Case 'Test-ParityPass rejects a success status with a different digest' {
  $digest = 'a' * 64
  $status = [pscustomobject]@{ state = 'success'; description = "nupkg sha256=$digest" }
  Assert-Equal $false (Test-ParityPass $status ('b' * 64)) 'other digest'
}

Test-Case 'Test-ParityPass rejects a failure state' {
  $digest = 'a' * 64
  Assert-Equal $false (Test-ParityPass ([pscustomobject]@{ state = 'failure'; description = "nupkg sha256=$digest" }) $digest) 'failure state'
}

Test-Case 'Test-ParityPass rejects no status' {
  $digest = 'a' * 64
  Assert-Equal $false (Test-ParityPass $null $digest) 'no status'
}

Test-Case 'Select-ParityStatus returns the newest real-parity status' {
  $digest = 'a' * 64
  $json = '[{"context":"other","state":"success","description":"x"},{"context":"real-parity","state":"failure","description":"real-parity failed; see the run"},{"context":"real-parity","state":"success","description":"nupkg sha256=' + $digest + '"}]'
  Assert-Equal 'failure' (Select-ParityStatus $json).state 'newest state'
}

Test-Case 'Select-ParityStatus returns null for an empty list' {
  Assert-Equal $null (Select-ParityStatus '[]') 'empty list'
}

Test-Case 'Select-ParityStatus returns null when there is no real-parity context' {
  Assert-Equal $null (Select-ParityStatus '[{"context":"other","state":"success","description":"x"}]') 'no real-parity'
}

Test-Case 'Select-ParityStatus returns null for a whitespace-only string' {
  Assert-Equal $null (Select-ParityStatus '   ') 'whitespace'
}

Test-Case 'Test-VersionListed matches a listed version' {
  Assert-Equal $true (Test-VersionListed -IndexJson '{"versions":["1.1.0","1.4.0","2.0.0"]}' -Version '2.0.0') 'listed'
}

Test-Case 'Test-VersionListed does not match an unlisted version' {
  Assert-Equal $false (Test-VersionListed -IndexJson '{"versions":["1.1.0","1.4.0","2.0.0"]}' -Version '3.0.0') 'not listed'
}

Test-Case 'Test-VersionListed matches a version case-insensitively' {
  Assert-Equal $true (Test-VersionListed -IndexJson '{"versions":["2.0.0-rc"]}' -Version '2.0.0-RC') 'case'
}

Test-Case 'Get-NuspecCommit reads repository.commit from the nuspec in the nupkg' {
  $dir = New-TempDirectory
  try {
    $zipPath = New-FakeNupkgWithEntry $dir 'lurp.nuspec' '<?xml version="1.0" encoding="utf-8"?><package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd"><metadata><id>lurp</id><version>9.9.9</version><repository type="git" url="https://github.com/example/lurp" commit="abc" /></metadata></package>'
    Assert-Equal 'abc' (Get-NuspecCommit $zipPath) 'commit'
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Get-NuspecCommit returns an empty string when the nuspec has no repository element' {
  $dir = New-TempDirectory
  try {
    $zipPath = New-FakeNupkgWithEntry $dir 'lurp.nuspec' '<?xml version="1.0" encoding="utf-8"?><package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd"><metadata><id>lurp</id><version>9.9.9</version></metadata></package>'
    Assert-Equal '' (Get-NuspecCommit $zipPath) 'commit'
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Get-NuspecCommit throws when the nupkg has no nuspec entry' {
  $dir = New-TempDirectory
  try {
    $zipPath = New-FakeNupkgWithEntry $dir 'lib/net10.0/lurp.dll' 'not a nuspec'
    Assert-Throws { Get-NuspecCommit $zipPath } "no .nuspec in $zipPath"
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

Test-Case 'Resolve-ReleaseTarget ignores DryRunInput on a push' {
  $target = Resolve-ReleaseTarget -EventName 'push' -RefName 'v3.0.0' -RefInput '' -DryRunInput 'true'
  Assert-Equal $false $target.DryRun 'dry run'
}

Test-Case 'Resolve-ReleaseTarget throws on an unsupported event' {
  Assert-Throws { Resolve-ReleaseTarget -EventName 'pull_request' -RefName '' -RefInput '' -DryRunInput '' } 'unsupported event'
}

Test-Case 'Resolve-ReleaseTarget takes a tag and dry run from a dispatch' {
  $target = Resolve-ReleaseTarget -EventName 'workflow_dispatch' -RefName '' -RefInput 'v3.0.0' -DryRunInput 'true'
  Assert-Equal 'v3.0.0' $target.Tag 'tag'
  Assert-Equal $true $target.DryRun 'dry run'
}

Test-Case 'Resolve-ReleaseTarget takes a tag with a real publish from a dispatch' {
  $target = Resolve-ReleaseTarget -EventName 'workflow_dispatch' -RefName '' -RefInput 'v3.0.0' -DryRunInput 'false'
  Assert-Equal 'v3.0.0' $target.Tag 'tag'
  Assert-Equal 'v3.0.0' $target.Ref 'ref'
  Assert-Equal $false $target.DryRun 'dry run'
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

Test-Case 'Resolve-ReleaseTarget rejects a tag with a trailing newline' {
  Assert-Throws { Resolve-ReleaseTarget -EventName 'workflow_dispatch' -RefName '' -RefInput ("v3.0.0`n") -DryRunInput 'false' } 'vX.Y.Z or a full commit SHA'
}

Test-Case 'Resolve-ReleaseTarget rejects a tag with a non-ASCII digit' {
  Assert-Throws { Resolve-ReleaseTarget -EventName 'workflow_dispatch' -RefName '' -RefInput ("v$([char]0x0663).0.0") -DryRunInput 'false' } 'vX.Y.Z or a full commit SHA'
}

$script:Queue = @()
$script:CallIndex = 0
$script:CallUrls = New-Object System.Collections.ArrayList

# Each call takes the next queued response; the last one repeats.
$script:FakeRequest = {
  param($Url)
  if ($script:CallIndex -ge 200) { throw 'fake request called more than 200 times' }
  [void]$script:CallUrls.Add($Url)
  $index = [Math]::Min($script:CallIndex, $script:Queue.Count - 1)
  $script:CallIndex++
  return $script:Queue[$index]
}

$script:FakeTime = [datetime]'2026-01-01T00:00:00'
$script:Sleeps = New-Object System.Collections.ArrayList
$script:FakeNow = { $script:FakeTime }
$script:FakeSleep = { param($Seconds) $script:FakeTime = $script:FakeTime.AddSeconds($Seconds); [void]$script:Sleeps.Add($Seconds) }

function Set-FakeResponses([object[]]$Responses) {
  $script:Queue = @($Responses)
  $script:CallIndex = 0
  $script:CallUrls.Clear()
  $script:FakeTime = [datetime]'2026-01-01T00:00:00'
  $script:Sleeps.Clear()
}

function New-FakeResponse([int]$Status, [string]$Content = '') {
  return [pscustomobject]@{ Status = $Status; Content = $Content }
}

$script:LeafJson = '{"catalogEntry":"https://example.test/catalog.json"}'

Test-Case 'Wait-CatalogCommit returns on a matching catalog entry' {
  Set-FakeResponses @((New-FakeResponse 200 $script:LeafJson), (New-FakeResponse 200 $script:RealCatalogJson))
  Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -Request $script:FakeRequest -Now $script:FakeNow -Sleep $script:FakeSleep
  Assert-Equal 2 $script:CallUrls.Count 'calls'
  Assert-Equal 0 $script:Sleeps.Count 'sleeps'
  Assert-Equal 'https://api.nuget.org/v3/registration5-semver1/lurp/2.0.0.json|https://example.test/catalog.json' ($script:CallUrls -join '|') 'urls'
}

Test-Case 'Wait-CatalogCommit throws at once on a commit mismatch and does not retry (B89)' {
  Set-FakeResponses @((New-FakeResponse 200 $script:LeafJson), (New-FakeResponse 200 $script:RealCatalogJson))
  Assert-Throws { Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:TagCommit -Request $script:FakeRequest -Now $script:FakeNow -Sleep $script:FakeSleep } "nuget.org catalog repository.commit '$script:RealCommit' does not match '$script:TagCommit'"
  Assert-Equal 2 $script:CallUrls.Count 'calls'
  Assert-Equal 0 $script:Sleeps.Count 'sleeps'
}

Test-Case 'Wait-CatalogCommit retries a 404 and then matches' {
  Set-FakeResponses @((New-FakeResponse 404), (New-FakeResponse 200 $script:LeafJson), (New-FakeResponse 200 $script:RealCatalogJson))
  Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -Request $script:FakeRequest -Now $script:FakeNow -Sleep $script:FakeSleep
  Assert-Equal 3 $script:CallUrls.Count 'calls'
  Assert-Equal 1 $script:Sleeps.Count 'sleeps'
  Assert-Equal 20 $script:Sleeps[0] 'sleep seconds'
  Assert-Equal 'https://api.nuget.org/v3/registration5-semver1/lurp/2.0.0.json|https://api.nuget.org/v3/registration5-semver1/lurp/2.0.0.json|https://example.test/catalog.json' ($script:CallUrls -join '|') 'urls'
}

Test-Case 'Wait-CatalogCommit gives up after the timeout when the entry never appears' {
  Set-FakeResponses @((New-FakeResponse 404))
  Assert-Throws { Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -Request $script:FakeRequest -Now $script:FakeNow -Sleep $script:FakeSleep } 'catalog entry not available after 30 minutes'
  Assert-Equal 91 $script:CallUrls.Count 'calls'
  Assert-Equal 90 $script:Sleeps.Count 'sleeps'
  Assert-Equal 'https://api.nuget.org/v3/registration5-semver1/lurp/2.0.0.json' $script:CallUrls[0] 'url'
}

Test-Case 'Wait-CatalogCommit fails at once on a 403' {
  Set-FakeResponses @((New-FakeResponse 403))
  Assert-Throws { Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -Request $script:FakeRequest -Now $script:FakeNow -Sleep $script:FakeSleep } 'nuget.org request failed: https://api.nuget.org/v3/registration5-semver1/lurp/2.0.0.json HTTP 403'
  Assert-Equal 1 $script:CallUrls.Count 'calls'
  Assert-Equal 0 $script:Sleeps.Count 'sleeps'
}

Test-Case 'Wait-CatalogCommit fails on a catalog 403' {
  Set-FakeResponses @((New-FakeResponse 200 $script:LeafJson), (New-FakeResponse 403))
  Assert-Throws { Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -Request $script:FakeRequest -Now $script:FakeNow -Sleep $script:FakeSleep } 'nuget.org request failed: https://example.test/catalog.json HTTP 403'
  Assert-Equal 2 $script:CallUrls.Count 'calls'
  Assert-Equal 0 $script:Sleeps.Count 'sleeps'
}

Test-Case 'Wait-CatalogCommit retries a catalog 503 and then matches' {
  Set-FakeResponses @((New-FakeResponse 200 $script:LeafJson), (New-FakeResponse 503), (New-FakeResponse 200 $script:LeafJson), (New-FakeResponse 200 $script:RealCatalogJson))
  Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -Request $script:FakeRequest -Now $script:FakeNow -Sleep $script:FakeSleep
  Assert-Equal 4 $script:CallUrls.Count 'calls'
  Assert-Equal 1 $script:Sleeps.Count 'sleeps'
  Assert-Equal 20 $script:Sleeps[0] 'sleep seconds'
  Assert-Equal 'https://api.nuget.org/v3/registration5-semver1/lurp/2.0.0.json|https://example.test/catalog.json|https://api.nuget.org/v3/registration5-semver1/lurp/2.0.0.json|https://example.test/catalog.json' ($script:CallUrls -join '|') 'urls'
}

Test-Case 'Wait-CatalogCommit throws on a catalog entry without repository' {
  Set-FakeResponses @((New-FakeResponse 200 $script:LeafJson), (New-FakeResponse 200 '{"id":"lurp","version":"2.0.0"}'))
  Assert-Throws { Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -Request $script:FakeRequest -Now $script:FakeNow -Sleep $script:FakeSleep } "nuget.org catalog repository.commit '' does not match '$script:RealCommit'"
  Assert-Equal 2 $script:CallUrls.Count 'calls'
  Assert-Equal 0 $script:Sleeps.Count 'sleeps'
}

Test-Case 'Wait-CatalogCommit throws when the leaf has no catalogEntry' {
  Set-FakeResponses @((New-FakeResponse 200 '{"items":[]}'))
  Assert-Throws { Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -Request $script:FakeRequest -Now $script:FakeNow -Sleep $script:FakeSleep } 'nuget.org registration leaf https://api.nuget.org/v3/registration5-semver1/lurp/2.0.0.json has no catalogEntry'
  Assert-Equal 1 $script:CallUrls.Count 'calls'
  Assert-Equal 0 $script:Sleeps.Count 'sleeps'
}

Test-Case 'Wait-CatalogCommit throws when the leaf is not JSON' {
  Set-FakeResponses @((New-FakeResponse 200 '<html>'))
  Assert-Throws { Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -Request $script:FakeRequest -Now $script:FakeNow -Sleep $script:FakeSleep } 'nuget.org registration leaf https://api.nuget.org/v3/registration5-semver1/lurp/2.0.0.json has no catalogEntry'
  Assert-Equal 1 $script:CallUrls.Count 'calls'
  Assert-Equal 0 $script:Sleeps.Count 'sleeps'
}

Test-Case 'Wait-CatalogCommit throws when the catalog is not JSON' {
  Set-FakeResponses @((New-FakeResponse 200 $script:LeafJson), (New-FakeResponse 200 '<html>'))
  Assert-Throws { Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit $script:RealCommit -Request $script:FakeRequest -Now $script:FakeNow -Sleep $script:FakeSleep } 'nuget.org catalog entry https://example.test/catalog.json is not valid JSON'
  Assert-Equal 2 $script:CallUrls.Count 'calls'
  Assert-Equal 0 $script:Sleeps.Count 'sleeps'
}

Test-Case 'Wait-CatalogCommit rejects a Version that is not X.Y.Z' {
  Set-FakeResponses @()
  Assert-Throws { Wait-CatalogCommit -Version '' -ExpectedCommit $script:RealCommit -Request $script:FakeRequest -Now $script:FakeNow -Sleep $script:FakeSleep } "Version must be X.Y.Z, got ''."
  Assert-Equal 0 $script:CallUrls.Count 'calls'
}

Test-Case 'Wait-CatalogCommit rejects an ExpectedCommit that is not a full SHA' {
  Set-FakeResponses @()
  Assert-Throws { Wait-CatalogCommit -Version '2.0.0' -ExpectedCommit '' -Request $script:FakeRequest -Now $script:FakeNow -Sleep $script:FakeSleep } "ExpectedCommit must be a full 40-character commit SHA, got ''."
  Assert-Equal 0 $script:CallUrls.Count 'calls'
}

Test-Case 'Invoke-NuGetRequest throws on a malformed URL' {
  Assert-Throws { Invoke-NuGetRequest 'not a url' } 'Invalid URI'
}

Test-Case 'Select-SuccessfulRunId returns the first successful run id' {
  $json = '[{"databaseId":1,"conclusion":"success","status":"completed"},{"databaseId":2,"conclusion":"success","status":"completed"}]'
  Assert-Equal ([long]1) (Select-SuccessfulRunId $json $script:RealCommit) 'first success'
}

Test-Case 'Select-SuccessfulRunId skips failed and in-progress runs' {
  $json = '[{"databaseId":1,"conclusion":"failure","status":"completed"},{"databaseId":2,"conclusion":"","status":"in_progress"},{"databaseId":3,"conclusion":"success","status":"completed"}]'
  Assert-Equal ([long]3) (Select-SuccessfulRunId $json $script:RealCommit) 'success id'
}

Test-Case 'Select-SuccessfulRunId throws when no run succeeded' {
  $json = '[{"databaseId":1,"conclusion":"failure","status":"completed"}]'
  Assert-Throws { Select-SuccessfulRunId $json $script:RealCommit } "No successful ci.yml run for commit $($script:RealCommit); the lurp-nupkg artifact does not exist."
}

Test-Case 'Select-SuccessfulRunId throws for an empty list' {
  Assert-Throws { Select-SuccessfulRunId '[]' $script:RealCommit } "No ci.yml run found for commit $($script:RealCommit)."
}

Test-Case 'Select-SuccessfulRunId throws for a whitespace-only string' {
  Assert-Throws { Select-SuccessfulRunId '   ' $script:RealCommit } "No ci.yml run found for commit $($script:RealCommit)."
}

Test-Case 'Test-NuGetVersionListed returns false on a 404' {
  Set-FakeResponses @((New-FakeResponse 404))
  Assert-Equal $false (Test-NuGetVersionListed -Version '2.0.0' -Request $script:FakeRequest) '404'
  Assert-Equal 1 $script:CallUrls.Count 'calls'
  Assert-Equal 'https://api.nuget.org/v3-flatcontainer/lurp/index.json' $script:CallUrls[0] 'url'
}

Test-Case 'Test-NuGetVersionListed returns true when the version is listed' {
  Set-FakeResponses @((New-FakeResponse 200 '{"versions":["1.0.0","2.0.0"]}'))
  Assert-Equal $true (Test-NuGetVersionListed -Version '2.0.0' -Request $script:FakeRequest) 'listed'
  Assert-Equal 1 $script:CallUrls.Count 'calls'
  Assert-Equal 'https://api.nuget.org/v3-flatcontainer/lurp/index.json' $script:CallUrls[0] 'url'
}

Test-Case 'Test-NuGetVersionListed returns false when the version is not listed' {
  Set-FakeResponses @((New-FakeResponse 200 '{"versions":["1.0.0"]}'))
  Assert-Equal $false (Test-NuGetVersionListed -Version '2.0.0' -Request $script:FakeRequest) 'not listed'
  Assert-Equal 1 $script:CallUrls.Count 'calls'
  Assert-Equal 'https://api.nuget.org/v3-flatcontainer/lurp/index.json' $script:CallUrls[0] 'url'
}

Test-Case 'Test-NuGetVersionListed throws on a 503' {
  Set-FakeResponses @((New-FakeResponse 503))
  Assert-Throws { Test-NuGetVersionListed -Version '2.0.0' -Request $script:FakeRequest } 'nuget.org flat container returned HTTP 503'
  Assert-Equal 1 $script:CallUrls.Count 'calls'
  Assert-Equal 'https://api.nuget.org/v3-flatcontainer/lurp/index.json' $script:CallUrls[0] 'url'
}

Test-Case 'Test-NuGetVersionListed throws on a network failure (status 0)' {
  Set-FakeResponses @((New-FakeResponse 0))
  Assert-Throws { Test-NuGetVersionListed -Version '2.0.0' -Request $script:FakeRequest } 'nuget.org flat container returned HTTP 0'
  Assert-Equal 1 $script:CallUrls.Count 'calls'
  Assert-Equal 'https://api.nuget.org/v3-flatcontainer/lurp/index.json' $script:CallUrls[0] 'url'
}

# A loopback port that was free a moment ago.
function Get-FreeLoopbackPort {
  $tcp = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
  $tcp.Start()
  try {
    return $tcp.LocalEndpoint.Port
  } finally {
    $tcp.Stop()
  }
}

function Write-Csproj([string]$Directory, [string]$Body) {
  $path = Join-Path $Directory 'Lurp.csproj'
  Set-Content -LiteralPath $path -Value "<Project Sdk=`"Microsoft.NET.Sdk`">`n$Body`n</Project>" -Encoding utf8
  return $path
}

Test-Case 'Get-LurpVersion reads the Version property and ignores a commented one' {
  $dir = New-TempDirectory
  try {
    $path = Write-Csproj $dir "  <!-- <Version>9.9.9</Version> -->`n  <PropertyGroup>`n    <Version>3.6.1</Version>`n  </PropertyGroup>"
    Assert-Equal '3.6.1' (Get-LurpVersion $path) 'version'
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Get-LurpVersion throws a named error when the project has no Version property' {
  $dir = New-TempDirectory
  try {
    $path = Write-Csproj $dir "  <PropertyGroup>`n    <OutputType>Exe</OutputType>`n  </PropertyGroup>"
    Assert-Throws { Get-LurpVersion $path } "Get-LurpVersion: '$path' must have exactly one <Version> property; found 0."
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Get-LurpVersion throws a named error when the project has two Version properties' {
  $dir = New-TempDirectory
  try {
    $path = Write-Csproj $dir "  <PropertyGroup>`n    <Version>1.0.0</Version>`n  </PropertyGroup>`n  <PropertyGroup Condition=`"'`$(Configuration)' == 'Release'`">`n    <Version>2.0.0</Version>`n  </PropertyGroup>"
    Assert-Throws { Get-LurpVersion $path } "Get-LurpVersion: '$path' must have exactly one <Version> property; found 2."
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Get-LurpVersion throws a named error when the Version property is empty' {
  $dir = New-TempDirectory
  try {
    $path = Write-Csproj $dir "  <PropertyGroup>`n    <Version> </Version>`n  </PropertyGroup>"
    Assert-Throws { Get-LurpVersion $path } "Get-LurpVersion: the <Version> property in '$path' is empty."
  } finally {
    Remove-Item -Recurse -Force $dir
  }
}

Test-Case 'Get-LurpVersion throws a named error when the project file does not exist' {
  $path = Join-Path ([IO.Path]::GetTempPath()) "$([guid]::NewGuid().ToString('N')).csproj"
  Assert-Throws { Get-LurpVersion $path } "Get-LurpVersion: '$path' does not exist."
}

# One local HTTP server for the Invoke-NuGetRequest cases: /ok answers 200 with
# a known body, every other path 404.
$script:ServerPrefix = "http://localhost:$(Get-FreeLoopbackPort)/"
$script:Listener = [System.Net.HttpListener]::new()
$script:Listener.Prefixes.Add($script:ServerPrefix)
$script:Listener.Start()
$script:ServerJob = Start-ThreadJob -ScriptBlock {
  $listener = $using:Listener
  while ($listener.IsListening) {
    try { $ctx = $listener.GetContext() } catch { break }
    if ($ctx.Request.Url.AbsolutePath -eq '/ok') {
      $bytes = [Text.Encoding]::UTF8.GetBytes('hello-body')
      $ctx.Response.StatusCode = 200
      $ctx.Response.ContentType = 'text/plain; charset=utf-8'
      $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    } else {
      $ctx.Response.StatusCode = 404
    }
    $ctx.Response.Close()
  }
}
try {
  Test-Case 'Invoke-NuGetRequest returns status and body for a 200' {
    $response = Invoke-NuGetRequest "$($script:ServerPrefix)ok"
    Assert-Equal 200 $response.Status 'status'
    Assert-Equal 'hello-body' $response.Content 'body'
  }

  Test-Case 'Invoke-NuGetRequest returns a 404 as a status instead of throwing' {
    Assert-Equal 404 (Invoke-NuGetRequest "$($script:ServerPrefix)missing").Status 'status'
  }

  Test-Case 'Invoke-NuGetRequest returns status 0 when the connection is refused' {
    Assert-Equal 0 (Invoke-NuGetRequest "http://localhost:$(Get-FreeLoopbackPort)/nope").Status 'status'
  }
} finally {
  $script:Listener.Stop()
  Wait-Job $script:ServerJob -Timeout 10 | Out-Null
  Remove-Job $script:ServerJob -Force -ErrorAction SilentlyContinue
}

Write-Host "$($script:Passed) passed, $($script:Failed) failed"
if ($script:Failed -gt 0) { exit 1 }
exit 0

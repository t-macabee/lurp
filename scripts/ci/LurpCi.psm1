# scripts/ci/LurpCi.psm1
#
# Shared workflow logic for the Repository CI jobs. It exists because the same
# steps were copied between ci.yml and real-parity.yml (audit B90): the
# vulnerable-package check, the nupkg SHA-256 check, the ci.yml run lookup and
# the nupkg download. Workflows import it with
#   Import-Module ./scripts/ci/LurpCi.psm1 -Force
# at the top of each pwsh step (step state does not carry over). The pure
# functions are tested by scripts/ci/LurpCi.Tests.ps1.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-CommitSha([string]$Sha) {
  return $Sha -match '\A[0-9a-fA-F]{40}\z'
}

function Get-VulnerablePackageHits([string]$Json) {
  if ([string]::IsNullOrWhiteSpace($Json)) { throw 'dotnet package list returned no output.' }
  $list = $null
  try {
    $list = $Json | ConvertFrom-Json
  } catch {
    throw "dotnet package list output is not JSON: $($_.Exception.Message)"
  }
  if (-not $list.PSObject.Properties['projects'] -or @($list.projects).Count -eq 0) { throw 'dotnet package list output has no projects.' }
  $hits = @()
  foreach ($project in $list.projects) {
    if (-not $project.PSObject.Properties['frameworks']) { continue }
    foreach ($framework in $project.frameworks) {
      $candidates = @()
      if ($framework.PSObject.Properties['topLevelPackages']) { $candidates += @($framework.topLevelPackages) }
      if ($framework.PSObject.Properties['transitivePackages']) { $candidates += @($framework.transitivePackages) }
      foreach ($package in $candidates) {
        if ($package -and $package.PSObject.Properties['vulnerabilities'] -and @($package.vulnerabilities).Count -gt 0) {
          $hits += "$($package.id) $($package.resolvedVersion)"
        }
      }
    }
  }
  return @($hits)
}

function Assert-NoVulnerablePackages {
  $json = dotnet package list --vulnerable --include-transitive --format json
  if ($LASTEXITCODE -ne 0) { throw "dotnet package list failed with exit code $LASTEXITCODE." }
  $hits = @(Get-VulnerablePackageHits $json)
  if ($hits.Count -gt 0) {
    $hits | ForEach-Object { Write-Host "::error::$_ has known vulnerabilities." }
    throw "vulnerable packages found"
  }
  Write-Host "No vulnerable packages."
}

function Assert-NupkgDigest([string]$Directory) {
  $files = @(Get-ChildItem -Path (Join-Path $Directory '*.nupkg') -ErrorAction SilentlyContinue)
  if ($files.Count -ne 1) {
    throw "expected exactly one nupkg in $Directory, found $($files.Count)"
  }
  $sumFile = Join-Path $Directory 'SHA256SUMS.txt'
  if (-not (Test-Path $sumFile)) { throw "SHA256SUMS.txt missing in $Directory" }
  $fields = (Get-Content $sumFile) -split '\s+' | Where-Object { $_ }
  $actual = (Get-FileHash $files[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $fields[0]) { throw "SHA-256 mismatch: expected $($fields[0]), got $actual." }
  if ($files[0].Name -ne $fields[1]) { throw "artifact name mismatch: $($files[0].Name) vs $($fields[1])." }
  return [pscustomobject]@{ Path = $files[0].FullName; Name = $files[0].Name; Digest = $actual }
}

function Select-SuccessfulRunId([string]$RunsJson, [string]$Sha) {
  if ([string]::IsNullOrWhiteSpace($RunsJson)) { throw "No ci.yml run found for commit $Sha." }
  $runs = @($RunsJson | ConvertFrom-Json)
  if ($runs.Count -eq 0) { throw "No ci.yml run found for commit $Sha." }
  $successes = @($runs | Where-Object { $_.conclusion -eq 'success' })
  if ($successes.Count -eq 0) { throw "No successful ci.yml run for commit $Sha; the lurp-nupkg artifact does not exist." }
  return $successes[0].databaseId
}

function Resolve-CiRun([string]$Sha) {
  if (-not (Test-CommitSha $Sha)) { throw "Input 'sha' must be a full 40-character commit SHA." }
  $runsJson = gh run list --workflow=ci.yml --commit $Sha --limit 5 --json databaseId,conclusion,status
  if ($LASTEXITCODE -ne 0) { throw "gh run list failed with exit code $LASTEXITCODE." }
  $runId = Select-SuccessfulRunId -RunsJson (@($runsJson) -join "`n") -Sha $Sha
  Write-Host "Using ci.yml run $runId for $Sha"
  return $runId
}

function Get-CiNupkg([string]$Sha, [string]$Destination, [string]$RunId) {
  if (-not $RunId) { $RunId = Resolve-CiRun $Sha }
  New-Item -ItemType Directory -Force -Path $Destination | Out-Null
  gh run download $RunId -n lurp-nupkg -D $Destination | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "lurp-nupkg artifact missing or expired for run $RunId" }
  $pkg = Assert-NupkgDigest $Destination
  $pkg | Add-Member -NotePropertyName RunId -NotePropertyValue $RunId
  return $pkg
}

function Write-ParityStatus([string]$Repository, [string]$Sha, [string]$State, [string]$Description, [string]$TargetUrl) {
  if (-not (Test-CommitSha $Sha)) { throw "Input 'sha' must be a full 40-character commit SHA." }
  if ($State -notin @('success', 'failure')) { throw "State must be 'success' or 'failure', got '$State'." }
  gh api "repos/$Repository/statuses/$Sha" -X POST -f "state=$State" -f "context=real-parity" -f "description=$Description" -f "target_url=$TargetUrl"
  if ($LASTEXITCODE -ne 0) { throw "gh api statuses failed with exit code $LASTEXITCODE." }
}

function Get-CatalogRepositoryCommit([string]$CatalogJson) {
  $entry = $CatalogJson | ConvertFrom-Json
  if (-not $entry.PSObject.Properties['repository']) { return '' }
  if (-not $entry.repository.PSObject.Properties['commit']) { return '' }
  return [string]$entry.repository.commit
}

function Get-CatalogCommitDecision([string]$CatalogJson, [string]$ExpectedCommit) {
  $actual = (Get-CatalogRepositoryCommit $CatalogJson).ToLowerInvariant()
  if ($actual -ne '' -and $actual -ceq $ExpectedCommit.ToLowerInvariant()) { return 'match' }
  return 'mismatch'
}

function Get-HttpRetryDecision([int]$StatusCode) {
  if ($StatusCode -ge 200 -and $StatusCode -le 299) { return 'ok' }
  if ($StatusCode -eq 0 -or $StatusCode -in @(404, 408, 429) -or $StatusCode -ge 500) { return 'retry' }
  return 'error'
}

# Returns the status and the body. A network failure is status 0 (retry);
# anything else propagates.
function Invoke-NuGetRequest([string]$Url) {
  try {
    $response = Invoke-WebRequest -Uri $Url -SkipHttpErrorCheck -TimeoutSec 30
    return [pscustomobject]@{ Status = [int]$response.StatusCode; Content = [string]$response.Content }
  } catch [System.Net.Http.HttpRequestException], [System.Threading.Tasks.TaskCanceledException], [System.IO.IOException] {
    return [pscustomobject]@{ Status = 0; Content = '' }
  }
}

function Wait-CatalogCommit([string]$Version, [string]$ExpectedCommit, [int]$TimeoutMinutes = 30, [int]$IntervalSeconds = 20, [scriptblock]$Request = { param($Url) Invoke-NuGetRequest $Url }, [scriptblock]$Now = { Get-Date }, [scriptblock]$Sleep = { param($Seconds) Start-Sleep -Seconds $Seconds }) {
  if ($Version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw "Version must be X.Y.Z, got '$Version'." }
  if (-not (Test-CommitSha $ExpectedCommit)) { throw "ExpectedCommit must be a full 40-character commit SHA, got '$ExpectedCommit'." }
  $leafUrl = "https://api.nuget.org/v3/registration5-semver1/lurp/$Version.json"
  $deadline = (& $Now).AddMinutes($TimeoutMinutes)
  while ($true) {
    $leaf = & $Request $leafUrl
    $decision = Get-HttpRetryDecision $leaf.Status
    $detail = "$leafUrl HTTP $($leaf.Status)"
    if ($decision -eq 'ok') {
      $catalogUrl = ''
      try {
        $leafEntry = ConvertFrom-Json $leaf.Content
        if ($leafEntry.PSObject.Properties['catalogEntry']) { $catalogUrl = [string]$leafEntry.catalogEntry }
      } catch {
        throw "nuget.org registration leaf $leafUrl has no catalogEntry."
      }
      if ([string]::IsNullOrWhiteSpace($catalogUrl)) { throw "nuget.org registration leaf $leafUrl has no catalogEntry." }
      $catalog = & $Request $catalogUrl
      $decision = Get-HttpRetryDecision $catalog.Status
      $detail = "$catalogUrl HTTP $($catalog.Status)"
    }
    if ($decision -eq 'error') { throw "nuget.org request failed: $detail." }
    if ($decision -eq 'ok') {
      try { [void](ConvertFrom-Json $catalog.Content) } catch { throw "nuget.org catalog entry $catalogUrl is not valid JSON." }
      $commit = Get-CatalogRepositoryCommit $catalog.Content
      if ((Get-CatalogCommitDecision -CatalogJson $catalog.Content -ExpectedCommit $ExpectedCommit) -eq 'match') {
        Write-Host "nuget.org catalog repository.commit $commit matches $ExpectedCommit."
        return
      }
      throw "nuget.org catalog repository.commit '$commit' does not match '$ExpectedCommit'."
    }
    if ((& $Now) -ge $deadline) { throw "catalog entry not available after $TimeoutMinutes minutes" }
    Write-Host "Catalog entry not ready ($detail); retrying in $IntervalSeconds seconds."
    & $Sleep $IntervalSeconds
  }
}

function Test-ParityPass($Status, [string]$Digest) {
  if ($null -eq $Status) { return $false }
  return ([string]$Status.state -eq 'success') -and ([string]$Status.description -ceq "nupkg sha256=$Digest")
}

function Select-ParityStatus([string]$StatusesJson) {
  if ([string]::IsNullOrWhiteSpace($StatusesJson)) { return $null }
  foreach ($status in @($StatusesJson | ConvertFrom-Json)) {
    if ($status.context -eq 'real-parity') { return $status }
  }
  return $null
}

function Get-ParityStatus([string]$Repository, [string]$Sha) {
  $lines = gh api "repos/$Repository/commits/$Sha/statuses?per_page=100"
  if ($LASTEXITCODE -ne 0) { throw "gh api commit statuses failed with exit code $LASTEXITCODE." }
  return Select-ParityStatus (@($lines) -join "`n")
}

function Test-VersionListed([string]$IndexJson, [string]$Version) {
  foreach ($listed in @((ConvertFrom-Json $IndexJson).versions)) {
    if ([string]$listed -ieq $Version) { return $true }
  }
  return $false
}

function Test-NuGetVersionListed([string]$Version, [scriptblock]$Request = { param($Url) Invoke-NuGetRequest $Url }) {
  $response = & $Request 'https://api.nuget.org/v3-flatcontainer/lurp/index.json'
  if ($response.Status -eq 404) { return $false }
  if ($response.Status -ne 200) { throw "nuget.org flat container returned HTTP $($response.Status)." }
  return Test-VersionListed -IndexJson $response.Content -Version $Version
}

function Get-NuspecCommit([string]$NupkgPath) {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zip = [System.IO.Compression.ZipFile]::OpenRead($NupkgPath)
  try {
    $entry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
    if (-not $entry) { throw "no .nuspec in $NupkgPath" }
    $reader = New-Object System.IO.StreamReader($entry.Open())
    try {
      [xml]$nuspec = $reader.ReadToEnd()
    } finally {
      $reader.Dispose()
    }
  } finally {
    $zip.Dispose()
  }
  $repository = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='repository']")
  if (-not $repository) { return '' }
  return [string]$repository.GetAttribute('commit')
}

function Resolve-ReleaseTarget([string]$EventName, [string]$RefName, [string]$RefInput, [string]$DryRunInput) {
  if ($EventName -eq 'push') {
    return [pscustomobject]@{ Ref = $RefName; Tag = $RefName; DryRun = $false }
  }
  if ($EventName -ne 'workflow_dispatch') { throw "unsupported event '$EventName'." }
  $dryRun = $DryRunInput -eq 'true'
  if ($RefInput -match '\Av[0-9]+\.[0-9]+\.[0-9]+\z') {
    return [pscustomobject]@{ Ref = $RefInput; Tag = $RefInput; DryRun = $dryRun }
  }
  if (-not (Test-CommitSha $RefInput)) {
    throw "ref must be vX.Y.Z or a full commit SHA, got '$RefInput'"
  }
  if (-not $dryRun) { throw 'a real publish needs a tag; dispatch with ref=vX.Y.Z or set dry_run' }
  return [pscustomobject]@{ Ref = $RefInput; Tag = ''; DryRun = $true }
}

Export-ModuleMember -Function Test-CommitSha, Get-VulnerablePackageHits, Assert-NoVulnerablePackages, Assert-NupkgDigest, Resolve-CiRun, Select-SuccessfulRunId, Get-CiNupkg, Write-ParityStatus, Get-CatalogRepositoryCommit, Get-CatalogCommitDecision, Get-HttpRetryDecision, Invoke-NuGetRequest, Wait-CatalogCommit, Test-ParityPass, Select-ParityStatus, Get-ParityStatus, Test-VersionListed, Test-NuGetVersionListed, Get-NuspecCommit, Resolve-ReleaseTarget

<#
.SYNOPSIS
    Builds the solution, runs the test suite with code coverage, and flattens
    the OpenCover output for NDepend consumption.

.DESCRIPTION
    Mirrors the CI workflow in .github/workflows/ci.yml:
    1. Build Release
    2. Run dotnet test --collect:"XPlat Code Coverage;Format=opencover"
       --results-directory ./CoverageResults (isolated from TestResults)
    3. Copy the GUID-named coverage.opencover.xml to TestResults/coverage.opencover.xml

    Uses the coverlet.collector package already referenced in
    tests/Lurp.Storage.Tests.csproj with --collect:"XPlat Code Coverage;Format=opencover".
    OpenCover is required for the most accurate NDepend aggregate coverage result;
    Cobertura under-reports coverage by ~4 percentage points (observed ~68% vs
    ~72%) and can cause the Percentage Coverage quality gate to fail.

    NDepend's CoverageDirFilter ("*.opencover.xml") reads the flattened file
    from TestResults/ — no additional coverage tool is added.

.EXAMPLE
    .\scripts\collect-coverage.ps1

.OUTPUTS
    TestResults/coverage.opencover.xml  (OpenCover format, flattened from GUID subdir)
#>

param(
    [string]$Configuration = "Release",
    [string]$TestProject = "tests/Lurp.Storage.Tests.csproj",
    [string]$ResultsDir = "TestResults",
    [string]$CoverageDir = "CoverageResults"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)

Push-Location $root

try {
    # 1. Build
    Write-Host "=== Building $Configuration ==="
    dotnet build Lurp.slnx --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }

    # 2. Clean previous coverage results and run tests with coverage
    Write-Host "=== Running tests with code coverage ==="
    $resolvedCoverageDir = Join-Path $root $CoverageDir
    if (Test-Path $resolvedCoverageDir) {
        $verified = (Resolve-Path $resolvedCoverageDir).Path
        $rootFull = (Resolve-Path $root).Path
        if (-not $verified.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar)) {
            throw "CoverageDir '$verified' is not inside repository root '$rootFull'"
        }
        Remove-Item -Recurse -Force $verified
        Write-Host "Removed previous coverage directory: $verified"
    }
    dotnet test $TestProject `
        --no-build `
        --configuration $Configuration `
        --verbosity normal `
        --collect:"XPlat Code Coverage;Format=opencover" `
        --results-directory $CoverageDir
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

    # 3. Flatten coverage file for NDepend
    $dest = Join-Path $ResultsDir "coverage.opencover.xml"
    $destFull = if (Test-Path $dest) { (Resolve-Path $dest).Path } else { $null }
    $candidates = Get-ChildItem -Path $CoverageDir -Recurse -Filter "coverage.opencover.xml" |
        Where-Object { $_.FullName -ne $destFull }
    if ($candidates.Count -eq 0) {
        throw "No coverage.opencover.xml found in $CoverageDir subdirectories"
    }
    if ($candidates.Count -gt 1) {
        throw "Expected exactly 1 coverage file, found $($candidates.Count): $($candidates.FullName -join ', ')"
    }
    $source = $candidates[0].FullName
    [xml]$xml = Get-Content $source
    if ($xml.DocumentElement.Name -ne "CoverageSession") {
        throw "Selected coverage file $source root element is '$($xml.DocumentElement.Name)', expected 'CoverageSession'"
    }
    New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null
    Copy-Item -Path $source -Destination $dest -Force
    Write-Host "Coverage flattened: $dest"
} finally {
    Pop-Location
}

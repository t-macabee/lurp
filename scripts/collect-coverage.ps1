<#
.SYNOPSIS
    Builds the solution, runs the test suite with code coverage, and flattens
    the Cobertura output for NDepend consumption.

.DESCRIPTION
    Mirrors the CI workflow in .github/workflows/ci.yml:
    1. Build Release
    2. Run dotnet test --collect:"XPlat Code Coverage" --results-directory ./TestResults
    3. Copy the GUID-named coverage.cobertura.xml to TestResults/coverage.cobertura.xml

    NDepend's CoverageDirFilter ("*.cobertura.xml") reads this flattened file.
    Uses coverlet.collector already referenced in tests/Lurp.Storage.Tests.csproj
    — no additional coverage tool is added.

.EXAMPLE
    .\scripts\collect-coverage.ps1

.OUTPUTS
    TestResults/coverage.cobertura.xml  (Cobertura format, flattend from GUID subdir)
#>

param(
    [string]$Configuration = "Release",
    [string]$TestProject = "tests/Lurp.Storage.Tests.csproj",
    [string]$ResultsDir = "TestResults"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)

Push-Location $root

try {
    # 1. Build
    Write-Host "=== Building $Configuration ==="
    dotnet build Lurp.slnx --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }

    # 2. Clean previous results and run tests with coverage
    Write-Host "=== Running tests with code coverage ==="
    Remove-Item -Recurse -Force $ResultsDir -ErrorAction SilentlyContinue
    dotnet test $TestProject `
        --no-build `
        --configuration $Configuration `
        --verbosity normal `
        --collect:"XPlat Code Coverage" `
        --results-directory $ResultsDir
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

    # 3. Flatten coverage file for NDepend
    $coverageFiles = Get-ChildItem -Path $ResultsDir -Recurse -Filter "coverage.cobertura.xml"
    if ($coverageFiles.Count -gt 0) {
        $source = $coverageFiles[0].FullName
        $dest = Join-Path $ResultsDir "coverage.cobertura.xml"
        Copy-Item -Path $source -Destination $dest -Force
        Write-Host "Coverage flattened: $dest"
    } else {
        Write-Warning "No coverage.cobertura.xml found in $ResultsDir subdirectories"
    }
} finally {
    Pop-Location
}

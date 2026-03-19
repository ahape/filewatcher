<#
.SYNOPSIS
    Runs tests, checks for missing dependencies, collects coverage, and generates a report.

.DESCRIPTION
    This cmdlet runs dotnet test with specific filters for Namespaces, Classes, or Methods.
    It collects code coverage using XPlat Code Coverage and generates an HTML report using ReportGenerator.

.PARAMETER TestNamespace
    Part of the namespace to filter tests (e.g., "MyApp.UnitTests").
    Passed to dotnet test --filter "FullyQualifiedName~Value".

.PARAMETER TestClass
    The specific test class to run.
    Passed to dotnet test --filter "FullyQualifiedName~Value".

.PARAMETER TestMethod
    The specific test method to run.
    Passed to dotnet test --filter "FullyQualifiedName~Value".

.PARAMETER CoverNamespace
    The namespace of your actual code (not the test) to include in the coverage report.
    passed to ReportGenerator -classfilters:"+Value*"

.PARAMETER CoverClass
    The specific class of your actual code to include in the coverage report.
    passed to ReportGenerator -classfilters:"+*.Value"

.EXAMPLE
    .\Test-Coverage.ps1 -TestClass "OrderServiceTests" -CoverClass "OrderService"

.NOTES
    The canonical version of this script is maintained in the brightmetrics/projects-plugin repository:
    https://github.com/brightmetrics/projects-plugin/blob/master/scripts/Test-Coverage.ps1
#>
[CmdletBinding()]
param(
    [Parameter(HelpMessage="Filter tests by Namespace.")]
    [string]$TestNamespace,

    [Parameter(HelpMessage="Filter tests by Class name.")]
    [string]$TestClass,

    [Parameter(HelpMessage="Filter tests by Method name.")]
    [string]$TestMethod,

    [Parameter(HelpMessage="Filter coverage report by Namespace.")]
    [string]$CoverNamespace,

    [Parameter(HelpMessage="Filter coverage report by Class.")]
    [string]$CoverClass
)

Push-Location $PSScriptRoot

try {
    # --- Configuration ---
    $toolsDir       = Join-Path $PSScriptRoot ".tools"
    $reportDir      = Join-Path $PSScriptRoot "coverage"
    $testResultsDir = Join-Path $PSScriptRoot "TestResults"
    $reportGenName  = "dotnet-reportgenerator-globaltool"

    # Modern OS Detection ($IsWindows is null in Windows PS 5.1)
    $isWindowsOS    = [bool]($IsWindows -or ($env:OS -eq 'Windows_NT'))

    # Path setup
    $reportGenExe   = Join-Path $toolsDir "reportgenerator.exe"
    $reportGenNix   = Join-Path $toolsDir "reportgenerator"

    # --- 1. Prerequisite Check: Coverlet ---
    # Scan for the package reference to avoid the confusing "Data collector not found" error
    $projFiles = Get-ChildItem *.csproj -Recurse
    if ($projFiles) {
        $hasCoverlet = Select-String -Path $projFiles.FullName -Pattern "coverlet.collector" -SimpleMatch -Quiet
        if (-not $hasCoverlet) {
            Write-Host "ERROR: Missing 'coverlet.collector' package." -ForegroundColor Red
            Write-Host "Run this in your test project: dotnet add package coverlet.collector" -ForegroundColor Yellow
            exit 1
        }
    }

    # --- 2. Setup: Install ReportGenerator locally (Portable) ---
    if (-not (Test-Path $reportGenExe) -and -not (Test-Path $reportGenNix)) {
        Write-Host "Installing ReportGenerator locally to '$toolsDir'..." -ForegroundColor Cyan
        & dotnet tool install $reportGenName --tool-path $toolsDir
    }

    # --- 3. Cleanup ---
    Write-Host "Cleaning up previous results..." -ForegroundColor Gray
    if (Test-Path $reportDir) { Remove-Item $reportDir -Recurse -Force }
    if (Test-Path $testResultsDir) { Remove-Item $testResultsDir -Recurse -Force }

    # --- 4. Execution: Run Tests ---
    Write-Host "Running tests..." -ForegroundColor Cyan

    # Prepare Test Filter
    # We use logic AND (&) so we can drill down: Namespace -> Class -> Method
    $filterParts = @()
    if (-not [string]::IsNullOrWhiteSpace($TestNamespace)) { $filterParts += "FullyQualifiedName~$TestNamespace" }
    if (-not [string]::IsNullOrWhiteSpace($TestClass))     { $filterParts += "FullyQualifiedName~$TestClass" }
    if (-not [string]::IsNullOrWhiteSpace($TestMethod))    { $filterParts += "FullyQualifiedName~$TestMethod" }

    # Build Args
    $testArgs = @("test", "--collect", "XPlat Code Coverage")

    if ($filterParts.Count -gt 0) {
        $filterString = $filterParts -join "&"
        Write-Verbose "Applying Test Filter: $filterString"
        $testArgs += "--filter", $filterString
    }

    # Run Dotnet Test
    & dotnet $testArgs

    # Check exit code immediately
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed. Report generation skipped." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    # --- 5. Reporting: Generate HTML ---
    Write-Host "Tests finished. Generating report..." -ForegroundColor Cyan

    # Determine the correct local executable path
    $genCommand = if ($isWindowsOS) { $reportGenExe } else { $reportGenNix }

    # Prepare Coverage Filter (ReportGenerator -classfilters)
    # Defaults to "+*" (include everything) if no params provided
    $covFilters = @()
    if (-not [string]::IsNullOrWhiteSpace($CoverNamespace)) { $covFilters += "+${CoverNamespace}*" }
    if (-not [string]::IsNullOrWhiteSpace($CoverClass))     { $covFilters += "+*.${CoverClass}" }

    $finalClassFilter = if ($covFilters.Count -gt 0) { $covFilters -join ";" } else { "+*" }

    Write-Verbose "Applying Coverage Filter: $finalClassFilter"

    # Run ReportGenerator
    $reportPattern = (Join-Path $testResultsDir '**/coverage.cobertura.xml') -replace '\\','/'
    & $genCommand -reports:$reportPattern `
                  -targetdir:$reportDir `
                  -reporttypes:"Html;TextSummary" `
                  -classfilters:$finalClassFilter

    # --- 6. Finish ---
    $reportFile = Join-Path $reportDir 'index.html'
    if (Test-Path $reportFile) {
        Write-Host "Success! Opening report: $reportFile" -ForegroundColor Green

        # Wrapped in a try block to prevent errors on headless CI/CD Build Servers
        try {
            Invoke-Item $reportFile -ErrorAction Stop
        } catch {
            Write-Verbose "Could not invoke report file. This is normal in headless environments."
        }

        try {
            Write-Output "<CoverageSummary>"
            $summaryFile = Join-Path $reportDir 'Summary.txt'
            Get-Content $summaryFile -Raw -ErrorAction Stop
            Write-Output "</CoverageSummary>"
        } catch {
            Write-Warning "Could not read Summary.txt. It may be missing or locked."
        }
    } else {
        Write-Host "Report file not found. Something went wrong generating the report." -ForegroundColor Red
        exit 1
    }
}
finally {
    Pop-Location
}

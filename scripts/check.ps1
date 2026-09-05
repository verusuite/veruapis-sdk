# Checks every SDK against spec/openapi.yaml, and holds each to its coverage bar.
#
# Nothing here is generated, so this is what keeps the hand-written clients
# honest: a route an SDK calls that the API does not serve fails the run, and so
# does coverage falling below the floor.

[CmdletBinding()]
param([ValidateSet("node", "go", "python", "dotnet", "all")][string]$Only = "all")

# Deliberately not "Stop". PowerShell turns any line a native command writes to
# stderr into a terminating error under that setting, and npm and go both use
# stderr for ordinary progress output. Exit codes are what is checked instead,
# which is what they actually mean.
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$spec = Join-Path $root "spec\openapi.yaml"

if (-not (Test-Path $spec)) {
    throw "No specification at $spec. Refresh it first:`n" +
          "  cd ..\veruapis; go run .\cmd\veruapis spec ..\veruapis-sdks\spec\openapi.yaml"
}

$age = (Get-Date) - (Get-Item $spec).LastWriteTime
"specification: {0:N0} days old" -f $age.TotalDays

$minCoverage = 95
$failed = $false

if ($Only -in "node", "all") {
    "`n==> node"
    Push-Location (Join-Path $root "node")
    npm run --silent check-spec
    if ($LASTEXITCODE -ne 0) { $failed = $true }
    npm run --silent coverage        # .c8rc.json carries the threshold
    if ($LASTEXITCODE -ne 0) { $failed = $true }
    Pop-Location
}

if ($Only -in "go", "all") {
    "`n==> go"
    Push-Location (Join-Path $root "go")

    # The spec check is a test, so it runs here.
    go test "-coverprofile=coverage.out" ./...
    if ($LASTEXITCODE -ne 0) { $failed = $true }

    # Go has no coverage threshold of its own. testing.Coverage() looks like the
    # place for one and reports a different, lower number than the profile does,
    # so the profile is what is measured.
    $total = go tool cover "-func=coverage.out" | Select-Object -Last 1
    if ($total -match '(\d+(?:\.\d+)?)%') {
        $pct = [double]$Matches[1]
        "coverage: $pct%"
        if ($pct -lt $minCoverage) {
            Write-Host "FAIL: coverage $pct% is below $minCoverage%" -ForegroundColor Red
            $failed = $true
        }
    } else {
        Write-Host "FAIL: could not read the coverage total" -ForegroundColor Red
        $failed = $true
    }

    Remove-Item coverage.out -ErrorAction SilentlyContinue
    Pop-Location
}

# python and dotnet get the same treatment as each is written.

if ($failed) { throw "an SDK failed its checks" }
"`nall checks passed"

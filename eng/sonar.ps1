[CmdletBinding()]
param(
    [string]$ProjectKey = "deidentify-dotnet"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

docker info *> $null
if ($LASTEXITCODE -ne 0) {
    throw "Docker is unavailable."
}

docker compose -f compose.sonar.yml up -d
if ($LASTEXITCODE -ne 0) {
    throw "SonarQube failed to start."
}

$ready = $false
for ($attempt = 0; $attempt -lt 120; $attempt++) {
    try {
        $status = Invoke-RestMethod -Uri "http://localhost:9000/api/system/status" -TimeoutSec 5
        if ($status.status -eq "UP") {
            $ready = $true
            break
        }
    }
    catch {
    }

    Start-Sleep -Seconds 5
}

if (-not $ready) {
    throw "SonarQube did not become healthy."
}

if ([string]::IsNullOrWhiteSpace($env:SONAR_TOKEN)) {
    throw "SONAR_TOKEN is required."
}

dotnet tool restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force artifacts\coverage | Out-Null

dotnet sonarscanner begin `
    /k:"$ProjectKey" `
    /d:sonar.host.url="http://localhost:9000" `
    /d:sonar.token="$env:SONAR_TOKEN" `
    /d:sonar.qualitygate.wait=true `
    /d:sonar.coverage.exclusions="eng/consumers/**,samples/**" `
    /d:sonar.cs.vscoveragexml.reportsPaths="artifacts/coverage/coverage.xml"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build Deidentify.sln -c Release --no-incremental
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet coverage collect `
    "dotnet test Deidentify.sln -c Release --no-build" `
    -f xml `
    -o artifacts\coverage\coverage.xml
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet sonarscanner end /d:sonar.token="$env:SONAR_TOKEN"
exit $LASTEXITCODE

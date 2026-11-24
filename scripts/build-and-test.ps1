# CI/CD Pipeline Generator - builds and runs ALL tests on Windows (PowerShell 7).
# The whole gate: the screen builds, the .NET solution builds with no warnings, every test passes.
# Usage (from the repo root): scripts\build-and-test.cmd
# With actionlint installed, the tests also check every generated GitHub Actions workflow with it.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$client = Join-Path $root 'src\cicdpipelinegenerator.client'

Push-Location $client
try {
    if (-not (Test-Path 'node_modules')) { npm ci --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' } }
    npm audit --audit-level=low; if ($LASTEXITCODE -ne 0) { throw 'npm audit found vulnerabilities' }
    npm run build; if ($LASTEXITCODE -ne 0) { throw 'The screen did not build' }
} finally { Pop-Location }

if (Get-Command actionlint -ErrorAction SilentlyContinue) {
    $env:CICDPIPELINEGENERATOR_ACTIONLINT = (Get-Command actionlint).Source
} else {
    Write-Warning 'actionlint is not installed: the actionlint test does nothing.'
}

dotnet test (Join-Path $root 'CICDPipelineGenerator.slnx') -nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
Write-Host 'All checks passed.'

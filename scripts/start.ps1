# Builds the screen (first time or after changes) and starts CI/CD Pipeline Generator at http://localhost:5090.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$client = Join-Path $root 'src\cicdpipelinegenerator.client'

if (-not (Test-Path (Join-Path $client 'node_modules'))) {
    Write-Host 'Installing the screen packages (first time only)...'
    Push-Location $client
    try { npm ci --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' } } finally { Pop-Location }
}

Write-Host 'Building the screen...'
Push-Location $client
try { npm run build; if ($LASTEXITCODE -ne 0) { throw 'The screen did not build' } } finally { Pop-Location }

# A key saved with setx reaches only terminals opened afterwards; read it from the user's settings when this one lacks it.
if (-not $env:ANTHROPIC_API_KEY) {
    $env:ANTHROPIC_API_KEY = [Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY', 'User')
}

if (-not $env:ANTHROPIC_API_KEY) {
    Write-Warning 'No ANTHROPIC_API_KEY: "Describe with AI" will not answer. Save one with: setx ANTHROPIC_API_KEY "your-key"'
}

Write-Host 'CI/CD Pipeline Generator is starting at http://localhost:5090 (Ctrl+C stops it).'
Start-Job { Start-Sleep 4; Start-Process 'http://localhost:5090' } | Out-Null
dotnet run --project (Join-Path $root 'src\CICDPipelineGenerator.Server') -c Release

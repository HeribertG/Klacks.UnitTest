# Copyright (c) Heribert Gasparoli Private. All rights reserved.

<#
.SYNOPSIS
Runs the live provider verification tests against a real messaging provider.

.DESCRIPTION
Reads credentials from a local, git-ignored JSON file so that no token ever reaches the
repository, a commit or a chat transcript. Copy live-credentials.local.example.json to
live-credentials.local.json, fill in your values, then run this script.

.PARAMETER Provider
Which provider to verify. Currently: Slack.

.EXAMPLE
powershell -File run-live-test.ps1 -Provider Slack
#>

param(
    [ValidateSet('Slack')]
    [string]$Provider = 'Slack'
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$credentialFile = Join-Path $scriptDir 'live-credentials.local.json'

if (-not (Test-Path $credentialFile)) {
    Write-Host "No credential file found at:" -ForegroundColor Yellow
    Write-Host "  $credentialFile"
    Write-Host ""
    Write-Host "Copy live-credentials.local.example.json to live-credentials.local.json and fill it in."
    Write-Host "The file is git-ignored, so the token stays on this machine."
    exit 1
}

$credentials = Get-Content $credentialFile -Raw | ConvertFrom-Json

function Set-RequiredVariable {
    param([string]$Name, [string]$Value, [string]$Hint)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        Write-Host "Missing '$Hint' in $credentialFile" -ForegroundColor Red
        exit 1
    }

    Set-Item -Path "env:$Name" -Value $Value
}

switch ($Provider) {
    'Slack' {
        Set-RequiredVariable -Name 'SLACK_BOT_TOKEN' -Value $credentials.slack.botToken -Hint 'slack.botToken'
        Set-RequiredVariable -Name 'SLACK_CHANNEL'   -Value $credentials.slack.channel  -Hint 'slack.channel'

        if (-not [string]::IsNullOrWhiteSpace($credentials.slack.signingSecret)) {
            $env:SLACK_SIGNING_SECRET = $credentials.slack.signingSecret
        }

        $filter = 'FullyQualifiedName~SlackLiveVerification'
    }
}

$repoRoot = Resolve-Path (Join-Path $scriptDir '..\..\..\..')
$project = Join-Path $repoRoot 'Klacks.UnitTest\Klacks.UnitTest.csproj'

Write-Host "Running $Provider live verification..." -ForegroundColor Cyan
Write-Host "Real messages will be sent to the configured channel." -ForegroundColor Yellow
Write-Host ""

# Build separately so a build failure is never mistaken for a provider failure.
& dotnet build $project -v q --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed - not running the live test." -ForegroundColor Red
    exit $LASTEXITCODE
}

& dotnet test $project --no-build --filter $filter --nologo
$testExit = $LASTEXITCODE

# Do not leave credentials in the session environment.
Remove-Item env:SLACK_BOT_TOKEN -ErrorAction SilentlyContinue
Remove-Item env:SLACK_CHANNEL -ErrorAction SilentlyContinue
Remove-Item env:SLACK_SIGNING_SECRET -ErrorAction SilentlyContinue

exit $testExit

# Copyright (c) Heribert Gasparoli Private. All rights reserved.

<#
.SYNOPSIS
Runs the live provider verification tests against a real messaging provider.

.DESCRIPTION
Reads credentials from a local, git-ignored JSON file so that no token ever reaches the
repository, a commit or a chat transcript. Copy live-credentials.local.example.json to
live-credentials.local.json, fill in your values, then run this script.

.PARAMETER Provider
Which provider to verify. Currently: Slack, WhatsApp, Line.

.EXAMPLE
powershell -File run-live-test.ps1 -Provider Slack

.EXAMPLE
powershell -File run-live-test.ps1 -Provider WhatsApp

.EXAMPLE
powershell -File run-live-test.ps1 -Provider Line
#>

param(
    [ValidateSet('Slack', 'WhatsApp', 'Line')]
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
    'WhatsApp' {
        Set-RequiredVariable -Name 'WHATSAPP_ACCESS_TOKEN'   -Value $credentials.whatsapp.accessToken  -Hint 'whatsapp.accessToken'
        Set-RequiredVariable -Name 'WHATSAPP_PHONE_NUMBER_ID' -Value $credentials.whatsapp.phoneNumberId -Hint 'whatsapp.phoneNumberId'
        Set-RequiredVariable -Name 'WHATSAPP_RECIPIENT'       -Value $credentials.whatsapp.recipient    -Hint 'whatsapp.recipient'

        if (-not [string]::IsNullOrWhiteSpace($credentials.whatsapp.appSecret)) {
            $env:WHATSAPP_APP_SECRET = $credentials.whatsapp.appSecret
        }

        Write-Host "Reply from the recipient phone to the test number NOW, before continuing." -ForegroundColor Yellow
        Write-Host "WhatsApp only accepts plain text while that 24 hour window is open." -ForegroundColor Yellow
        Write-Host ""

        $filter = 'FullyQualifiedName~WhatsAppLiveVerification'
    }
    'Line' {
        Set-RequiredVariable -Name 'LINE_CHANNEL_ACCESS_TOKEN' -Value $credentials.line.channelAccessToken -Hint 'line.channelAccessToken'
        Set-RequiredVariable -Name 'LINE_USER_ID'              -Value $credentials.line.userId             -Hint 'line.userId'

        if (-not [string]::IsNullOrWhiteSpace($credentials.line.channelSecret)) {
            $env:LINE_CHANNEL_SECRET = $credentials.line.channelSecret
        }

        Write-Host "LINE has no conversation window - no precondition to arrange." -ForegroundColor Cyan
        Write-Host "The recipient account must have added the bot as a friend." -ForegroundColor Yellow
        Write-Host "Step5 sends a deliberately over-long message to find the real limit." -ForegroundColor Yellow
        Write-Host ""

        $filter = 'FullyQualifiedName~LineLiveVerification'
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
Remove-Item env:WHATSAPP_ACCESS_TOKEN -ErrorAction SilentlyContinue
Remove-Item env:WHATSAPP_PHONE_NUMBER_ID -ErrorAction SilentlyContinue
Remove-Item env:WHATSAPP_RECIPIENT -ErrorAction SilentlyContinue
Remove-Item env:WHATSAPP_APP_SECRET -ErrorAction SilentlyContinue
Remove-Item env:LINE_CHANNEL_ACCESS_TOKEN -ErrorAction SilentlyContinue
Remove-Item env:LINE_USER_ID -ErrorAction SilentlyContinue
Remove-Item env:LINE_CHANNEL_SECRET -ErrorAction SilentlyContinue

exit $testExit

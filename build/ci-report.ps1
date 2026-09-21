# Collects CI stage logs and posts a condensed diagnostic report to the repository log-sink issue.
# Used because raw Actions log downloads are not reachable from the development sandbox.
param(
    [string]$RunId = $env:GITHUB_RUN_ID,
    [string]$Sha = $env:GITHUB_SHA,
    [string]$Restore = 'skipped',
    [string]$Build = 'skipped',
    [string]$Test = 'skipped',
    [string]$Publish = 'skipped',
    [string]$Package = 'skipped'
)

$ErrorActionPreference = 'Continue'

function Get-Relevant {
    param([string]$Path, [int]$Keep = 120)
    if (-not (Test-Path $Path)) { return "(no log produced: $Path)" }
    $lines = @(Get-Content -Path $Path -ErrorAction SilentlyContinue)
    if ($lines.Count -eq 0) { return "(empty log: $Path)" }
    $pattern = 'error|Error [A-Z]+|warning CS|Unhandled|Exception|Failed|FAILED|failed|Passed!|Build succeeded|Test Run|assert'
    $filtered = @($lines | Where-Object { $_ -match $pattern })
    if ($filtered.Count -eq 0) { $filtered = $lines }
    $selected = @($filtered | Select-Object -Last $Keep)
    return ($selected -join "`n")
}

$fence = [string]([char]96) * 3
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("### Dentiva CI run $RunId")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("commit: ``$Sha``")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| stage | result |")
[void]$sb.AppendLine("| --- | --- |")
[void]$sb.AppendLine("| restore | $Restore |")
[void]$sb.AppendLine("| build | $Build |")
[void]$sb.AppendLine("| test | $Test |")
[void]$sb.AppendLine("| publish | $Publish |")
[void]$sb.AppendLine("| package | $Package |")
[void]$sb.AppendLine("")

$logs = @(
    @{ Name = 'restore'; Path = 'ci-restore.log'; Keep = 40 },
    @{ Name = 'build';   Path = 'ci-build.log';   Keep = 160 },
    @{ Name = 'test';    Path = 'ci-test.log';    Keep = 140 },
    @{ Name = 'publish'; Path = 'ci-publish.log'; Keep = 60 },
    @{ Name = 'package'; Path = 'ci-package.log'; Keep = 80 },
    @{ Name = 'smoke';   Path = 'ci-smoke.log';   Keep = 120 }
)

foreach ($log in $logs) {
    $content = Get-Relevant -Path $log.Path -Keep $log.Keep
    [void]$sb.AppendLine("<details><summary>$($log.Name)</summary>")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine($fence)
    [void]$sb.AppendLine($content)
    [void]$sb.AppendLine($fence)
    [void]$sb.AppendLine("</details>")
    [void]$sb.AppendLine("")
}

$body = $sb.ToString()
if ($body.Length -gt 60000) { $body = $body.Substring(0, 60000) + "`n(truncated)" }
Set-Content -Path 'ci-comment.md' -Value $body -Encoding utf8

$title = 'CI Log Sink'
$issues = gh issue list --state open --json number,title --limit 50 | ConvertFrom-Json
$target = $issues | Where-Object { $_.title -eq $title } | Select-Object -First 1
if ($null -eq $target) {
    gh issue create --title $title --body 'Automated diagnostics channel for the Dentiva Windows build pipeline.' | Out-Null
    $issues = gh issue list --state open --json number,title --limit 50 | ConvertFrom-Json
    $target = $issues | Where-Object { $_.title -eq $title } | Select-Object -First 1
}

if ($null -ne $target) {
    gh issue comment $target.number --body-file 'ci-comment.md' | Out-Null
    Write-Host "Diagnostics posted to issue #$($target.number)"
} else {
    Write-Host 'Could not resolve the log sink issue.'
}

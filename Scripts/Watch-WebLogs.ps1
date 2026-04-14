$webLogs = "$env:TMP\bmweblog2.txt"
Write-Output $null > $webLogs
Get-Content $webLogs -Wait

[CmdletBinding()]
param (
    [int]$Port = 8081,
    [string]$ServerDirectory = "C:\src\azure\BrightMetricsWeb"
)

# 1. Validate Port (Must be > 1024 to run without Admin rights)
if ($Port -le 1024 -or $Port -gt 65535) {
    Write-Error "Port must be between 1025 and 65535 to run without Administrator privileges."
    return
}

# 2. Validate Server Directory exists
if (-not (Test-Path -Path $ServerDirectory -PathType Container)) {
    Write-Error "The directory '$ServerDirectory' does not exist."
    return
}

# 3. Validate IIS Express is installed
$iisExpressPath = "C:\Program Files\IIS Express\iisexpress.exe"
if (-not (Test-Path -Path $iisExpressPath -PathType Leaf)) {
    Write-Error "IIS Express not found at '$iisExpressPath'. Please verify it is installed."
    return
}

# Output useful info to the console
Write-Host "Starting IIS Express..." -ForegroundColor Green
Write-Host "Directory : $ServerDirectory" -ForegroundColor Cyan
Write-Host "URL       : http://localhost:$Port" -ForegroundColor Cyan
Write-Host "Press 'Q' or Ctrl+C in this window to stop the server." -ForegroundColor Yellow
Write-Host ("-" * 50)

try {
    # 4. Construct arguments as an array (Robust against paths with spaces in PS 7.x)
    $iisArgs = @(
        "/path:$ServerDirectory",
        "/port:$Port"
    )

    # 5. Use the Call Operator (&) to run in the exact same shell without an admin prompt
    & $iisExpressPath $iisArgs
}
catch {
    Write-Error "An unexpected error occurred while running IIS Express: $_"
}

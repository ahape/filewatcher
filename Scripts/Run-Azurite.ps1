$dataDir = "$env:TMP\azurite_data"
$azuritePorts = @(10000, 10001, 10002)

# 1. Dependency Check
$npxCommand = Get-Command npx -ErrorAction SilentlyContinue
if (-not $npxCommand) {
    throw "npx is not recognized. Ensure Node.js/npm is installed and in your PATH."
}
# Get the actual path to npx.cmd so Start-Process doesn't fail
$npxPath = $npxCommand.Source

# 2. Cleanup existing processes & wait for ports to release
Write-Host "Checking for processes on Azurite ports..."
$connections = Get-NetTCPConnection -LocalPort $azuritePorts -ErrorAction SilentlyContinue
if ($connections) {
    # Get unique PIDs, ignoring System Idle (0) and System (4)
    $pids = $connections.OwningProcess | Select-Object -Unique | Where-Object { $_ -gt 4 }

    # FIX: Changed $pid to $targetPid to avoid overwriting PowerShell's automatic $PID variable
    foreach ($targetPid in $pids) {
        $process = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if ($process) {
            Write-Host "Killing process '$($process.ProcessName)' (PID: $targetPid)..."
            try {
                Stop-Process -Id $targetPid -Force -ErrorAction Stop

                # Crucial: Wait for the process to actually die so the port is freed
                $process | Wait-Process -Timeout 5 -ErrorAction Stop
            } catch {
                throw "Failed to kill process $targetPid. You may need to run this script as Administrator."
            }
        }
    }
}

# 3. Clean and recreate the data directory safely
if (Test-Path $dataDir) {
    Write-Host "Cleaning up old Azurite data..."
    try {
        Remove-Item $dataDir -Recurse -Force -ErrorAction Stop
    } catch {
        throw "Failed to delete '$dataDir'. A file may be locked by another program."
    }
}

try {
    New-Item $dataDir -ItemType Directory -Force | Out-Null
} catch {
    throw "Failed to create directory '$dataDir'."
}

# 4. Start Azurite Asynchronously
Write-Host "Starting Azurite in the background..."
$logFile = Join-Path $dataDir "azurite.log"

$startArgs = @{
    FilePath               = $npxPath
    ArgumentList           = "--yes azurite -l `"$dataDir`""
    WindowStyle            = "Hidden"
    RedirectStandardOutput = $logFile
    RedirectStandardError  = $logFile
    PassThru               = $true
}
$npxProcess = Start-Process @startArgs

# 5. Find the actual Node Process ID
Write-Host "Waiting for Azurite to initialize and bind to ports..."
$actualPid = 0
$timeout = 15 # Seconds to wait for startup
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

while ($stopwatch.Elapsed.TotalSeconds -lt $timeout) {
    # Check if a process has bound to the main Blob port (10000)
    $conn = Get-NetTCPConnection -LocalPort 10000 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1

    if ($conn -and $conn.OwningProcess) {
        $actualPid = $conn.OwningProcess
        break
    }

    # If the wrapper process died prematurely, throw an error
    if ($npxProcess.HasExited) {
        throw "Azurite wrapper process exited unexpectedly. Check logs at: $logFile"
    }

    Start-Sleep -Milliseconds 500
}

$stopwatch.Stop()

if ($actualPid -ne 0) {
    Write-Host "Azurite started successfully! Log file: $logFile"

    # Output ONLY the PID to the success stream so other scripts can capture it
    # e.g., $azuritePid = .\Start-Azurite.ps1
    return $actualPid
} else {
    # Cleanup the wrapper if it hung but never bound the ports
    $npxProcess | Stop-Process -Force -ErrorAction SilentlyContinue
    throw "Azurite failed to start or bind to port 10000 within $timeout seconds. Check logs at: $logFile"
}

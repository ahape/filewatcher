$dataDir = "$env:TMP\azurite_data"
$azuritePorts = @(10000, 10001, 10002)

# 1. Dependency Check
if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
    throw "npx is not recognized. Ensure Node.js/npm is installed and in your PATH."
}

# 2. Cleanup existing processes & wait for ports to release
Write-Host "Checking for processes on Azurite ports..."
$connections = Get-NetTCPConnection -LocalPort $azuritePorts -ErrorAction SilentlyContinue
if ($connections) {
    # Get unique PIDs, ignoring System Idle (0) and System (4)
    $pids = $connections.OwningProcess | Select-Object -Unique | Where-Object { $_ -gt 4 }

    foreach ($pid in $pids) {
        $process = Get-Process -Id $pid -ErrorAction SilentlyContinue
        if ($process) {
            Write-Host "Killing process '$($process.ProcessName)' (PID: $pid)..."
            try {
                Stop-Process -Id $pid -Force -ErrorAction Stop

                # Crucial: Wait for the process to actually die so the port is freed
                $process | Wait-Process -Timeout 5 -ErrorAction Stop
            } catch {
                throw "Failed to kill process $pid. You may need to run this script as Administrator."
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
        throw "Failed to delete '$dataDir'. A file may be locked by another program (like Antivirus)."
    }
}

try {
    New-Item $dataDir -ItemType Directory -Force | Out-Null
} catch {
    throw "Failed to create directory '$dataDir'."
}

# 4. Start Azurite
Write-Host "Starting Azurite..."
npx --yes azurite -l $dataDir

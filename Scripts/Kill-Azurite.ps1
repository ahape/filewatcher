#Get-NetTCPConnection -LocalPort 10000,10001,10002 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess | Stop-Process -Force
$ports = @(10000, 10001, 10002)

foreach ($port in $ports) {
    $connections = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue
    if ($connections) {
        foreach ($conn in $connections) {
            $processId = $conn.OwningProcess
            if ($processId) {
                $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
                $processName = if ($process) { $process.ProcessName } else { "Unknown" }

                Write-Host "Port $port is in use by process $processId ($processName). Killing it..."
                Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
                Write-Host "Successfully stopped process $processId."
            }
        }
    } else {
        Write-Host "Port $port is free."
    }
}

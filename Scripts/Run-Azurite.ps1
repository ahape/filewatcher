$dataDir = "$env:TMP\azurite_data"
Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item $dataDir -ItemType Directory -Force | Out-Null

# Kill any process occupying port 10001 before starting Azurite
Get-NetTCPConnection -LocalPort 10001 -ErrorAction SilentlyContinue |
    ForEach-Object {
        Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }

npx --yes azurite -l $dataDir

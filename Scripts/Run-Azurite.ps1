$dataDir = "$env:TMP\azurite_data"
Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item $dataDir -ItemType Directory -Force | Out-Null
npx --yes azurite -l $dataDir

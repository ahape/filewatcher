# Filewatcher

Filewatcher is a tiny helper that keeps two folders in sync. Point it at the files you edit and the places they need to be copied to, then leave it running—every save is copied over automatically with a short debounce so you do not spam deployments.

## Why you might want it
- Watch any number of individual files across different folders.
- Debounced copying prevents half-written files from being deployed.
- Optional backups give you a safety net before overwriting destination files.
- Simple console UI: press `r` to reload the config, `q` to quit, anything else for a status snapshot.

## Requirements
- [ .NET SDK 9.0+](https://dotnet.microsoft.com/download)
- Windows file paths are used in the sample config, but any OS supported by .NET works as long as the paths are valid.

## First-time setup
1. **Clone or download** this repo.
2. **Create a config**:
   - On the first run the program creates `watchconfig.json` if it does not exist and exits, prompting you to edit it.
   - Alternatively, copy `watchconfig.example.json` to `watchconfig.json` and edit that file directly.
3. **Edit the config** so every mapping has a real `source` (the file you edit) and `destination` (where the copy should land).

## Running the watcher
```powershell
dotnet run
```
While the app is running:
- `r` reloads `watchconfig.json` without restarting.
- `q` exits cleanly.
- Any other key prints the current watcher/status summary.

Keep the console window open; Filewatcher writes a short log each time it copies a file or hits an error.

## Configuration reference (`watchconfig.json`)
```json
{
  "settings": {
    "debounceMs": 1000,
    "createBackups": false,
    "logLevel": "Info"
  },
  "mappings": [
    {
      "source": "C:\\path\\to\\file.js",
      "destination": "D:\\deploy\\file.js",
      "enabled": true,
      "description": "What this file is for"
    }
  ]
}
```
- `debounceMs`: wait time (in ms) after a change before copying. Increase if your editor saves in bursts.
- `createBackups`: `true` creates timestamped backups of the destination before overwriting.
- `logLevel`: currently informational only; keep as `Info`.
- Each entry in `mappings`:
  - `source`: full path to the file you actively edit.
  - `destination`: full path that should receive the copy (directories are created if missing).
  - `enabled`: toggle without deleting the entry.
  - `description`: optional label that appears in the console log.

## Troubleshooting
- **"Source file not found" warning**: confirm the path and that the file exists before starting Filewatcher.
- **Nothing copies on change**: verify the mapping is `enabled` and the config was reloaded (`r`).
- **Permissions issues**: make sure you can write to the destination folder and, if needed, run the console as an administrator.
- **Need to watch more files**: just add more mappings; Filewatcher creates a single watcher per folder automatically.

That is it—configure once, keep it running, and your files stay in sync.

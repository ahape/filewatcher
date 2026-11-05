# Builds a project with the newest installed MSBuild (located via the CommonScripts module)
# and exits with MSBuild's exit code so FileWatcher jobs can detect failures.
[CmdletBinding()]
param (
    [Parameter(Mandatory)]
    [string]$Project,

    # Extra MSBuild arguments, e.g. /p:Configuration=Release
    [Parameter(ValueFromRemainingArguments)]
    [string[]]$MSBuildArgs = @()
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -Path $Project -PathType Leaf)) {
    Write-Error "Project '$Project' does not exist."
}

Import-Module CommonScripts -DisableNameChecking
$msbuild = (Find-MSBuild).LatestPath

& $msbuild $Project @MSBuildArgs
exit $LASTEXITCODE

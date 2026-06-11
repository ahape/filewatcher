[CmdletBinding()]
param(
    [int]$Port = 8081,
    [string]$Browser = $null
)

function Start-WebAppInBrowser {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$LoginUrl,
        [string]$Browser
    )
    Write-Verbose ""

    if ($Browser) {
        Write-Verbose "Launching WebApp in browser: $Browser"
        Start-Process $Browser -ArgumentList "`"$LoginUrl`"" -ErrorAction Stop
    } else {
        Write-Verbose "Launching WebApp in default browser"
        Start-Process "`"$LoginUrl`"" -ErrorAction Stop
    }

    Write-Verbose "  WebApp launched"
    Write-Verbose ""
}

# So that '. ./Start-WebApp.ps1' can simply load the functions rather than launching everything.
if ($MyInvocation.InvocationName -ne '.') {
    if (-not $Browser) {
        #$Browser = 'C:\Program Files\Google\Chrome Dev\Application\chrome.exe'
        #$Browser = 'C:\Program Files\Firefox Developer Edition\firefox.exe'
    }

    $url = "http://localhost:$Port/Login.aspx?ssoProvider=e9374cae-2860-43df-8141-4e8701973991"

    Start-WebAppInBrowser -LoginUrl $url -Browser $Browser
}

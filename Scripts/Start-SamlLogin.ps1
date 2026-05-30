<#
.SYNOPSIS
  Initiates a SAML SP-initiated login against Google IdP via HTTP-Redirect binding,
  stashes relay state in Redis, and opens the IdP URL in a browser.
#>
[CmdletBinding()]
param(
    [int]    $Port              = 8081,
    [string] $SamlId            = ${env:FW:SAML_ID},
    [string] $IdpUrl            = ${env:FW:IDP_URL},
    [string] $Issuer            = ${env:FW:ISSUER_URL},
    [string] $ConnectionString  = ${env:FW:REDIS_CONNECTION_STRING},
    [int]    $RelayStateTtlSec  = 3600,
    #[string] $Browser           = 'C:\Program Files\Firefox Developer Edition\firefox.exe'
    [string] $Browser           = 'C:\Program Files\Google\Chrome Dev\Application\chrome.exe'
)

function New-RedisConnection {
    <#
        Accepts a StackExchange.Redis-style connection string:
            "host[:port][,host:port,...][,key=value,...]"
        Recognized options: password, user, ssl (true/false), sslhost,
        defaultDatabase, name. Other options are ignored.
        Only the first endpoint is used (no failover/sentinel logic).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConnectionString
    )
    # ---- Parse connection string ----
    $endpoints = @()
    $options   = @{}
    foreach ($tok in $ConnectionString.Split(',')) {
        $t = $tok.Trim()
        if (-not $t) { continue }
        $eq = $t.IndexOf('=')                         # first '=' only — passwords often contain '='
        if ($eq -gt 0) {
            $k = $t.Substring(0, $eq).Trim().ToLowerInvariant()
            $v = $t.Substring($eq + 1).Trim()
            $options[$k] = $v
        } else {
            $endpoints += $t
        }
    }
    if (-not $endpoints) {
        throw "Connection string contains no endpoint"
    }

    $endpoint = $endpoints[0]
    $colon    = $endpoint.LastIndexOf(':')            # LastIndexOf works for host:port and [::1]:port
    if ($colon -lt 0) {
        $redisHost = $endpoint
        $redisPort = 6379
    } else {
        $redisHost = $endpoint.Substring(0, $colon)
        $redisPort = [int]$endpoint.Substring($colon + 1)
    }

    $useSsl = $options['ssl'] -and ($options['ssl'] -ieq 'true' -or $options['ssl'] -eq '1')

    Write-Verbose "  Redis endpoint    = ${redisHost}:${redisPort}"
    Write-Verbose "  Redis ssl         = $useSsl"
    Write-Verbose "  Redis user        = $($options['user'])"
    Write-Verbose "  Redis password    = $(if ($options['password']) { '***' } else { '<none>' })"
    Write-Verbose "  Redis database    = $($options['defaultdatabase'])"
    # ---- TCP + (optional) TLS ----
    $client = [System.Net.Sockets.TcpClient]::new($redisHost, $redisPort)
    $stream = $client.GetStream()

    if ($useSsl) {
        Write-Verbose "  Wrapping in SslStream"
        $sslHost = if ($options.ContainsKey('sslhost')) { $options['sslhost'] } else { $redisHost }
        $sslStream = [System.Net.Security.SslStream]::new($stream, $false)
        $sslStream.AuthenticateAsClient($sslHost)
        $stream = $sslStream
    }

    $conn = @{ Client = $client; Stream = $stream }

    # ---- AUTH / SELECT / CLIENT SETNAME ----
    if ($options['password']) {
        Write-Verbose "  Sending AUTH"
        $authArgs = if ($options['user']) {
            @('AUTH', $options['user'], $options['password'])
        } else {
            @('AUTH', $options['password'])
        }
        $reply = Invoke-RedisCommand -Connection $conn -Arguments $authArgs
        Write-Verbose "    AUTH reply    = $reply"
    }

    if ($options['defaultdatabase']) {
        Write-Verbose "  Selecting database $($options['defaultdatabase'])"
        $reply = Invoke-RedisCommand -Connection $conn -Arguments @('SELECT', $options['defaultdatabase'])
        Write-Verbose "    SELECT reply  = $reply"
    }

    if ($options['name']) {
        try {
            $reply = Invoke-RedisCommand -Connection $conn -Arguments @('CLIENT', 'SETNAME', $options['name'])
            Write-Verbose "    CLIENT SETNAME reply = $reply"
        } catch {
            Write-Verbose "    CLIENT SETNAME failed (non-fatal): $_"
        }
    }

    return $conn
}

function Invoke-RedisCommand {
    param(
        [Parameter(Mandatory)] $Connection,
        [Parameter(Mandatory)] [string[]] $Arguments
    )
    # --- Build RESP request: *<n>\r\n  $<len>\r\n<arg>\r\n ... ---
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.Append('*').Append($Arguments.Count).Append("`r`n")
    foreach ($a in $Arguments) {
        $len = [System.Text.Encoding]::UTF8.GetByteCount($a)
        [void]$sb.Append('$').Append($len).Append("`r`n").Append($a).Append("`r`n")
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
    $Connection.Stream.Write($bytes, 0, $bytes.Length)
    $Connection.Stream.Flush()

    return Read-RedisReply -Stream $Connection.Stream
}

function Read-RedisReply {
    param([Parameter(Mandatory)] $Stream)

    $line = Read-RedisLine -Stream $Stream
    $type = $line[0]
    $rest = $line.Substring(1)

    switch ($type) {
        '+' { return $rest }                         # Simple string  (e.g. "OK")
        '-' { throw "Redis error: $rest" }           # Error
        ':' { return [long]$rest }                   # Integer
        '$' {                                        # Bulk string
            $len = [int]$rest
            if ($len -lt 0) { return $null }         # $-1 => nil
            $buf = [byte[]]::new($len)
            $read = 0
            while ($read -lt $len) {
                $r = $Stream.Read($buf, $read, $len - $read)
                if ($r -eq 0) { break }
                $read += $r
            }
            [void]$Stream.ReadByte()                 # consume CR
            [void]$Stream.ReadByte()                 # consume LF
            return [System.Text.Encoding]::UTF8.GetString($buf)
        }
        '*' {                                        # Array
            $n = [int]$rest
            if ($n -lt 0) { return $null }
            $arr = New-Object object[] $n
            for ($i = 0; $i -lt $n; $i++) { $arr[$i] = Read-RedisReply -Stream $Stream }
            return ,$arr
        }
        default { throw "Unexpected reply prefix: $type" }
    }
}

function Read-RedisLine {
    param([Parameter(Mandatory)] $Stream)
    $bytes = [System.Collections.Generic.List[byte]]::new()
    $prev = 0
    while ($true) {
        $b = $Stream.ReadByte()
        if ($b -eq -1) { break }
        if ($prev -eq 0x0D -and $b -eq 0x0A) {       # \r\n terminator
            $bytes.RemoveAt($bytes.Count - 1)        # drop the \r we appended
            break
        }
        $bytes.Add([byte]$b)
        $prev = $b
    }
    return [System.Text.Encoding]::UTF8.GetString($bytes.ToArray())
}

function New-SamlAuthnRequestXml {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Issuer,
        [Parameter(Mandatory)][string]$AcsUrl,
        [Parameter(Mandatory)][string]$Destination
    )

    $requestId    = '_' + [guid]::NewGuid()
    $issueInstant = [DateTime]::UtcNow.ToString('s') + 'Z'

    Write-Verbose ""
    Write-Verbose "Building AuthnRequest"
    Write-Verbose "  ID            = $requestId"
    Write-Verbose "  IssueInstant  = $issueInstant"
    Write-Verbose "  ACS           = $AcsUrl"
    Write-Verbose "  Destination   = $Destination"
    Write-Verbose "  Issuer        = $Issuer"
    Write-Verbose ""

    @"
<samlp:AuthnRequest
  AssertionConsumerServiceURL="$AcsUrl"
  Destination="$Destination"
  ID="$requestId"
  IssueInstant="$issueInstant"
  ProtocolBinding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST"
  Version="2.0"
  xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol"
>
  <saml:Issuer xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">$Issuer</saml:Issuer>
  <samlp:NameIDPolicy
    AllowCreate="true"
    Format="urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress"
  />
</samlp:AuthnRequest>
"@
}

function ConvertTo-SamlRedirectPayload {
    <#
        SAML HTTP-Redirect binding: raw DEFLATE (RFC 1951, no zlib/gzip wrapper)
        then base64. Returns the base64 string ready to be URL-encoded.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Xml
    )

    Write-Verbose ""
    Write-Verbose "Compressing XML payload"
    Write-Verbose "  Input size    = $($Xml.Length) chars"

    $ms = [System.IO.MemoryStream]::new()
    try {
        $deflate = [System.IO.Compression.DeflateStream]::new(
            $ms, [System.IO.Compression.CompressionMode]::Compress, $true)
        $writer  = [System.IO.StreamWriter]::new(
            $deflate, [System.Text.UTF8Encoding]::new($false))

        $writer.Write($Xml)
        $writer.Close()   # flushes writer AND deflate stream — both required

        $bytes = $ms.ToArray()
        Write-Verbose "  Deflated size = $($bytes.Length) bytes"

        $b64 = [Convert]::ToBase64String($bytes)
        Write-Verbose "  Base64 size   = $($b64.Length) chars"
        $b64
    }
    finally {
        $ms.Dispose()
    }
}

function Set-SamlRelayState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RelayState,
        [Parameter(Mandatory)][string]$BrightmetricsHost,
        [Parameter(Mandatory)][string]$SamlId,
        [Parameter(Mandatory)][string]$ConnectionString,
        [int]   $ExpirySeconds = 3600
    )

    $key = "${env:FW:RELAY_STATE_PREFIX}:$RelayState"

    Write-Verbose ""
    Write-Verbose "Connecting to Redis"
    $conn = New-RedisConnection -ConnectionString $ConnectionString
    try {
        Write-Verbose "  Connected"
        Write-Verbose ""

        $payload = [pscustomobject]@{
            samlId            = $SamlId
            brightmetricsHost = $BrightmetricsHost
        } | ConvertTo-Json -Compress

        Write-Verbose ""
        Write-Verbose "Writing relay state"
        Write-Verbose "  Key           = $key"
        Write-Verbose "  TTL           = ${ExpirySeconds}s"
        Write-Verbose "  Payload       = $payload"

        $reply = Invoke-RedisCommand $conn @('SET', $key, $payload, 'EX', "$ExpirySeconds")
        Write-Verbose "  Redis reply   = $reply"
        Write-Verbose ""
    }
    finally {
        if ($conn) {
            Write-Verbose ""
            Write-Verbose "Closing Redis connection"
            Write-Verbose ""

            $conn.Stream.Dispose()
            $conn.Client.Dispose()

            Write-Verbose "Sleeping for 1 second to allow for Redis persistence"
            Start-Sleep -Seconds 1
        }
    }
}

function Get-SamlRedirectUrl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$IdpUrl,
        [Parameter(Mandatory)][string]$SamlRequest,
        [Parameter(Mandatory)][string]$RelayState
    )
    Write-Verbose ""
    Write-Verbose "Building redirect URL"

    $sep = if ($IdpUrl -match '\?') { '&' } else { '?' }

    $url = $IdpUrl + $sep +
           'SAMLRequest=' + [uri]::EscapeDataString($SamlRequest) + '&' +
           'RelayState='  + [uri]::EscapeDataString($RelayState)

    Write-Verbose "  URL = $url"
    Write-Verbose ""
    $url
}

function Start-SamlLoginInBrowser {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Url,
        #[string]$Browser = 'C:\Program Files\Firefox Developer Edition\firefox.exe'
        [string]$Browser = 'C:\Program Files\Google\Chrome Dev\Application\chrome.exe'
    )
    Write-Verbose ""
    Write-Verbose "Launching browser: $Browser"

    Start-Process $Browser -ArgumentList "`"$Url`"" -ErrorAction Stop

    Write-Verbose "  Browser launched"
    Write-Verbose ""
}

# So that '. ./Start-SamlLogin.ps1' can simply load the functions rather than launching everything.
if ($MyInvocation.InvocationName -ne '.') {
    if (-not $ConnectionString) {
        throw "Redis connection string not provided. Set FW:REDIS_CONNECTION_STRING or pass -ConnectionString."
    }

    $sw                = [System.Diagnostics.Stopwatch]::StartNew()
    $relayState        = [guid]::NewGuid().ToString()
    $brightmetricsHost = "http://localhost:$Port"
    $acsUrl            = "$Issuer/${env:FW:ACS_ENDPOINT}"

    Write-Verbose ""
    Write-Verbose "SAML login flow"
    Write-Verbose "  RelayState        = $relayState"
    Write-Verbose "  BrightmetricsHost = $brightmetricsHost"
    Write-Verbose "  SamlId            = $SamlId"
    Write-Verbose ""

    $xml         = New-SamlAuthnRequestXml -Issuer $Issuer -AcsUrl $acsUrl -Destination $IdpUrl
    $samlRequest = ConvertTo-SamlRedirectPayload -Xml $xml

    Set-SamlRelayState `
        -RelayState        $relayState `
        -BrightmetricsHost $brightmetricsHost `
        -SamlId            $SamlId `
        -ConnectionString  $ConnectionString `
        -ExpirySeconds     $RelayStateTtlSec

    $url = Get-SamlRedirectUrl -IdpUrl $IdpUrl -SamlRequest $samlRequest -RelayState $relayState

    Start-SamlLoginInBrowser -Url $url -Browser $Browser

    Write-Verbose "Finished in $($sw.Elapsed)"
    $sw.Stop()
}

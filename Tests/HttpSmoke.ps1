param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$server = Join-Path $repository "Server/bin/$Configuration/net10.0/PdfCustode.Server.dll"
if (!(Test-Path -LiteralPath $server)) { throw 'Compilare prima con dotnet build Tests -c Release.' }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('pdfcustode-http-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$authority = [Security.Cryptography.RSA]::Create(2048)
$device = [Security.Cryptography.RSA]::Create(4096)
$other = [Security.Cryptography.RSA]::Create(2048)
$serviceProcess = $null
try {
    $code = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($code)))
    $state = @{ DocumentId='smoke-document'; DocumentKey=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)); Codes=@(@{CodeHash=$hash; DeviceKeyHash=$null; License=$null}) }
    $state | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $fixture 'server-state.json')
    $authority.ExportPkcs8PrivateKeyPem() | Set-Content (Join-Path $fixture 'authority-private.pem')
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $base = "http://127.0.0.1:$port"
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.ArgumentList.Add($server)
    $start.ArgumentList.Add('--urls'); $start.ArgumentList.Add($base)
    $start.ArgumentList.Add('--contentRoot'); $start.ArgumentList.Add($fixture)
    $start.Environment['PDFCUSTODE_DATA'] = $fixture
    $start.Environment['SharePoint__Enabled'] = 'false'
    $start.Environment['Logging__LogLevel__Default'] = 'Warning'
    $serviceProcess = [Diagnostics.Process]::Start($start)
    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(5)
    for ($attempt=0; $attempt -lt 40; $attempt++) {
        try { $response=$client.GetAsync($base).GetAwaiter().GetResult(); $response.Dispose(); break }
        catch { if ($serviceProcess.HasExited -or $attempt -eq 39) { throw }; Start-Sleep -Milliseconds 250 }
    }
    function Send-Activation([string]$body,[int]$expected,[string]$mime='application/json') {
        $content = [Net.Http.StringContent]::new($body,[Text.Encoding]::UTF8,$mime)
        try {
            $response = $client.PostAsync("$base/api/activate",$content).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -ne $expected) { throw "Atteso HTTP $expected, ricevuto $([int]$response.StatusCode)" }
                if (!$response.Headers.Contains('X-Request-Id')) { throw 'Identificativo di correlazione assente' }
                return $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            } finally { $response.Dispose() }
        } finally { $content.Dispose() }
    }
    $body = @{Code=$code;DocumentId='smoke-document';DevicePublicKey=[Convert]::ToBase64String($device.ExportSubjectPublicKeyInfo())}
    $profileError = Send-Activation ($body | ConvertTo-Json -Compress) 400
    if (!($profileError | ConvertFrom-Json).error) { throw 'Messaggio di validazione profilo assente' }
    $body.Nome = ('è' * 100)
    $body.Cognome = ('ò' * 100)
    $body.CodiceUtente = ('ù' * 128)
    $body.NomeMacchina = ('à' * 255)
    $body.Piattaforma = 'Windows'
    $json = $body | ConvertTo-Json -Compress -EscapeHandling EscapeNonAscii
    if ([Text.Encoding]::UTF8.GetByteCount($json) -le 4096) { throw 'Fixture Unicode troppo piccola per verificare il limite body' }
    $first = Send-Activation $json 200
    $retryBody = @{Code=$code;DocumentId='smoke-document';DevicePublicKey=$body.DevicePublicKey}
    $retry = Send-Activation ($retryBody | ConvertTo-Json -Compress) 200
    if ($first -ne $retry) { throw 'Retry diverso dalla prima licenza' }
    $body.DevicePublicKey = [Convert]::ToBase64String($other.ExportSubjectPublicKeyInfo())
    $null = Send-Activation ($body|ConvertTo-Json -Compress) 403
    $body.DocumentId = 'wrong-document'
    $null = Send-Activation ($body|ConvertTo-Json -Compress) 403
    $null = Send-Activation '{' 400
    $null = Send-Activation 'test' 415 'text/plain'
    $null = Send-Activation ('{"Code":"' + ('X'*9000) + '"}') 413
    for($i=0;$i -lt 12;$i++) { $last=$client.PostAsync("$base/api/activate",[Net.Http.StringContent]::new('{}',[Text.Encoding]::UTF8,'application/json')).GetAwaiter().GetResult() }
    if ([int]$last.StatusCode -ne 429) { throw 'Rate limit non applicato' }
    $last.Dispose()
    $history = Get-ChildItem (Join-Path $fixture '.audit/history') -Filter '*.jsonl' | Get-Content
    if ($history.Count -lt 10) { throw 'Eventi audit mancanti' }
    if (($history -join "`n").Contains($code)) { throw 'Codice presente nei log' }
    if (($history -join "`n").Contains($body.CodiceUtente)) { throw 'Dati del profilo presenti nei log' }
    Write-Output "HTTP smoke PASS: profile validation, maximum Unicode profile, activation, profile-free retry, device binding, unknown document, 400/413/415/429, correlation, $($history.Count) audit events, no plaintext code or profile."
} finally {
    if ($serviceProcess -and !$serviceProcess.HasExited) { $serviceProcess.Kill(); $serviceProcess.WaitForExit() }
    $authority.Dispose(); $device.Dispose(); $other.Dispose()
    # This uniquely generated directory contains only synthetic test data.
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf).StartsWith('pdfcustode-http-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

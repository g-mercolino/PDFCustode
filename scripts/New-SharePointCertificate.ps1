param([string]$PublicCertificatePath = (Join-Path $env:TEMP 'PdfCustode-SharePoint.cer'))
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $PublicCertificatePath) { throw 'Il file pubblico esiste già. Scegliere un percorso nuovo.' }
$certificate = New-SelfSignedCertificate -Subject 'CN=PdfCustode SharePoint' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears(1)
Export-Certificate -Cert $certificate -FilePath $PublicCertificatePath | Out-Null
Write-Output "Certificato PUBBLICO da caricare in Entra: $PublicCertificatePath"
Write-Output "CertificateThumbprint: $($certificate.Thumbprint)"
Write-Output 'La chiave privata resta nello store CurrentUser del PC. Avviare il servizio con lo stesso account.'

param([switch]$SoloWindows)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet publish App/PdfCustode.App.csproj -c Release -f net10.0-windows10.0.19041.0 -p:RuntimeIdentifierOverride=win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:AllowInsecureTestServer=true '-p:AndroidSdkDirectory=C:/Program Files (x86)/Android/android-sdk' '-p:JavaSdkDirectory=C:/Program Files (x86)/Android/openjdk/jdk-21.0.8' -o Destinatario-Windows --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw 'Compilazione Windows fallita.' }
if (!$SoloWindows) {
    dotnet publish App/PdfCustode.App.csproj -c Release -f net10.0-android -p:AllowInsecureTestServer=true -p:RunAOTCompilation=false -p:AndroidPackageFormats=apk '-p:AndroidSdkDirectory=C:/Program Files (x86)/Android/android-sdk' '-p:JavaSdkDirectory=C:/Program Files (x86)/Android/openjdk/jdk-21.0.8' -o Destinatario-Android --source https://api.nuget.org/v3/index.json
    if ($LASTEXITCODE -ne 0) { throw 'Compilazione Android fallita.' }
}

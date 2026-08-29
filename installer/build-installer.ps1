# Builds the Multi-SSH Windows installer.
# 1) Publishes a self-contained single-file exe (no .NET needed on the target).
# 2) Code-signs the exe (if the signing cert exists), then compiles the installer,
#    then code-signs the installer too. Signing is skipped with a warning if no cert.
#
# Usage:  pwsh installer\build-installer.ps1
# Requires: .NET SDK, Inno Setup 6 (ISCC.exe). For signing: run make-signing-cert.ps1 first.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot   # repo root
Push-Location $root
try {
    $subject = "CN=Multi-SSH (joc00p)"
    $timestamp = "http://timestamp.digicert.com"
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey } | Select-Object -First 1

    function Sign([string]$file) {
        if (-not $cert) { Write-Warning "No signing cert ($subject) - leaving $file unsigned."; return }
        $r = Set-AuthenticodeSignature -FilePath $file -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $timestamp
        Write-Host ("Signed {0}  (signer={1}, timestamped={2})" -f `
            (Split-Path $file -Leaf), $r.SignerCertificate.Subject, [bool]$r.TimeStamperCertificate) -ForegroundColor Green
    }

    Write-Host "Publishing self-contained exe..." -ForegroundColor Cyan
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true | Out-Host

    $publishExe = "bin\Release\net8.0-windows\win-x64\publish\Multi-SSH.exe"
    Sign $publishExe   # sign the app before packaging so the installed exe is signed

    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup (ISCC.exe) not found. Install: winget install JRSoftware.InnoSetup" }

    Write-Host "Compiling installer..." -ForegroundColor Cyan
    & $iscc "installer\Multi-SSH.iss" | Out-Host

    $setup = "installer\Output\Multi-SSH-Setup-1.0.55.exe"
    Sign $setup        # sign the installer itself

    Write-Host "Done: $setup" -ForegroundColor Green
}
finally {
    Pop-Location
}

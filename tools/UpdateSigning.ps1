# Shared helpers for the ScheduleWidget update scripts (dot-source: . "$PSScriptRoot\UpdateSigning.ps1").
# Signature = RSA 3072, SHA-256, PKCS#1 v1.5 over the UTF-8 canonical string
#   ScheduleWidget|<version>|<file>|<size>|<sha256>
# The PRIVATE key lives outside the project in %USERPROFILE%\.schedulewidget\update-signing-key.xml,
# protected with DPAPI (CurrentUser). It is never printed and never written anywhere else.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$script:UpdateKeyEntropy = [Text.Encoding]::UTF8.GetBytes('ScheduleWidget|update-signing|v1')

function Get-UpdateKeyDirectory { Join-Path $env:USERPROFILE '.schedulewidget' }
function Get-UpdatePrivateKeyPath { Join-Path (Get-UpdateKeyDirectory) 'update-signing-key.xml' }
function Get-UpdatePublicKeySourcePath { Join-Path (Split-Path $PSScriptRoot -Parent) 'ScheduleWidget\Services\UpdatePublicKey.cs' }

function Save-UpdatePrivateKey([Security.Cryptography.RSACryptoServiceProvider]$Rsa, [string]$Path) {
    $plain = [Text.Encoding]::UTF8.GetBytes($Rsa.ToXmlString($true))
    try {
        $blob = [Security.Cryptography.ProtectedData]::Protect($plain, $script:UpdateKeyEntropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    } finally { [Array]::Clear($plain, 0, $plain.Length) }
    $dir = Split-Path $Path -Parent
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    # File content: a marker line + base64 of the DPAPI blob (only this Windows user can decrypt it).
    $text = "SCHEDULEWIDGET-UPDATE-KEY-DPAPI-V1`r`n" + [Convert]::ToBase64String($blob) + "`r`n"
    [IO.File]::WriteAllText($Path, $text, (New-Object Text.UTF8Encoding($false)))
}

function Read-UpdatePrivateKey([string]$Path = (Get-UpdatePrivateKeyPath)) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Update signing key not found: $Path . Run tools\New-UpdateKey.ps1 first (on the PC that publishes updates)."
    }
    $lines = [IO.File]::ReadAllLines($Path) | Where-Object { $_.Trim() }
    if ($lines.Count -lt 2 -or $lines[0].Trim() -ne 'SCHEDULEWIDGET-UPDATE-KEY-DPAPI-V1') { throw "Unrecognized key file format: $Path" }
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($lines[1].Trim()), $script:UpdateKeyEntropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try {
        $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
        $rsa.PersistKeyInCsp = $false
        $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($plain))
    } finally { [Array]::Clear($plain, 0, $plain.Length) }
    if ($rsa.PublicOnly) { throw 'Key file does not contain a private key.' }
    return $rsa
}

function Get-UpdatePublicKeyXml([string]$SourcePath = (Get-UpdatePublicKeySourcePath)) {
    $src = [IO.File]::ReadAllText($SourcePath)
    $m = [regex]::Match($src, 'Xml\s*=\s*"([^"]+)"')
    if (-not $m.Success) { throw "No public key constant in $SourcePath" }
    return $m.Groups[1].Value
}

function Get-UpdateCanonicalString([string]$Version, [string]$File, [long]$Size, [string]$Sha256) {
    return 'ScheduleWidget|' + $Version + '|' + $File + '|' + $Size.ToString([Globalization.CultureInfo]::InvariantCulture) + '|' + $Sha256.ToLowerInvariant()
}

function New-UpdateSignature([Security.Cryptography.RSACryptoServiceProvider]$Rsa, [string]$Canonical) {
    $data = [Text.Encoding]::UTF8.GetBytes($Canonical)
    $sig = $Rsa.SignData($data, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    return [Convert]::ToBase64String($sig)
}

function Test-UpdateSignature([string]$PublicKeyXml, [string]$Canonical, [string]$SignatureBase64) {
    try {
        $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
        $rsa.PersistKeyInCsp = $false
        $rsa.FromXmlString($PublicKeyXml)
        $data = [Text.Encoding]::UTF8.GetBytes($Canonical)
        return $rsa.VerifyData($data, [Convert]::FromBase64String($SignatureBase64), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    } catch { return $false }
}

function Get-FileSha256Hex([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $fs = [IO.File]::OpenRead($Path)
    try { return (($sha.ComputeHash($fs) | ForEach-Object { $_.ToString('x2') }) -join '') } finally { $fs.Dispose(); $sha.Dispose() }
}

function Get-MSBuildPath {
    $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $p = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($p) { return $p }
    }
    throw 'MSBuild not found (install Visual Studio Build Tools or run from a Developer PowerShell).'
}

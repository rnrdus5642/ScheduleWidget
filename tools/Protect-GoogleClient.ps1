# Turns the Google OAuth client JSON (downloaded from Google Cloud Console, "데스크톱 앱") into ScheduleWidget\google_client.bin,
# which the build embeds in ScheduleWidget.exe (AES-256-CBC, same key as GoogleCalendarService.DecryptClient).
# Usage: powershell -ExecutionPolicy Bypass -File tools\Protect-GoogleClient.ps1 -Json <downloaded client_secret_....json>
param([Parameter(Mandatory = $true)][string]$Json, [string]$Out)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$plain = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Json))
$parsed = [Text.Encoding]::UTF8.GetString($plain) | ConvertFrom-Json
$client = if ($parsed.installed) { $parsed.installed } elseif ($parsed.web) { $parsed.web } else { $parsed }
if (-not $client.client_id -or -not ($client.client_id -like '*.apps.googleusercontent.com')) { throw 'Google OAuth 클라이언트 JSON이 아닙니다.' }

$sha = [Security.Cryptography.SHA256]::Create()
$aes = [Security.Cryptography.Aes]::Create()
$aes.Key = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes('ScheduleWidget|google-client|v1|7f3c9a2e5d8b41f6'))
$aes.Mode = 'CBC'; $aes.Padding = 'PKCS7'; $aes.GenerateIV()
$cipher = $aes.CreateEncryptor().TransformFinalBlock($plain, 0, $plain.Length)
$out = if ($Out) { $Out } else { Join-Path $root 'ScheduleWidget\google_client.bin' }
[IO.File]::WriteAllBytes($out, [byte[]]($aes.IV + $cipher))
"Wrote $out (client $($client.client_id.Substring(0, 12))…). Rebuild to embed it; do not keep the plain JSON in the project."

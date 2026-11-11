# Build the website installer from a signed release ZIP. Prerequisites are downloaded from Microsoft and publisher-verified.
param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [string]$CompilerPath = $env:SCHEDULEWIDGET_ISCC
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UpdateSigning.ps1')
$projectRoot = Split-Path $PSScriptRoot -Parent
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path

if (-not $CompilerPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $CompilerPath = $command.Source }
    $candidates = @(
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe')
    )
    $toolRoot = Join-Path $projectRoot '.work\tools'
    if (Test-Path -LiteralPath $toolRoot) {
        $candidates += @(Get-ChildItem -LiteralPath $toolRoot -Directory -Filter 'inno-setup-*' |
            Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'compiler\ISCC.exe' })
    }
    foreach ($candidate in $candidates) {
        if (-not $CompilerPath -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { $CompilerPath = $candidate }
    }
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Install Inno Setup 7 or set SCHEDULEWIDGET_ISCC / -CompilerPath to ISCC.exe.'
}
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path

$manifest = Get-Content -LiteralPath (Join-Path $publishRoot 'latest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.app -ne 'ScheduleWidget' -or $manifest.version -notmatch '\A\d+(?:\.\d+){1,3}\z' -or
    $manifest.file -cne ('ScheduleWidget-' + $manifest.version + '.zip')) { throw 'Unexpected release manifest.' }
$canonical = Get-UpdateCanonicalString $manifest.version $manifest.file $manifest.size $manifest.sha256
if (-not (Test-UpdateSignature (Get-UpdatePublicKeyXml) $canonical $manifest.signature)) { throw 'Release signature is invalid.' }
$zipPath = Join-Path $publishRoot $manifest.file
if ((Get-Item -LiteralPath $zipPath).Length -ne $manifest.size -or (Get-FileSha256Hex $zipPath) -ne $manifest.sha256) { throw 'Release ZIP hash or size is invalid.' }

# Explicit payload list keeps user data, test executables and signing material out of the installer.
$required = @('ScheduleWidget.exe','ScheduleWidget.Updater.exe','ScheduleWidget.exe.config',
    'Newtonsoft.Json.dll','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll',
    'Player/character.html','Player/player.html','runtimes/win-x64/native/WebView2Loader.dll',
    'runtimes/win-x86/native/WebView2Loader.dll','runtimes/win-arm64/native/WebView2Loader.dll')
foreach ($pet in @('mochi-white','mochi-black','mochi-blue','mochi-red')) {
    $required += 'DefaultPets/' + $pet + '/pet.json'
    $required += 'DefaultPets/' + $pet + '/spritesheet.webp'
}
$allowed = $required + @('ScheduleWidget.Updater.exe.config')
$payload = Join-Path $publishRoot ('installer-payload-' + [guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($zip.Entries | ForEach-Object { $_.FullName })
    if (@($names | Sort-Object -Unique).Count -ne $names.Count) { throw 'Duplicate ZIP entries.' }
    foreach ($name in $names) { if ($allowed -cnotcontains $name) { throw ('Unexpected ZIP entry: ' + $name) } }
    foreach ($name in $required) { if ($names -cnotcontains $name) { throw ('Missing ZIP entry: ' + $name) } }
    foreach ($entry in $zip.Entries) {
        $target = Join-Path $payload $entry.FullName
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$false)
    }
} finally { $zip.Dispose() }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'ScheduleWidget.exe')).ProductVersion
if ($version -ne $manifest.version) { throw 'Application version does not match the signed release.' }

$prerequisites = Join-Path $projectRoot '.work\tools\setup-prerequisites'
[IO.Directory]::CreateDirectory($prerequisites) | Out-Null
$downloads = @(
    @{Name='MicrosoftEdgeWebview2Setup.exe'; Url='https://go.microsoft.com/fwlink/p/?LinkId=2124703'},
    @{Name='ndp48-web.exe'; Url='https://go.microsoft.com/fwlink/?LinkId=2085155'}
)
$verifiedPrerequisites = @()
foreach ($download in $downloads) {
    $target = Join-Path $prerequisites $download.Name
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        $temporary = $target + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        try {
            Invoke-WebRequest -Uri $download.Url -UseBasicParsing -OutFile $temporary -TimeoutSec 120
            $signature = Get-AuthenticodeSignature -LiteralPath $temporary
            if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|, )O=Microsoft Corporation(?:,|$)') { throw ('Invalid Microsoft signature: ' + $download.Name) }
            [IO.File]::Move($temporary,$target)
        } finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $target
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|, )O=Microsoft Corporation(?:,|$)') { throw ('Invalid cached prerequisite: ' + $download.Name) }
    $verifiedPrerequisites += [pscustomobject]@{File=$download.Name;SHA256=(Get-FileSha256Hex $target);Publisher=$signature.SignerCertificate.Subject}
}

& $CompilerPath '/Qp' ('/DPayloadDir=' + $payload) ('/DAppVersion=' + $version) ('/DOutputDir=' + $publishRoot) ('/DPrerequisitesDir=' + $prerequisites) (Join-Path $projectRoot 'installer\ScheduleWidget.iss')
if ($LASTEXITCODE -ne 0) { throw ('Installer compilation failed: ' + $LASTEXITCODE) }
$setupPath = Join-Path $publishRoot 'ScheduleWidget-Setup.exe'
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw 'Installer was not produced.' }
$receipt = [pscustomobject]@{Version=$version;Compiler=$CompilerPath;Installer=$setupPath;Size=(Get-Item -LiteralPath $setupPath).Length;
    SHA256=(Get-FileSha256Hex $setupPath);Payload=$payload;PayloadFiles=$names.Count;Prerequisites=$verifiedPrerequisites}
$receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $publishRoot 'installer-build.json') -Encoding UTF8
Write-Host ('Installer: ' + $setupPath + ' (' + $receipt.Size + ' bytes, sha256 ' + $receipt.SHA256 + ')')

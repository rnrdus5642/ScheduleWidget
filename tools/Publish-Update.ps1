# Builds, packages, signs and publishes a ScheduleWidget update.
#   powershell -ExecutionPolicy Bypass -File tools\Publish-Update.ps1 [-Notes "..."] [-Repo owner/repo] [-DryRun]
# Steps: stamp AssemblyInformationalVersion (numeric System.Version) in ScheduleWidget\Properties\AssemblyInfo.cs -> build Release into
# .work\publish\<version>\app\ -> zip (no *.pdb, doc *.xml, Pet folders; DefaultPets kept) -> latest.json signed with the DPAPI-protected key
# (both left in .work\publish\<version>\) -> gh release create v<version> <zip> latest.json --repo <owner/repo>.
# Repo: -Repo, else UpdateClient.GitHubRepository in the app source (a public repository: the app reads releases without a token).
# -DryRun prints the GitHub command instead of running it. -ObjName sets obj\<name>\ (default Publish). -FromFolder <dir> packages an existing build folder (no stamp, no build).
param(
    [string]$Notes = '',
    [string]$Repo,
    [string]$Version,
    [string]$FromFolder,
    [switch]$DryRun,
    [string]$ObjName = 'Publish'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UpdateSigning.ps1')
$root = Split-Path $PSScriptRoot -Parent

# 1. Key first: no key, no publish. The key must match the public key embedded in the app source.
$rsa = Read-UpdatePrivateKey
$publicXml = Get-UpdatePublicKeyXml
$embeddedModulus = ([xml]$publicXml).RSAKeyValue.Modulus
$keyModulus = ([xml]$rsa.ToXmlString($false)).RSAKeyValue.Modulus
if ($embeddedModulus -ne $keyModulus) { throw 'The signing key does not match ScheduleWidget\Services\UpdatePublicKey.cs. Run tools\New-UpdateKey.ps1 (without -Force) to rewrite the public key, then rebuild.' }

# 2. Version
$sourceFolder = $null
if (-not $Version -and $FromFolder) {
    $sourceFolder = (Resolve-Path -LiteralPath $FromFolder).Path
    $sourceExe = Join-Path $sourceFolder 'ScheduleWidget.exe'
    if (-not (Test-Path -LiteralPath $sourceExe)) { throw "Source folder is missing ScheduleWidget.exe: $FromFolder" }
    $Version = [Diagnostics.FileVersionInfo]::GetVersionInfo($sourceExe).ProductVersion
}
if (-not $Version) { $Version = (Get-Date).ToString('yyyy.MMdd.HHmm', [Globalization.CultureInfo]::InvariantCulture) }
if ($Version -notmatch '\A\d+(?:\.\d+){1,3}\z') { throw "Version must be a numeric System.Version with 2 to 4 components, got '$Version'." }
$parsedVersion = $null
if (-not [Version]::TryParse($Version, [ref]$parsedVersion)) { throw "Version is outside the supported System.Version range: '$Version'." }
if ($FromFolder -and -not $sourceFolder) { $sourceFolder = (Resolve-Path -LiteralPath $FromFolder).Path }
if ($sourceFolder -and -not (Test-Path -LiteralPath $sourceFolder -PathType Container)) { throw "Source folder is not a directory: $FromFolder" }

# 2b. Repository (checked before building: no repository, no release; -DryRun only prints a placeholder)
if (-not $Repo) {
    $clientSource = Get-Content -LiteralPath (Join-Path $root 'ScheduleWidget\Services\UpdateClient.cs') -Raw -Encoding UTF8
    $m = [regex]::Match($clientSource, 'GitHubRepository\s*=\s*"([^"]*)"')
    if ($m.Success) { $Repo = $m.Groups[1].Value.Trim() }
}
if (-not $Repo -or $Repo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    if (-not $DryRun) { throw 'No GitHub repository: pass -Repo owner/repo or set UpdateClient.GitHubRepository.' }
    Write-Warning 'No GitHub repository set; the dry run shows <owner/repo>.'
    $Repo = '<owner/repo>'
}

$infoPath = $null
$infoBytes = $null
$stamped = $null
$stampedBytes = $null
$publishSucceeded = $false
try {
$work = Join-Path $root ".work\publish\$Version"
$appOut = Join-Path $work 'app'
if ($sourceFolder) {
    $sourceFull = [IO.Path]::GetFullPath($sourceFolder).TrimEnd([char]92, [char]47)
    $workFull = [IO.Path]::GetFullPath($work).TrimEnd([char]92, [char]47)
    $separator = [IO.Path]::DirectorySeparatorChar
    $sourceInsideWork = $sourceFull.Equals($workFull, [StringComparison]::OrdinalIgnoreCase) -or
        $sourceFull.StartsWith($workFull + $separator, [StringComparison]::OrdinalIgnoreCase)
    $workInsideSource = $workFull.StartsWith($sourceFull + $separator, [StringComparison]::OrdinalIgnoreCase)
    if ($sourceInsideWork -or $workInsideSource) { throw "-FromFolder overlaps the publish output '$work'. Choose a source and version whose folders do not overlap." }
}
if (Test-Path -LiteralPath $work) {
    if (-not (Test-Path -LiteralPath $work -PathType Container) -or ([IO.File]::GetAttributes($work) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Publish output is not a regular directory: $work"
    }
    Remove-Item -LiteralPath $work -Recurse -Force
}
New-Item -ItemType Directory -Path $work | Out-Null

if ($FromFolder) {
    Copy-Item -Path (Join-Path $sourceFolder '*') -Destination (New-Item -ItemType Directory -Path $appOut).FullName -Recurse -Force
    Write-Host "Packaging existing folder $sourceFolder as $Version"
} else {
    # 3. Stamp the version into AssemblyInfo.cs (keeps its encoding / line endings); restored if the build fails.
    $infoPath = Join-Path $root 'ScheduleWidget\Properties\AssemblyInfo.cs'
    $infoBytes = [IO.File]::ReadAllBytes($infoPath)
    $hasBom = $infoBytes.Length -ge 3 -and $infoBytes[0] -eq 0xEF -and $infoBytes[1] -eq 0xBB -and $infoBytes[2] -eq 0xBF
    $info = (New-Object Text.UTF8Encoding($false)).GetString($infoBytes, $(if ($hasBom) { 3 } else { 0 }), $infoBytes.Length - $(if ($hasBom) { 3 } else { 0 }))
    $nl = if ($info.Contains("`r`n")) { "`r`n" } else { "`n" }
    $attr = "[assembly: AssemblyInformationalVersion(`"$Version`")]"
    $pattern = '\[assembly:\s*AssemblyInformationalVersion\("[^"]*"\)\]'
    $stamped = if ([regex]::IsMatch($info, $pattern)) { [regex]::Replace($info, $pattern, $attr) } else { $info.TrimEnd("`r", "`n") + $nl + $attr + $nl }
    $stampedEncoding = New-Object Text.UTF8Encoding($hasBom)
    $stampedBytes = [byte[]]($stampedEncoding.GetPreamble() + $stampedEncoding.GetBytes($stamped))
    $stampTemp = $infoPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $stampBackup = $infoPath + '.' + [Guid]::NewGuid().ToString('N') + '.bak'
    try {
        [IO.File]::WriteAllBytes($stampTemp, $stampedBytes)
        [IO.File]::Replace($stampTemp, $infoPath, $stampBackup)
    } finally {
        foreach ($temporary in $stampTemp, $stampBackup) { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue } }
    }
    Write-Host "Version $Version stamped into AssemblyInfo.cs"

    $msbuild = Get-MSBuildPath
    & $msbuild (Join-Path $root 'ScheduleWidget\ScheduleWidget.csproj') /nologo /v:m /p:Configuration=Release "/p:OutputPath=$appOut\" "/p:IntermediateOutputPath=obj\$ObjName\"
    if ($LASTEXITCODE -ne 0) { throw "App build failed ($LASTEXITCODE)." }
    $updOut = Join-Path $work 'updater'
    & $msbuild (Join-Path $root 'Updater\ScheduleWidget.Updater.csproj') /nologo /v:m /p:Configuration=Release "/p:OutputPath=$updOut\" "/p:IntermediateOutputPath=obj\$ObjName\"
    if ($LASTEXITCODE -ne 0) { throw "Updater build failed ($LASTEXITCODE)." }
    if (-not (Test-Path -LiteralPath (Join-Path $appOut 'ScheduleWidget.Updater.exe'))) {
        Copy-Item -LiteralPath (Join-Path $updOut 'ScheduleWidget.Updater.exe') -Destination $appOut
        $cfg = Join-Path $updOut 'ScheduleWidget.Updater.exe.config'
        if (Test-Path -LiteralPath $cfg) { Copy-Item -LiteralPath $cfg -Destination $appOut }
        Write-Host 'ScheduleWidget.Updater.exe copied into the package (not part of the app build output).'
    }
}
$packageVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $appOut 'ScheduleWidget.exe')).ProductVersion
if ($packageVersion -ne $Version) { throw "Package exe reports version '$packageVersion', expected '$Version'." }

foreach ($required in 'ScheduleWidget.exe', 'ScheduleWidget.Updater.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $appOut $required))) { throw "Package folder is missing $required." }
}
# The default characters (기본 캐릭터) ship in DefaultPets\<id>\ (pet.json + spritesheet.webp): an update without them would
# leave fresh installs with no character.
foreach ($pet in 'mochi-white', 'mochi-black', 'mochi-blue', 'mochi-red') {
    foreach ($file in 'pet.json', 'spritesheet.webp') {
        if (-not (Test-Path -LiteralPath (Join-Path $appOut "DefaultPets\$pet\$file"))) { throw "Package folder is missing DefaultPets\$pet\$file." }
    }
}

# 4. Zip (entries use '/'; excluded: *.pdb, XML doc files next to a same-named dll/exe, any Pet folder, an old Characters folder, vshost files)
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zipName = "ScheduleWidget-$Version.zip"
$zipPath = Join-Path $work $zipName
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
$count = 0
try {
    foreach ($f in Get-ChildItem -LiteralPath $appOut -Recurse -File) {
        $rel = $f.FullName.Substring($appOut.Length).TrimStart('\')
        $segments = $rel.Split('\')
        if ($segments | Where-Object { $_ -ieq 'Pet' }) { continue }
        if ($segments.Count -gt 1 -and $segments[0] -ieq 'Characters') { continue } # Codex characters are never shipped (CodexPets)
        # DefaultPets (기본 캐릭터) is kept: only its pet.json and spritesheet.webp, nothing else that may sit there.
        if ($segments.Count -gt 1 -and $segments[0] -ieq 'DefaultPets' -and -not ($segments.Count -eq 3 -and ($segments[2] -ieq 'pet.json' -or $segments[2] -ieq 'spritesheet.webp'))) { continue }
        if ($f.Extension -ieq '.pdb' -or $f.Name -like '*.vshost.*') { continue }
        if ($f.Extension -ieq '.xml') {
            $base = Join-Path $f.DirectoryName $f.BaseName
            if ((Test-Path -LiteralPath "$base.dll") -or (Test-Path -LiteralPath "$base.exe")) { continue }
        }
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, ($segments -join '/'), [IO.Compression.CompressionLevel]::Optimal)
        $count++
    }
} finally { $zip.Dispose() }

# 5. Manifest + signature (verified against the embedded public key before anything is published)
$size = (Get-Item -LiteralPath $zipPath).Length
$sha = Get-FileSha256Hex $zipPath
$canonical = Get-UpdateCanonicalString $Version $zipName $size $sha
$signature = New-UpdateSignature $rsa $canonical
$rsa.Dispose()
if (-not (Test-UpdateSignature $publicXml $canonical $signature)) { throw 'Signature self-check failed.' }
function J([string]$s) { return ($s | ConvertTo-Json) }
$published = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz', [Globalization.CultureInfo]::InvariantCulture)
$json = "{`n" +
    "  `"app`": `"ScheduleWidget`",`n" +
    "  `"version`": $(J $Version),`n" +
    "  `"published`": $(J $published),`n" +
    "  `"file`": $(J $zipName),`n" +
    "  `"size`": $size,`n" +
    "  `"sha256`": $(J $sha),`n" +
    "  `"notes`": $(J $Notes),`n" +
    "  `"signature`": $(J $signature)`n}`n"
$manifestPath = Join-Path $work 'latest.json'
[IO.File]::WriteAllText($manifestPath, $json, (New-Object Text.UTF8Encoding($false)))
Write-Host "Package: $zipPath ($count files, $size bytes, sha256 $sha)"

# 6. GitHub release (the app reads latest.json and the zip from the latest release)
$notesFile = Join-Path $work 'release-notes.txt'
[IO.File]::WriteAllText($notesFile, $(if ($Notes) { $Notes } else { "ScheduleWidget $Version" }), (New-Object Text.UTF8Encoding($false)))
$ghArgs = @('release', 'create', "v$Version", $zipPath, $manifestPath, '--repo', $Repo, '--title', "ScheduleWidget $Version", '--notes-file', $notesFile, '--latest')
if ($DryRun) {
    $publishSucceeded = $true
    Write-Host ('[DryRun] gh ' + (($ghArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '))
} else {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) not found.' }
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)." }
    $publishSucceeded = $true
    Write-Host "Published GitHub release v$Version to $Repo"
}
Write-Host "Done: $Version"
} finally {
    if (-not $publishSucceeded -and $infoPath -and $infoBytes -and $stamped) {
        $nowBytes = [IO.File]::ReadAllBytes($infoPath)
        if ([Convert]::ToBase64String($nowBytes) -ceq [Convert]::ToBase64String($stampedBytes)) {
            [IO.File]::WriteAllBytes($infoPath, $infoBytes)
            Write-Host 'AssemblyInfo.cs restored after failed publish.'
        } else {
            Write-Host 'AssemblyInfo.cs changed during publishing; left as is.'
        }
    }
}

# Exercises real Setup/Uninstall with an isolated app ID, mutex, startup key and shortcuts. Never launches the widget.
param([Parameter(Mandatory=$true)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$release = Get-Content -LiteralPath (Join-Path $PublishDirectory 'installer-build.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $release.Installer).Hash -ne $release.SHA256) { throw 'Installer differs from the build receipt.' }
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry32)
$machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry32)
$webViewKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$webUser = $registry.OpenSubKey($webViewKey)
$webMachine = $machine.OpenSubKey($webViewKey)
$framework = $machine.OpenSubKey('Software\Microsoft\NET Framework Setup\NDP\v4\Full')
try {
    $webReady = ($webUser -and $webUser.GetValue('pv') -and $webUser.GetValue('pv') -ne '0.0.0.0') -or
        ($webMachine -and $webMachine.GetValue('pv') -and $webMachine.GetValue('pv') -ne '0.0.0.0')
    if (-not $webReady -or -not $framework -or $framework.GetValue('Release',0) -lt 461808) {
        throw 'Installer checks require existing .NET 4.7.2+ and WebView2; tests do not change shared runtimes.'
    }
} finally { if ($webUser) {$webUser.Dispose()}; if ($webMachine) {$webMachine.Dispose()}; if ($framework) {$framework.Dispose()}; $machine.Dispose() }

$id = [guid]::NewGuid().ToString('N')
$product = 'SWInstallerCheck-' + $id
$mutexName = $product + '-Running'
$startupKey = 'Software\ScheduleWidgetInstallerChecks\' + $id + '\Run'
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $product + '_is1'
$runRoot = Join-Path $projectRoot ('.work\installer-checks\' + $id)
$appDir = [IO.Path]::GetFullPath((Join-Path $runRoot 'app'))
if (-not $appDir.StartsWith([IO.Path]::GetFullPath($runRoot) + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Test destination escaped the workspace.' }
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
$compilerArguments = @('/Qp',('/DPayloadDir=' + $release.Payload),('/DAppVersion=' + $release.Version),('/DOutputDir=' + $runRoot),
    ('/DPrerequisitesDir=' + (Join-Path $projectRoot '.work\tools\setup-prerequisites')),('/DProductId=' + $product),
    ('/DRuntimeMutex=' + $mutexName),('/DStartupRegistryKey=' + $startupKey),(Join-Path $projectRoot 'installer\ScheduleWidget.iss'))
& $release.Compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Fixture installer compilation failed.' }
$setup = Join-Path $runRoot ($product + '-Setup.exe')
$appExe = Join-Path $appDir 'ScheduleWidget.exe'
$uninstaller = Join-Path $appDir 'unins000.exe'
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($product + '.lnk')
$startLink = Join-Path ([Environment]::GetFolderPath('Programs')) ($product + '\ScheduleWidget.lnk')
$script:passed = 0
$script:installerTimedOut = $false
$completed = $false

function Require([bool]$Condition,[string]$Description) {
    if (-not $Condition) { throw $Description }
    $script:passed++
    Write-Host ('PASS ' + $Description)
}
function RunInstaller([string]$Executable,[string]$Label,[bool]$Install) {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG="' + (Join-Path $runRoot ($Label + '.log')) + '"'))
    if ($Install) { $arguments += @('/DIR="' + $appDir + '"') }
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(60000)) {
            $script:installerTimedOut = $true
            throw ('Installer is still running: PID ' + $process.Id + '; log ' + $Label)
        }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
function ReadStartup {
    $key = $registry.OpenSubKey($startupKey)
    try { if ($key) { return $key.GetValue('ScheduleWidget') } } finally { if ($key) {$key.Dispose()} }
}
function WriteStartup([string]$Command) {
    $key = $registry.CreateSubKey($startupKey)
    try { $key.SetValue('ScheduleWidget',$Command) } finally { $key.Dispose() }
}
function CopyFixture([string]$Source,[string]$Target) {
    # Explorer/antivirus can briefly read a newly installed executable while resolving its shortcut icon.
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try { [IO.File]::Copy($Source,$Target,$true); return }
        catch [IO.IOException] { if ($attempt -eq 29) { throw }; Start-Sleep -Milliseconds 200 }
    }
}
function FindUninstaller {
    $key = $registry.OpenSubKey($uninstallKey)
    try { $path = $key.GetValue('UninstallString').Trim('"') } finally { if ($key) {$key.Dispose()} }
    if ([IO.Path]::GetDirectoryName($path) -ne $appDir -or [IO.Path]::GetFileName($path) -notmatch '^unins\d+\.exe$') { throw 'Unexpected fixture uninstaller path.' }
    return $path
}

try {
    Require ((RunInstaller $setup 'fresh-install' $true) -eq 0) 'Fresh installation succeeds without launching the widget'
    foreach ($file in (Get-ChildItem -LiteralPath $release.Payload -File -Recurse)) {
        $relative = $file.FullName.Substring($release.Payload.Length + 1)
        if ((Get-FileHash -LiteralPath (Join-Path $appDir $relative)).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw ('Installed payload mismatch: ' + $relative) }
    }
    Require $true 'Installed files match every verified ZIP payload file'
    $key = $registry.OpenSubKey($uninstallKey)
    try { Require ($key -and $key.GetValue('DisplayVersion') -eq $release.Version -and $key.GetValue('InstallLocation').TrimEnd('\') -eq $appDir) 'Windows uninstall registration has the correct version and location' }
    finally { if ($key) {$key.Dispose()} }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($link in @($desktopLink,$startLink)) {
        $shortcut = $shell.CreateShortcut($link)
        Require ((Test-Path -LiteralPath $link) -and $shortcut.TargetPath -eq $appExe -and $shortcut.WorkingDirectory -eq $appDir) 'Shortcut targets the installed app and working directory'
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
    }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    $marker = Join-Path $appDir 'user-kept.txt'
    [IO.File]::WriteAllText($marker,'This unrelated file must survive setup and uninstall.')
    Require ((RunInstaller $setup 'reinstall' $true) -eq 0 -and (Test-Path -LiteralPath $marker)) 'Reinstallation preserves unrelated files'

    $future = Join-Path $runRoot 'future.exe'
    $provider = New-Object Microsoft.CSharp.CSharpCodeProvider
    $options = New-Object CodeDom.Compiler.CompilerParameters
    $options.GenerateExecutable = $true
    $options.OutputAssembly = $future
    try {
        $compiled = $provider.CompileAssemblyFromSource($options,'[assembly: System.Reflection.AssemblyFileVersion("9999.1231.2359.0")] public static class Future { public static void Main() {} }')
        if ($compiled.Errors.HasErrors) { throw 'Future-version fixture compilation failed.' }
    } finally { $provider.Dispose() }
    CopyFixture $future $appExe
    Require ((RunInstaller $setup 'refuse-downgrade' $true) -ne 0 -and (Get-FileHash -LiteralPath $appExe).Hash -eq (Get-FileHash -LiteralPath $future).Hash) 'An older installer refuses to overwrite a newer app'
    CopyFixture (Join-Path $release.Payload 'ScheduleWidget.exe') $appExe

    $mutex = New-Object Threading.Mutex($false,$mutexName)
    try { Require ((RunInstaller $uninstaller 'running-app-guard' $false) -ne 0 -and (Test-Path -LiteralPath $appExe)) 'Uninstall refuses while its application mutex exists' }
    finally { $mutex.Dispose() }
    WriteStartup ('"' + $appExe + '"')
    Require ((RunInstaller $uninstaller 'uninstall' $false) -eq 0) 'Uninstall completes successfully'
    Require (-not (Test-Path -LiteralPath $appExe) -and (Test-Path -LiteralPath $marker)) 'Uninstall removes application files and preserves unrelated files'
    $key = $registry.OpenSubKey($uninstallKey)
    try { Require (-not $key -and -not (Test-Path -LiteralPath $desktopLink) -and -not (Test-Path -LiteralPath $startLink)) 'Uninstall removes its registration and both shortcuts' }
    finally { if ($key) {$key.Dispose()} }
    Require (-not (ReadStartup)) 'Uninstall removes only its own startup command'

    Require ((RunInstaller $setup 'second-install' $true) -eq 0) 'A fresh install works after uninstall'
    $uninstaller = FindUninstaller
    $otherCommand = '"C:\Other ScheduleWidget\ScheduleWidget.exe"'
    WriteStartup $otherCommand
    Require ((RunInstaller $uninstaller 'other-startup-uninstall' $false) -eq 0 -and (ReadStartup) -eq $otherCommand) 'Uninstall preserves a startup command belonging to another installation'
    $registry.DeleteSubKeyTree(('Software\ScheduleWidgetInstallerChecks\' + $id),$false)
    [pscustomobject]@{Passed=$script:passed;Failed=0;Directory=$runRoot;ProductId=$product;Version=$release.Version} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'checks.json') -Encoding UTF8
    Write-Host ($script:passed.ToString() + ' installer checks passed. Artifacts: ' + $runRoot)
    $completed = $true
} finally {
    # Failed assertions must not leave test applications and shortcuts on the user's desktop.
    # Never race an installer which exceeded the timeout; the error above identifies its process and log.
    if (-not $completed -and -not $script:installerTimedOut) {
        try {
            $key = $registry.OpenSubKey($uninstallKey)
            $registered = $null -ne $key
            if ($key) { $key.Dispose() }
            if ($registered -and (RunInstaller (FindUninstaller) 'cleanup-after-failure' $false) -ne 0) { throw 'Uninstall returned an error.' }
            $registry.DeleteSubKeyTree(('Software\ScheduleWidgetInstallerChecks\' + $id),$false)
        } catch { Write-Warning ('Fixture cleanup needs attention at ' + $runRoot + ': ' + $_.Exception.Message) }
    }
    $registry.Dispose()
}

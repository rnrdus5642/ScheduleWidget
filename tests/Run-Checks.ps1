param([int]$TimeoutSeconds = 120)
$ErrorActionPreference = 'Stop'
if ($TimeoutSeconds -lt 1) { throw 'TimeoutSeconds must be positive.' }

$projectRoot = Split-Path $PSScriptRoot -Parent
$msbuildCommand = Get-Command msbuild -ErrorAction SilentlyContinue
if ($msbuildCommand) {
    $msbuildPath = $msbuildCommand.Source
} else {
    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswherePath) {
        $msbuildPath = & $vswherePath -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
}
if (-not $msbuildPath) { throw 'MSBuild not found. Install Visual Studio with .NET desktop development.' }

# Every run builds its own app and uses disposable data. The normal Release folder stays usable.
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $projectRoot ('.work\checks\' + $runId)
$appOutput = Join-Path $runRoot 'app'
$dataOutput = Join-Path $runRoot 'data'
[IO.Directory]::CreateDirectory($appOutput) | Out-Null
Write-Host "Check artifacts: $runRoot"

& $msbuildPath (Join-Path $projectRoot 'ScheduleWidget.sln') /t:Restore /p:RestorePackagesConfig=true /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "Package restore failed ($LASTEXITCODE)." }
& $msbuildPath (Join-Path $PSScriptRoot 'ScheduleWidget.Checks.csproj') /t:Build /p:Configuration=Release /p:Platform=AnyCPU "/p:OutDir=$appOutput\" /p:IntermediateOutputPath=obj\Checks\ /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "Check build failed ($LASTEXITCODE)." }

# A separate process gives WPF an STA thread and ensures a hung check cannot block indefinitely.
$startInfo = New-Object Diagnostics.ProcessStartInfo
$startInfo.FileName = Join-Path $appOutput 'ScheduleWidget.Checks.exe'
$startInfo.Arguments = '"' + $dataOutput + '"'
$startInfo.WorkingDirectory = $appOutput
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
$startInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
$checkProcess = New-Object Diagnostics.Process
$checkProcess.StartInfo = $startInfo
try {
    if (-not $checkProcess.Start()) { throw 'Could not start checks.' }
    $outputTask = $checkProcess.StandardOutput.ReadToEndAsync()
    $errorTask = $checkProcess.StandardError.ReadToEndAsync()
    if (-not $checkProcess.WaitForExit($TimeoutSeconds * 1000)) {
        $checkProcess.Kill()
        $checkProcess.WaitForExit()
        throw "Checks timed out after $TimeoutSeconds seconds. Artifacts: $runRoot"
    }
    $standardOutput = $outputTask.GetAwaiter().GetResult()
    $standardError = $errorTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $runRoot 'checks.log'), $standardOutput + $standardError)
    Write-Host $standardOutput.TrimEnd()
    if ($standardError) { Write-Host $standardError.TrimEnd() }
    if ($checkProcess.ExitCode -ne 0) { throw "Checks failed ($($checkProcess.ExitCode)). Artifacts: $runRoot" }
} finally {
    $checkProcess.Dispose()
}

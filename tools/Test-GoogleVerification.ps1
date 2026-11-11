# Read-only pre-submission evidence. Does not read schedules/tokens, sign in, sync, revoke access, or submit a review.
param(
    [Parameter(Mandatory=$true)][string]$ApplicationPath,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$application = (Resolve-Path -LiteralPath $ApplicationPath).Path
$appDirectory = Split-Path $application -Parent
Add-Type -AssemblyName System.Net.Http,System.Security
[void][Reflection.Assembly]::LoadFrom((Join-Path $appDirectory 'Newtonsoft.Json.dll'))
$assembly = [Reflection.Assembly]::LoadFrom($application)
$service = $assembly.GetType('ScheduleWidget.GoogleCalendarService',$true)
$scopes = @($service.GetField('Scope',[Reflection.BindingFlags]'NonPublic,Static').GetRawConstantValue().Split(' ') | Where-Object { $_ })
$expectedScopes = @('https://www.googleapis.com/auth/calendar.events.owned','https://www.googleapis.com/auth/drive.file','https://www.googleapis.com/auth/userinfo.email')
$scopeMatch = @((Compare-Object ($scopes | Sort-Object) ($expectedScopes | Sort-Object))).Count -eq 0
$resource = $assembly.GetManifestResourceStream('ScheduleWidget.google_client.bin')
if (-not $resource) { throw 'The reviewed application has no embedded OAuth client.' }
try {
    $memory = New-Object IO.MemoryStream
    try { $resource.CopyTo($memory); $encrypted = $memory.ToArray() } finally { $memory.Dispose() }
} finally { $resource.Dispose() }
$plain = [ScheduleWidget.GoogleCalendarService]::DecryptClient($encrypted)
try {
    $document = [Text.Encoding]::UTF8.GetString($plain) | ConvertFrom-Json
    $desktop = $null -ne $document.installed
    $project = $document.installed.project_id
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $clientFingerprint = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes([string]$document.installed.client_id))).Replace('-','') }
    finally { $hash.Dispose() }
} finally { [Array]::Clear($plain,0,$plain.Length); $document = $null }

$artworkHash = $null
$compiledResources = $assembly.GetManifestResourceStream('ScheduleWidget.g.resources')
if ($compiledResources) {
    $reader = New-Object Resources.ResourceReader($compiledResources)
    try {
        $entries = $reader.GetEnumerator()
        while ($entries.MoveNext()) {
            if ($entries.Key -ne 'assets/google/sign-in-with-google.png') { continue }
            $hash = [Security.Cryptography.SHA256]::Create()
            try { $artworkHash = [BitConverter]::ToString($hash.ComputeHash([IO.Stream]$entries.Value)).Replace('-','') }
            finally { $hash.Dispose() }
        }
    } finally { $reader.Dispose(); $compiledResources.Dispose() }
}
$approvedArtwork = $artworkHash -eq 'E048546F162B43A5F3809796F88DF02A8E13F07CA1280A9C1635018956E05C05'
$overrides = (Test-Path -LiteralPath (Join-Path $appDirectory 'google_client.json')) -or
    (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'ScheduleWidget\google_client.json'))
$pages = @()
foreach ($url in @('https://schedule.jwstudio.page/','https://schedule.jwstudio.page/privacy.html')) {
    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 20
        $pages += [pscustomobject]@{Url=$url;HttpStatus=$response.StatusCode;PrivacyLink=$response.Content.Contains('privacy.html');
            GoogleDisclosure=$response.Content.Contains('Google');ScopeDisclosure=($response.Content.Contains('calendar.events.owned') -and $response.Content.Contains('drive.file') -and $response.Content.Contains('userinfo.email'))}
    } catch { $pages += [pscustomobject]@{Url=$url;HttpStatus=0;ErrorType=$_.Exception.GetType().Name} }
}
try {
    $records = Resolve-DnsName -Name jwstudio.page -Type TXT -ErrorAction Stop
    $dnsVerificationRecord = @($records | ForEach-Object { $_.Strings } | Where-Object { $_ -like 'google-site-verification=*' }).Count -gt 0
} catch { $dnsVerificationRecord = $null }
$publicVersion = $null
$publicVersionError = $null
try {
    $publicManifest = Invoke-WebRequest -Uri 'https://github.com/rnrdus5642/ScheduleWidget/releases/latest/download/latest.json' -UseBasicParsing -TimeoutSec 20
    $manifestText = if ($publicManifest.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($publicManifest.Content) } else { [string]$publicManifest.Content }
    $publicVersion = ($manifestText | ConvertFrom-Json).version
} catch { $publicVersionError = $_.Exception.GetType().Name }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($application).ProductVersion
$localPassed = $desktop -and $project -eq 'schedulewidget-production' -and $scopeMatch -and $approvedArtwork -and -not $overrides
$publicPagesPassed = @($pages | Where-Object { $_.HttpStatus -ne 200 -or -not $_.PrivacyLink -or -not $_.GoogleDisclosure }).Count -eq 0 -and $pages[1].ScopeDisclosure
$report = [pscustomobject]@{
    CheckedAt=(Get-Date -Format o);ApplicationVersion=$version;ApplicationSHA256=(Get-FileHash -LiteralPath $application).Hash
    ProjectId=$project;DesktopClient=$desktop;ClientIdSHA256=$clientFingerprint;Scopes=$scopes;ScopesMatch=$scopeMatch
    ApprovedGoogleButtonEmbedded=$approvedArtwork;LocalClientOverridePresent=$overrides;LocalPreparationChecksPassed=$localPassed
    PublicPages=$pages;PublicPageChecksPassed=$publicPagesPassed;GoogleVerificationDnsRecordPresent=$dnsVerificationRecord
    LatestPublicVersion=$publicVersion;VersionMatchesLatest=($version -eq $publicVersion);PublicVersionCheckError=$publicVersionError
    GoogleApprovalStatus='Not checked: requires authenticated Google Console'
    StillRequiresConsoleOrUser=@('Search Console ownership','Audience and publishing status','Published branding','Data access review status','Support and developer email','Real OAuth consent and demonstration video','Review submission')
}
$json = $report | ConvertTo-Json -Depth 6
if ($ReportPath) {
    $destination = [IO.Path]::GetFullPath($ReportPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::WriteAllText($destination,$json,(New-Object Text.UTF8Encoding($false)))
}
Write-Output $json
if (-not $localPassed -or -not $publicPagesPassed) { exit 1 }

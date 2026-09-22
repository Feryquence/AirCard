# Adapted for Air Card from NelloKudo/Apple-Mobile-Drivers-Installer.
# GPL-3.0; see LICENSE.txt and NOTICE.md. Modified 2026-09-22.
# This bundled script runs only after the user chooses driver installation.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$taskDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$logPath = Join-Path $taskDirectory 'install.log'
$staging = Join-Path $taskDirectory 'downloads'
$script:restartRequired = $false
$ownsStaging = $false
$exitCode = 1

function Write-InstallLog([string]$Message) {
    [IO.File]::AppendAllText($logPath, ('[{0:HH:mm:ss}] {1}{2}' -f [DateTime]::Now, $Message, [Environment]::NewLine), (New-Object Text.UTF8Encoding($false)))
}
function Get-InstallFile([string]$Uri, [string]$Destination) {
    Write-InstallLog "Downloading $([IO.Path]::GetFileName($Destination))..."
    $client = New-Object Net.WebClient
    try { $client.DownloadFile($Uri, $Destination) } finally { $client.Dispose() }
    if (!(Test-Path -LiteralPath $Destination -PathType Leaf) -or (Get-Item -LiteralPath $Destination).Length -eq 0) { throw 'Download is empty.' }
}
function Assert-AppleSignature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or !$signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(?i)(?:^|,\s*)O="?Apple Inc\."?(?:,|$)') {
        throw "The downloaded Apple installer has no valid Apple signature: $Path"
    }
}
function Invoke-InstallProcess([string]$Path, [string]$Arguments, [string]$WorkingDirectory, [int[]]$SuccessCodes = @(0)) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -notin $SuccessCodes) { throw "$([IO.Path]::GetFileName($Path)) failed with exit code $($process.ExitCode)." }
    if ($process.ExitCode -eq 3010 -or $process.ExitCode -eq 1641) { $script:restartRequired = $true }
}
try {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator permission is required.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    if (Test-Path -LiteralPath $staging) { throw 'The download staging directory already exists; start a new installation attempt.' }
    New-Item -ItemType Directory -Path $staging | Out-Null
    $ownsStaging = $true

    $itunes = Join-Path $staging 'iTunes64Setup.exe'
    Get-InstallFile 'https://www.apple.com/itunes/download/win64' $itunes
    Assert-AppleSignature $itunes
    Write-InstallLog 'Extracting Apple Mobile Device Support. iTunes itself will not be installed.'
    Invoke-InstallProcess $itunes '/extract' $staging
    $msi = Join-Path $staging 'AppleMobileDeviceSupport64.msi'
    if (!(Test-Path -LiteralPath $msi -PathType Leaf)) { throw 'The Apple installer did not extract AppleMobileDeviceSupport64.msi.' }
    Assert-AppleSignature $msi
    $msiLog = Join-Path $taskDirectory 'mobile-device-support.log'
    $arguments = '/i "{0}" /qn /norestart /L*v "{1}"' -f $msi, $msiLog
    Write-InstallLog 'Installing Apple Mobile Device Support...'
    Invoke-InstallProcess (Join-Path $env:WINDIR 'System32\msiexec.exe') $arguments $staging @(0, 3010, 1641)

    $packages = @(
        @{ Name = 'AppleUSB'; Uri = 'https://catalog.s.download.windowsupdate.com/d/msdownload/update/driver/drvs/2020/11/01d96dfd-2f6f-46f7-8bc3-fd82088996d2_a31ff7000e504855b3fa124bf27b3fe5bc4d0893.cab' },
        @{ Name = 'AppleNet'; Uri = 'https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2017/11/netaapl_7503681835e08ce761c52858949731761e1fa5a1.cab' }
    )
    foreach ($package in $packages) {
        $cab = Join-Path $staging ($package.Name + '.cab')
        Get-InstallFile $package.Uri $cab
        $expanded = Join-Path $staging $package.Name
        New-Item -ItemType Directory -Path $expanded | Out-Null
        Invoke-InstallProcess (Join-Path $env:WINDIR 'System32\expand.exe') ('-F:* "{0}" "{1}"' -f $cab, $expanded) $staging
        $infFiles = @(Get-ChildItem -LiteralPath $expanded -Filter '*.inf' -File -Recurse)
        if ($infFiles.Count -eq 0) { throw "No INF driver files were found in $($package.Name)." }
        foreach ($inf in $infFiles) {
            Write-InstallLog "Installing $($inf.Name)..."
            # PnPUtil retains Windows catalog/signature enforcement.
            Invoke-InstallProcess (Join-Path $env:WINDIR 'System32\pnputil.exe') ('/add-driver "{0}" /install' -f $inf.FullName) $staging @(0, 3010)
        }
    }
    Write-InstallLog 'Driver installation completed.'
    $exitCode = 0
    if ($script:restartRequired) { Write-InstallLog 'Restart Windows to finish installation.'; $exitCode = 3010 }
}
catch { Write-InstallLog ('Installation failed: ' + $_.Exception.Message); $exitCode = 1 }
finally {
    # Delete only the downloads directory created inside this unique app job.
    # Leave the script and diagnostic logs for inspection after any failure.
    try {
        if ($ownsStaging -and (Test-Path -LiteralPath $staging)) {
            $resolved = [IO.Path]::GetFullPath((Get-Item -LiteralPath $staging).FullName)
            $expected = [IO.Path]::GetFullPath((Join-Path $taskDirectory 'downloads'))
            if ($resolved -ne $expected -or [IO.Path]::GetDirectoryName($resolved) -ne $taskDirectory -or
                ((Get-Item -LiteralPath $staging).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing unsafe staging cleanup.' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    catch { Write-InstallLog ('Download cleanup skipped: ' + $_.Exception.Message) }
}
exit $exitCode

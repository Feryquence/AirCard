param([string]$Artifacts)
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'ThirdParty/Apple-Mobile-Drivers-Installer/AppleDrivInstaller.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$script:checks = 0
function Assert([bool]$Condition, [string]$Name) { $script:checks++; if (!$Condition) { throw "FAIL: $Name" } }
function Assert-Throws([scriptblock]$Action, [string]$Name) { $caught = $false; try { & $Action } catch { $caught = $true }; Assert $caught $Name }

# Load only these pure/checking helpers, never the installer body. Every OS
# installer and signature provider below is replaced by a test double.
foreach ($name in @('Invoke-InstallProcess', 'Assert-AppleSignature')) {
    $definition = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true) | Where-Object Name -eq $name
    if (@($definition).Count -ne 1) { throw "Missing helper: $name" }
    Invoke-Expression $definition.Extent.Text
}
function Start-Process {
    param($FilePath, $ArgumentList, $WorkingDirectory, $WindowStyle, [switch]$Wait, [switch]$PassThru)
    Assert ($Wait -and $PassThru -and $WindowStyle -eq 'Hidden') 'installer waits and checks a hidden process'
    [pscustomobject]@{ ExitCode = $script:mockExit }
}
function Get-AuthenticodeSignature { param($LiteralPath) $script:mockSignature }
$script:restartRequired = $false; $script:mockExit = 0
Invoke-InstallProcess 'fake.exe' '/extract' 'C:\fake'
Assert (!$script:restartRequired) 'normal success does not require a restart'
$script:mockExit = 3010
Invoke-InstallProcess 'fake.exe' '/install' 'C:\fake' @(0, 3010)
Assert $script:restartRequired 'restart-required code is preserved'
$script:restartRequired = $false; $script:mockExit = 1641
Invoke-InstallProcess 'fake.exe' '/install' 'C:\fake' @(0, 3010, 1641)
Assert $script:restartRequired 'MSI restart-initiated code is preserved'
$script:mockExit = 1603
Assert-Throws { Invoke-InstallProcess 'fake.exe' '/install' 'C:\fake' @(0, 3010) } 'failed MSI cannot announce success'
$script:mockExit = 3010
Assert-Throws { Invoke-InstallProcess 'fake.exe' '/extract' 'C:\fake' } 'extraction accepts only its configured success codes'
$script:mockSignature = [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = 'CN=Apple Inc., O=Apple Inc., C=US' } }
Assert-AppleSignature 'fake.exe'
Assert $true 'valid Apple publisher accepted'
$script:mockSignature.Status = 'HashMismatch'
Assert-Throws { Assert-AppleSignature 'fake.exe' } 'tampered installer rejected'
$script:mockSignature.Status = 'Valid'; $script:mockSignature.SignerCertificate.Subject = 'CN=Apple Inc., O=Unrelated Publisher, C=US'
Assert-Throws { Assert-AppleSignature 'fake.exe' } 'publisher name in CN alone is insufficient'
$script:mockSignature.SignerCertificate = $null
Assert-Throws { Assert-AppleSignature 'fake.exe' } 'missing signer rejected'
$result = "PASS: $script:checks driver-installer assertions; no downloads, UAC prompts or driver installation performed."
Write-Output $result
if ($Artifacts) { [IO.File]::WriteAllText((Join-Path $Artifacts 'driver-installer-results.txt'), $result) }

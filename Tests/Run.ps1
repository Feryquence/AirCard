param([string]$ReferenceAssemblies, [string]$Artifacts = (Join-Path $env:TEMP ('AirCardTests-' + [Guid]::NewGuid().ToString('N'))), [switch]$NativeBindings)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
$Artifacts = (Resolve-Path -LiteralPath $Artifacts).Path
& (Join-Path $projectRoot 'Build.ps1') -ReferenceAssemblies $ReferenceAssemblies
Copy-Item -LiteralPath (Join-Path $projectRoot 'bin\Release\AirCard.exe') -Destination $Artifacts
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$compiler = & $vswhere -latest -products '*' -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
if (!$compiler) { throw 'C# compiler not found.' }
if (!$ReferenceAssemblies) { $ReferenceAssemblies = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2' }
New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
$exe = Join-Path $Artifacts 'AirCard.Tests.exe'
$compilerArgs = @('/nologo','/target:exe','/platform:x64','/langversion:7.3','/nostdlib+', "/out:$exe", "/r:$Artifacts\AirCard.exe")
foreach ($name in @('mscorlib','System','System.Core','System.Xaml','System.Xml','System.Drawing','System.IO.Compression','System.IO.Compression.FileSystem','WindowsBase','PresentationCore','PresentationFramework')) { $compilerArgs += "/r:$ReferenceAssemblies\$name.dll" }
$compilerArgs += Join-Path $PSScriptRoot 'Smoke.cs'
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $exe $Artifacts
if ($LASTEXITCODE -ne 0) { throw "Tests failed: $LASTEXITCODE" }
& (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'DriverInstaller.ps1') -Artifacts $Artifacts
if ($LASTEXITCODE -ne 0) { throw 'Driver installer helper tests failed.' }
if ($NativeBindings) {
    $nativeExe = Join-Path $Artifacts 'AirCard.NativeSmoke.exe'
    & $compiler /nologo /target:exe /platform:x64 /langversion:7.3 "/out:$nativeExe" "/r:$Artifacts\AirCard.exe" (Join-Path $PSScriptRoot 'NativeSmoke.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Native smoke test compilation failed.' }
    & $nativeExe
    if ($LASTEXITCODE -ne 0) { throw 'Native bindings smoke test failed.' }
}

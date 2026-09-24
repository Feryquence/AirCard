param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$ReferenceAssemblies, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools with .NET desktop build tools.' }
$builder = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (!$builder) { throw 'MSBuild was not found.' }
$buildArgs = @((Join-Path $PSScriptRoot 'AirCard.csproj'), '/t:Build', "/p:Configuration=$Configuration", '/p:Platform=x64', '/nologo', '/v:minimal')
if ($ReferenceAssemblies) { $buildArgs += "/p:FrameworkPathOverride=$ReferenceAssemblies"; $buildArgs += '/p:AutomaticallyUseReferenceAssemblyPackages=false' }
if ($OutputPath) { $buildArgs += "/p:OutputPath=$OutputPath" }
$metadataRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata'
$metadata = Get-ChildItem -LiteralPath $metadataRoot -Directory | Where-Object { $_.Name -match '^10\.\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'Windows.winmd' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$metadata) { throw 'Install the Windows 10/11 SDK to build PDF import support.' }
$buildArgs += "/p:WindowsMetadataPath=$metadata"
& $builder @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }

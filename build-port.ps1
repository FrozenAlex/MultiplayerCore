param(
    [Parameter(Mandatory=$true)][string]$BeatSaberDir,
    [string]$ModRefsDir = $BeatSaberDir,
    [Parameter(Mandatory=$true)][string]$BeatTogetherRepo
)
$ErrorActionPreference = 'Stop'
$coreProject = Join-Path $PSScriptRoot 'MultiplayerCore/MultiplayerCore.csproj'
$coreDll = Join-Path $PSScriptRoot 'MultiplayerCore/bin/Release/netstandard2.1/MultiplayerCore.dll'
$btProject = Join-Path $BeatTogetherRepo 'BeatTogether/BeatTogether.csproj'
$argsCommon = @('-c','Release', "-p:BeatSaberDir=$BeatSaberDir", "-p:ModRefsDir=$ModRefsDir", '-p:DisableCopyToPlugins=true','-p:DisableCopyToGame=true')
dotnet build $coreProject @argsCommon
if ($LASTEXITCODE -ne 0) { throw 'MultiplayerCore build failed' }
dotnet build $btProject @argsCommon "-p:MultiplayerCorePath=$coreDll"
if ($LASTEXITCODE -ne 0) { throw 'BeatTogether build failed' }
$env:BEATSABER_TEST_GAME = $BeatSaberDir
$env:MULTIPLAYERCORE_TEST_DLL = $coreDll
$env:BEATTOGETHER_TEST_DLL = Join-Path $BeatTogetherRepo 'BeatTogether/bin/Release/netstandard2.1/BeatTogether.dll'
$env:MULTIPLAYER_TEST_MODREFS = Join-Path $ModRefsDir 'Plugins'
dotnet test (Join-Path $PSScriptRoot 'MultiplayerCore.Tests/MultiplayerCore.Tests.csproj') -c Release --logger 'trx;LogFileName=port-1.45.3.trx'
if ($LASTEXITCODE -ne 0) { throw 'PC validation failed' }

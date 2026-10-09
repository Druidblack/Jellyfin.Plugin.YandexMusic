param(
    [ValidateSet('Release','Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $ProjectDir 'Jellyfin.Plugin.YandexMusic.csproj'
$PublishDir = Join-Path $ProjectDir "bin\$Configuration\net10.0\publish"
$DistDir = Join-Path $ProjectDir 'dist'
$PackageDir = Join-Path $DistDir 'Yandex Music Metadata_0.1.6.0'
$ZipPath = Join-Path $DistDir 'Jellyfin.Plugin.YandexMusic_0.1.6.0.zip'

Write-Host 'Restoring packages...'
dotnet restore $Project

Write-Host "Publishing $Configuration build..."
dotnet publish $Project -c $Configuration --no-restore -p:EnableAnalyzers=false -p:TreatWarningsAsErrors=false

if (Test-Path $PackageDir) { Remove-Item $PackageDir -Recurse -Force }
New-Item -ItemType Directory -Path $PackageDir -Force | Out-Null

$PluginDll = Join-Path $PublishDir 'Jellyfin.Plugin.YandexMusic.dll'
if (-not (Test-Path $PluginDll)) {
    throw "Plugin DLL was not produced: $PluginDll"
}

Copy-Item $PluginDll $PackageDir

$Pdb = Join-Path $PublishDir 'Jellyfin.Plugin.YandexMusic.pdb'
if (Test-Path $Pdb) { Copy-Item $Pdb $PackageDir }

if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path (Join-Path $PackageDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal

Write-Host ''
Write-Host 'Build completed.' -ForegroundColor Green
Write-Host "Plugin folder: $PackageDir"
Write-Host "ZIP package:   $ZipPath"

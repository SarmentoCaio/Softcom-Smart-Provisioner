param(
    [Parameter(Mandatory=$false)][string]$DownloadUrl = "COLE_A_URL_PUBLICA_DO_ZIP_AQUI",
    [Parameter(Mandatory=$false)][string]$Notes = "Atualizacao do Softcom Smart Provisioner.",
    [Parameter(Mandatory=$false)][switch]$Required
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "src\SoftcomSmartProvisioner\SoftcomSmartProvisioner.csproj"
[xml]$project = Get-Content -LiteralPath $csproj
$version = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw "Versao nao encontrada no csproj." }

$publish = Join-Path $root "publish\win-x64"
if (-not (Test-Path $publish)) { throw "Execute PUBLICAR-WINDOWS.bat antes de gerar a release." }

$releaseDir = Join-Path $root "release\v$version"
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
$zip = Join-Path $releaseDir "Softcom-Smart-Provisioner-v$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()

$manifest = [ordered]@{
    version = $version
    downloadUrl = $DownloadUrl
    sha256 = $hash
    required = [bool]$Required
    notes = $Notes
}
$manifestPath = Join-Path $releaseDir "latest.json"
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host ""
Write-Host "Release gerada:" -ForegroundColor Green
Write-Host $zip
Write-Host $manifestPath
Write-Host "SHA256: $hash"
if ($DownloadUrl -like "COLE_*") {
    Write-Host "ATENCAO: edite downloadUrl em latest.json antes de publicar." -ForegroundColor Yellow
}

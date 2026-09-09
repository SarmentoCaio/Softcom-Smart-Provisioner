param(
    [Parameter(Mandatory=$false)][string]$Version,
    [Parameter(Mandatory=$false)][string]$Message
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Invoke-Git {
    param(
        [Parameter(Mandatory=$true)]
        [string[]]$GitArgs
    )

    # No Windows PowerShell 5.x, mensagens informativas do Git escritas em STDERR
    # (por exemplo, "From https://github.com/..." durante fetch/pull) podem virar
    # NativeCommandError quando ErrorActionPreference = Stop, mesmo com exit code 0.
    # Executamos o comando com Continue e decidimos o sucesso exclusivamente pelo
    # LASTEXITCODE do Git.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & git @GitArgs 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        $text = (($output | Out-String).Trim())
        throw "Falha ao executar git $($GitArgs -join ' '): $text"
    }

    return $output
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Read-Host "Nova versao (ex.: 1.0.2)"
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Versao nao informada."
}
$Version = $Version.Trim().TrimStart([char[]]@('v','V'))
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Versao invalida. Use o formato X.Y.Z, por exemplo 1.0.2."
}

if ([string]::IsNullOrWhiteSpace($Message)) {
    $Message = "Prepara release v$Version"
}

$mainProject = Join-Path $root "src\SoftcomSmartProvisioner\SoftcomSmartProvisioner.csproj"
$updaterProject = Join-Path $root "src\SoftcomSmartProvisioner.Updater\SoftcomSmartProvisioner.Updater.csproj"

function Set-ProjectVersion([string]$Path, [string]$NewVersion) {
    [xml]$xml = Get-Content -LiteralPath $Path
    $group = $xml.Project.PropertyGroup | Select-Object -First 1
    if ($null -eq $group) {
        throw "PropertyGroup nao encontrado em $Path."
    }

    if ($null -eq $group.Version) {
        $node = $xml.CreateElement("Version")
        $node.InnerText = $NewVersion
        [void]$group.AppendChild($node)
    } else {
        $group.Version = $NewVersion
    }

    if ($Path -eq $mainProject) {
        $assemblyVersion = "$NewVersion.0"
        if ($null -eq $group.AssemblyVersion) {
            $node = $xml.CreateElement("AssemblyVersion")
            $node.InnerText = $assemblyVersion
            [void]$group.AppendChild($node)
        } else {
            $group.AssemblyVersion = $assemblyVersion
        }
        if ($null -eq $group.FileVersion) {
            $node = $xml.CreateElement("FileVersion")
            $node.InnerText = $assemblyVersion
            [void]$group.AppendChild($node)
        } else {
            $group.FileVersion = $assemblyVersion
        }
    }

    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.OmitXmlDeclaration = $true
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try { $xml.Save($writer) } finally { $writer.Dispose() }
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "Git nao localizado no PATH."
}

$branchOutput = @(Invoke-Git -GitArgs @("branch", "--show-current"))
$branch = (($branchOutput -join "`n").Trim())
if ([string]::IsNullOrWhiteSpace($branch)) {
    throw "Nao foi possivel identificar a branch atual do Git."
}
if ($branch -ne "main") {
    throw "Execute a publicacao a partir da branch main. Branch atual: $branch"
}

$localTagOutput = @(Invoke-Git -GitArgs @("tag", "--list", "v$Version"))
$localTag = (($localTagOutput -join "`n").Trim())
if (-not [string]::IsNullOrWhiteSpace($localTag)) {
    throw "A tag v$Version ja existe localmente."
}

[void](Invoke-Git -GitArgs @("fetch", "origin", "--tags"))
$remoteTagOutput = @(Invoke-Git -GitArgs @("ls-remote", "--tags", "origin", "refs/tags/v$Version"))
$remoteTag = (($remoteTagOutput -join "`n").Trim())
if (-not [string]::IsNullOrWhiteSpace($remoteTag)) {
    throw "A tag v$Version ja existe no GitHub."
}

Set-ProjectVersion $mainProject $Version
Set-ProjectVersion $updaterProject $Version

[void](Invoke-Git -GitArgs @("add", "."))
& git diff --cached --quiet
$diffExit = $LASTEXITCODE
if ($diffExit -eq 1) {
    [void](Invoke-Git -GitArgs @("commit", "-m", $Message))
} elseif ($diffExit -ne 0) {
    throw "Falha ao verificar alteracoes preparadas no Git. Codigo: $diffExit"
} else {
    Write-Host "Nenhuma alteracao de codigo para commit; seguindo com a tag." -ForegroundColor Yellow
}

[void](Invoke-Git -GitArgs @("push", "origin", "main"))
[void](Invoke-Git -GitArgs @("tag", "-a", "v$Version", "-m", "Softcom Smart Provisioner v$Version"))
[void](Invoke-Git -GitArgs @("push", "origin", "v$Version"))

Write-Host ""
Write-Host "Tag v$Version enviada." -ForegroundColor Green
Write-Host "O GitHub Actions agora ira compilar, criar a Release e atualizar update/latest.json automaticamente."
Write-Host "Acompanhe em: https://github.com/SarmentoCaio/Softcom-Smart-Provisioner/actions"

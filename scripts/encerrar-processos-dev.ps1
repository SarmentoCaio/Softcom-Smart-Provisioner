$ErrorActionPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$binRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'src\SoftcomSmartProvisioner\bin'))

$targets = Get-CimInstance Win32_Process | Where-Object {
    $name = $_.Name
    $isProvisionerProcess = (
        $name -ieq 'SoftcomSmartProvisioner.exe' -or
        $name -ieq 'SoftcomSmartProvisioner.Updater.exe' -or
        $name -ieq 'adb.exe' -or
        $name -ieq 'scrcpy.exe'
    )

    $isProvisionerProcess -and
    $_.ExecutablePath -and
    ([System.IO.Path]::GetFullPath($_.ExecutablePath)).StartsWith($binRoot, [System.StringComparison]::OrdinalIgnoreCase)
}

foreach ($process in $targets) {
    Write-Host ("Encerrando processo local que bloqueia o build: {0} (PID {1})" -f $process.Name, $process.ProcessId)
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
}

# Aguarda o Windows liberar os handles dos executaveis antes do MSBuild copiar o apphost.
Start-Sleep -Milliseconds 700

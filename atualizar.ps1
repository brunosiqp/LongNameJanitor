<#
  Atualiza o LongNameJanitor que já está nesta máquina (serviço do Windows ou o .exe aberto direto).
  Rode a partir da pasta descompactada do pacote novo (o ATUALIZAR.bat faz isso com um duplo clique):

      powershell -ExecutionPolicy Bypass -File .\atualizar.ps1 [-InstallDir <pasta>]

  Acha a instalação sozinho (pelo serviço ou pelo processo em execução), guarda uma cópia em <pasta>\backup\<data>,
  para o programa, troca o .exe e as configurações do pacote, mantém a pasta data\ (contagem, cache de NF, histórico,
  chave de administrador) e liga de novo do mesmo jeito que estava.
#>
param([string]$InstallDir)
$ErrorActionPreference = 'Stop'
$name = 'LongNameJanitor'
$src = $PSScriptRoot
$newExe = Join-Path $src 'LongNameJanitor.exe'

function Step($text) { Write-Host "`n> $text" -ForegroundColor Cyan }
function Same($a, $b) { [IO.Path]::GetFullPath($a).TrimEnd('\') -ieq [IO.Path]::GetFullPath($b).TrimEnd('\') }

if (-not (Test-Path $newExe)) { throw "Não achei o LongNameJanitor.exe novo em $src" }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

$svc = Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction SilentlyContinue
$procs = @(Get-Process $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -and -not (Same $_.Path $newExe) })

if (-not $InstallDir) {
    if ($svc) { $InstallDir = Split-Path ($svc.PathName -replace '^"([^"]+)".*$', '$1') }
    elseif ($procs) { $InstallDir = Split-Path $procs[0].Path }
    else { throw "Não achei o LongNameJanitor instalado nem rodando nesta máquina. Rode: .\atualizar.ps1 -InstallDir <pasta onde ele está>" }
}
if (-not (Test-Path (Join-Path $InstallDir 'LongNameJanitor.exe'))) { throw "Não há LongNameJanitor.exe em $InstallDir" }
if (Same $InstallDir $src) { throw "Rode o atualizar a partir da pasta do pacote NOVO, não da própria instalação ($InstallDir)." }
$asService = [bool]$svc -and (Same (Split-Path ($svc.PathName -replace '^"([^"]+)".*$', '$1')) $InstallDir)
if ($asService -and -not $isAdmin) { throw "Ele roda como serviço: abra o PowerShell como administrador (ou use o ATUALIZAR.bat)." }

Step "Atualizando $InstallDir ($(if ($asService) { 'serviço do Windows' } else { 'programa aberto direto' }))"

# 1. backup
$backup = Join-Path $InstallDir ('backup\' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory $backup -Force | Out-Null
Get-ChildItem $InstallDir -File | Where-Object { $_.Name -like '*.exe' -or $_.Name -like '*.json' -or $_.Name -like '*.ps1' } |
    Copy-Item -Destination $backup
Write-Host "  cópia da versão atual em $backup"

# 2. parar
$wasRunning = $false
if ($asService) {
    if ((Get-Service $name).Status -ne 'Stopped') { $wasRunning = $true; Stop-Service $name -Force }
    Write-Host "  serviço parado"
}
foreach ($p in @(Get-Process $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -and (Same (Split-Path $_.Path) $InstallDir) })) {
    $wasRunning = $true
    Stop-Process -Id $p.Id -Force
    $p.WaitForExit(15000) | Out-Null
    Write-Host "  programa fechado (processo $($p.Id))"
}
$exe = Join-Path $InstallDir 'LongNameJanitor.exe'
foreach ($i in 1..30) {   # espera o Windows soltar o .exe
    try { [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None').Close(); break } catch { Start-Sleep 1 }
}

# 3. trocar os arquivos (data\ fica como está)
foreach ($f in 'LongNameJanitor.exe', 'appsettings.json', 'appsettings.local.example.json', 'install.ps1', 'uninstall.ps1', 'atualizar.ps1', 'ATUALIZAR.bat') {
    $from = Join-Path $src $f
    if (Test-Path $from) { Copy-Item $from $InstallDir -Force; Write-Host "  $f" }
}
$local = Join-Path $src 'appsettings.local.json'
if (Test-Path $local) { Copy-Item $local $InstallDir -Force; Write-Host "  appsettings.local.json (o anterior está no backup)" }
else { Write-Host "  appsettings.local.json mantido (o pacote não trouxe um)" }

# 4. ligar de novo
Step "Ligando"
if ($asService) { Start-Service $name }
else { Start-Process $exe -WorkingDirectory $InstallDir }

$port = 5080
try { $u = (Get-Content (Join-Path $InstallDir 'appsettings.json') -Raw | ConvertFrom-Json).Urls; if ($u -match ':(\d+)') { $port = [int]$Matches[1] } } catch { }
$status = $null
foreach ($i in 1..20) {
    Start-Sleep 2
    try { $status = Invoke-RestMethod "http://localhost:$port/api/status" -TimeoutSec 3 } catch { continue }
    if (-not ($status.folders | Where-Object { $_.state -eq 'Connecting' -or $_.processedState -eq 'Connecting' })) { break }
}
if (-not $status) {
    Write-Host "`nO programa não respondeu em http://localhost:$port. Para voltar: copie os arquivos de $backup para $InstallDir." -ForegroundColor Red
    exit 1
}

function Show-Folder($label, $path, $state, $err) {
    switch ($state) {
        'Watching' { Write-Host "  OK          $label $path" -ForegroundColor Green }
        'Off'      { Write-Host "  DESLIGADA   $label $path" -ForegroundColor DarkGray }
        'Paused'   { Write-Host "  PAUSADA     $label $path (retome pelo painel)" -ForegroundColor DarkGray }
        'Invalid'  { Write-Host "  INVÁLIDA    $label $path" -ForegroundColor Red; Write-Host "              $err" -ForegroundColor Red }
        default    { Write-Host "  SEM ACESSO  $label $path" -ForegroundColor Yellow; Write-Host "              $err" -ForegroundColor Yellow }
    }
}
foreach ($f in $status.folders) {
    Show-Folder 'pasta:      ' $f.path $f.state $f.lastError
    if ($f.processedPath) { Show-Folder 'processados:' $f.processedPath $f.processedState $f.processedError }
}
if ($status.dryRun) { Write-Host "`n  MODO SIMULAÇÃO: nada é apagado nem movido até sair da simulação no painel." -ForegroundColor Yellow }
else { Write-Host "`n  Valendo de verdade (sem simulação)." -ForegroundColor Green }
Write-Host "  Total removido até agora: $($status.total) · contagem e histórico mantidos" -ForegroundColor Gray

Write-Host "`nPainel: http://$($env:COMPUTERNAME):$port" -ForegroundColor Cyan
$keyFile = Join-Path $InstallDir 'data\admin-key.txt'
if (Test-Path $keyFile) { Write-Host "Chave de administrador (cadeado no topo do painel): $((Get-Content $keyFile -Raw).Trim())" -ForegroundColor Cyan }
Write-Host "Versão anterior guardada em $backup" -ForegroundColor DarkGray

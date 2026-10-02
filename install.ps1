#Requires -RunAsAdministrator
<#
  Instala (ou atualiza) o LongNameJanitor como serviço do Windows.
  Rode no servidor, a partir da pasta descompactada, num PowerShell como administrador:

      powershell -ExecutionPolicy Bypass -File .\install.ps1

  Primeira vez: pede a conta que roda o serviço (precisa acessar a pasta de rede).
  Já instalado: só troca o .exe e reinicia; appsettings.json e a pasta data\ do servidor são mantidos.
#>
param(
    [string]$InstallDir = "C:\Tools\LongNameJanitor",
    [int]$Port = 5080
)
$ErrorActionPreference = 'Stop'
$name = 'LongNameJanitor'
$exe = Join-Path $InstallDir 'LongNameJanitor.exe'

function Step($text) { Write-Host "`n> $text" -ForegroundColor Cyan }

function Grant-LogonAsService([string]$account) {
    # Direito "Fazer logon como serviço"; sem ele o serviço não sobe (erro 1069).
    $sid = (New-Object Security.Principal.NTAccount $account).Translate([Security.Principal.SecurityIdentifier]).Value
    $tmp = Join-Path $env:TEMP "lnj_rights"
    secedit /export /cfg "$tmp.inf" /areas USER_RIGHTS | Out-Null
    $line = Get-Content "$tmp.inf" | Where-Object { $_ -like 'SeServiceLogonRight*' }
    if ($line -and $line -match [regex]::Escape("*$sid")) { return }
    $new = if ($line) { "$line,*$sid" } else { "SeServiceLogonRight = *$sid" }
    @"
[Unicode]
Unicode=yes
[Version]
signature="`$CHICAGO`$"
Revision=1
[Privilege Rights]
$new
"@ | Set-Content "$tmp.new.inf" -Encoding Unicode
    secedit /configure /db "$tmp.sdb" /cfg "$tmp.new.inf" /areas USER_RIGHTS | Out-Null
    Remove-Item "$tmp*" -Force -ErrorAction SilentlyContinue
}

$service = Get-Service $name -ErrorAction SilentlyContinue

if ($service) {
    Step "Atualizando o serviço existente"
    Stop-Service $name -Force
    Start-Sleep 2
} else {
    Step "Instalando em $InstallDir"
}

New-Item -ItemType Directory $InstallDir -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'LongNameJanitor.exe') $InstallDir -Force
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $InstallDir -Force
# Configuração já existente no servidor nunca é sobrescrita.
foreach ($cfg in 'appsettings.json', 'appsettings.local.json') {
    $from = Join-Path $PSScriptRoot $cfg
    if (Test-Path (Join-Path $InstallDir $cfg)) { Write-Host "  $cfg do servidor mantido" }
    elseif (Test-Path $from) { Copy-Item $from $InstallDir }
}
if (-not (Test-Path (Join-Path $InstallDir 'appsettings.local.json'))) {
    Copy-Item (Join-Path $PSScriptRoot 'appsettings.local.example.json') (Join-Path $InstallDir 'appsettings.local.json')
    Write-Host "`nCrie suas pastas e regras em $InstallDir\appsettings.local.json (copiei o exemplo)" -ForegroundColor Yellow
    Write-Host "e rode o install.ps1 de novo." -ForegroundColor Yellow
    notepad (Join-Path $InstallDir 'appsettings.local.json')
    exit 0
}

if (-not $service) {
    Step "Conta do serviço"
    Write-Host "  Use uma conta de domínio que consiga modificar arquivos na pasta de rede (ex.: DOMINIO\svc_janitor)."
    $cred = Get-Credential -Message "Conta que roda o LongNameJanitor (DOMINIO\usuario)"
    $account = $cred.UserName

    Grant-LogonAsService $account
    # A conta grava contagem e histórico em data\
    icacls $InstallDir /grant "${account}:(OI)(CI)M" | Out-Null

    if (-not [Diagnostics.EventLog]::SourceExists($name)) { New-EventLog -LogName Application -Source $name }

    New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName 'LongNameJanitor' `
        -Description 'Vigia pastas e remove arquivos com nome longo. Painel em http://localhost:5080' `
        -Credential $cred | Out-Null
    sc.exe config $name start= delayed-auto | Out-Null
    sc.exe failure $name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

    if (-not (Get-NetFirewallRule -DisplayName "$name painel" -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName "$name painel" -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
        Write-Host "  Porta $Port liberada no firewall"
    }
}

Step "Iniciando"
Start-Service $name

# Confere pelo próprio painel se as pastas estão acessíveis com a conta do serviço.
$status = $null
foreach ($i in 1..15) {
    Start-Sleep 2
    try { $status = Invoke-RestMethod "http://localhost:$Port/api/status" -TimeoutSec 3 } catch { continue }
    if (-not ($status.folders | Where-Object state -eq 'Connecting')) { break }
}

if (-not $status) {
    Write-Host "`nO serviço não respondeu. Veja o Visualizador de Eventos > Aplicativo (origem $name)." -ForegroundColor Red
    exit 1
}

if (-not $status.folders) {
    Write-Host "  Nenhuma pasta configurada em $InstallDir\appsettings.local.json" -ForegroundColor Yellow
}
foreach ($f in $status.folders) {
    if ($f.state -eq 'Watching') {
        Write-Host "  OK  $($f.path)" -ForegroundColor Green
    } else {
        Write-Host "  SEM ACESSO  $($f.path)" -ForegroundColor Yellow
        Write-Host "      $($f.lastError)" -ForegroundColor Yellow
        Write-Host "      Confira se a conta do serviço tem permissão na pasta. Ele tenta de novo sozinho." -ForegroundColor Yellow
    }
}
Write-Host "`nPainel: http://$($env:COMPUTERNAME):$Port" -ForegroundColor Cyan

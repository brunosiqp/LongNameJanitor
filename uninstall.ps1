#Requires -RunAsAdministrator
# Remove o serviço e a regra de firewall. A pasta de instalação (com data\ e o histórico) fica; apague à mão se quiser.
$name = 'LongNameJanitor'
if (Get-Service $name -ErrorAction SilentlyContinue) {
    Stop-Service $name -Force -ErrorAction SilentlyContinue
    sc.exe delete $name | Out-Null
    Write-Host "Serviço removido"
}
Get-NetFirewallRule -DisplayName "$name painel" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host "Pronto. Os arquivos continuam em $PSScriptRoot"

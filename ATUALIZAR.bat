@echo off
rem Atualiza o LongNameJanitor desta maquina com os arquivos desta pasta (pede permissao de administrador).
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%~dp0atualizar.ps1\"'"

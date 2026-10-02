# LongNameJanitor

Serviço do Windows que vigia pastas (locais ou de rede) e **apaga ou move** arquivos cujo nome passa de N
caracteres e contém um texto (ex.: um CNPJ). Tem um painel no navegador com o que está acontecendo.

![Painel](docs/painel.png)

- Reage na hora (`FileSystemWatcher`) e também varre a pasta ao iniciar e a cada `RescanMinutes`.
- Pasta de rede caiu (servidor reiniciou, SMB, rede)? Reconecta sozinho, com espera crescente de 5 s até 5 min.
- Arquivo ainda sendo gravado (em uso): tenta por ~10 s; se não der, conta como falha e a próxima varredura pega.
- **Contagem salva**: total, por dia e últimos 300 arquivos em `data\stats.json`; não zera ao reiniciar.
- **Histórico**: cada arquivo tratado vira uma linha em `data\historico\aaaa-MM-dd.csv` (abre no Excel).

## Instalar no servidor

1. Gere o pacote (na máquina de desenvolvimento, com o .NET 10 SDK):

   ```
   dotnet publish -c Release -o publish
   ```

   A pasta `publish\` tem o `LongNameJanitor.exe` (já leva o runtime: o servidor não precisa de .NET),
   a configuração e os scripts. Compacte e copie para o servidor.

2. No servidor, descompacte e rode num **PowerShell como administrador**:

   ```
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

   - Sem `appsettings.local.json`, o script copia o exemplo para `C:\Tools\LongNameJanitor\`, abre no Bloco de
     Notas para você colocar suas pastas e para. Salve e rode o `install.ps1` de novo.
   - Ele pede a **conta que roda o serviço**: precisa ser uma conta de domínio com permissão de modificar
     arquivos na pasta de rede (a conta padrão do Windows não enxerga compartilhamentos).
   - Dá o direito "Fazer logon como serviço" à conta, cria o serviço (início automático atrasado, reinicia
     sozinho se cair), libera a porta 5080 no firewall, inicia e confere se cada pasta está acessível.

3. Abra o painel: `http://<servidor>:5080`.

**Atualizar:** gere o pacote de novo e rode o mesmo `install.ps1`. Ele troca só o .exe e reinicia; a
configuração e a contagem do servidor ficam. **Remover:** `uninstall.ps1` (os arquivos e o histórico ficam).

Parâmetros: `.\install.ps1 -InstallDir D:\Apps\LongNameJanitor -Port 8080`.

## Configuração

- `appsettings.json`: valores gerais (porta do painel, varredura, logs). Vai no repositório.
- `appsettings.local.json`: **suas pastas e regras**. Fica fora do git; partir de
  [appsettings.local.example.json](appsettings.local.example.json).

```json
{
  "Janitor": {
    "Folders": [
      {
        "Path": "\\\\fileserver\\share\\nfe\\Outbox\\Process",
        "Action": "Delete",
        "MaxNameLength": 32,
        "CountExtension": true,
        "NameContains": [ "11222333000181" ],
        "IncludeSubdirectories": false
      }
    ]
  }
}
```

Um arquivo só é tratado se **as duas** regras valem: nome com mais de `MaxNameLength` caracteres **e** contém
algum dos textos de `NameContains` (em qualquer posição; lista vazia = qualquer nome).

| Campo | O que faz |
|---|---|
| `Urls` | Endereço do painel. `http://*:5080` aceita acesso de outras máquinas. |
| `Janitor:RescanMinutes` | Intervalo da varredura de segurança. |
| `Janitor:DataPath` | Onde salvar contagem e histórico (padrão: `data` ao lado do .exe). |
| `Path` | Pasta vigiada. Em JSON cada `\` vira `\\` (`\\servidor` → `"\\\\servidor"`). Use sempre o caminho `\\...`: serviços não enxergam unidades mapeadas (`Z:\`). |
| `Action` | `Delete` (apaga) ou `Move` (move para `MoveTo`; nome repetido ganha `_aaaaMMdd_HHmmssfff`). |
| `MaxNameLength` | Tamanho máximo do nome. |
| `CountExtension` | `true` conta o nome com a extensão (`relatorio.pdf` = 13). |
| `NameContains` | Textos que o nome precisa conter (basta um). |
| `IncludeSubdirectories` | Vigia também as subpastas. |

Para vigiar mais pastas, adicione outro bloco em `Folders`. Depois de editar, reinicie o serviço
(`Restart-Service LongNameJanitor`).

Logs: Visualizador de Eventos > Aplicativo, origem `LongNameJanitor`.

O painel é só leitura e não tem login: qualquer um na rede que alcance a porta vê os nomes dos arquivos. Se
precisar restringir, use `"Urls": "http://localhost:5080"` (só no próprio servidor) ou limite a regra de firewall
com `-RemoteAddress`.

## Testar no console

Rodando o .exe direto ele funciona igual, com o log na tela (Ctrl+C para sair). Dá para sobrescrever a
configuração pela linha de comando:

```
LongNameJanitor.exe --Janitor:Folders:0:Path="\\servidor\pasta" --Janitor:Folders:0:Action=Move --Janitor:Folders:0:MoveTo="C:\quarentena"
```

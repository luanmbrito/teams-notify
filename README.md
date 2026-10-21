# Teams Notify

Notificador para o **Microsoft Teams novo** no Windows. Transforma as mensagens e chamadas do Teams em um
**popup no meio da tela**, com som e voz, para que nada passe despercebido, mesmo com o Teams minimizado
ou em segundo plano.

O projeto existe em **duas versões equivalentes**, na mesma estrutura de configuração:

| Pasta | Tecnologia | Porta do painel |
|---|---|---|
| [`team-notify-python/`](team-notify-python) | Python 3.12 | `8765` |
| [`team-notify-csharp/`](team-notify-csharp) | C# / .NET 9 (WinForms + WPF) | `8766` |

---

## Qual é o intuito?

O Teams novo mostra apenas um banner pequeno e discreto no canto da tela, que some em poucos segundos. Em um
ambiente de trabalho (fábrica, escritório, ligações, reuniões) é fácil perder uma mensagem urgente ou uma chamada.

O Teams Notify resolve isso: detecta o banner, lê **quem enviou** e **o que foi escrito** e exibe um popup
grande, centralizado, que **não rouba o foco** do que você está digitando, além de tocar um aviso sonoro e,
se você quiser, falar o nome do remetente.

## O que o projeto faz

- **Popup central** para mensagens do Teams, sempre no topo e sem tirar o foco da janela atual.
- **Som e voz em português** ("Mensagem de Maria"); toque longo e prioritário para chamadas e reuniões.
- **Mensagens prioritárias** por palavras-chave (ex.: "urgente", "parou", "ajuda") ou por remetente.
- **Silenciar remetentes**, **horário de silêncio**, **modo foco** e **pausa**.
- **Agrupar** mensagens da mesma pessoa dentro de N minutos em um só popup (prioritárias e chamadas sempre aparecem).
- **Lembrete** de "sem resposta há X min" (considera lida quando você traz o Teams para a frente).
- **Soneca**, fila de popups com contador e popup de chamada que fecha sozinho ao atender.
- **Aviso no celular via [ntfy](https://ntfy.sh)** (desligado por padrão; só envia quando você está parado no PC).
- **Ícone na bandeja** com Painel, Pausar, Modo foco, Testar popup, Configurações, Iniciar com o Windows e Sair.
- **Painel local** (`http://127.0.0.1:<porta>`, acessível só no próprio computador) com estatísticas do dia e
  todas as configurações.
- **Logs opcionais**: histórico (`historico.csv`), texto das mensagens e log de erros.

## Como funciona

O Teams novo **não usa as notificações do Windows**; ele desenha um banner próprio (janela `TeamsWebView`).
O programa fica monitorando essa janela e, quando ela aparece, lê remetente e texto pela **acessibilidade do
Windows (UI Automation)**. As notificações comuns do Windows também são lidas, como reserva.

Em seguida aplica as regras do `config.json` (prioridade, silêncio, agrupamento, horário...) e decide se exibe o
popup, toca o som, fala o nome ou avisa no celular.

## Python ou C#: como usar cada um

As duas versões têm as **mesmas funções**, leem o **mesmo `config.json`** e gravam o **mesmo `historico.csv`**
(uma pode ler os arquivos da outra).

### Versão Python (`team-notify-python`)

Bibliotecas: `winsdk`, `pystray`, `pillow`, `uiautomation` (já incluídas em `libs/`, sem `pip install`).

- Requer **Python 3.12 (64 bits)** instalado.
- Dois cliques em `iniciar.bat` (ou `python teams_notify.py` para ver erros no console).
- Arquivos principais: `teams_notify.py` (programa) e `painel.py` (painel e configurações).
- Se trocar a versão do Python, refaça as libs: `pip install -r requirements.txt --target libs --upgrade`.
- Também é possível empacotar em `.exe` com PyInstaller (veja o `LEIA-ME.md` da pasta).

**Quando escolher:** ótima para ajustar o código rapidamente, ver erros no console e não depender de executável
(evita bloqueio de antivírus sobre `.exe`).

### Versão C# (`team-notify-csharp`)

Port em **.NET 9**, com popup em WinForms e UI Automation via `System.Windows.Automation`.

- Desenvolvimento: `dotnet run -c Release`.
- Gerar um único `.exe`: `powershell -ExecutionPolicy Bypass -File publicar.ps1` → `publicar\TeamsNotify.exe`.
- A máquina que for rodar precisa do **.NET 9 Desktop Runtime** (o `.exe` tem ~25 MB).
- `TeamsNotify.exe --teste` mostra dois popups de teste; `--autoteste` confere as partes nativas e grava `autoteste.txt`.
- O `config.json` fica ao lado do `.exe` e é criado com valores padrão no primeiro uso.

**Quando escolher:** ideal para distribuir a outros computadores com um único arquivo, com inicialização mais
leve e sem instalar Python.

## Importante

- **Não rode as duas versões ao mesmo tempo:** as duas leem o mesmo banner e você receberia popups duplicados.
- **Antivírus:** o Kaspersky da empresa classificou o `.exe` como `PDM:Trojan.Win32.Generic` (falso positivo, pois o
  programa lê janelas de outro aplicativo e grava na inicialização). Se bloquear, peça ao TI uma exceção para a pasta.
- A opção "Iniciar com o Windows" grava na inicialização do usuário (a C# usa `HKCU\...\Run`, nome `TeamsNotifyCSharp`).
- A detecção de chamadas depende dos textos e botões do banner; se o Teams mudar o formato, ajuste em Configurações
  ou ligue o log de erros.

## Estrutura

```
Teams-notify/
├── team-notify-python/   # versão Python (teams_notify.py, painel.py, config.json, iniciar.bat, libs/)
└── team-notify-csharp/   # versão C# (*.cs, TeamsNotify.csproj, Resources/, publicar.ps1)
```

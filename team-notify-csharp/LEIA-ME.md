# Teams Notify (versão C#)

Port em C# / .NET 9 do programa em Python da pasta acima. Mesmas funções, mesmo `config.json`
e mesmo `historico.csv` (os dois programas leem os arquivos um do outro).

## Funções
- Popup no meio da tela para mensagens do Teams novo, **sem roubar o foco** (janela não ativável, sempre no topo).
- Beep + voz em português ("Mensagem de Maria"), toque longo e prioritário para chamadas/reuniões.
- Mensagens prioritárias (palavras / remetentes), silenciar remetentes, horário de silêncio, modo foco, pausa.
- **Agrupar** mensagens da mesma pessoa dentro de N minutos num só popup (prioritárias e chamadas sempre aparecem).
- Lembrete "sem resposta há X min" (considera visto quando você traz o Teams para a frente).
- Soneca, fila de popups com contador, popup de chamada que fecha sozinho ao atender.
- Aviso no celular via ntfy (desligado por padrão; só envia quando você está parado no PC).
- Ícone na bandeja (Painel, Pausar, Modo foco, Parar lembretes, Testar popup, Configurações, Iniciar com o Windows, Sair).
- Painel local em `http://127.0.0.1:8766` (só este computador): estatísticas do dia + todas as configurações.
- Logs com interruptores: histórico, texto das mensagens, log de erros.

## Como o Teams é lido
O Teams novo não usa notificação do Windows: mostra um banner próprio (janela `TeamsWebView`).
O programa detecta quando ela fica visível e lê remetente e texto por UI Automation
(`System.Windows.Automation`). Notificações normais do Windows também são lidas, como reserva.

## Rodar / gerar
- Desenvolvimento: `dotnet run -c Release` (ou `dotnet build -c Release` e abrir `bin\Release\...\TeamsNotify.exe`).
- Publicar um único `.exe`: `powershell -ExecutionPolicy Bypass -File publicar.ps1` → `publicar\TeamsNotify.exe`
  (framework-dependent, ~25 MB; precisa do **.NET 9 Desktop Runtime** no computador que vai rodar).
- `TeamsNotify.exe --teste` mostra dois popups de teste; `--autoteste` confere as partes nativas e grava `autoteste.txt`.
- `config.json` fica ao lado do `.exe` (criado com valores padrão no primeiro uso).

## Atenção
- **Não rode junto com a versão em Python:** os dois leem o mesmo banner e você receberia popups duplicados.
  (Portas diferentes: Python 8765, C# 8766.)
- Antivírus: o Kaspersky desta empresa classificou o `.exe` do Python como `PDM:Trojan.Win32.Generic`
  (análise de comportamento: ler janelas de outro programa + gravar na inicialização). Este `.exe`
  em C# faz o mesmo e **pode ser bloqueado igualmente**. Se isso acontecer, peça ao TI uma exceção para a pasta.
- Início com o Windows usa a chave `HKCU\...\Run` (nome `TeamsNotifyCSharp`).
- Ainda sem teste com uma mensagem real do Teams nesta versão (a leitura do banner foi validada em
  janela Chromium genérica e por testes das regras).

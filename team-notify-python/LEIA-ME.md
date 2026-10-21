# Teams Notify

Popup central + som + voz para mensagens do Teams novo (Windows).

## Opção A: TeamsNotify.exe (não precisa de Python)
Copie `TeamsNotify.exe` para uma pasta e dê dois cliques. O `config.json` (e o histórico, se ligado)
ficam na mesma pasta do .exe; se o config não existir, ele é criado com os valores padrão.
Para conferir as partes nativas após gerar o .exe: `TeamsNotify.exe --autoteste` (cria `autoteste.txt`).
Para gerar de novo após mudar o código: `gerar_exe.bat` (precisa de Python 3.12 e `pip install pyinstaller`).
Observação: .exe sem assinatura digital pode ser bloqueado por antivírus/políticas da empresa.

## Opção B: com Python (scripts)
1. Python 3.12 (64 bits). Os pacotes já vêm na pasta `libs`, não precisa de `pip install`.
   (Se trocar a versão do Python, refaça a pasta: `pip install -r requirements.txt --target libs --upgrade`.)
2. Dois cliques em `iniciar.bat` (ou `python teams_notify.py` para ver erros no console).
4. Ícone na bandeja: duplo clique abre o painel (`http://127.0.0.1:8765`).
   Em "Iniciar com o Windows" (menu do ícone ou painel) ele sobe sozinho no login.

## Como funciona
O Teams novo não usa notificação do Windows: mostra um banner próprio (janela `TeamsWebView`).
O programa detecta quando ela fica visível e lê remetente e texto pela acessibilidade do Windows
(UI Automation). Notificações normais do Windows também são lidas, como reserva.

## Arquivos
- `teams_notify.py` programa principal; `painel.py` painel e configurações (só em 127.0.0.1).
- `config.json` configurações (edite pelo painel).
- `historico.csv` e `teams_notify.log` só existem se o salvamento estiver ligado.

## Limitações conhecidas
- Detecção de chamadas/reuniões baseada em textos e botões do banner; ajuste em Configurações.
- "Já visto" = você trouxe a janela do Teams para a frente.
- Se o Teams mudar o formato do banner, ligue o log de erros para ver o que ele enviou.

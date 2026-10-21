"""Teams Notify - notificações do Teams viram popup central + som + voz.

Lê as notificações do Windows (UserNotificationListener), filtra as do Teams e:
popup no meio da tela, lembrete de mensagens sem resposta, toque longo para
chamadas, voz, aviso no celular quando você está ausente, painel do dia e
ícone na bandeja com pausa / modo foco.
"""
import asyncio
import csv
import ctypes
import json
import logging
import os
import queue
import shutil
import subprocess
import sys
import threading
import time
import tkinter as tk
import urllib.request
import winsound
from datetime import datetime
from pathlib import Path

FROZEN = getattr(sys, "frozen", False)  # True quando rodando como TeamsNotify.exe
BASE = Path(sys.executable).parent if FROZEN else Path(__file__).parent

# pacotes de terceiros ficam em ./libs (versão portátil: dispensa "pip install")
if not FROZEN:
    sys.path.insert(0, str(BASE / "libs"))

import ctypes.wintypes
import pystray
import uiautomation as auto
from PIL import Image, ImageDraw
from winsdk.windows.ui.notifications import NotificationKinds
from winsdk.windows.ui.notifications.management import (
    UserNotificationListener,
    UserNotificationListenerAccessStatus,
)

import painel

CONFIG = BASE / "config.json"
HISTORICO = BASE / "historico.csv"
if FROZEN and not CONFIG.exists():  # .exe copiado sozinho: cria o config a partir do padrão embutido
    shutil.copy(Path(sys._MEIPASS) / "config.padrao.json", CONFIG)
STARTUP = (Path(os.environ.get("APPDATA", "")) / "Microsoft" / "Windows" / "Start Menu"
           / "Programs" / "Startup" / "TeamsNotify.vbs")

class _LogDeErros(logging.FileHandler):
    """Grava em teams_notify.log só enquanto "logs.erros" estiver ligado no config."""

    def emit(self, record):
        try:
            if not carregar_config().get("logs", {}).get("erros", True):
                return
        except Exception:
            pass  # config ilegível: melhor registrar o erro do que perdê-lo
        super().emit(record)


logging.basicConfig(handlers=[_LogDeErros(BASE / "teams_notify.log", encoding="utf-8", delay=True)],
                    level=logging.WARNING, format="%(asctime)s %(levelname)s %(message)s")

fila = queue.Queue()          # listener/bandeja -> interface
estado = {"pausado": False, "foco": False}
ativas = {}                   # id da notificação -> dados (para lembretes)
ultimo_popup = {}             # remetente -> quando mostramos o último popup (agrupamento)


# ---------------------------------------------------------------- utilidades
_cache_cfg = {"mtime": None, "dados": None}


def carregar_config():
    """Relê o config.json só quando o arquivo muda (o loop do banner chama isto ~10x/s)."""
    mtime = CONFIG.stat().st_mtime_ns
    if mtime != _cache_cfg["mtime"]:
        with open(CONFIG, encoding="utf-8-sig") as f:
            _cache_cfg["dados"] = json.load(f)
        _cache_cfg["mtime"] = mtime
    return _cache_cfg["dados"]


def em_horario_silencio(cfg):
    h = cfg["horario_silencio"]
    if not h.get("ativo"):
        return False
    agora = datetime.now().strftime("%H:%M")
    ini, fim = h["inicio"], h["fim"]
    return (ini <= agora or agora < fim) if ini > fim else (ini <= agora < fim)


def registrar(cfg, app, remetente, texto, tipo, prio, motivo):
    logs = cfg.get("logs", {})
    if not logs.get("historico", True):
        return
    if not logs.get("salvar_texto", True):
        texto = ""
    novo = not HISTORICO.exists()
    with open(HISTORICO, "a", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f, delimiter=";")
        if novo:
            w.writerow(["data_hora", "app", "remetente", "mensagem", "tipo", "prioridade", "motivo"])
        w.writerow([datetime.now().strftime("%Y-%m-%d %H:%M:%S"), app, remetente, texto,
                    tipo, "sim" if prio else "nao", motivo])


def classificar(cfg, remetente, texto, botoes=()):
    """Retorna 'silenciar', 'chamada', 'prioritaria' ou 'normal'."""
    r, t = remetente.lower(), texto.lower()
    junto = r + " " + t
    if any(s.lower() in r for s in cfg["remetentes_silenciados"]):
        return "silenciar"
    if any(p.lower() in junto for p in cfg["palavras_chamada"]):
        return "chamada"
    if any(k.lower() in b.lower() for b in botoes for k in cfg.get("botoes_chamada", [])):
        return "chamada"
    if any(s.lower() in r for s in cfg["remetentes_prioritarios"]):
        return "prioritaria"
    if any(p.lower() in junto for p in cfg["palavras_prioritarias"]):
        return "prioritaria"
    return "normal"


class _LASTINPUT(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint), ("dwTime", ctypes.c_uint)]


def segundos_ocioso():
    ctypes.windll.kernel32.GetTickCount.restype = ctypes.c_uint
    li = _LASTINPUT()
    li.cbSize = ctypes.sizeof(li)
    ctypes.windll.user32.GetLastInputInfo(ctypes.byref(li))
    return (ctypes.windll.kernel32.GetTickCount() - li.dwTime) / 1000


# ---------------------------------------------------------------- celular / voz
def enviar_celular(cfg, remetente, corpo):
    """Push via ntfy, só se ativo e você estiver parado há N minutos."""
    c = cfg["celular"]
    if not c["ativo"] or not c["topico"]:
        return
    if segundos_ocioso() < c["ausente_minutos"] * 60:
        return
    dados = {"topic": c["topico"], "title": f"Teams: {remetente}", "priority": 4,
             "message": corpo if c["enviar_texto"] and corpo else "Nova mensagem no Teams"}
    req = urllib.request.Request(c["servidor"].rstrip("/"), data=json.dumps(dados).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    try:
        urllib.request.urlopen(req, timeout=8).read()
    except Exception as e:
        logging.warning("Falha ao enviar para o celular: %s", e)


def falar(texto):
    ps = ("Add-Type -AssemblyName System.Speech;"
          "$s=New-Object System.Speech.Synthesis.SpeechSynthesizer;"
          "$v=$s.GetInstalledVoices()|Where-Object{$_.VoiceInfo.Culture.Name -eq 'pt-BR'}|Select-Object -First 1;"
          "if($v){$s.SelectVoice($v.VoiceInfo.Name)};$s.Speak($env:TN_TEXTO)")
    try:
        subprocess.run(["powershell", "-NoProfile", "-Command", ps], timeout=30,
                       env={**os.environ, "TN_TEXTO": texto}, creationflags=0x08000000)
    except Exception as e:
        logging.warning("Falha na voz: %s", e)


# ---------------------------------------------------------------- processamento comum
def processar(cfg, app, id_, remetente, corpo, botoes=()):
    """Classifica, registra no histórico e (se permitido) enfileira o popup."""
    classe = classificar(cfg, remetente, corpo, botoes)
    tipo = "chamada" if classe == "chamada" else "msg"
    prio = classe in ("prioritaria", "chamada")
    motivo = ""
    if classe == "silenciar":
        motivo = "silenciado"
    elif estado["pausado"]:
        motivo = "pausado"
    elif em_horario_silencio(cfg):
        motivo = "horário de silêncio"
    elif estado["foco"] and classe == "normal":
        motivo = "modo foco"
    janela = cfg.get("agrupar_minutos", 0) * 60
    chave = remetente.strip().lower()
    if not motivo and janela and not prio and time.time() - ultimo_popup.get(chave, 0) < janela:
        motivo = "agrupada"  # mesma pessoa dentro do intervalo: só o primeiro popup aparece
    registrar(cfg, app, remetente, corpo, tipo, prio, motivo)
    if motivo:
        return None
    ultimo_popup[chave] = time.time()
    item = {"tipo": tipo, "id": id_, "prio": prio, "remetente": remetente, "corpo": corpo}
    fila.put(item)
    ativas[id_] = {**item, "criada": time.time(), "ultimo": time.time(), "avisos": 0}
    threading.Thread(target=enviar_celular, args=(cfg, remetente, corpo), daemon=True).start()
    return tipo


def lembretes(cfg):
    """Mensagens sem resposta: reavisa depois de X minutos."""
    lc = cfg["lembrete"]
    if not lc["ativo"] or estado["pausado"] or estado["foco"]:
        return
    agora = time.time()
    for a in list(ativas.values()):
        if a["tipo"] == "chamada" or a["avisos"] >= lc["maximo"]:
            continue
        espera = (lc["minutos"] if a["avisos"] == 0 else lc["repetir_minutos"]) * 60
        if agora - a["ultimo"] >= espera:
            a["avisos"] += 1
            a["ultimo"] = agora
            fila.put({**{k: a[k] for k in ("id", "prio", "remetente", "corpo")},
                      "tipo": "lembrete", "minutos": int((agora - a["criada"]) / 60)})


# ---------------------------------------------------------------- banner do Teams novo
# O Teams novo não usa notificação do Windows: mostra um banner próprio (janela
# "TeamsWebView" reaproveitada). Aqui detectamos quando ela fica visível e lemos o
# texto pela acessibilidade (UI Automation).
_u32 = ctypes.windll.user32
_k32 = ctypes.windll.kernel32
_k32.OpenProcess.restype = ctypes.c_void_p
_k32.CloseHandle.argtypes = [ctypes.c_void_p]


def _info_janela(h):
    t = ctypes.create_unicode_buffer(256)
    c = ctypes.create_unicode_buffer(128)
    _u32.GetWindowTextW(h, t, 256)
    _u32.GetClassNameW(h, c, 128)
    r = ctypes.wintypes.RECT()
    _u32.GetWindowRect(h, ctypes.byref(r))
    return c.value, t.value, r.right - r.left


def eh_banner(titulo, largura):
    return titulo == "Microsoft Teams" and largura < 600


def banners_visiveis():
    achados = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(h, _):
        if _u32.IsWindowVisible(h):
            classe, titulo, largura = _info_janela(h)
            if classe == "TeamsWebView" and eh_banner(titulo, largura):
                achados.append(h)
        return True

    _u32.EnumWindows(cb, 0)
    return set(achados)


def teams_em_foco():
    """True se a janela em primeiro plano é o Teams (não o banner)."""
    h = _u32.GetForegroundWindow()
    if not h:
        return False
    classe, titulo, largura = _info_janela(h)
    if classe != "TeamsWebView" or eh_banner(titulo, largura):
        return False
    pid = ctypes.wintypes.DWORD()
    _u32.GetWindowThreadProcessId(h, ctypes.byref(pid))
    proc = _k32.OpenProcess(0x1000, False, pid.value)
    if not proc:
        return False
    buf = ctypes.create_unicode_buffer(520)
    n = ctypes.wintypes.DWORD(520)
    _k32.QueryFullProcessImageNameW(proc, 0, buf, ctypes.byref(n))
    _k32.CloseHandle(proc)
    return os.path.basename(buf.value).lower() == "ms-teams.exe"


def ler_banner(hwnd):
    """Retorna (remetente, corpo, botoes) ou None se o conteúdo ainda não carregou."""
    raiz = auto.ControlFromHandle(hwnd)
    textos, botoes = [], []
    for c, _ in auto.WalkControl(raiz, includeTop=True, maxDepth=30):
        tipo, nome = c.ControlTypeName, (c.Name or "").strip()
        if tipo == "ButtonControl" and nome:
            botoes.append(nome)
        elif tipo == "TextControl" and nome and nome != "Microsoft Teams":
            textos.append(nome)
    # Cartão de mensagem do Teams: ["<corpo> <remetente>", "<remetente>", "<corpo>"]
    if len(textos) >= 3 and textos[0] == f"{textos[-1]} {textos[-2]}":
        return textos[-2], textos[-1], botoes
    if textos:  # formato desconhecido (ex.: chamada): usa o que houver e registra p/ ajuste
        logging.warning("Banner fora do padrão: textos=%s botoes=%s", textos, botoes)
        return textos[0], "\n".join(textos[1:]), botoes
    return None


def vigiar_teams():
    with auto.UIAutomationInitializerInThread():
        visiveis = banners_visiveis()  # o que já estava na tela ao iniciar não conta
        est = {}                       # hwnd -> estado da exibição atual
        ultimo_assin = {}              # hwnd -> última (remetente, corpo) emitida
        chamadas = {}                  # hwnd -> id da chamada em curso
        contador, t_lembrete = 0, 0
        while True:
            try:
                cfg = carregar_config()
                agora = time.time()
                atuais = banners_visiveis()
                for h in atuais - visiveis:
                    est[h] = {"inicio": agora, "emitido": False, "leitura": 0}
                for h in atuais:
                    e = est[h] if h in est else est.setdefault(h, {"inicio": agora, "emitido": False, "leitura": 0})
                    if agora - e["leitura"] < (0.25 if agora - e["inicio"] < 2 else 0.7):
                        continue
                    e["leitura"] = agora
                    lido = ler_banner(h)
                    if not lido:
                        continue
                    assin = (lido[0], lido[1])
                    # conteúdo igual ao da última vez pode ser sobra da mensagem anterior:
                    # só aceita como repetição depois de 1,2 s de banner visível
                    if assin != ultimo_assin.get(h) or (not e["emitido"] and agora - e["inicio"] > 1.2):
                        ultimo_assin[h] = assin
                        e["emitido"] = True
                        contador += 1
                        id_ = f"b{contador}"
                        if processar(cfg, "Microsoft Teams", id_, lido[0], lido[1], lido[2]) == "chamada":
                            chamadas[h] = id_
                for h in visiveis - atuais:  # banner sumiu: chamada atendida/recusada/perdida
                    est.pop(h, None)
                    if h in chamadas:
                        i = chamadas.pop(h)
                        ativas.pop(i, None)
                        fila.put({"tipo": "resolvida", "id": i})
                visiveis = atuais

                if ativas and teams_em_foco():  # você abriu o Teams: considera visto
                    for i in [i for i in list(ativas) if isinstance(i, str)]:
                        ativas.pop(i, None)
                        fila.put({"tipo": "resolvida", "id": i})
                if agora - t_lembrete >= 1:
                    t_lembrete = agora
                    lembretes(cfg)
            except Exception:
                logging.exception("Falha ao vigiar o banner do Teams")
                time.sleep(1)
            time.sleep(0.1)


# ---------------------------------------------------------------- listener (notificações do Windows)
async def ouvir():
    listener = UserNotificationListener.current
    acesso = await listener.request_access_async()
    if acesso != UserNotificationListenerAccessStatus.ALLOWED:
        fila.put({"tipo": "erro", "id": None, "prio": True, "remetente": "Teams Notify",
                  "corpo": "Acesso às notificações negado.\nConfigurações > Privacidade > Notificações."})
        return

    vistos, primeira = set(), True
    while True:
        try:
            cfg = carregar_config()
            notifs = await listener.get_notifications_async(NotificationKinds.TOAST)
            atuais = set()
            for n in notifs:
                atuais.add(n.id)
                if n.id in vistos:
                    continue
                vistos.add(n.id)
                if primeira:  # ignora o que já estava na central ao iniciar
                    continue
                app = n.app_info.display_info.display_name
                if not any(a.lower() in app.lower() for a in cfg["apps"]):
                    continue
                binding = n.notification.visual.get_binding("ToastGeneric")
                textos = [t.text for t in binding.get_text_elements()] if binding else []
                remetente = textos[0] if textos else app
                corpo = "\n".join(textos[1:])
                processar(cfg, app, n.id, remetente, corpo)

            # notificação sumiu da central = você leu / atendeu no Teams
            for i in [i for i in list(ativas) if isinstance(i, int) and i not in atuais]:
                ativas.pop(i, None)
                fila.put({"tipo": "resolvida", "id": i})
            vistos &= atuais
            primeira = False
        except Exception as e:  # um erro pontual não derruba o serviço
            logging.exception("Falha ao ler notificações")
        await asyncio.sleep(1)


# ---------------------------------------------------------------- interface
CABECALHO = {
    "msg": ("#E65100", "💬 Nova mensagem no Teams"),
    "lembrete": ("#6A1B9A", "⏰ Sem resposta"),
    "chamada": ("#1565C0", "📞 CHAMADA / REUNIÃO"),
    "erro": ("#C62828", "⚠ Teams Notify"),
}


class App:
    def __init__(self):
        self.cfg = carregar_config()
        self.root = tk.Tk()
        self.root.attributes("-alpha", 0)  # sem isso a janela-raiz pisca e rouba o foco ao iniciar
        self.root.withdraw()
        self.pendentes = []
        self.win = None
        self.atual = None
        self.pausado_ate = 0
        self.beep_job = None
        self.icone = None
        self.root.report_callback_exception = lambda *e: logging.error("Erro na interface", exc_info=e)
        self.root.after(300, self.tick)

    # -- fila ------------------------------------------------------------
    def tick(self):
        try:
            self._tick()
        except Exception:  # um erro não pode parar o ciclo de popups
            logging.exception("Erro no ciclo da interface")
            if self.atual in self.pendentes:  # descarta o popup que falhou para não travar a fila
                self.pendentes.remove(self.atual)
            self._destruir()
        self.root.after(300, self.tick)

    def _tick(self):
        try:
            while True:
                item = fila.get_nowait()
                t = item["tipo"]
                if t == "resolvida":
                    self.resolver(item["id"])
                elif t == "sair":
                    if self.icone:
                        self.icone.stop()
                    self.root.quit()
                    return
                elif t == "chamada":
                    self.pendentes.insert(0, item)
                    if self.win is not None and self.atual["tipo"] != "chamada":
                        self._destruir()  # chamada tem prioridade; a mensagem continua na fila
                else:
                    self.pendentes.append(item)
        except queue.Empty:
            pass
        livre = self.pendentes and self.win is None
        if livre and (time.time() >= self.pausado_ate or self.pendentes[0]["tipo"] == "chamada"):
            self.mostrar(self.pendentes[0])
        elif self.win is not None:
            self.atualizar_contador()

    def resolver(self, id_):
        self.pendentes = [p for p in self.pendentes if p["id"] != id_]
        if self.win is not None and self.atual["id"] == id_:
            self._destruir()

    # -- popup -----------------------------------------------------------
    def mostrar(self, item):
        self.cfg = carregar_config()
        self.atual = item
        tipo = item["tipo"]
        cor, titulo = CABECALHO[tipo]
        if tipo == "msg" and item["prio"]:
            cor, titulo = "#C62828", "🔴 PRIORITÁRIA"
        if tipo == "lembrete":
            titulo = f"⏰ Sem resposta há {item['minutos']} min"
        w, h = self.cfg["largura"], self.cfg["altura"]
        win = tk.Toplevel(self.root)
        self.win = win
        win.withdraw()  # só mostra depois de marcar a janela como "não ativável"
        win.overrideredirect(True)
        win.attributes("-topmost", True)
        win.geometry(f"{w}x{h}+{(win.winfo_screenwidth() - w) // 2}+{(win.winfo_screenheight() - h) // 2}")
        win.configure(bg=cor)

        quadro = tk.Frame(win, bg="white")
        quadro.pack(fill="both", expand=True, padx=4, pady=4)
        topo = tk.Frame(quadro, bg=cor)
        topo.pack(fill="x")
        tk.Label(topo, text=titulo, bg=cor, fg="white", font=("Segoe UI", 12, "bold"),
                 anchor="w").pack(side="left", padx=10, pady=6)
        self.lbl_cont = tk.Label(topo, text="", bg=cor, fg="white", font=("Segoe UI", 10))
        self.lbl_cont.pack(side="right", padx=10)
        tk.Label(quadro, text=item["remetente"], bg="white", fg="#222", font=("Segoe UI", 16, "bold"),
                 anchor="w", wraplength=w - 40, justify="left").pack(fill="x", padx=16, pady=(12, 2))
        tk.Label(quadro, text=item["corpo"][:400], bg="white", fg="#333", font=("Segoe UI", 12),
                 anchor="nw", wraplength=w - 40, justify="left").pack(fill="both", expand=True, padx=16)
        botoes = tk.Frame(quadro, bg="white")
        botoes.pack(fill="x", padx=12, pady=10)
        estilo = dict(font=("Segoe UI", 11, "bold"), relief="flat", padx=14, pady=6, cursor="hand2")
        tk.Button(botoes, text="Abrir Teams", bg="#464EB8", fg="white", command=self.abrir, **estilo).pack(side="left")
        tk.Button(botoes, text="OK", bg="#2E7D32", fg="white", command=self.fechar, **estilo).pack(side="left", padx=8)
        if tipo != "chamada":
            tk.Button(botoes, text=f"Soneca {self.cfg['soneca_minutos']} min", bg="#757575", fg="white",
                      command=self.soneca, **estilo).pack(side="right")
        self.sem_foco(win)
        self.atualizar_contador()
        self.tocar(item, voz=True)

    @staticmethod
    def sem_foco(win):
        """Exibe o popup sem ativá-lo: o teclado continua no app em que você está."""
        win.update_idletasks()
        gp, gl, sl = _u32.GetParent, _u32.GetWindowLongW, _u32.SetWindowLongW
        gp.restype, gp.argtypes = ctypes.c_void_p, [ctypes.c_void_p]
        gl.restype, gl.argtypes = ctypes.c_long, [ctypes.c_void_p, ctypes.c_int]
        sl.restype, sl.argtypes = ctypes.c_long, [ctypes.c_void_p, ctypes.c_int, ctypes.c_long]
        hwnd = gp(win.winfo_id()) or win.winfo_id()
        # WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW (sem botão na barra de tarefas)
        sl(hwnd, -20, gl(hwnd, -20) | 0x08000000 | 0x80)
        # SW_SHOWNOACTIVATE direto (o deiconify do Tk ativaria a janela) + sempre no topo sem ativar
        _u32.ShowWindow.argtypes = [ctypes.c_void_p, ctypes.c_int]
        _u32.SetWindowPos.argtypes = [ctypes.c_void_p, ctypes.c_void_p] + [ctypes.c_int] * 4 + [ctypes.c_uint]
        _u32.ShowWindow(hwnd, 4)
        _u32.SetWindowPos(hwnd, ctypes.c_void_p(-1), 0, 0, 0, 0, 0x13)  # TOPMOST | NOMOVE | NOSIZE | NOACTIVATE

    def atualizar_contador(self):
        extra = len(self.pendentes) - 1
        if self.win is not None and self.lbl_cont.winfo_exists():
            self.lbl_cont.config(text=f"+{extra} na fila" if extra > 0 else "")

    # -- som -------------------------------------------------------------
    def tocar(self, item, voz=False):
        if self.win is None or self.atual is not item:
            return
        threading.Thread(target=self._som, args=(item, voz, self.cfg), daemon=True).start()
        atraso = 3000 if item["tipo"] == "chamada" else self.cfg["repetir_beep_segundos"] * 1000
        self.beep_job = self.root.after(atraso, self.tocar, item, False)

    @staticmethod
    def _som(item, voz, cfg):
        tipo = item["tipo"]
        if not cfg.get("som", {}).get("ativo", True):
            pass
        elif tipo == "chamada":
            for f in (880, 1100, 880, 1100):
                winsound.Beep(f, 230)
        elif item["prio"]:
            for _ in range(3):
                winsound.Beep(1200, 180)
                winsound.Beep(900, 180)
        else:
            winsound.Beep(1000, 200)
            winsound.Beep(1300, 250)
        if voz and cfg["voz"]["ativo"]:
            de = item["remetente"]
            frase = {"chamada": f"Chamada ou reunião do Teams. {de}",
                     "lembrete": f"Sem resposta há {item.get('minutos', 0)} minutos. {de}"
                     }.get(tipo, f"{'Mensagem prioritária' if item['prio'] else 'Mensagem'} de {de}")
            if cfg["voz"]["falar_texto"] and tipo == "msg":
                frase += ". " + item["corpo"][:200]
            falar(frase)

    # -- ações -----------------------------------------------------------
    def _destruir(self):
        if self.beep_job:
            self.root.after_cancel(self.beep_job)
            self.beep_job = None
        if self.win is not None:
            self.win.destroy()
            self.win = None

    def fechar(self):  # "vi": some, mas o lembrete continua se não for resolvida
        if self.atual in self.pendentes:
            self.pendentes.remove(self.atual)
        self._destruir()

    def abrir(self):  # abriu o Teams = considera tudo resolvido
        os.startfile("msteams:")
        self.pendentes.clear()
        ativas.clear()
        self._destruir()

    def soneca(self):
        self.pausado_ate = time.time() + self.cfg["soneca_minutos"] * 60
        self._destruir()  # mensagem continua na fila e volta depois

    def run(self):
        threading.Thread(target=lambda: asyncio.run(ouvir()), daemon=True).start()
        threading.Thread(target=vigiar_teams, daemon=True).start()
        painel.iniciar_servidor({"estado": estado, "mudou": lambda: atualizar_bandeja(self.icone),
                                 "inicio_get": inicio_ativo, "inicio_set": definir_inicio})
        self.icone = criar_bandeja()
        threading.Thread(target=self.icone.run, daemon=True).start()
        self.root.mainloop()


# ---------------------------------------------------------------- bandeja
def desenhar_icone(cor):
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse((4, 4, 60, 60), fill=cor)
    d.ellipse((22, 22, 42, 42), fill="white")
    return img


def inicio_ativo():
    return STARTUP.exists()


def definir_inicio(ativo):
    if not ativo:
        STARTUP.unlink(missing_ok=True)
        return
    if FROZEN:
        comando = f'"""{sys.executable}"""'
    else:
        pyw = Path(sys.executable).with_name("pythonw.exe")
        comando = f'"""{pyw}"" ""{BASE / "teams_notify.py"}"""'
    # VBS roda sem janela de console; usa a codepage ANSI por causa dos acentos no caminho
    STARTUP.write_text(f'CreateObject("WScript.Shell").Run {comando}, 0, False\r\n', encoding="mbcs")


def alternar_inicio(icon, item):
    definir_inicio(not inicio_ativo())


def cor_bandeja():
    return "#757575" if estado["pausado"] else "#E65100" if estado["foco"] else "#2E7D32"


def rotulo_bandeja():
    if estado["pausado"]:
        return "Teams Notify - PAUSADO"
    return "Teams Notify - modo foco" if estado["foco"] else "Teams Notify - ativo"


def atualizar_bandeja(icon):
    if icon is not None:
        icon.icon = desenhar_icone(cor_bandeja())
        icon.title = rotulo_bandeja()
        icon.update_menu()


def criar_bandeja():
    def pausar(icon, item):
        estado["pausado"] = not estado["pausado"]
        atualizar_bandeja(icon)

    def foco(icon, item):
        estado["foco"] = not estado["foco"]
        atualizar_bandeja(icon)

    def limpar(icon, item):
        ativas.clear()

    def teste(icon, item):
        fila.put({"tipo": "msg", "id": None, "prio": False, "remetente": "Teste", "corpo": "Popup de teste."})

    menu = pystray.Menu(
        pystray.MenuItem("Painel e configurações", lambda i, it: painel.abrir(), default=True),
        pystray.MenuItem(lambda i: "Retomar notificações" if estado["pausado"] else "Pausar notificações", pausar),
        pystray.MenuItem("Modo foco (só prioritárias e chamadas)", foco, checked=lambda i: estado["foco"]),
        pystray.Menu.SEPARATOR,
        pystray.MenuItem("Parar lembretes pendentes", limpar),
        pystray.MenuItem("Testar popup", teste),
        pystray.MenuItem("Configurações", lambda i, it: painel.abrir("#cfg")),
        pystray.MenuItem("Iniciar com o Windows", alternar_inicio, checked=lambda i: inicio_ativo()),
        pystray.Menu.SEPARATOR,
        pystray.MenuItem("Sair", lambda i, it: fila.put({"tipo": "sair"})),
    )
    return pystray.Icon("teams_notify", desenhar_icone(cor_bandeja()), rotulo_bandeja(), menu)


# ---------------------------------------------------------------- main
if __name__ == "__main__":
    if "--autoteste" in sys.argv:  # confere as partes nativas (útil após gerar o .exe)
        with auto.UIAutomationInitializerInThread():
            controles = sum(1 for _ in auto.WalkControl(auto.GetRootControl(), maxDepth=1))
        acesso = UserNotificationListener.current.get_access_status()
        (BASE / "autoteste.txt").write_text(f"uiautomation ok ({controles} controles); notificacoes: {acesso}; "
                                            f"frozen={FROZEN}; base={BASE}", encoding="utf-8")
        sys.exit(0)
    mutex = ctypes.windll.kernel32.CreateMutexW(None, False, "TeamsNotify_instancia_unica")
    if ctypes.windll.kernel32.GetLastError() == 183:  # já está rodando
        sys.exit(0)
    if "--teste" in sys.argv:
        def _teste():  # com atraso, para dar tempo de medir o foco depois da inicialização
            fila.put({"tipo": "msg", "id": None, "prio": True, "remetente": "Maria (Produção)",
                      "corpo": "Linha 3 parou, preciso de ajuda urgente!"})
            fila.put({"tipo": "msg", "id": None, "prio": False, "remetente": "João", "corpo": "Bom dia, tem um minuto?"})
        threading.Timer(5, _teste).start()
    App().run()

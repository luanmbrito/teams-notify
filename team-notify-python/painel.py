"""Painel local (só 127.0.0.1): estatísticas do dia + configurações do Teams Notify.

Protegido contra outros sites: confere o Host (DNS rebinding), exige JSON + token
aleatório por execução nas alterações e usa CSP restritiva.
"""
import csv
import hmac
import json
import secrets
import sys
import threading
import webbrowser
from collections import Counter
from datetime import date, datetime
from html import escape
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

BASE = Path(sys.executable).parent if getattr(sys, "frozen", False) else Path(__file__).parent
HISTORICO = BASE / "historico.csv"
CONFIG = BASE / "config.json"

# caminho no config.json -> tipo ("bool", "lines", "time", "url", "text" ou ("int", min, max))
SCHEMA = {
    "som.ativo": "bool",
    "voz.ativo": "bool",
    "voz.falar_texto": "bool",
    "logs.historico": "bool",
    "logs.salvar_texto": "bool",
    "logs.erros": "bool",
    "agrupar_minutos": ("int", 0, 240),
    "repetir_beep_segundos": ("int", 5, 600),
    "soneca_minutos": ("int", 1, 240),
    "horario_silencio.ativo": "bool",
    "horario_silencio.inicio": "time",
    "horario_silencio.fim": "time",
    "lembrete.ativo": "bool",
    "lembrete.minutos": ("int", 1, 240),
    "lembrete.repetir_minutos": ("int", 1, 240),
    "lembrete.maximo": ("int", 1, 20),
    "palavras_prioritarias": "lines",
    "palavras_chamada": "lines",
    "botoes_chamada": "lines",
    "remetentes_prioritarios": "lines",
    "remetentes_silenciados": "lines",
    "celular.ativo": "bool",
    "celular.servidor": "url",
    "celular.topico": "text",
    "celular.ausente_minutos": ("int", 1, 240),
    "celular.enviar_texto": "bool",
}


# ---------------------------------------------------------------- config
def ler_config():
    with open(CONFIG, encoding="utf-8-sig") as f:
        return json.load(f)


def obter(cfg, caminho, padrao=None):
    for parte in caminho.split("."):
        if not isinstance(cfg, dict) or parte not in cfg:
            return padrao
        cfg = cfg[parte]
    return cfg


def definir(cfg, caminho, valor):
    *pais, ultimo = caminho.split(".")
    for p in pais:
        cfg = cfg.setdefault(p, {})
    cfg[ultimo] = valor


def validar(dados):
    if not isinstance(dados, dict):
        raise ValueError("formato inválido")
    saida = {}
    for k, v in dados.items():
        tipo = SCHEMA.get(k)
        if tipo is None:
            raise ValueError(f"campo desconhecido: {k}")
        ok = True
        if tipo == "bool":
            ok = isinstance(v, bool)
        elif tipo == "lines":
            ok = isinstance(v, list) and all(isinstance(x, str) and len(x) <= 200 for x in v)
            if ok:
                v = [x.strip() for x in v if x.strip()][:200]
        elif tipo == "time":
            try:
                ok = isinstance(v, str) and bool(datetime.strptime(v, "%H:%M"))
            except ValueError:
                ok = False
        elif tipo == "url":
            ok = isinstance(v, str) and len(v) <= 200 and v.startswith(("https://", "http://"))
        elif tipo == "text":
            ok = isinstance(v, str) and len(v) <= 100
            v = v.strip() if ok else v
        else:
            _, lo, hi = tipo
            ok = isinstance(v, int) and not isinstance(v, bool) and lo <= v <= hi
        if not ok:
            raise ValueError(f"valor inválido em {k}")
        saida[k] = v
    return saida


def salvar_config(novos):
    cfg = ler_config()
    for caminho, valor in novos.items():
        definir(cfg, caminho, valor)
    tmp = CONFIG.with_suffix(".tmp")
    tmp.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
    tmp.replace(CONFIG)


# ---------------------------------------------------------------- histórico
def ler_historico(dia):
    if not HISTORICO.exists():
        return []
    with open(HISTORICO, encoding="utf-8-sig", newline="") as f:
        return [r for r in csv.DictReader(f, delimiter=";") if r["data_hora"].startswith(dia)]


def barras(contagem, maximo=10):
    itens = contagem.most_common(maximo)
    if not itens:
        return '<p class="mut">Sem dados.</p>'
    topo = itens[0][1]
    return "".join(
        f'<div class="row"><span class="n" title="{escape(k)}">{escape(k)}</span>'
        f'<span class="b" style="width:{max(2, int(v / topo * 160))}px"></span><span class="v">{v}</span></div>'
        for k, v in itens)


def bloco_painel(dia):
    linhas = ler_historico(dia)
    ocultas = sum(1 for r in linhas if r["motivo"])
    prio = sum(r["prioridade"] == "sim" for r in linhas)
    chamadas = sum(r["tipo"] == "chamada" for r in linhas)
    por_hora = Counter(int(r["data_hora"][11:13]) for r in linhas)
    topo_h = max(por_hora.values(), default=1)
    horas = "".join(
        f'<div><i style="height:{int(por_hora.get(h, 0) / topo_h * 110)}px" title="{por_hora.get(h, 0)}"></i>{h:02d}</div>'
        for h in range(24))
    recentes = "".join(
        f'<tr><td>{escape(r["data_hora"][11:16])}</td><td>{escape(r["remetente"])}</td>'
        f'<td>{escape(r["mensagem"][:120])}</td>'
        f'<td><span class="tag">{escape(r["tipo"] + (" · " + r["motivo"] if r["motivo"] else ""))}</span></td></tr>'
        for r in reversed(linhas[-40:]))
    vazio = '<tr><td colspan="4" class="mut">Nada neste dia.</td></tr>'
    return f"""
<div class="kpis">
<div class="card kpi"><b>{len(linhas)}</b><span>notificações recebidas</span></div>
<div class="card kpi"><b>{prio}</b><span>prioritárias</span></div>
<div class="card kpi"><b>{chamadas}</b><span>chamadas / reuniões</span></div>
<div class="card kpi"><b>{ocultas}</b><span>ocultadas (silêncio/foco/pausa)</span></div></div>
<div class="grid"><div class="card"><h2>Quem mais chamou</h2>{barras(Counter(r["remetente"] for r in linhas))}</div>
<div class="card"><h2>Por hora</h2><div class="horas">{horas}</div></div></div>
<div class="card" style="margin-top:12px"><h2>Últimas notificações</h2>
<table><tr><th>Hora</th><th>De</th><th>Mensagem</th><th>Tipo</th></tr>{recentes or vazio}</table></div>"""


# ---------------------------------------------------------------- página
CSS = """
:root{--bg:#f5f6fa;--card:#fff;--tx:#1f2430;--mut:#6b7280;--bar:#464EB8;--bd:#e5e7eb;--ok:#2E7D32;--err:#C62828}
@media(prefers-color-scheme:dark){:root{--bg:#14161c;--card:#1d2029;--tx:#e8eaf0;--mut:#9aa1b0;--bar:#7c85ff;--bd:#2c303c}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--tx);font:15px/1.45 'Segoe UI',sans-serif}
main{max-width:980px;margin:0 auto;padding:24px 16px 80px}h1{font-size:22px;margin:0 0 4px}.mut{color:var(--mut)}
.abas{display:flex;gap:6px;margin:16px 0;border-bottom:1px solid var(--bd)}
.abas button{background:none;border:0;border-bottom:3px solid transparent;padding:8px 14px;font:inherit;font-weight:600;color:var(--mut);cursor:pointer}
.abas button.on{color:var(--tx);border-color:var(--bar)}.aba{display:none}.aba.on{display:block}
.kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin:18px 0}
.card{background:var(--card);border:1px solid var(--bd);border-radius:10px;padding:14px 16px;margin-bottom:12px}
.kpi b{display:block;font-size:28px}.kpi span{color:var(--mut);font-size:13px}
h2{font-size:16px;margin:0 0 10px}.grid{display:grid;grid-template-columns:1fr 1fr;gap:12px}
@media(max-width:760px){.grid{grid-template-columns:1fr}}
.row{display:flex;align-items:center;gap:8px;margin:5px 0}.row .n{width:150px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.row .b{height:14px;background:var(--bar);border-radius:3px;min-width:2px}.row .v{color:var(--mut);font-size:13px}
.horas{display:flex;align-items:flex-end;gap:3px;height:140px}.horas div{flex:1;text-align:center;font-size:10px;color:var(--mut)}
.horas i{display:block;background:var(--bar);border-radius:3px 3px 0 0;margin-bottom:3px}
table{width:100%;border-collapse:collapse;font-size:13px}td,th{text-align:left;padding:6px 8px;border-bottom:1px solid var(--bd);vertical-align:top}
th{color:var(--mut);font-weight:600}.tag{font-size:11px;padding:1px 6px;border-radius:8px;background:var(--bd)}
.cfg label.l{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:7px 0;border-bottom:1px solid var(--bd)}
.cfg label.l:last-child{border:0}.cfg small{display:block;color:var(--mut);font-size:12px}
.cfg input[type=number]{width:80px}.cfg input[type=text],.cfg textarea{width:100%;font:inherit;padding:6px 8px;border:1px solid var(--bd);border-radius:6px;background:var(--bg);color:var(--tx)}
.cfg input[type=number],.cfg input[type=time]{font:inherit;padding:5px 8px;border:1px solid var(--bd);border-radius:6px;background:var(--bg);color:var(--tx)}
.cfg input[type=checkbox]{width:20px;height:20px;accent-color:var(--bar)}.cfg textarea{min-height:84px}.blk{padding:7px 0}
.salvar{position:fixed;bottom:0;left:0;right:0;background:var(--card);border-top:1px solid var(--bd);padding:10px 16px;text-align:center}
.salvar button{background:var(--bar);color:#fff;border:0;border-radius:8px;padding:9px 28px;font:inherit;font-weight:700;cursor:pointer}
#msg{margin-left:14px;font-weight:600}.aviso{font-size:13px;color:var(--mut);margin:6px 0 0}
"""

JS = """
const T="__TOKEN__";
const $=s=>document.querySelector(s), $$=s=>[...document.querySelectorAll(s)];
function aviso(t,ok=true){const m=$('#msg');m.textContent=t;m.style.color=ok?'var(--ok)':'var(--err)';setTimeout(()=>m.textContent='',4000)}
async function api(p,b){
  const r=await fetch(p,{method:'POST',headers:{'Content-Type':'application/json','X-Token':T},body:JSON.stringify(b)});
  const j=await r.json().catch(()=>({}));if(!r.ok)throw new Error(j.erro||('erro '+r.status));return j}
function aba(n){$$('.aba').forEach(e=>e.classList.toggle('on',e.id===n));$$('.abas button').forEach(e=>e.classList.toggle('on',e.dataset.a===n));
  $('.salvar').style.display=n==='cfg'?'block':'none';history.replaceState(null,'','#'+n)}
$$('.abas button').forEach(b=>b.addEventListener('click',()=>aba(b.dataset.a)));
aba(location.hash==='#cfg'?'cfg':'painel');
$$('[data-live]').forEach(el=>el.addEventListener('change',async()=>{
  try{await api(el.dataset.live==='inicio'?'/api/inicio':'/api/estado',
    el.dataset.live==='inicio'?{ativo:el.checked}:{[el.dataset.live]:el.checked});aviso('Aplicado ✓ (pode salvar abaixo outras mudanças)')}
  catch(e){el.checked=!el.checked;aviso(e.message,false)}}));
$('#dia').addEventListener('change',e=>{location.search='?dia='+e.target.value+'#painel'});
$('#salvar').addEventListener('click',async()=>{
  const d={};
  $$('[data-k]').forEach(el=>{const t=el.dataset.t;
    d[el.dataset.k]=t==='bool'?el.checked:t==='int'?Number(el.value):t==='lines'?el.value.split('\\n'):el.value});
  try{await api('/api/config',d);aviso('Configurações salvas ✓')}catch(e){aviso(e.message,false)}});
"""


def campo(caminho, rotulo, cfg, dica=""):
    tipo = SCHEMA[caminho]
    v = obter(cfg, caminho)
    d = f'<small>{escape(dica)}</small>' if dica else ""
    k = f'data-k="{caminho}"'
    if tipo == "bool":
        entrada, t = f'<input type="checkbox" {k} data-t="bool" {"checked" if v else ""}>', "bool"
    elif tipo == "lines":
        return (f'<div class="blk"><b>{escape(rotulo)}</b>{d}'
                f'<textarea {k} data-t="lines">{escape(chr(10).join(v or []))}</textarea></div>')
    elif tipo == "time":
        entrada = f'<input type="time" {k} data-t="time" value="{escape(str(v or ""))}">'
    elif tipo in ("text", "url"):
        return (f'<div class="blk"><b>{escape(rotulo)}</b>{d}'
                f'<input type="text" {k} data-t="text" value="{escape(str(v or ""), quote=True)}"></div>')
    else:
        _, lo, hi = tipo
        entrada = f'<input type="number" {k} data-t="int" min="{lo}" max="{hi}" value="{int(v or lo)}">'
    return f'<label class="l"><span>{escape(rotulo)}{d}</span>{entrada}</label>'


def live(chave, rotulo, marcado, dica=""):
    d = f'<small>{escape(dica)}</small>' if dica else ""
    return (f'<label class="l"><span>{escape(rotulo)}{d}</span>'
            f'<input type="checkbox" data-live="{chave}" {"checked" if marcado else ""}></label>')


def pagina(srv, dia):
    cfg, h = ler_config(), srv.hooks
    st = h["estado"]
    c = lambda *a, **k: campo(*a, cfg=cfg, **k)  # noqa: E731
    geral = f"""
<div class="card cfg"><h2>Controles rápidos <span class="mut" style="font-weight:400">(aplicam na hora)</span></h2>
{live("pausado", "Pausar todas as notificações", st["pausado"], "Nada aparece nem toca; fica só no histórico.")}
{live("foco", "Modo foco", st["foco"], "Só mensagens prioritárias e chamadas aparecem.")}
{live("inicio", "Iniciar com o Windows", h["inicio_get"]())}</div>
<div class="card cfg"><h2>Som e voz</h2>
{c("som.ativo", "Tocar beep")}
{c("voz.ativo", "Falar o remetente", dica="Ex.: “Mensagem de Maria”.")}
{c("voz.falar_texto", "Falar também o texto da mensagem", dica="Cuidado com conversas em voz alta perto de outras pessoas.")}
{c("repetir_beep_segundos", "Repetir o beep a cada (segundos)", dica="Enquanto o popup estiver aberto.")}
{c("soneca_minutos", "Duração da soneca (minutos)")}</div>
<div class="card cfg"><h2>Logs e histórico</h2>
{c("logs.historico", "Salvar histórico de notificações", dica="Arquivo historico.csv. É ele que alimenta o painel; desligado, o painel para de receber dados novos.")}
{c("logs.salvar_texto", "Salvar o texto das mensagens no histórico", dica="Desligado: guarda só remetente, hora e tipo, sem o conteúdo.")}
{c("logs.erros", "Salvar log de erros", dica="Arquivo teams_notify.log, usado para diagnosticar problemas.")}</div>
<div class="card cfg"><h2>Agrupar mensagens da mesma pessoa</h2>
{c("agrupar_minutos", "Intervalo (minutos)", dica="Várias mensagens da mesma pessoa dentro do intervalo geram um só popup. 0 desliga. Prioritárias e chamadas sempre aparecem.")}</div>
<div class="card cfg"><h2>Mensagem sem resposta</h2>
{c("lembrete.ativo", "Lembrar de mensagens não resolvidas")}
{c("lembrete.minutos", "Primeiro lembrete após (minutos)")}
{c("lembrete.repetir_minutos", "Repetir a cada (minutos)")}
{c("lembrete.maximo", "Máximo de lembretes por mensagem")}</div>
<div class="card cfg"><h2>Horário de silêncio</h2>
{c("horario_silencio.ativo", "Ativar horário de silêncio")}
{c("horario_silencio.inicio", "Início")}
{c("horario_silencio.fim", "Fim")}</div>
<div class="card cfg"><h2>Filtros</h2>
{c("palavras_prioritarias", "Palavras prioritárias", dica="Uma por linha. Popup vermelho e som mais forte.")}
{c("remetentes_prioritarios", "Remetentes prioritários", dica="Uma por linha (parte do nome basta).")}
{c("remetentes_silenciados", "Remetentes silenciados", dica="Uma por linha. Só vão para o histórico.")}
{c("palavras_chamada", "Textos que indicam chamada/reunião", dica="Uma por linha. Ajuste após a primeira ligação real.")}
{c("botoes_chamada", "Botões que indicam chamada/reunião", dica="Se o banner tiver um botão com esse texto (ex.: Aceitar), é tratado como chamada.")}</div>
<div class="card cfg"><h2>Aviso no celular (ntfy)</h2>
<p class="aviso">Usa um serviço externo. Confira se a política da empresa permite. Só envia quando você está parado no PC.</p>
{c("celular.ativo", "Enviar para o celular")}
{c("celular.servidor", "Servidor", dica="Ex.: https://ntfy.sh ou um servidor próprio.")}
{c("celular.topico", "Tópico", dica="Use um nome longo e aleatório, como uma senha.")}
{c("celular.ausente_minutos", "Considerar ausente após (minutos parado)")}
{c("celular.enviar_texto", "Incluir o texto da mensagem", dica="Desligado: envia só “Nova mensagem no Teams”.")}</div>"""
    return f"""<!doctype html><html lang="pt-BR"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Teams Notify</title>
<style>{CSS}</style></head><body><main>
<h1>Teams Notify</h1><div class="mut">Painel e configurações · só acessível neste computador</div>
<div class="abas"><button data-a="painel">Painel</button><button data-a="cfg">Configurações</button></div>
<section class="aba" id="painel"><label>Dia: <input type="date" id="dia" value="{escape(dia)}"></label>
{'' if obter(cfg, "logs.historico", True) else '<p class="aviso">O histórico está desligado em Configurações; este painel não recebe dados novos.</p>'}
{bloco_painel(dia)}</section>
<section class="aba" id="cfg">{geral}</section></main>
<div class="salvar"><button id="salvar">Salvar configurações</button><span id="msg"></span></div>
<script>{JS.replace("__TOKEN__", srv.token)}</script></body></html>"""


# ---------------------------------------------------------------- servidor
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def _host_ok(self):
        return self.headers.get("Host") in (f"127.0.0.1:{self.server.server_port}",
                                             f"localhost:{self.server.server_port}")

    def _resp(self, codigo, corpo, tipo="application/json"):
        dados = corpo.encode("utf-8")
        self.send_response(codigo)
        self.send_header("Content-Type", tipo + "; charset=utf-8")
        self.send_header("Content-Length", str(len(dados)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; "
                         "script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; form-action 'none'")
        self.end_headers()
        self.wfile.write(dados)

    def _erro(self, codigo, msg):
        self._resp(codigo, json.dumps({"erro": msg}))

    def do_GET(self):
        if not self._host_ok():
            return self._erro(403, "host não permitido")
        url = urlparse(self.path)
        if url.path != "/":
            return self._erro(404, "não encontrado")
        dia = parse_qs(url.query).get("dia", [date.today().isoformat()])[0]
        try:
            date.fromisoformat(dia)
        except ValueError:
            dia = date.today().isoformat()
        self._resp(200, pagina(self.server, dia), "text/html")

    def do_POST(self):
        if not self._host_ok():
            return self._erro(403, "host não permitido")
        if not hmac.compare_digest(self.headers.get("X-Token", ""), self.server.token):
            return self._erro(403, "token inválido")
        if not self.headers.get("Content-Type", "").startswith("application/json"):
            return self._erro(415, "use JSON")
        tamanho = int(self.headers.get("Content-Length") or 0)
        if tamanho > 65536:
            return self._erro(413, "corpo grande demais")
        try:
            dados = json.loads(self.rfile.read(tamanho) or b"{}")
            h = self.server.hooks
            if self.path == "/api/config":
                salvar_config(validar(dados))
            elif self.path == "/api/estado":
                for chave in ("pausado", "foco"):
                    if chave in dados:
                        if not isinstance(dados[chave], bool):
                            raise ValueError(f"valor inválido em {chave}")
                        h["estado"][chave] = dados[chave]
                h["mudou"]()
            elif self.path == "/api/inicio":
                if not isinstance(dados.get("ativo"), bool):
                    raise ValueError("valor inválido")
                h["inicio_set"](dados["ativo"])
            else:
                return self._erro(404, "não encontrado")
        except (ValueError, json.JSONDecodeError) as e:
            return self._erro(400, str(e))
        except Exception as e:  # noqa: BLE001
            return self._erro(500, f"falha ao aplicar: {e}")
        self._resp(200, json.dumps({"ok": True}))


_srv = None


def iniciar_servidor(hooks):
    """hooks: estado (dict), mudou(), inicio_get(), inicio_set(bool)."""
    global _srv
    for porta in (8765, 0):
        try:
            _srv = ThreadingHTTPServer(("127.0.0.1", porta), Handler)
            break
        except OSError:
            continue
    _srv.hooks = hooks
    _srv.token = secrets.token_urlsafe(24)
    threading.Thread(target=_srv.serve_forever, daemon=True).start()


def abrir(aba=""):
    webbrowser.open(f"http://127.0.0.1:{_srv.server_port}/{aba}")

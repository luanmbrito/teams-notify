using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TeamsNotify;

/// <summary>
/// Painel local (só 127.0.0.1): estatísticas do dia + configurações.
/// Protegido contra outros sites: confere o Host (DNS rebinding), exige JSON + token aleatório
/// por execução nas alterações e usa CSP restritiva.
/// </summary>
static class PanelServer
{
    static HttpListener? _http;
    static string _token = "";
    static int _porta;

    /// <summary>caminho no config.json -> tipo: bool | lines | time | url | text | int:min:max</summary>
    static readonly Dictionary<string, string> Esquema = new()
    {
        ["som.ativo"] = "bool", ["voz.ativo"] = "bool", ["voz.falar_texto"] = "bool",
        ["logs.historico"] = "bool", ["logs.salvar_texto"] = "bool", ["logs.erros"] = "bool",
        ["agrupar_minutos"] = "int:0:240", ["repetir_beep_segundos"] = "int:5:600", ["soneca_minutos"] = "int:1:240",
        ["horario_silencio.ativo"] = "bool", ["horario_silencio.inicio"] = "time", ["horario_silencio.fim"] = "time",
        ["lembrete.ativo"] = "bool", ["lembrete.minutos"] = "int:1:240",
        ["lembrete.repetir_minutos"] = "int:1:240", ["lembrete.maximo"] = "int:1:20",
        ["palavras_prioritarias"] = "lines", ["palavras_chamada"] = "lines", ["botoes_chamada"] = "lines",
        ["remetentes_prioritarios"] = "lines", ["remetentes_silenciados"] = "lines",
        ["celular.ativo"] = "bool", ["celular.servidor"] = "url", ["celular.topico"] = "text",
        ["celular.ausente_minutos"] = "int:1:240", ["celular.enviar_texto"] = "bool",
    };

    public static int Porta => _porta;

    public static void Iniciar()
    {
        _token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
        foreach (var porta in new[] { 8766, PortaLivre() })
        {
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{porta}/");
                l.Start();
                _http = l;
                _porta = porta;
                break;
            }
            catch (Exception e) { Logger.Warn($"Porta {porta} indisponível: {e.Message}"); }
        }
        if (_http == null) { Logger.Error("Painel não pôde iniciar"); return; }
        _ = Task.Run(Atender);
    }

    public static void Parar() { try { _http?.Close(); } catch { } }

    public static void Abrir(string aba = "")
    {
        if (_porta == 0) return;
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{_porta}/{aba}") { UseShellExecute = true });
    }

    static int PortaLivre()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static async Task Atender()
    {
        while (_http is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Tratar(ctx));
        }
    }

    // ------------------------------------------------------------ requisições
    static void Tratar(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            string? host = req.Headers["Host"];
            if (host != $"127.0.0.1:{_porta}" && host != $"localhost:{_porta}") { Erro(ctx, 403, "host não permitido"); return; }

            string caminho = req.Url!.AbsolutePath;
            if (req.HttpMethod == "GET" && caminho == "/")
            {
                string html = Recurso("painel.html").Replace("__TOKEN__", _token);
                Responder(ctx, 200, html, "text/html");
            }
            else if (req.HttpMethod == "GET" && caminho == "/api/dados")
            {
                if (!TokenOk(req)) { Erro(ctx, 403, "token inválido"); return; }
                Responder(ctx, 200, Dados(req.QueryString["dia"]).ToJsonString(), "application/json");
            }
            else if (req.HttpMethod == "POST") Postar(ctx, caminho);
            else Erro(ctx, 404, "não encontrado");
        }
        catch (Exception e)
        {
            Logger.Error("Erro no painel", e);
            try { Erro(ctx, 500, "falha interna"); } catch { }
        }
    }

    static bool TokenOk(HttpListenerRequest req)
    {
        var enviado = Encoding.UTF8.GetBytes(req.Headers["X-Token"] ?? "");
        return CryptographicOperations.FixedTimeEquals(enviado, Encoding.UTF8.GetBytes(_token));
    }

    static void Postar(HttpListenerContext ctx, string caminho)
    {
        var req = ctx.Request;
        if (!TokenOk(req)) { Erro(ctx, 403, "token inválido"); return; }
        if (!(req.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) { Erro(ctx, 415, "use JSON"); return; }
        if (req.ContentLength64 > 65536) { Erro(ctx, 413, "corpo grande demais"); return; }

        try
        {
            using var doc = JsonDocument.Parse(new StreamReader(req.InputStream, Encoding.UTF8).ReadToEnd());
            var corpo = doc.RootElement;
            if (corpo.ValueKind != JsonValueKind.Object) throw new ArgumentException("formato inválido");
            switch (caminho)
            {
                case "/api/config":
                    Config.Salvar(Validar(corpo));
                    break;
                case "/api/estado":
                    foreach (var chave in new[] { "pausado", "foco" })
                    {
                        if (!corpo.TryGetProperty(chave, out var v)) continue;
                        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException($"valor inválido em {chave}");
                        if (chave == "pausado") Hub.Pausado = v.GetBoolean(); else Hub.Foco = v.GetBoolean();
                    }
                    Hub.NotificarEstado();
                    break;
                case "/api/inicio":
                    if (!corpo.TryGetProperty("ativo", out var a) || a.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new ArgumentException("valor inválido");
                    Autostart.Definir(a.GetBoolean());
                    break;
                default:
                    Erro(ctx, 404, "não encontrado");
                    return;
            }
        }
        catch (Exception e) when (e is ArgumentException or JsonException)
        {
            Erro(ctx, 400, e.Message);
            return;
        }
        catch (Exception e)
        {
            Erro(ctx, 500, "falha ao aplicar: " + e.Message);
            return;
        }
        Responder(ctx, 200, "{\"ok\":true}", "application/json");
    }

    static Dictionary<string, JsonNode?> Validar(JsonElement corpo)
    {
        var saida = new Dictionary<string, JsonNode?>();
        foreach (var p in corpo.EnumerateObject())
        {
            if (!Esquema.TryGetValue(p.Name, out var tipo)) throw new ArgumentException($"campo desconhecido: {p.Name}");
            var v = p.Value;
            string invalido = $"valor inválido em {p.Name}";
            JsonNode? valor;
            if (tipo == "bool")
            {
                if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException(invalido);
                valor = JsonValue.Create(v.GetBoolean());
            }
            else if (tipo == "lines")
            {
                if (v.ValueKind != JsonValueKind.Array) throw new ArgumentException(invalido);
                var itens = new List<string>();
                foreach (var x in v.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.String || x.GetString()!.Length > 200) throw new ArgumentException(invalido);
                    var s = x.GetString()!.Trim();
                    if (s != "") itens.Add(s);
                }
                var arr = new JsonArray();
                foreach (var s in itens.Take(200)) arr.Add(s);
                valor = arr;
            }
            else if (tipo == "time")
            {
                if (v.ValueKind != JsonValueKind.String ||
                    !DateTime.TryParseExact(v.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw new ArgumentException(invalido);
                valor = JsonValue.Create(v.GetString());
            }
            else if (tipo == "url")
            {
                var s = v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                if (s.Length > 200 || !(s.StartsWith("https://") || s.StartsWith("http://"))) throw new ArgumentException(invalido);
                valor = JsonValue.Create(s);
            }
            else if (tipo == "text")
            {
                if (v.ValueKind != JsonValueKind.String || v.GetString()!.Length > 100) throw new ArgumentException(invalido);
                valor = JsonValue.Create(v.GetString()!.Trim());
            }
            else // int:min:max
            {
                var partes = tipo.Split(':');
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n) ||
                    n < int.Parse(partes[1]) || n > int.Parse(partes[2])) throw new ArgumentException(invalido);
                valor = JsonValue.Create(n);
            }
            saida[p.Name] = valor;
        }
        return saida;
    }

    // ------------------------------------------------------------ dados do painel
    static JsonObject Dados(string? diaTexto)
    {
        string dia = DateTime.TryParseExact(diaTexto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? diaTexto! : DateTime.Today.ToString("yyyy-MM-dd");
        var linhas = LerHistorico(dia);

        var porRemetente = new JsonArray();
        foreach (var g in linhas.GroupBy(l => l[2]).OrderByDescending(g => g.Count()).Take(10))
            porRemetente.Add(new JsonArray(g.Key, g.Count()));
        var porHora = new int[24];
        foreach (var l in linhas)
            if (int.TryParse(l[0].AsSpan(11, 2), out int h) && h is >= 0 and < 24) porHora[h]++;
        var recentes = new JsonArray();
        foreach (var l in Enumerable.Reverse(linhas.TakeLast(40)))
            recentes.Add(new JsonObject
            {
                ["hora"] = l[0][11..16], ["remetente"] = l[2], ["mensagem"] = l[3].Length > 120 ? l[3][..120] : l[3],
                ["tipo"] = l[4], ["motivo"] = l[6],
            });

        return new JsonObject
        {
            ["dia"] = dia,
            ["config"] = JsonNode.Parse(Config.Carregar().ToJsonString()),
            ["estado"] = new JsonObject { ["pausado"] = Hub.Pausado, ["foco"] = Hub.Foco, ["inicio"] = Autostart.Ativo },
            ["stats"] = new JsonObject
            {
                ["total"] = linhas.Count,
                ["prioritarias"] = linhas.Count(l => l[5] == "sim"),
                ["chamadas"] = linhas.Count(l => l[4] == "chamada"),
                ["ocultadas"] = linhas.Count(l => l[6] != ""),
                ["porRemetente"] = porRemetente,
                ["porHora"] = new JsonArray(porHora.Select(n => (JsonNode?)n).ToArray()),
                ["recentes"] = recentes,
            },
        };
    }

    /// <summary>Linhas do dia, sempre com 7 colunas: data_hora;app;remetente;mensagem;tipo;prioridade;motivo.</summary>
    static List<List<string>> LerHistorico(string dia)
    {
        string arquivo = Path.Combine(Config.Dir, "historico.csv");
        if (!File.Exists(arquivo)) return new();
        string texto;
        using (var fs = new FileStream(arquivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs, Encoding.UTF8)) texto = sr.ReadToEnd();
        return Csv.Ler(texto).Skip(1)
            .Where(l => l.Count >= 7 && l[0].Length >= 16 && l[0].StartsWith(dia)).ToList();
    }

    // ------------------------------------------------------------ resposta
    static string Recurso(string nome)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(nome)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    static void Erro(HttpListenerContext ctx, int codigo, string mensagem) =>
        Responder(ctx, codigo, JsonSerializer.Serialize(new { erro = mensagem }), "application/json");

    static void Responder(HttpListenerContext ctx, int codigo, string corpo, string tipo)
    {
        var bytes = Encoding.UTF8.GetBytes(corpo);
        var r = ctx.Response;
        r.StatusCode = codigo;
        r.ContentType = tipo + "; charset=utf-8";
        r.ContentLength64 = bytes.Length;
        r.Headers["Cache-Control"] = "no-store";
        r.Headers["X-Content-Type-Options"] = "nosniff";
        r.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; form-action 'none'";
        r.OutputStream.Write(bytes, 0, bytes.Length);
        r.OutputStream.Close();
    }
}

/// <summary>Início com o Windows (chave Run do usuário atual).</summary>
static class Autostart
{
    const string Chave = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Nome = "TeamsNotifyCSharp";

    public static bool Ativo
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Chave);
            return k?.GetValue(Nome) != null;
        }
    }

    public static void Definir(bool ativo)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Chave);
        if (ativo) k.SetValue(Nome, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Nome, throwOnMissingValue: false);
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TeamsNotify;

/// <summary>Uma notificação a mostrar. Tipo: msg | lembrete | chamada | erro.</summary>
sealed record Item(string Tipo, string? Id, bool Prio, string Remetente, string Corpo, int Minutos = 0);

/// <summary>Evento para a interface. Tipo: item | resolvida | sair.</summary>
sealed record UiEvento(string Tipo, Item? Item = null, string? Id = null);

sealed class Ativa
{
    public required Item Item { get; init; }
    public DateTime Criada { get; init; } = DateTime.Now;
    public DateTime Ultimo { get; set; } = DateTime.Now;
    public int Avisos { get; set; }
}

/// <summary>Estado compartilhado e regras de negócio (classificar, agrupar, lembrar, registrar).</summary>
static class Hub
{
    public static volatile bool Pausado;
    public static volatile bool Foco;
    public static event Action? EstadoMudou;

    public static readonly ConcurrentQueue<UiEvento> Fila = new();
    public static readonly ConcurrentDictionary<string, Ativa> Ativas = new();
    static readonly Dictionary<string, DateTime> UltimoPopup = new();
    static readonly object TravaPopup = new();
    static readonly object TravaHistorico = new();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static void NotificarEstado() => EstadoMudou?.Invoke();

    public static void Erro(string mensagem) =>
        Fila.Enqueue(new UiEvento("item", new Item("erro", null, true, "Teams Notify", mensagem)));

    // ------------------------------------------------------------ classificação
    /// <summary>"silenciar", "chamada", "prioritaria" ou "normal".</summary>
    public static string Classificar(string remetente, string texto, IEnumerable<string>? botoes = null)
    {
        string r = remetente.ToLowerInvariant();
        string junto = r + " " + texto.ToLowerInvariant();
        if (Config.Lista("remetentes_silenciados").Any(s => r.Contains(s.ToLowerInvariant()))) return "silenciar";
        if (Config.Lista("palavras_chamada").Any(p => junto.Contains(p.ToLowerInvariant()))) return "chamada";
        if (botoes != null)
        {
            var chaves = Config.Lista("botoes_chamada");
            if (botoes.Any(b => chaves.Any(k => b.Contains(k, StringComparison.OrdinalIgnoreCase)))) return "chamada";
        }
        if (Config.Lista("remetentes_prioritarios").Any(s => r.Contains(s.ToLowerInvariant()))) return "prioritaria";
        if (Config.Lista("palavras_prioritarias").Any(p => junto.Contains(p.ToLowerInvariant()))) return "prioritaria";
        return "normal";
    }

    static bool EmHorarioSilencio()
    {
        if (!Config.Bool("horario_silencio.ativo")) return false;
        string agora = DateTime.Now.ToString("HH:mm");
        string ini = Config.Texto("horario_silencio.inicio", "18:00");
        string fim = Config.Texto("horario_silencio.fim", "07:30");
        return string.CompareOrdinal(ini, fim) > 0
            ? string.CompareOrdinal(ini, agora) <= 0 || string.CompareOrdinal(agora, fim) < 0
            : string.CompareOrdinal(ini, agora) <= 0 && string.CompareOrdinal(agora, fim) < 0;
    }

    // ------------------------------------------------------------ processamento
    /// <summary>Classifica, registra no histórico e (se permitido) enfileira o popup. Retorna o tipo mostrado.</summary>
    public static string? Processar(string app, string id, string remetente, string corpo, IEnumerable<string>? botoes = null)
    {
        string classe = Classificar(remetente, corpo, botoes);
        string tipo = classe == "chamada" ? "chamada" : "msg";
        bool prio = classe is "prioritaria" or "chamada";

        string motivo = "";
        if (classe == "silenciar") motivo = "silenciado";
        else if (Pausado) motivo = "pausado";
        else if (EmHorarioSilencio()) motivo = "horário de silêncio";
        else if (Foco && classe == "normal") motivo = "modo foco";

        int janela = Config.Int("agrupar_minutos", 0) * 60;
        string chave = remetente.Trim().ToLowerInvariant();
        if (motivo == "" && janela > 0 && !prio)
        {
            lock (TravaPopup)
                if (UltimoPopup.TryGetValue(chave, out var quando) && (DateTime.Now - quando).TotalSeconds < janela)
                    motivo = "agrupada"; // mesma pessoa dentro do intervalo: só o primeiro popup aparece
        }

        Registrar(app, remetente, corpo, tipo, prio, motivo);
        if (motivo != "") return null;

        lock (TravaPopup) UltimoPopup[chave] = DateTime.Now;
        var item = new Item(tipo, id, prio, remetente, corpo);
        Fila.Enqueue(new UiEvento("item", item));
        Ativas[id] = new Ativa { Item = item };
        _ = Task.Run(() => EnviarCelular(remetente, corpo));
        return tipo;
    }

    /// <summary>Mensagens ainda sem resposta: reavisa depois de X minutos.</summary>
    public static void Lembretes()
    {
        if (!Config.Bool("lembrete.ativo", true) || Pausado || Foco) return;
        int primeiro = Config.Int("lembrete.minutos", 10);
        int repetir = Config.Int("lembrete.repetir_minutos", 10);
        int maximo = Config.Int("lembrete.maximo", 3);
        var agora = DateTime.Now;
        foreach (var a in Ativas.Values)
        {
            if (a.Item.Tipo == "chamada" || a.Avisos >= maximo) continue;
            double espera = (a.Avisos == 0 ? primeiro : repetir) * 60;
            if ((agora - a.Ultimo).TotalSeconds < espera) continue;
            a.Avisos++;
            a.Ultimo = agora;
            Fila.Enqueue(new UiEvento("item", a.Item with { Tipo = "lembrete", Minutos = (int)(agora - a.Criada).TotalMinutes }));
        }
    }

    public static void Resolver(string id)
    {
        if (Ativas.TryRemove(id, out _)) Fila.Enqueue(new UiEvento("resolvida", Id: id));
    }

    public static void ResolverTodasDoBanner()
    {
        foreach (var id in Ativas.Keys.Where(k => k.StartsWith('b')).ToList()) Resolver(id);
    }

    // ------------------------------------------------------------ histórico
    static readonly string Historico = Path.Combine(Config.Dir, "historico.csv");

    public static void Registrar(string app, string remetente, string texto, string tipo, bool prio, string motivo)
    {
        try
        {
            if (!Config.Bool("logs.historico", true)) return;
            if (!Config.Bool("logs.salvar_texto", true)) texto = "";
            lock (TravaHistorico)
            {
                bool novo = !File.Exists(Historico);
                var linha = string.Join(';', new[]
                {
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), app, remetente, texto, tipo, prio ? "sim" : "nao", motivo,
                }.Select(Csv.Escapar));
                var sb = new StringBuilder();
                if (novo) sb.AppendLine("data_hora;app;remetente;mensagem;tipo;prioridade;motivo");
                sb.AppendLine(linha);
                File.AppendAllText(Historico, sb.ToString(), new UTF8Encoding(novo)); // BOM só na criação (compatível com o Excel)
            }
        }
        catch (Exception e) { Logger.Error("Falha ao gravar o histórico", e); }
    }

    // ------------------------------------------------------------ celular (ntfy)
    static async Task EnviarCelular(string remetente, string corpo)
    {
        try
        {
            if (!Config.Bool("celular.ativo")) return;
            string topico = Config.Texto("celular.topico");
            if (topico == "") return;
            if (Native.SegundosOcioso() < Config.Int("celular.ausente_minutos", 5) * 60) return;
            var dados = new
            {
                topic = topico,
                title = $"Teams: {remetente}",
                priority = 4,
                message = Config.Bool("celular.enviar_texto") && corpo != "" ? corpo : "Nova mensagem no Teams",
            };
            string url = Config.Texto("celular.servidor", "https://ntfy.sh").TrimEnd('/');
            using var conteudo = new StringContent(JsonSerializer.Serialize(dados), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, conteudo);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception e) { Logger.Warn("Falha ao enviar para o celular: " + e.Message); }
    }
}

/// <summary>CSV com separador ';' (leitura e escrita), compatível com o csv do Python.</summary>
static class Csv
{
    public static string Escapar(string campo) =>
        campo.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + campo.Replace("\"", "\"\"") + "\"" : campo;

    public static List<List<string>> Ler(string texto)
    {
        var linhas = new List<List<string>>();
        var linha = new List<string>();
        var campo = new StringBuilder();
        bool aspas = false;
        for (int i = 0; i < texto.Length; i++)
        {
            char c = texto[i];
            if (aspas)
            {
                if (c == '"')
                {
                    if (i + 1 < texto.Length && texto[i + 1] == '"') { campo.Append('"'); i++; }
                    else aspas = false;
                }
                else campo.Append(c);
            }
            else if (c == '"') aspas = true;
            else if (c == ';') { linha.Add(campo.ToString()); campo.Clear(); }
            else if (c == '\n') { linha.Add(campo.ToString()); campo.Clear(); linhas.Add(linha); linha = new(); }
            else if (c != '\r') campo.Append(c);
        }
        if (campo.Length > 0 || linha.Count > 0) { linha.Add(campo.ToString()); linhas.Add(linha); }
        return linhas;
    }
}

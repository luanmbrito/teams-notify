using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeamsNotify;

/// <summary>
/// config.json (mesmo formato da versão em Python). Relido só quando o arquivo muda.
/// Chaves desconhecidas são preservadas ao salvar.
/// </summary>
static class Config
{
    static readonly object Trava = new();
    static JsonNode? _cache;
    static DateTime _mtime;

    public static string Dir => AppContext.BaseDirectory;
    public static string Caminho => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions Escrita = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // mantém acentos legíveis
    };

    public static JsonNode Carregar()
    {
        lock (Trava)
        {
            try
            {
                if (!File.Exists(Caminho)) CriarPadrao();
                var mt = File.GetLastWriteTimeUtc(Caminho);
                if (_cache == null || mt != _mtime)
                {
                    _cache = JsonNode.Parse(File.ReadAllText(Caminho, Encoding.UTF8))
                             ?? throw new InvalidDataException("config.json vazio");
                    _mtime = mt;
                }
            }
            catch (Exception) when (_cache != null) { /* arquivo no meio de uma gravação: mantém o anterior */ }
            return _cache ?? new JsonObject();
        }
    }

    static void CriarPadrao()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("config.padrao.json")!;
        using var f = File.Create(Caminho);
        s.CopyTo(f);
    }

    public static JsonNode? Obter(string caminho)
    {
        JsonNode? n = Carregar();
        foreach (var parte in caminho.Split('.'))
        {
            if (n is JsonObject o && o.TryGetPropertyValue(parte, out var v)) n = v;
            else return null;
        }
        return n;
    }

    public static bool Bool(string caminho, bool padrao = false)
    {
        try { return Obter(caminho)?.GetValue<bool>() ?? padrao; } catch { return padrao; }
    }

    public static int Int(string caminho, int padrao = 0)
    {
        try { return Obter(caminho)?.GetValue<int>() ?? padrao; } catch { return padrao; }
    }

    public static string Texto(string caminho, string padrao = "")
    {
        try { return Obter(caminho)?.GetValue<string>() ?? padrao; } catch { return padrao; }
    }

    public static string[] Lista(string caminho)
    {
        if (Obter(caminho) is not JsonArray a) return Array.Empty<string>();
        return a.Select(x => { try { return x?.GetValue<string>() ?? ""; } catch { return ""; } })
                .Where(s => s.Length > 0).ToArray();
    }

    /// <summary>Grava os valores (caminho com pontos, ex.: "voz.ativo") e mantém o resto do arquivo.</summary>
    public static void Salvar(System.Collections.Generic.Dictionary<string, JsonNode?> novos)
    {
        lock (Trava)
        {
            if (!File.Exists(Caminho)) CriarPadrao();
            var raiz = JsonNode.Parse(File.ReadAllText(Caminho, Encoding.UTF8))!.AsObject();
            foreach (var (caminho, valor) in novos) Definir(raiz, caminho, valor);
            var tmp = Caminho + ".tmp";
            File.WriteAllText(tmp, raiz.ToJsonString(Escrita), new UTF8Encoding(false));
            File.Move(tmp, Caminho, overwrite: true);
            _cache = null;
        }
    }

    static void Definir(JsonObject raiz, string caminho, JsonNode? valor)
    {
        var partes = caminho.Split('.');
        var atual = raiz;
        for (int i = 0; i < partes.Length - 1; i++)
        {
            if (atual[partes[i]] is JsonObject o) atual = o;
            else { var novo = new JsonObject(); atual[partes[i]] = novo; atual = novo; }
        }
        atual[partes[^1]] = valor;
    }
}

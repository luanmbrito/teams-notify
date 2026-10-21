using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Speech.Synthesis;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TeamsNotify;

/// <summary>Fila de popups (thread de interface): mostra um por vez, com som, voz, soneca e prioridade para chamadas.</summary>
sealed class PopupManager
{
    readonly List<Item> _pendentes = new();
    readonly Timer _tick = new() { Interval = 300 };
    readonly Timer _repetir = new();
    readonly Action _sair;
    PopupForm? _janela;
    Item? _atual;
    DateTime _pausadoAte = DateTime.MinValue;

    public PopupManager(Action sair)
    {
        _sair = sair;
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        _repetir.Tick += (_, _) => { if (_atual != null) Tocar(_atual, voz: false); };
    }

    void Tick()
    {
        try
        {
            while (Hub.Fila.TryDequeue(out var ev))
            {
                switch (ev.Tipo)
                {
                    case "sair": _sair(); return;
                    case "resolvida": Resolver(ev.Id!); break;
                    case "item":
                        if (ev.Item!.Tipo == "chamada")
                        {
                            _pendentes.Insert(0, ev.Item);
                            if (_janela != null && _atual!.Tipo != "chamada") Destruir(); // chamada tem prioridade
                        }
                        else _pendentes.Add(ev.Item);
                        break;
                }
            }

            bool livre = _pendentes.Count > 0 && _janela == null;
            if (livre && (DateTime.Now >= _pausadoAte || _pendentes[0].Tipo == "chamada")) Mostrar(_pendentes[0]);
            else if (_janela != null) _janela.DefinirFila(_pendentes.Count - 1);
        }
        catch (Exception e)
        {
            Logger.Error("Erro no ciclo da interface", e);
            if (_atual != null) _pendentes.Remove(_atual); // descarta o popup que falhou para não travar a fila
            Destruir();
        }
    }

    void Resolver(string id)
    {
        _pendentes.RemoveAll(p => p.Id == id);
        if (_janela != null && _atual?.Id == id) Destruir();
    }

    void Mostrar(Item item)
    {
        _atual = item;
        var (cor, titulo) = item.Tipo switch
        {
            "lembrete" => ("#6A1B9A", $"⏰ Sem resposta há {item.Minutos} min"),
            "chamada" => ("#1565C0", "📞 CHAMADA / REUNIÃO"),
            "erro" => ("#C62828", "⚠ Teams Notify"),
            _ => item.Prio ? ("#C62828", "🔴 PRIORITÁRIA") : ("#E65100", "💬 Nova mensagem no Teams"),
        };
        _janela = new PopupForm(item, ColorTranslator.FromHtml(cor), titulo,
            Config.Int("largura", 560), Config.Int("altura", 260), Config.Int("soneca_minutos", 10),
            abrir: Abrir, fechar: Fechar, soneca: Soneca);
        _janela.DefinirFila(_pendentes.Count - 1);
        _janela.Show(); // ShowWithoutActivation: não rouba o foco

        _repetir.Interval = item.Tipo == "chamada" ? 3000 : Math.Max(5, Config.Int("repetir_beep_segundos", 20)) * 1000;
        _repetir.Start();
        Tocar(item, voz: true);
    }

    void Destruir()
    {
        _repetir.Stop();
        if (_janela != null) { _janela.Close(); _janela.Dispose(); _janela = null; }
    }

    // "vi": some, mas o lembrete continua se a mensagem não for resolvida
    void Fechar()
    {
        if (_atual != null) _pendentes.Remove(_atual);
        Destruir();
    }

    // abriu o Teams = considera tudo resolvido
    void Abrir()
    {
        try { Process.Start(new ProcessStartInfo("msteams:") { UseShellExecute = true }); }
        catch (Exception e) { Logger.Warn("Não foi possível abrir o Teams: " + e.Message); }
        _pendentes.Clear();
        Hub.Ativas.Clear();
        Destruir();
    }

    void Soneca()
    {
        _pausadoAte = DateTime.Now.AddMinutes(Config.Int("soneca_minutos", 10));
        Destruir(); // a mensagem continua na fila e volta depois
    }

    // ------------------------------------------------------------ som e voz
    static void Tocar(Item item, bool voz) => _ = Task.Run(() => Som(item, voz));

    static void Som(Item item, bool voz)
    {
        try
        {
            if (Config.Bool("som.ativo", true))
            {
                if (item.Tipo == "chamada") foreach (var f in new[] { 880, 1100, 880, 1100 }) Native.Bip(f, 230);
                else if (item.Prio) for (int i = 0; i < 3; i++) { Native.Bip(1200, 180); Native.Bip(900, 180); }
                else { Native.Bip(1000, 200); Native.Bip(1300, 250); }
            }
            if (voz && Config.Bool("voz.ativo", true)) Falar(Frase(item));
        }
        catch (Exception e) { Logger.Warn("Falha no som: " + e.Message); }
    }

    static string Frase(Item item)
    {
        string de = item.Remetente;
        string frase = item.Tipo switch
        {
            "chamada" => $"Chamada ou reunião do Teams. {de}",
            "lembrete" => $"Sem resposta há {item.Minutos} minutos. {de}",
            _ => $"{(item.Prio ? "Mensagem prioritária" : "Mensagem")} de {de}",
        };
        if (Config.Bool("voz.falar_texto") && item.Tipo == "msg")
            frase += ". " + (item.Corpo.Length > 200 ? item.Corpo[..200] : item.Corpo);
        return frase;
    }

    static void Falar(string texto)
    {
        try
        {
            using var s = new SpeechSynthesizer();
            try { s.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet, 0, new CultureInfo("pt-BR")); } catch { /* usa a voz padrão */ }
            s.Speak(texto);
        }
        catch (Exception e) { Logger.Warn("Falha na voz: " + e.Message); }
    }
}

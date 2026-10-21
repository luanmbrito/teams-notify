using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace TeamsNotify;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--autoteste")) { AutoTeste(); return; }
        if (args.Length == 2 && args[0] == "--ler-janela") { LerJanela(args[1]); return; }

        using var mutex = new Mutex(true, "TeamsNotifyCSharp_instancia_unica", out bool primeira);
        if (!primeira) return; // já está rodando

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Logger.Error("Erro na interface", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Error("Erro não tratado", e.ExceptionObject as Exception);
        Application.Run(new TrayApp(args.Contains("--teste")));
    }

    /// <summary>Diagnóstico: lê uma janela pelo mesmo caminho do banner e grava só contagens e tempo (nunca o conteúdo).</summary>
    static void LerJanela(string hwndTexto)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string resultado;
        try
        {
            var raiz = System.Windows.Automation.AutomationElement.FromHandle(new IntPtr(long.Parse(hwndTexto)));
            var cache = new System.Windows.Automation.CacheRequest { TreeScope = System.Windows.Automation.TreeScope.Element };
            cache.Add(System.Windows.Automation.AutomationElement.NameProperty);
            cache.Add(System.Windows.Automation.AutomationElement.ControlTypeProperty);
            int total = 0, textos = 0, botoes = 0, comNome = 0;
            using (cache.Activate())
                foreach (System.Windows.Automation.AutomationElement e in
                         raiz.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
                {
                    total++;
                    if (!string.IsNullOrWhiteSpace(e.Cached.Name)) comNome++;
                    if (e.Cached.ControlType == System.Windows.Automation.ControlType.Text) textos++;
                    if (e.Cached.ControlType == System.Windows.Automation.ControlType.Button) botoes++;
                }
            resultado = $"elementos={total} comNome={comNome} textos={textos} botoes={botoes} tempo={sw.ElapsedMilliseconds}ms";
        }
        catch (Exception e) { resultado = "erro: " + e.Message; }
        File.WriteAllText(Path.Combine(Config.Dir, "ler_janela.txt"), resultado);
    }

    /// <summary>Confere as partes nativas e as regras; grava autoteste.txt ao lado do programa.</summary>
    static void AutoTeste()
    {
        var linhas = new List<string>();
        void Ok(string nome, bool ok, string extra = "") => linhas.Add($"{(ok ? "OK  " : "FALHA")} {nome} {extra}".TrimEnd());

        try { Ok("config", Config.Carregar() != null, $"({Config.Caminho})"); } catch (Exception e) { Ok("config", false, e.Message); }

        // regra de leitura do banner (textos reais observados)
        var textos = new List<string> { "mecanina simas Jonas Amaral - Fundição Batatais", "Jonas Amaral - Fundição Batatais", "mecanina simas" };
        var leitura = BannerWatcher.Interpretar(textos, new List<string> { "Responder" });
        Ok("leitura do cartão", leitura is { Remetente: "Jonas Amaral - Fundição Batatais", Corpo: "mecanina simas" });

        Ok("classifica chamada por botão", Hub.Classificar("Carlos", "oi", new[] { "Aceitar", "Recusar" }) == "chamada");
        Ok("classifica mensagem comum", Hub.Classificar("João", "bom dia") == "normal");
        Ok("classifica prioritária", Hub.Classificar("Maria", "Linha 3 parou") == "prioritaria");

        // agrupamento: 2 mensagens normais da mesma pessoa -> 1 popup; prioritária sempre aparece
        try
        {
            while (Hub.Fila.TryDequeue(out _)) { }
            int janela = Config.Int("agrupar_minutos");
            Hub.Processar("Teams", "x1", "Pessoa Agrupada", "oi");
            Hub.Processar("Teams", "x2", "pessoa agrupada ", "tem um minuto?");
            Hub.Processar("Teams", "x3", "Pessoa Agrupada", "urgente!");
            int n = Hub.Fila.Count;
            Hub.Ativas.Clear();
            while (Hub.Fila.TryDequeue(out _)) { }
            Ok("agrupamento", janela > 0 && n == 2, $"({n} popups de 3 mensagens, janela {janela} min)");
        }
        catch (Exception e) { Ok("agrupamento", false, e.Message); }

        try
        {
            var raiz = System.Windows.Automation.AutomationElement.RootElement;
            int n = raiz.FindAll(System.Windows.Automation.TreeScope.Children, System.Windows.Automation.Condition.TrueCondition).Count;
            Ok("UI Automation", n > 0, $"({n} janelas)");
        }
        catch (Exception e) { Ok("UI Automation", false, e.Message); }

        Ok("notificações do Windows", ToastListener.StatusAcesso() == "Allowed", $"({ToastListener.StatusAcesso()})");

        try
        {
            using var s = new System.Speech.Synthesis.SpeechSynthesizer();
            var pt = s.GetInstalledVoices().Select(v => v.VoiceInfo.Culture.Name).Where(c => c.StartsWith("pt")).ToList();
            Ok("voz", s.GetInstalledVoices().Count > 0, $"(pt: {(pt.Count > 0 ? string.Join(",", pt) : "nenhuma, usa a padrão")})");
        }
        catch (Exception e) { Ok("voz", false, e.Message); }

        File.WriteAllLines(Path.Combine(Config.Dir, "autoteste.txt"), linhas);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace TeamsNotify;

/// <param name="Padrao">true quando o cartão tem o formato conhecido de mensagem (leitura completa).</param>
sealed record Leitura(string Remetente, string Corpo, List<string> Botoes, bool Padrao = true);

/// <summary>
/// O Teams novo não usa notificação do Windows: mostra um banner próprio (janela "TeamsWebView"
/// reaproveitada). Detectamos quando ela fica visível e lemos o texto por UI Automation.
/// </summary>
static class BannerWatcher
{
    const string ClasseJanela = "TeamsWebView";

    sealed class Exibicao
    {
        public DateTime Inicio = DateTime.Now;
        public DateTime UltimaLeitura = DateTime.MinValue;
        public bool Emitido;
        public (string, string)? Candidata; // leitura de formato desconhecido aguardando confirmação
    }

    public static void Iniciar()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "banner-teams" };
        t.SetApartmentState(ApartmentState.MTA); // UI Automation não deve rodar em thread de interface
        t.Start();
    }

    public static bool EhBanner(string titulo, int largura) => titulo == "Microsoft Teams" && largura < 600;

    public static HashSet<IntPtr> BannersVisiveis()
    {
        var achados = new HashSet<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsWindowVisible(h))
            {
                var (classe, titulo, largura) = Native.Info(h);
                if (classe == ClasseJanela && EhBanner(titulo, largura)) achados.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return achados;
    }

    /// <summary>True se a janela em primeiro plano é o Teams (e não o banner).</summary>
    public static bool TeamsEmFoco()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        var (classe, titulo, largura) = Native.Info(h);
        if (classe != ClasseJanela || EhBanner(titulo, largura)) return false;
        return string.Equals(Native.NomeDoProcesso(h), "ms-teams", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Regra de leitura do cartão: ["corpo remetente", "remetente", "corpo"].</summary>
    public static Leitura? Interpretar(List<string> textos, List<string> botoes)
    {
        if (textos.Count >= 3 && textos[0] == $"{textos[^1]} {textos[^2]}")
            return new Leitura(textos[^2], textos[^1], botoes);
        if (textos.Count > 0) // formato desconhecido (ex.: chamada): usa o que houver
            return new Leitura(textos[0], string.Join("\n", textos.Skip(1)), botoes, Padrao: false);
        return null;
    }

    /// <summary>Lê (remetente, corpo, botões) do banner; null se o conteúdo ainda não carregou.</summary>
    public static Leitura? Ler(IntPtr hwnd)
    {
        var raiz = AutomationElement.FromHandle(hwnd);
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        var textos = new List<string>();
        var botoes = new List<string>();
        using (cache.Activate())
        {
            foreach (AutomationElement e in raiz.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                string nome = (e.Cached.Name ?? "").Trim();
                if (nome == "") continue;
                var tipo = e.Cached.ControlType;
                if (tipo == ControlType.Button) botoes.Add(nome);
                else if (tipo == ControlType.Text && nome != "Microsoft Teams") textos.Add(nome);
            }
        }
        return Interpretar(textos, botoes);
    }

    static void Loop()
    {
        var visiveis = BannersVisiveis(); // o que já estava na tela ao iniciar não conta
        var estado = new Dictionary<IntPtr, Exibicao>();
        var ultimaAssinatura = new Dictionary<IntPtr, (string, string)>();
        var chamadas = new Dictionary<IntPtr, string>();
        int contador = 0;
        var ultimoLembrete = DateTime.MinValue;

        while (true)
        {
            try
            {
                var agora = DateTime.Now;
                var atuais = BannersVisiveis();

                foreach (var h in atuais.Except(visiveis)) estado[h] = new Exibicao();
                foreach (var h in atuais)
                {
                    if (!estado.TryGetValue(h, out var e)) estado[h] = e = new Exibicao();
                    double passo = (agora - e.Inicio).TotalSeconds < 2 ? 0.25 : 0.7;
                    if ((agora - e.UltimaLeitura).TotalSeconds < passo) continue;
                    e.UltimaLeitura = agora;

                    var lido = Ler(h);
                    if (lido == null) continue;
                    var assinatura = (lido.Remetente, lido.Corpo);
                    if (!lido.Padrao)
                    {
                        // O Chromium monta a árvore de acessibilidade aos poucos: formato desconhecido só vale
                        // depois de duas leituras iguais seguidas (evita popup com texto parcial).
                        if (e.Candidata != assinatura) { e.Candidata = assinatura; continue; }
                        if (!ultimaAssinatura.TryGetValue(h, out var anteriorDesc) || anteriorDesc != assinatura)
                            Logger.Warn($"Banner fora do padrão: textos=[{lido.Remetente} | {lido.Corpo.Replace('\n', '|')}] botoes=[{string.Join(" | ", lido.Botoes)}]");
                    }
                    // conteúdo igual ao da última vez pode ser sobra da mensagem anterior:
                    // só aceita como repetição depois de 1,2 s de banner visível
                    bool mudou = !ultimaAssinatura.TryGetValue(h, out var anterior) || anterior != assinatura;
                    if (mudou || (!e.Emitido && (agora - e.Inicio).TotalSeconds > 1.2))
                    {
                        ultimaAssinatura[h] = assinatura;
                        e.Emitido = true;
                        string id = "b" + (++contador);
                        if (Hub.Processar("Microsoft Teams", id, lido.Remetente, lido.Corpo, lido.Botoes) == "chamada")
                            chamadas[h] = id;
                    }
                }

                foreach (var h in visiveis.Except(atuais)) // banner sumiu: chamada atendida/recusada/perdida
                {
                    estado.Remove(h);
                    if (chamadas.Remove(h, out var id)) Hub.Resolver(id);
                }
                visiveis = atuais;

                if (!Hub.Ativas.IsEmpty && TeamsEmFoco()) Hub.ResolverTodasDoBanner(); // você abriu o Teams: considera visto
                if ((agora - ultimoLembrete).TotalSeconds >= 1)
                {
                    ultimoLembrete = agora;
                    Hub.Lembretes();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Falha ao vigiar o banner do Teams", ex);
                Thread.Sleep(1000);
            }
            Thread.Sleep(100);
        }
    }
}

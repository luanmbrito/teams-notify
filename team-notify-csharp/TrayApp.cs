using System;
using System.Drawing;
using System.Windows.Forms;

namespace TeamsNotify;

/// <summary>Ícone na bandeja (pausar, modo foco, painel...) e ciclo de vida do programa.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _icone;
    readonly PopupManager _popups;
    readonly Control _ui;
    readonly ToolStripMenuItem _pausar, _foco, _inicio;
    IntPtr _hIcone = IntPtr.Zero;
    Timer? _timerTeste; // campo: um Timer local poderia ser coletado antes de disparar

    public TrayApp(bool modoTeste)
    {
        _ui = new Control();
        _ = _ui.Handle; // cria o handle na thread de interface (para BeginInvoke)

        PanelServer.Iniciar();
        _popups = new PopupManager(Sair);

        _pausar = new ToolStripMenuItem("Pausar notificações", null, (_, _) => { Hub.Pausado = !Hub.Pausado; Hub.NotificarEstado(); });
        _foco = new ToolStripMenuItem("Modo foco (só prioritárias e chamadas)", null, (_, _) => { Hub.Foco = !Hub.Foco; Hub.NotificarEstado(); });
        _inicio = new ToolStripMenuItem("Iniciar com o Windows", null, (_, _) => Autostart.Definir(!Autostart.Ativo));
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Painel e configurações", null, (_, _) => PanelServer.Abrir()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) });
        menu.Items.Add(_pausar);
        menu.Items.Add(_foco);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Parar lembretes pendentes", null, (_, _) => Hub.Ativas.Clear());
        menu.Items.Add("Testar popup", null, (_, _) => Hub.Fila.Enqueue(new UiEvento("item", new Item("msg", null, false, "Teste", "Popup de teste."))));
        menu.Items.Add("Configurações", null, (_, _) => PanelServer.Abrir("#cfg"));
        menu.Items.Add(_inicio);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Sair());
        menu.Opening += (_, _) => { _inicio.Checked = Autostart.Ativo; AtualizarMenu(); };

        _icone = new NotifyIcon { Visible = true, ContextMenuStrip = menu };
        _icone.DoubleClick += (_, _) => PanelServer.Abrir();
        AtualizarIcone();
        Hub.EstadoMudou += () => _ui.BeginInvoke(AtualizarIcone);

        BannerWatcher.Iniciar();
        ToastListener.Iniciar();

        if (modoTeste)
        {
            // com atraso, para dar tempo de medir o foco depois da inicialização
            _timerTeste = new Timer { Interval = 5000 };
            _timerTeste.Tick += (_, _) =>
            {
                _timerTeste.Stop();
                Hub.Fila.Enqueue(new UiEvento("item", new Item("msg", null, true, "Maria (Produção)", "Linha 3 parou, preciso de ajuda urgente!")));
                Hub.Fila.Enqueue(new UiEvento("item", new Item("msg", null, false, "João", "Bom dia, tem um minuto?")));
            };
            _timerTeste.Start();
        }
    }

    void AtualizarMenu()
    {
        _pausar.Text = Hub.Pausado ? "Retomar notificações" : "Pausar notificações";
        _foco.Checked = Hub.Foco;
    }

    void AtualizarIcone()
    {
        string cor = Hub.Pausado ? "#757575" : Hub.Foco ? "#E65100" : "#2E7D32";
        _icone.Text = Hub.Pausado ? "Teams Notify - PAUSADO" : Hub.Foco ? "Teams Notify - modo foco" : "Teams Notify - ativo";
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b1 = new SolidBrush(ColorTranslator.FromHtml(cor));
            g.FillEllipse(b1, 2, 2, 28, 28);
            g.FillEllipse(Brushes.White, 11, 11, 10, 10);
        }
        var anterior = _hIcone;
        _hIcone = bmp.GetHicon();
        _icone.Icon = Icon.FromHandle(_hIcone);
        if (anterior != IntPtr.Zero) DestroyIcon(anterior);
        AtualizarMenu();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    void Sair()
    {
        _icone.Visible = false;
        _icone.Dispose();
        PanelServer.Parar();
        ExitThread();
    }
}

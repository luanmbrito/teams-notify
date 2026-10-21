using System;
using System.Drawing;
using System.Windows.Forms;

namespace TeamsNotify;

/// <summary>Popup no meio da tela. Não ativa a janela: o teclado continua no app em que você está.</summary>
sealed class PopupForm : Form
{
    const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    readonly Label _contador;

    public PopupForm(Item item, Color cor, string titulo, int largura, int altura, int sonecaMin,
                     Action abrir, Action fechar, Action soneca)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = cor;
        Padding = new Padding(4);

        float escala = DeviceDpi / 96f;
        Size = new Size((int)(largura * escala), (int)(altura * escala));
        var tela = Screen.PrimaryScreen!.Bounds;
        Location = new Point(tela.Left + (tela.Width - Width) / 2, tela.Top + (tela.Height - Height) / 2);

        var grade = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 1, RowCount = 4 };
        grade.RowStyles.Add(new RowStyle(SizeType.Absolute, 38 * escala));
        grade.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grade.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grade.RowStyles.Add(new RowStyle(SizeType.Absolute, 54 * escala));
        Controls.Add(grade);

        // cabeçalho colorido
        var topo = new Panel { Dock = DockStyle.Fill, BackColor = cor, Margin = Padding.Empty };
        topo.Controls.Add(new Label
        {
            Text = titulo, ForeColor = Color.White, Font = new Font("Segoe UI", 12, FontStyle.Bold),
            Dock = DockStyle.Left, AutoSize = true, Padding = new Padding(10, 8, 0, 0), UseCompatibleTextRendering = true,
        });
        _contador = new Label
        {
            ForeColor = Color.White, Font = new Font("Segoe UI", 10), Dock = DockStyle.Right,
            AutoSize = true, Padding = new Padding(0, 10, 10, 0),
        };
        topo.Controls.Add(_contador);
        grade.Controls.Add(topo, 0, 0);

        grade.Controls.Add(new Label
        {
            Text = item.Remetente, ForeColor = Color.FromArgb(34, 34, 34), Font = new Font("Segoe UI", 16, FontStyle.Bold),
            AutoSize = true, MaximumSize = new Size((int)((largura - 40) * escala), 0), Margin = new Padding(16, 12, 16, 2),
        }, 0, 1);

        var corpo = item.Corpo.Length > 400 ? item.Corpo[..400] : item.Corpo;
        grade.Controls.Add(new Label
        {
            Text = corpo, ForeColor = Color.FromArgb(51, 51, 51), Font = new Font("Segoe UI", 12),
            Dock = DockStyle.Fill, Margin = new Padding(16, 0, 16, 0),
        }, 0, 2);

        // botões
        var rodape = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        rodape.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rodape.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var esquerda = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 0, 0) };
        esquerda.Controls.Add(Botao("Abrir Teams", Color.FromArgb(0x46, 0x4E, 0xB8), abrir));
        esquerda.Controls.Add(Botao("OK", Color.FromArgb(0x2E, 0x7D, 0x32), fechar));
        rodape.Controls.Add(esquerda, 0, 0);
        if (item.Tipo != "chamada")
        {
            var direita = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 8, 8, 0) };
            direita.Controls.Add(Botao($"Soneca {sonecaMin} min", Color.FromArgb(0x75, 0x75, 0x75), soneca));
            rodape.Controls.Add(direita, 1, 0);
        }
        grade.Controls.Add(rodape, 0, 3);
    }

    static Button Botao(string texto, Color cor, Action acao)
    {
        var b = new Button
        {
            Text = texto, BackColor = cor, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11, FontStyle.Bold), AutoSize = true, Cursor = Cursors.Hand,
            Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 0, 8, 0), UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => acao();
        return b;
    }

    public void DefinirFila(int extra) => _contador.Text = extra > 0 ? $"+{extra} na fila" : "";

    /// <summary>Exibe sem ativar a janela (SW_SHOWNOACTIVATE).</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }
}

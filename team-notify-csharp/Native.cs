using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TeamsNotify;

/// <summary>Chamadas diretas à API do Windows.</summary>
static class Native
{
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("kernel32.dll")] static extern bool Beep(uint freq, uint ms);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    /// <summary>(classe, título, largura) de uma janela.</summary>
    public static (string Classe, string Titulo, int Largura) Info(IntPtr hwnd)
    {
        var c = new StringBuilder(128);
        var t = new StringBuilder(256);
        GetClassName(hwnd, c, c.Capacity);
        GetWindowText(hwnd, t, t.Capacity);
        GetWindowRect(hwnd, out var r);
        return (c.ToString(), t.ToString(), r.Right - r.Left);
    }

    public static string? NomeDoProcesso(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    /// <summary>Segundos desde a última tecla/mouse.</summary>
    public static double SegundosOcioso()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return 0;
        return unchecked((uint)Environment.TickCount - li.dwTime) / 1000.0;
    }

    public static void Bip(int freq, int ms)
    {
        try { Beep((uint)freq, (uint)ms); } catch { /* sem placa de som */ }
    }
}

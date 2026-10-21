using System;
using System.IO;

namespace TeamsNotify;

/// <summary>Log de erros em teams_notify.log; só grava enquanto "logs.erros" estiver ligado.</summary>
static class Logger
{
    static readonly object Trava = new();

    public static void Warn(string msg) => Escrever("WARNING", msg);
    public static void Error(string msg, Exception? e = null) => Escrever("ERROR", e == null ? msg : msg + Environment.NewLine + e);

    static void Escrever(string nivel, string msg)
    {
        try
        {
            if (!Config.Bool("logs.erros", true)) return;
            lock (Trava)
                File.AppendAllText(Path.Combine(Config.Dir, "teams_notify.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {nivel} {msg}{Environment.NewLine}");
        }
        catch { /* log nunca pode derrubar o programa */ }
    }
}

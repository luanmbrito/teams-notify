using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace TeamsNotify;

/// <summary>Notificações normais do Windows (reserva: Teams clássico, Teams no navegador etc.).</summary>
static class ToastListener
{
    public static void Iniciar() => _ = Task.Run(Loop);

    public static string StatusAcesso()
    {
        try { return UserNotificationListener.Current.GetAccessStatus().ToString(); }
        catch (Exception e) { return "erro: " + e.Message; }
    }

    static async Task Loop()
    {
        UserNotificationListener listener;
        try
        {
            listener = UserNotificationListener.Current;
            var acesso = await listener.RequestAccessAsync();
            if (acesso != UserNotificationListenerAccessStatus.Allowed)
            {
                Hub.Erro("Acesso às notificações negado.\nConfigurações > Privacidade > Notificações.");
                return;
            }
        }
        catch (Exception e)
        {
            Logger.Error("Notificações do Windows indisponíveis", e);
            return;
        }

        var vistos = new HashSet<uint>();
        bool primeira = true;
        while (true)
        {
            try
            {
                var notifs = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                var atuais = new HashSet<uint>();
                foreach (var n in notifs)
                {
                    atuais.Add(n.Id);
                    if (!vistos.Add(n.Id) || primeira) continue; // ignora o que já estava na central ao iniciar

                    string app = n.AppInfo.DisplayInfo.DisplayName;
                    if (!Config.Lista("apps").Any(a => app.Contains(a, StringComparison.OrdinalIgnoreCase))) continue;

                    var binding = n.Notification.Visual.GetBinding("ToastGeneric");
                    var textos = binding?.GetTextElements().Select(t => t.Text).ToList() ?? new List<string>();
                    string remetente = textos.Count > 0 ? textos[0] : app;
                    string corpo = string.Join("\n", textos.Skip(1));
                    Hub.Processar(app, "t" + n.Id, remetente, corpo);
                }

                // notificação sumiu da central = você leu / atendeu no Teams
                foreach (var chave in Hub.Ativas.Keys.Where(k => k.StartsWith('t')).ToList())
                    if (uint.TryParse(chave.AsSpan(1), out var id) && !atuais.Contains(id)) Hub.Resolver(chave);
                vistos.IntersectWith(atuais);
                primeira = false;
            }
            catch (Exception e) { Logger.Error("Falha ao ler notificações", e); }
            await Task.Delay(1000);
        }
    }
}

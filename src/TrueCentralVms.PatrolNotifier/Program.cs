using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.PatrolNotifier;

/// <summary>
/// Escucha el progreso de la base (named pipe del servicio truecentral-patrol)
/// y lo muestra en <see cref="ProgressWindow"/>. Una sola instancia por sesión.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [STAThread]
    public static int Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\TrueCentralRondasAviso", out bool first);
        if (!first) return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new ProgressWindow();
        var reminders = new ReminderWindow();
        if (args.Contains("--demo")) _ = DemoAsync(window);
        else if (args.Contains("--demo-reminder")) _ = DemoReminderAsync(reminders);
        else _ = Task.Run(() => ListenAsync(window, reminders));
        app.Run();
        return 0;
    }

    private static async Task ListenAsync(ProgressWindow window, ReminderWindow reminders)
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PatrolDockProgress.PipeName, PipeDirection.In, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                using var reader = new StreamReader(pipe);
                while (await reader.ReadLineAsync() is { } line)
                {
                    PatrolDockProgress? p = null;
                    try { p = JsonSerializer.Deserialize<PatrolDockProgress>(line, Json); }
                    catch (JsonException) { }
                    if (p is null) continue;
                    if (p.Stage.StartsWith("reminder-", StringComparison.Ordinal))
                        reminders.Dispatcher.Invoke(() => reminders.Show(p));
                    else window.Dispatcher.Invoke(() => window.Show(p));
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                // El servicio no está corriendo (o se reinició): reintentar en silencio.
            }
            await Task.Delay(3000);
        }
    }

    /// <summary>truecentral-patrol-aviso --demo-reminder [--snapshots carpeta]: muestra los tres recordatorios.</summary>
    private static async Task DemoReminderAsync(ReminderWindow window)
    {
        string? snapshots = Environment.GetCommandLineArgs().SkipWhile(a => a != "--snapshots").Skip(1).FirstOrDefault();
        foreach (var (kind, text) in new[]
        {
            ("upcoming", "«Perímetro norte» a las 22:00. Prepare el bastón."),
            ("due", "«Perímetro norte», programada a las 22:00. Tiene hasta las 22:10 para salir."),
            ("late", "«Perímetro norte», programada a las 22:00. Se avisó a la central."),
        })
        {
            window.Dispatcher.Invoke(() =>
            {
                window.Show(new PatrolDockProgress("", null, "reminder-" + kind, 0, 0, text, DateTime.Now) { RunId = 1 });
                if (snapshots is not null) window.SaveSnapshot(Path.Combine(snapshots, $"recordatorio-{kind}.png"));
            });
            await Task.Delay(3000);
        }
        window.Dispatcher.Invoke(() => window.Show(new PatrolDockProgress("", null, "reminder-clear", 0, 0, null, DateTime.Now) { RunId = 1 }));
    }

    /// <summary>truecentral-patrol-aviso --demo: recorre todas las etapas sin bastón (para revisar el aviso).</summary>
    private static async Task DemoAsync(ProgressWindow window)
    {
        string? snapshots = Environment.GetCommandLineArgs().SkipWhile(a => a != "--snapshots").Skip(1).FirstOrDefault();
        void Show(string stage, int block = 0, int blocks = 0, string? msg = null) =>
            window.Dispatcher.Invoke(() =>
            {
                window.Show(new PatrolDockProgress("COM7", 7406, stage, block, blocks, msg, DateTime.Now));
                if (snapshots is not null) window.SaveSnapshot(Path.Combine(snapshots, $"aviso-{stage}-{block}.png"));
            });
        Show("detected"); await Task.Delay(800);
        Show("clock"); await Task.Delay(800);
        for (int i = 1; i <= 40; i++) { Show("reading", i, 40); await Task.Delay(60); }
        Show("uploading", 40, 40); await Task.Delay(1000);
        Show("erasing"); await Task.Delay(2500);
        Show("done", msg: "200 lecturas (200 nuevas), 96 pasos nuevos. Bastón vaciado.");
    }
}

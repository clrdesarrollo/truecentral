using Avalonia;

namespace TrueCentralVms.AlarmScreen;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // el script de actualización de la Pi lo usa para saber qué versión hay instalada
        if (args.Contains("--version"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

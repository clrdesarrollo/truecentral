using System.IO;
using System.Windows;
using FlyleafLib;
using TrueCentralVms.Client.Views;

namespace TrueCentralVms.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Motor de video (FFmpeg): una sola inicialización por proceso. Los
        // binarios los aporta el paquete Flyleaf.FFmpeg (carpeta FFmpeg junto
        // al exe); como respaldo se busca tools\ffmpeg\bin subiendo al repo.
        Engine.Start(new EngineConfig
        {
            FFmpegPath = ResolveFFmpegPath(),
            UIRefresh = false,
            LogLevel = LogLevel.Quiet,
        });

        new LoginWindow().Show();
    }

    private static string ResolveFFmpegPath()
    {
        // Instalado: carpeta FFmpeg junto al exe. En desarrollo: el build
        // compartido del release de Flyleaf en tools\ffmpeg-flyleaf (la
        // versión DEBE calzar con FlyleafLib; ver el csproj del cliente).
        string packaged = Path.Combine(AppContext.BaseDirectory, "FFmpeg");
        if (Directory.Exists(packaged) && Directory.EnumerateFiles(packaged, "avcodec*.dll").Any())
            return packaged;

        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "tools", "ffmpeg-flyleaf");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "avcodec*.dll").Any())
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return packaged; // Engine.Start reportará el error con claridad
    }
}

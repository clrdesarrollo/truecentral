using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Media;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Alarma sonora en el equipo del operador. El sonido lo elige la
/// automatización en el servidor (los mismos archivos que se cargan para los
/// parlantes IP); el cliente lo descarga una vez, lo deja en la carpeta
/// temporal del usuario y lo reproduce desde ahí.
///
/// Si el sonido no se puede descargar o reproducir, suena el pitido del
/// sistema: una alarma que no avisa es peor que una alarma fea.
/// </summary>
public sealed class AlertSoundPlayer(ApiClient api)
{
    private static string CacheDirectory =>
        Path.Combine(Path.GetTempPath(), "CLRTrueCentralVMS", "sonidos");

    /// <summary>Sonidos ya descargados en esta sesión: nombre → ruta local.</summary>
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    /// <summary>Reproductor único: una alarma nueva interrumpe la anterior.</summary>
    private MediaPlayer? _player;
    private int _remaining;
    /// <summary>true = sonar hasta que alguien confirme o cierre el aviso (repeticiones = 0).</summary>
    private bool _loop;
    private CancellationTokenSource? _systemLoop;

    /// <summary>
    /// Hace sonar la alarma. <paramref name="sound"/> es el nombre de un
    /// sonido del servidor o <c>"sistema"</c>; <paramref name="repeat"/> las
    /// veces que se repite y <b>0 = sonar hasta que alguien confirme la alerta
    /// o cierre la ventana</b> (lo corta <see cref="Stop"/>). Nunca lanza.
    /// </summary>
    public async Task PlayAsync(string? sound, int repeat)
    {
        if (string.IsNullOrWhiteSpace(sound)) return;
        Stop();                                   // una alarma nueva reemplaza a la anterior
        _loop = repeat <= 0;
        repeat = _loop ? 1 : Math.Clamp(repeat, 1, 5);

        if (sound == Core.Contracts.WorkflowNotificationDto.SystemSoundName)
        {
            PlaySystem(repeat);
            return;
        }

        string? path = await EnsureDownloadedAsync(sound);
        if (path is null)
        {
            PlaySystem(repeat);
            return;
        }

        // MediaPlayer exige el hilo de interfaz (necesita su Dispatcher).
        Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                _player ??= CreatePlayer();
                _remaining = repeat - 1;
                _player.Open(new Uri(path));
                _player.Play();
            }
            catch
            {
                PlaySystem(repeat);
            }
        });
    }

    /// <summary>
    /// Calla la alarma: la usa el botón "Silenciar", el acuse de recibo y el
    /// cierre de la ventana. Sin esto, un sonido en bucle no pararía nunca.
    /// </summary>
    public void Stop()
    {
        _loop = false;
        _remaining = 0;
        _systemLoop?.Cancel();
        _systemLoop = null;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            try { _player?.Stop(); } catch (Exception) { /* ya estaba detenido */ }
        });
    }

    private MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer();
        player.MediaEnded += (_, _) =>
        {
            if (!_loop && _remaining-- <= 0) return;
            try
            {
                player.Position = TimeSpan.Zero;
                player.Play();
            }
            catch { /* el archivo desapareció: no vale la pena insistir */ }
        };
        // Un archivo corrupto no puede dejar el aviso mudo para siempre.
        player.MediaFailed += (_, _) => { _remaining = 0; PlaySystem(1); };
        return player;
    }

    private void PlaySystem(int repeat)
    {
        var loop = _systemLoop = new CancellationTokenSource();
        bool forever = _loop;
        var token = loop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; forever || i < repeat; i++)
                {
                    if (token.IsCancellationRequested) return;
                    try { SystemSounds.Exclamation.Play(); } catch (Exception) { return; }
                    await Task.Delay(forever ? 1500 : 700, token);
                }
            }
            catch (OperationCanceledException) { /* silenciada */ }
        }, token);
    }

    /// <summary>Descarga el sonido (una vez por sesión) y devuelve su ruta local.</summary>
    private async Task<string?> EnsureDownloadedAsync(string sound)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(sound, out string? cached) && File.Exists(cached))
                return cached;
        }

        await _downloadLock.WaitAsync();
        try
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(sound, out string? cached) && File.Exists(cached))
                    return cached;
            }

            if (await api.GetWorkflowSoundAsync(sound) is not { } file) return null;
            Directory.CreateDirectory(CacheDirectory);
            string path = Path.Combine(CacheDirectory, SafeName(file.FileName));
            await File.WriteAllBytesAsync(path, file.Content);
            lock (_cache) _cache[sound] = path;
            return path;
        }
        catch
        {
            return null;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private static string SafeName(string fileName)
    {
        string clean = new(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        return clean.Length == 0 ? "alarma.wav" : clean;
    }
}

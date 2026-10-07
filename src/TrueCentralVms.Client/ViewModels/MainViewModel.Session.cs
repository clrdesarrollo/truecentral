using System.Windows;
using System.Windows.Threading;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Última sesión de la Vista en Vivo (Configuración → Video → "Al iniciar,
/// volver a abrir las cámaras de la última sesión"): qué cámara había en cada
/// cuadro de la grilla principal y de cada pantalla auxiliar, con su división y
/// el monitor de cada ventana.
///
/// Se guarda a medida que cambia (con un respiro de dos segundos para juntar
/// los cambios seguidos) y no solo al salir: si el cliente se cae, se lo cierra
/// a la fuerza o se corta la luz, igual queda lo último que había. Se guarda
/// siempre, aunque la opción esté apagada, para que al encenderla sirva desde
/// el próximo inicio.
/// </summary>
public partial class MainViewModel
{
    /// <summary>La primera división (la del constructor): restaurar espera a que termine.</summary>
    private Task _initialLayout = Task.CompletedTask;

    /// <summary>Restaurando: lo que se abre no es un cambio del operador y no se guarda a medias.</summary>
    private bool _restoringSession;

    /// <summary>Saliendo: cerrar las ventanas y liberar los cuadros no debe pisar la última sesión.</summary>
    private bool _sessionFrozen;

    /// <summary>
    /// Ya se decidió si restaurar (con el árbol cargado). Antes de eso no se
    /// guarda nada: la grilla vacía del arranque pisaría la sesión que se está
    /// por reabrir.
    /// </summary>
    private bool _sessionReady;

    partial void OnIsLiveViewOpenChanged(bool value) => ScheduleSessionSave();

    private DispatcherTimer? _sessionSaveTimer;

    /// <summary>Guarda la sesión dentro de dos segundos (los cambios seguidos se juntan en uno).</summary>
    internal void ScheduleSessionSave()
    {
        if (!_sessionReady || _sessionFrozen || _restoringSession) return;
        if (_sessionSaveTimer is null)
        {
            _sessionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _sessionSaveTimer.Tick += (_, _) =>
            {
                _sessionSaveTimer!.Stop();
                SaveSessionNow();
            };
        }
        _sessionSaveTimer.Stop();
        _sessionSaveTimer.Start();
    }

    /// <summary>Guarda ya la sesión (al salir, antes de cerrar ventanas y cuadros).</summary>
    private void SaveSessionNow()
    {
        if (!_sessionReady || _sessionFrozen || _api.BaseUrl is null || _api.Username is null) return;
        try
        {
            _settings.LastSession = CaptureSession();
            _settings.Save();
        }
        catch { /* mejor esfuerzo: no poder guardar no puede molestar al operador */ }
    }

    private LiveSession CaptureSession()
    {
        var session = new LiveSession
        {
            ServerUrl = _api.BaseUrl ?? "",
            Username = _api.Username ?? "",
            SavedAtUtc = DateTime.UtcNow,
            LiveViewOpen = IsLiveViewOpen,
        };
        Fill(session.Main, CurrentLayout, Cells);
        foreach (var window in _auxWindows.Where(w => w.IsLoaded))
        {
            var screen = new LiveSessionScreen { Slot = window.Vm.SlotNumber };
            Fill(screen, window.Vm.CurrentLayout, window.Vm.Cells);
            // Maximizada: sus límites "normales" dicen en qué monitor estaba. En
            // pantalla completa la ventana está Normal con los límites del
            // monitor, que también sirven. RestoreBounds vacío trae infinitos,
            // que el JSON no acepta: ahí van los límites actuales.
            var bounds = window.WindowState == WindowState.Maximized && !window.RestoreBounds.IsEmpty
                ? window.RestoreBounds
                : new Rect(window.Left, window.Top,
                    double.IsNaN(window.Width) ? window.ActualWidth : window.Width,
                    double.IsNaN(window.Height) ? window.ActualHeight : window.Height);
            screen.Left = bounds.Left;
            screen.Top = bounds.Top;
            screen.Width = bounds.Width;
            screen.Height = bounds.Height;
            screen.Maximized = window.WindowState == WindowState.Maximized || window.Vm.IsGridFullscreen;
            session.AuxScreens.Add(screen);
        }
        return session;

        static void Fill(LiveSessionGrid grid, VideoLayout layout, IEnumerable<VideoCellViewModel> cells)
        {
            grid.LayoutName = layout.Key;
            grid.Columns = layout.Columns;
            grid.Rows = layout.Rows;
            grid.Cells = cells.Select((cell, index) => (cell, index))
                .Where(x => x.cell.AssignedChannel is not null)
                .Select(x => new LiveSessionCell
                {
                    Index = x.index,
                    ChannelId = x.cell.AssignedChannel!.Channel.Id,
                    Profile = (int)x.cell.Profile,
                })
                .ToList();
        }
    }

    /// <summary>
    /// Vuelve a abrir la última sesión, si la opción está encendida y es de
    /// este servidor y este usuario. Se llama con el árbol ya cargado (las
    /// cámaras se buscan en él): las que ya no existen, quedaron deshabilitadas
    /// o están fuera del alcance del usuario dejan su cuadro libre.
    /// </summary>
    public async Task RestoreLastSessionAsync()
    {
        try { await RestoreCoreAsync(); }
        // Desde acá cada cambio se guarda (también si no había nada que restaurar).
        finally { _sessionReady = true; }
    }

    private async Task RestoreCoreAsync()
    {
        var session = _settings.LastSession;
        if (!_settings.RestoreLastSession || session is null || !session.HasCameras) return;
        if (!string.Equals(session.ServerUrl.TrimEnd('/'), _api.BaseUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(session.Username, _api.Username, StringComparison.OrdinalIgnoreCase))
            return;

        await _initialLayout;
        var byId = Devices.SelectMany(d => d.Channels)
            .GroupBy(c => c.Channel.Id)
            .ToDictionary(g => g.Key, g => g.First());
        int total = session.Main.Cells.Count + session.AuxScreens.Sum(s => s.Cells.Count);
        int done = 0, opened = 0, missing = 0;

        _restoringSession = true;
        IsBulkOpening = true;
        BulkOpeningText = $"Volviendo a abrir la última sesión ({total} cámara(s))…";
        StatusMessage = BulkOpeningText;
        OpenLiveView();
        var loading = Application.Current.MainWindow is { IsLoaded: true } owner
            ? Views.LoadingWindow.Open(owner, BulkOpeningText)
            : null;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        void Progress()
        {
            BulkOpeningText = $"Volviendo a abrir la última sesión… {done}/{total}";
            loading?.Update(BulkOpeningText);
        }
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Render);

            // Grilla principal.
            await ApplyLayoutAsync(VideoLayout.Restore(session.Main.LayoutName, session.Main.Columns, session.Main.Rows));
            SelectedCell = null;
            foreach (var cell in Cells) cell.Clear();
            var (o, m) = await OpenSessionCellsAsync(Cells, session.Main.Cells, byId, n => { done += n; Progress(); });
            opened += o;
            missing += m;

            // Pantallas auxiliares, cada una donde estaba.
            foreach (var saved in session.AuxScreens
                         .Where(s => s.Slot is >= 1 and <= MaxAuxScreens)
                         .GroupBy(s => s.Slot).Select(g => g.First())
                         .OrderBy(s => s.Slot))
            {
                if (_auxWindows.Any(w => w.Vm.SlotNumber == saved.Slot)) continue;
                var window = CreateAuxWindow(saved.Slot);
                var occupied = new List<Window>();
                if (Application.Current.MainWindow is { } main) occupied.Add(main);
                occupied.AddRange(_auxWindows.Where(w => w != window && w.IsLoaded));
                window.ShowAt(new Rect(saved.Left, saved.Top, saved.Width, saved.Height), saved.Maximized, occupied);

                await window.Vm.InitialLayout;
                await window.Vm.ApplyLayoutAsync(VideoLayout.Restore(saved.LayoutName, saved.Columns, saved.Rows));
                (o, m) = await OpenSessionCellsAsync(window.Vm.Cells, saved.Cells, byId, n => { done += n; Progress(); });
                opened += o;
                missing += m;
            }

            StatusMessage = missing == 0
                ? $"Última sesión restaurada: {opened} cámara(s) en pantalla."
                : $"Última sesión restaurada: {opened} cámara(s) en pantalla; {missing} ya no está(n) disponible(s) " +
                  "y su cuadro quedó libre.";
        }
        catch (Exception ex)
        {
            // Abrir video toca driver, GPU y red: una falla no puede llevarse el arranque.
            StatusMessage = $"No se pudo restaurar toda la última sesión: {ex.Message}";
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
            loading?.Finish();
            IsBulkOpening = false;
            _restoringSession = false;
            RefreshLiveChannels();
        }
    }

    /// <summary>
    /// Abre las cámaras guardadas en sus cuadros, por tandas (mismo criterio
    /// que abrir un equipo completo). Devuelve cuántas se abrieron y cuántas ya
    /// no estaban en el inventario de este usuario.
    /// </summary>
    private static async Task<(int Opened, int Missing)> OpenSessionCellsAsync(IList<VideoCellViewModel> cells,
        IReadOnlyList<LiveSessionCell> saved, IReadOnlyDictionary<int, ChannelNode> byId, Action<int> progress)
    {
        var targets = saved
            .Where(c => c.Index >= 0 && c.Index < cells.Count && byId.ContainsKey(c.ChannelId))
            .GroupBy(c => c.Index).Select(g => g.First())
            .OrderBy(c => c.Index)
            .ToList();
        int missing = saved.Count - targets.Count;
        if (missing > 0) progress(missing);

        for (int i = 0; i < targets.Count; i += OpenBatchSize)
        {
            int upTo = Math.Min(i + OpenBatchSize, targets.Count);
            var wave = new List<Task>();
            for (int j = i; j < upTo; j++)
            {
                var item = targets[j];
                var profile = item.Profile == (int)StreamProfile.Main ? StreamProfile.Main : StreamProfile.Sub;
                wave.Add(cells[item.Index].OpenAsync(byId[item.ChannelId], profile));
            }
            await Task.WhenAll(wave);
            progress(upTo - i);
            await BreatheAsync();
        }
        return (targets.Count, missing);
    }
}

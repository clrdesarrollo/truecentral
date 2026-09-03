using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Pantalla auxiliar de Vista en Vivo (estilo iVMS-4200, hasta 3): una ventana
/// independiente con su propia grilla de video, pensada para llevarse a otro
/// monitor. Comparte el árbol de dispositivos del shell (mismo inventario y
/// estados en línea) pero es dueña de sus cuadros — players propios, streams
/// propios —, así que cerrarla solo detiene lo que ella mostraba. El audio
/// sigue siendo exclusivo en TODA la aplicación: lo coordina el shell.
/// </summary>
public partial class AuxScreenViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    /// <summary>Número de la pantalla (1..3), fijo mientras esté abierta; al
    /// cerrarla su número queda libre para la próxima.</summary>
    public int SlotNumber { get; }

    public string WindowTitle => $"Pantalla auxiliar {SlotNumber}";

    /// <summary>Árbol del shell, compartido: el mismo inventario y los mismos
    /// estados en línea (y el filtro del buscador de la ventana principal).</summary>
    public ObservableCollection<DeviceNode> Devices => _shell.Devices;

    public ObservableCollection<VideoCellViewModel> Cells { get; } = [];

    public IReadOnlyList<VideoLayout> Layouts => VideoLayout.Standard;

    [ObservableProperty] private VideoLayout _currentLayout = VideoLayout.Default;
    [ObservableProperty] private int _maximizedIndex = -1;
    [ObservableProperty] private VideoCellViewModel? _selectedCell;
    /// <summary>Cuadro bajo el puntero durante un arrastre (se ilumina).</summary>
    [ObservableProperty] private VideoCellViewModel? _dropTargetCell;
    /// <summary>Pantalla completa de la grilla en ESTA ventana (cubre su
    /// monitor; Esc sale). Independiente de la de la ventana principal.</summary>
    [ObservableProperty] private bool _isGridFullscreen;
    [ObservableProperty] private bool _isTreeCollapsed;
    /// <summary>Modo zoom digital (lupa + recuadro) de esta ventana.</summary>
    [ObservableProperty] private bool _isDigitalZoomMode;
    [ObservableProperty] private bool _isBulkOpening;
    [ObservableProperty] private string _bulkOpeningText = "";
    [ObservableProperty] private string _statusMessage = HintMessage;

    private const string HintMessage =
        "Doble clic en un canal del árbol lo abre aquí; arrastre esta ventana al monitor que quiera.";

    /// <summary>Ventana que hospeda esta pantalla (el aviso modal de la
    /// apertura masiva y las notificaciones salen sobre ella, en su monitor).</summary>
    public Window? OwnerWindow { get; set; }

    /// <summary>Se guardó un archivo local desde esta pantalla: la ventana
    /// muestra la notificación flotante en su propio monitor.</summary>
    public event Action<string, string, string>? MediaSaved;

    public AuxScreenViewModel(MainViewModel shell, int slotNumber)
    {
        _shell = shell;
        SlotNumber = slotNumber;
        _ = ApplyLayoutAsync(VideoLayout.Default);
    }

    [RelayCommand]
    private void EnterGridFullscreen() => IsGridFullscreen = true;

    partial void OnIsDigitalZoomModeChanged(bool value) =>
        StatusMessage = value
            ? "Zoom digital: arrastre sobre el video para marcar el área; clic derecho vuelve a 1×."
            : HintMessage;

    /// <summary>Cambio de división en curso (clics rápidos ignorados).</summary>
    private bool _layoutBusy;

    [RelayCommand]
    private async Task SelectLayoutAsync(VideoLayout layout)
    {
        if (_layoutBusy) return;
        _layoutBusy = true;
        try { await ApplyLayoutAsync(layout); }
        finally { _layoutBusy = false; }
    }

    /// <summary>Igual que en la grilla principal: celdas por tandas cediendo
    /// ciclos, porque crear/liberar players es caro (superficies de GPU).</summary>
    public async Task ApplyLayoutAsync(VideoLayout layout)
    {
        MaximizedIndex = -1;
        CurrentLayout = layout;
        int count = layout.CellCount;
        while (Cells.Count > count)
        {
            var cell = Cells[^1];
            Cells.RemoveAt(Cells.Count - 1);
            cell.AudioActivated -= _shell.OnCellAudioActivated;
            cell.MediaSaved -= OnCellMediaSaved;
            cell.Dispose();
            await MainViewModel.BreatheAsync();
        }
        while (Cells.Count < count)
        {
            var cell = new VideoCellViewModel(_shell.Api, _shell.Settings);
            cell.AudioActivated += _shell.OnCellAudioActivated;
            cell.MediaSaved += OnCellMediaSaved;
            Cells.Add(cell);
            await MainViewModel.BreatheAsync();
        }
        for (int i = 0; i < Cells.Count; i++)
            Cells[i].Index = i + 1;
        if (SelectedCell is not null && !Cells.Contains(SelectedCell))
            SelectedCell = null;
    }

    private void OnCellMediaSaved(string title, string glyph, string path) =>
        Application.Current.Dispatcher.InvokeAsync(() => MediaSaved?.Invoke(title, glyph, path));

    /// <summary>Cuadro promovido a stream principal por estar maximizado
    /// (misma regla que la grilla principal: al restaurar vuelve a secundario
    /// si nadie lo tocó en el intertanto).</summary>
    private VideoCellViewModel? _autoPromotedCell;
    private ChannelNode? _autoPromotedChannel;

    public void ToggleMaximize(VideoCellViewModel cell)
    {
        int index = Cells.IndexOf(cell);
        if (index < 0) return;
        if (MaximizedIndex == index)
        {
            MaximizedIndex = -1;
            if (_autoPromotedCell == cell && cell.AssignedChannel == _autoPromotedChannel &&
                _autoPromotedChannel is not null)
            {
                if (cell.Profile == StreamProfile.Main)
                    _ = cell.SwitchToProfileAsync(StreamProfile.Sub, hardFallbackOnFailure: true);
                else
                    cell.CancelPendingSwitch();
            }
            _autoPromotedCell = null;
            _autoPromotedChannel = null;
            return;
        }
        if (cell.IsEmpty) return;
        MaximizedIndex = index;
        SelectedCell = cell;
        if (cell.Profile == StreamProfile.Sub && cell.AssignedChannel is { } node)
        {
            _autoPromotedCell = cell;
            _autoPromotedChannel = node;
            _ = cell.SwitchToProfileAsync(StreamProfile.Main);
        }
    }

    /// <summary>Abre un canal en el cuadro seleccionado / primer libre /
    /// primero, y avanza la selección (doble clics consecutivos van llenando).</summary>
    public async Task OpenChannelAsync(ChannelNode node)
    {
        var cell = SelectedCell
            ?? Cells.FirstOrDefault(c => c.IsEmpty)
            ?? Cells.FirstOrDefault();
        if (cell is null) return;

        int next = Cells.IndexOf(cell) + 1;
        SelectedCell = next < Cells.Count ? Cells[next] : null;

        await cell.OpenAsync(node, DefaultProfileForOpen());
    }

    /// <summary>Drag & drop del árbol a un cuadro específico.</summary>
    public async Task OpenChannelInCellAsync(ChannelNode node, VideoCellViewModel cell) =>
        await cell.OpenAsync(node, DefaultProfileForOpen());

    /// <summary>
    /// Soltar un cuadro sobre otro de ESTA grilla. El origen puede venir de
    /// cualquier ventana (principal u otra auxiliar): viaja el player con el
    /// video andando, así que mover una cámara entre monitores no corta el
    /// stream ni pide una concesión nueva.
    /// </summary>
    public void SwapCells(VideoCellViewModel source, VideoCellViewModel target)
    {
        if (ReferenceEquals(source, target)) return;
        if (!Cells.Contains(target)) return;
        if (source.IsEmpty && target.IsEmpty) return;

        bool exchange = !target.IsEmpty;
        string moved = source.Title ?? "";
        string displaced = target.Title ?? "";

        VideoCellViewModel.SwapContent(source, target);

        SelectedCell = target;
        StatusMessage = exchange
            ? $"Cuadros intercambiados: \"{moved}\" ↔ \"{displaced}\"."
            : $"\"{moved}\" movida al cuadro {target.Index}.";
    }

    /// <summary>Canales que se abren juntos en la apertura masiva.</summary>
    private const int OpenBatchSize = 4;

    /// <summary>Doble clic en un equipo: abre todos sus canales habilitados en
    /// esta pantalla, con grilla a medida (misma preferencia que la principal).</summary>
    public async Task OpenDeviceAsync(DeviceNode device)
    {
        var channels = device.Channels.ToList();
        if (channels.Count == 0)
        {
            StatusMessage = $"\"{device.Device.Name}\" no tiene canales habilitados.";
            return;
        }

        if (channels.Count > 64)
        {
            StatusMessage = $"\"{device.Device.Name}\" tiene {channels.Count} canales: se abren los primeros 64.";
            channels = channels.Take(64).ToList();
        }

        IsBulkOpening = true;
        BulkOpeningText = $"Abriendo {channels.Count} canal(es) de \"{device.Device.Name}\"…";
        StatusMessage = BulkOpeningText;
        var loading = OwnerWindow is { IsLoaded: true } owner
            ? Views.LoadingWindow.Open(owner, BulkOpeningText)
            : null;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Render);

            if (_shell.Settings.FitGridToDevice)
                await ApplyLayoutAsync(VideoLayout.FitFor(channels.Count));
            else
                await ApplyLayoutAsync(Layouts.FirstOrDefault(l => l.CellCount >= channels.Count) ?? Layouts[^1]);

            SelectedCell = null;
            var profile = DefaultProfileForOpen();
            int openCount = Math.Min(channels.Count, Cells.Count);
            for (int i = openCount; i < Cells.Count; i++)
                Cells[i].Clear();

            for (int i = 0; i < openCount; i += OpenBatchSize)
            {
                int upTo = Math.Min(i + OpenBatchSize, openCount);
                var wave = new List<Task>();
                for (int j = i; j < upTo; j++)
                    wave.Add(Cells[j].OpenAsync(channels[j], profile));
                await Task.WhenAll(wave);
                BulkOpeningText = $"Abriendo canales de \"{device.Device.Name}\"… {upTo}/{openCount}";
                StatusMessage = BulkOpeningText;
                loading?.Update(BulkOpeningText);
                await MainViewModel.BreatheAsync();
            }
            StatusMessage = $"{openCount} canal(es) de \"{device.Device.Name}\" en pantalla.";
        }
        catch (Exception ex)
        {
            // Igual que en la grilla principal: una falla de video no puede
            // llevarse la aplicación (el doble clic del árbol es async void).
            StatusMessage = $"No se pudieron abrir todos los canales de \"{device.Device.Name}\": {ex.Message}";
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
            loading?.Finish();
            IsBulkOpening = false;
        }
    }

    /// <summary>Misma regla que la grilla principal (Configuración → Video).</summary>
    private StreamProfile DefaultProfileForOpen() => _shell.Settings.DefaultProfile switch
    {
        "main" => StreamProfile.Main,
        "sub" => StreamProfile.Sub,
        _ => Cells.Count <= 4 ? StreamProfile.Main : StreamProfile.Sub,
    };

    [RelayCommand]
    public void ClearAll()
    {
        foreach (var cell in Cells)
            cell.Clear();
    }

    /// <summary>La ventana se cerró: se liberan sus players.</summary>
    public void Dispose()
    {
        foreach (var cell in Cells)
            cell.Dispose();
        Cells.Clear();
    }
}

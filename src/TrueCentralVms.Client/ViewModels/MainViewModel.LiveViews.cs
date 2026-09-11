using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>Una vista guardada, como la muestra la lista del botón "Vistas".</summary>
/// <param name="CanEdit">El usuario puede actualizarla o eliminarla (es suya, o es administrador).</param>
public sealed record SavedViewItem(int Id, string Name, string Detail, bool Shared, bool CanEdit);

/// <summary>
/// Vistas guardadas de la Vista en Vivo (las "Custom View" de iVMS-4200): la
/// división de la grilla y qué canal estaba en cada cuadro. Viven en el
/// servidor, así que el operador recupera su pantalla de siempre desde
/// cualquier puesto, y las marcadas como compartidas las ve todo el mundo.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Vistas visibles para este usuario (propias + compartidas).</summary>
    public ObservableCollection<SavedViewItem> SavedViews { get; } = [];

    /// <summary>Lista desplegada del botón "Vistas".</summary>
    [ObservableProperty] private bool _isViewsPopupOpen;

    /// <summary>Nombre escrito en el recuadro "Guardar la grilla actual".</summary>
    [ObservableProperty] private string _newViewName = "";

    /// <summary>La vista nueva queda visible para todos los puestos.</summary>
    [ObservableProperty] private bool _newViewShared;

    /// <summary>Aviso dentro del desplegable (errores y confirmaciones).</summary>
    [ObservableProperty] private string _savedViewsStatus = "";

    /// <summary>Vista cargada por última vez, para el rótulo del botón.</summary>
    [ObservableProperty] private string _activeViewName = "";

    /// <summary>Una operación contra el servidor en curso (evita clics dobles).</summary>
    [ObservableProperty] private bool _savedViewsBusy;

    partial void OnIsViewsPopupOpenChanged(bool value)
    {
        // La lista se refresca al desplegarla: otro puesto pudo guardar o
        // cambiar una vista compartida mientras este estaba abierto.
        if (value) _ = LoadSavedViewsAsync();
    }

    /// <summary>Trae del servidor las vistas que este usuario puede ver.</summary>
    [RelayCommand]
    public async Task LoadSavedViewsAsync()
    {
        try
        {
            var views = await _api.GetLiveViewsAsync();
            SavedViews.Clear();
            foreach (var view in views)
                SavedViews.Add(ToItem(view));
            SavedViewsStatus = SavedViews.Count == 0
                ? "Todavía no hay vistas guardadas: arme la grilla y guárdela aquí abajo."
                : "";
        }
        catch (Exception ex)
        {
            SavedViewsStatus = ex.Message;
        }
    }

    private SavedViewItem ToItem(LiveViewDto view)
    {
        string owner = view.Shared && !string.Equals(view.Owner, _api.Username, StringComparison.OrdinalIgnoreCase)
            ? $" · compartida por {view.Owner}"
            : view.Shared ? " · compartida" : "";
        return new SavedViewItem(view.Id, view.Name,
            $"{view.Items.Count} cámara(s) · división {view.LayoutName}{owner}",
            view.Shared, view.CanEdit);
    }

    /// <summary>Foto de la grilla actual en el formato que guarda el servidor.</summary>
    private LiveViewSaveRequest SnapshotRequest(string name, bool shared) => new(
        name, CurrentLayout.Name, CurrentLayout.Columns, CurrentLayout.Rows, shared,
        Cells.Select((cell, index) => (cell, index))
            .Where(x => x.cell.AssignedChannel is not null)
            .Select(x => new LiveViewItemDto(x.index, x.cell.AssignedChannel!.Channel.Id, (int)x.cell.Profile))
            .ToList());

    /// <summary>
    /// Guarda la grilla actual como vista nueva. Si el nombre ya es de una
    /// vista propia se ofrece reemplazarla (en vez de devolver un error por
    /// nombre repetido, que es lo que el operador quiso hacer igual).
    /// </summary>
    [RelayCommand]
    private async Task SaveCurrentViewAsync()
    {
        string name = NewViewName.Trim();
        if (name.Length == 0)
        {
            SavedViewsStatus = "Escriba un nombre para la vista.";
            return;
        }
        if (Cells.All(c => c.AssignedChannel is null))
        {
            SavedViewsStatus = "La grilla está vacía: abra las cámaras que quiere guardar.";
            return;
        }

        var existing = SavedViews.FirstOrDefault(v =>
            string.Equals(v.Name, name, StringComparison.CurrentCultureIgnoreCase) && v.CanEdit);
        if (existing is not null)
        {
            if (Confirm($"Ya existe la vista \"{existing.Name}\". ¿Reemplazarla con la grilla actual?",
                    "Reemplazar vista") is false)
                return;
            await OverwriteViewAsync(existing);
            return;
        }

        await RunAsync(async () =>
        {
            var saved = await _api.CreateLiveViewAsync(SnapshotRequest(name, NewViewShared));
            NewViewName = "";
            NewViewShared = false;
            ActiveViewName = saved.Name;
            await LoadSavedViewsAsync();
            SavedViewsStatus = $"Vista \"{saved.Name}\" guardada.";
            StatusMessage = $"Vista \"{saved.Name}\" guardada con {saved.Items.Count} cámara(s).";
        });
    }

    /// <summary>Reemplaza el contenido de una vista con la grilla actual.</summary>
    [RelayCommand]
    private async Task UpdateSavedViewAsync(SavedViewItem item)
    {
        if (item is null) return;
        if (Cells.All(c => c.AssignedChannel is null))
        {
            SavedViewsStatus = "La grilla está vacía: no hay nada que guardar en la vista.";
            return;
        }
        if (Confirm($"¿Actualizar la vista \"{item.Name}\" con las cámaras que hay ahora en la grilla?",
                "Actualizar vista") is false)
            return;
        await OverwriteViewAsync(item);
    }

    /// <summary>Escritura de la vista con la grilla actual, ya confirmada.</summary>
    private async Task OverwriteViewAsync(SavedViewItem item)
    {
        await RunAsync(async () =>
        {
            var saved = await _api.UpdateLiveViewAsync(item.Id, SnapshotRequest(item.Name, item.Shared));
            NewViewName = "";
            ActiveViewName = saved.Name;
            await LoadSavedViewsAsync();
            SavedViewsStatus = $"Vista \"{saved.Name}\" actualizada.";
            StatusMessage = $"Vista \"{saved.Name}\" actualizada con {saved.Items.Count} cámara(s).";
        });
    }

    [RelayCommand]
    private async Task DeleteSavedViewAsync(SavedViewItem item)
    {
        if (item is null) return;
        if (Confirm($"¿Eliminar la vista \"{item.Name}\"? Esta acción no se puede deshacer.",
                "Eliminar vista") is false)
            return;

        await RunAsync(async () =>
        {
            await _api.DeleteLiveViewAsync(item.Id);
            if (ActiveViewName == item.Name) ActiveViewName = "";
            await LoadSavedViewsAsync();
            SavedViewsStatus = $"Vista \"{item.Name}\" eliminada.";
        });
    }

    /// <summary>Carga una vista en la grilla (la lista se cierra sola).</summary>
    [RelayCommand]
    private async Task ApplySavedViewAsync(SavedViewItem item)
    {
        if (item is null) return;
        IsViewsPopupOpen = false;
        try
        {
            // El servidor devuelve la versión vigente (otro puesto pudo
            // cambiarla) y de paso registra en la bitácora quién la cargó.
            var view = await _api.ApplyLiveViewAsync(item.Id);
            await ApplyLiveViewAsync(view);
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo cargar la vista \"{item.Name}\": {ex.Message}";
        }
    }

    /// <summary>
    /// Arma la grilla de una vista guardada: primero la división y después las
    /// cámaras, por tandas y con el mismo aviso bloqueante que la apertura de
    /// un equipo completo (cada cuadro crea su player y abre un RTSP contra el
    /// grabador: de golpe, ni la interfaz ni el equipo dan abasto).
    /// </summary>
    private async Task ApplyLiveViewAsync(LiveViewDto view)
    {
        // Canales del inventario por id: los de la vista que ya no existen (o
        // que quedaron deshabilitados) se informan y su cuadro queda libre.
        var byId = Devices.SelectMany(d => d.Channels)
            .GroupBy(c => c.Channel.Id)
            .ToDictionary(g => g.Key, g => g.First());

        IsBulkOpening = true;
        BulkOpeningText = $"Cargando la vista \"{view.Name}\"…";
        StatusMessage = BulkOpeningText;
        var loading = Application.Current.MainWindow is { IsLoaded: true } owner
            ? Views.LoadingWindow.Open(owner, BulkOpeningText)
            : null;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);

            var layout = VideoLayout.Restore(view.LayoutName, view.Columns, view.Rows);
            await ApplyLayoutAsync(layout);
            // Si la vista trae una de las divisiones del selector, esa pasa a
            // ser la preferencia local (la próxima sesión abre en ella).
            if (Layouts.Any(l => l.Name == layout.Name) && _settings.LastLayout != layout.Name)
            {
                _settings.LastLayout = layout.Name;
                _settings.Save();
            }

            SelectedCell = null;
            foreach (var cell in Cells)
                cell.Clear();

            var targets = view.Items
                .Where(i => i.CellIndex >= 0 && i.CellIndex < Cells.Count && byId.ContainsKey(i.ChannelId))
                .OrderBy(i => i.CellIndex)
                .ToList();
            int missing = view.Items.Count - targets.Count;

            for (int i = 0; i < targets.Count; i += OpenBatchSize)
            {
                int upTo = Math.Min(i + OpenBatchSize, targets.Count);
                var wave = new List<Task>();
                for (int j = i; j < upTo; j++)
                {
                    var item = targets[j];
                    var profile = item.StreamType == (int)StreamProfile.Main ? StreamProfile.Main : StreamProfile.Sub;
                    wave.Add(Cells[item.CellIndex].OpenAsync(byId[item.ChannelId], profile));
                }
                await Task.WhenAll(wave);
                BulkOpeningText = $"Cargando la vista \"{view.Name}\"… {upTo}/{targets.Count}";
                loading?.Update(BulkOpeningText);
                await BreatheAsync();
            }

            ActiveViewName = view.Name;
            StatusMessage = missing == 0
                ? $"Vista \"{view.Name}\": {targets.Count} cámara(s) en pantalla."
                : $"Vista \"{view.Name}\": {targets.Count} cámara(s) en pantalla; " +
                  $"{missing} ya no está(n) en el inventario y su cuadro quedó libre.";
        }
        catch (Exception ex)
        {
            // Abrir video toca driver, GPU y red: una falla ahí no puede
            // llevarse la aplicación (el clic de la lista es async void).
            StatusMessage = $"No se pudo cargar toda la vista \"{view.Name}\": {ex.Message}";
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
            loading?.Finish();
            IsBulkOpening = false;
        }
    }

    /// <summary>Ejecuta una operación de la lista mostrando su error dentro del
    /// desplegable (y sin dejar que dos clics la disparen dos veces).</summary>
    private async Task RunAsync(Func<Task> operation)
    {
        if (SavedViewsBusy) return;
        SavedViewsBusy = true;
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            SavedViewsStatus = ex.Message;
        }
        finally
        {
            SavedViewsBusy = false;
        }
    }

    /// <summary>Confirmación sí/no sobre la ventana principal.</summary>
    private static bool Confirm(string message, string title) =>
        MessageBox.Show(Application.Current.MainWindow, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}

using TrueCentralVms.Client.Views;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>Un tablero, como lo muestra la lista del botón "Tableros".</summary>
/// <param name="CanEdit">El usuario puede actualizarlo o eliminarlo (es suyo y puede
/// crear tableros propios, o es compartido y administra los compartidos).</param>
/// <param name="Locked">Compartido y sin permiso para cambiarlo: se usa tal cual.</param>
public sealed record SavedViewItem(int Id, string Name, string Detail, bool Shared, bool CanEdit, bool Locked,
    IReadOnlyList<int> RoleIds);

/// <summary>
/// Tableros del Centro de mando (antes "vistas guardadas"): la división de la
/// grilla y lo que había en cada cuadro (cámara, recurso, tarjeta o página
/// web). Viven en el servidor, así el operador recupera su pantalla desde
/// cualquier puesto. Los compartidos los define quien administra los tableros
/// (desde aquí o desde el panel web, donde se eligen los roles que los ven) y
/// los demás los usan con candado; los propios necesitan "Crear tableros propios".
/// </summary>
public partial class MainViewModel
{
    /// <summary>Tableros visibles para este usuario (compartidos primero, después los propios).</summary>
    public ObservableCollection<SavedViewItem> SavedViews { get; } = [];

    /// <summary>Lista desplegada del botón "Tableros".</summary>
    [ObservableProperty] private bool _isViewsPopupOpen;

    /// <summary>Nombre escrito en el recuadro "Guardar la grilla actual".</summary>
    [ObservableProperty] private string _newViewName = "";

    /// <summary>El tablero nuevo es compartido (solo quien administra los compartidos).</summary>
    [ObservableProperty] private bool _newViewShared;

    /// <summary>Aviso dentro del desplegable (errores y confirmaciones).</summary>
    [ObservableProperty] private string _savedViewsStatus = "";

    /// <summary>Tablero cargado por última vez, para el rótulo del botón.</summary>
    [ObservableProperty] private string _activeViewName = "";

    /// <summary>Una operación contra el servidor en curso (evita clics dobles).</summary>
    [ObservableProperty] private bool _savedViewsBusy;

    partial void OnIsViewsPopupOpenChanged(bool value)
    {
        // La lista se refresca al desplegarla: pudieron guardar o cambiar un
        // tablero compartido mientras este puesto estaba abierto.
        if (value) _ = LoadSavedViewsAsync();
    }

    /// <summary>Trae del servidor los tableros que este usuario puede ver.</summary>
    [RelayCommand]
    public async Task LoadSavedViewsAsync()
    {
        try
        {
            var views = await _api.GetLiveViewsAsync();
            SavedViews.Clear();
            foreach (var view in views.OrderBy(v => v.Shared ? 0 : 1).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase))
                SavedViews.Add(ToItem(view));
            SavedViewsStatus = SavedViews.Count > 0 ? ""
                : CanSaveViews ? "Todavía no hay tableros: arme la grilla y guárdela aquí abajo."
                : "Todavía no hay tableros.";
        }
        catch (Exception ex)
        {
            SavedViewsStatus = ex.Message;
        }
    }

    private SavedViewItem ToItem(LiveViewDto view)
    {
        int cameras = view.Items.Count(i => DashboardItemKinds.IsCamera(i.Kind));
        int others = view.Items.Count - cameras;
        string contents = cameras > 0 && others > 0 ? $"{cameras} cámara(s) y {others} cuadro(s) más"
            : others > 0 ? $"{others} cuadro(s)" : $"{cameras} cámara(s)";
        string audience = !view.Shared ? ""
            : view.Roles is { Count: > 0 } roles ? " · para " + string.Join(", ", roles)
            : view.RestrictToRoles ? " · sin roles asignados"
            : " · compartido";
        string layout = VideoLayout.Find(view.LayoutName)?.Label ?? view.LayoutName;
        return new SavedViewItem(view.Id, view.Name, $"{contents} · división {layout}{audience}",
            view.Shared, view.CanEdit, view.Shared && !view.CanEdit, view.RoleIds ?? []);
    }

    /// <summary>Foto de la grilla actual en el formato que guarda el servidor.</summary>
    private LiveViewSaveRequest SnapshotRequest(string name, bool shared, IReadOnlyList<int>? roleIds = null) => new(
        name, CurrentLayout.Key, CurrentLayout.Columns, CurrentLayout.Rows, shared,
        Cells.Select((cell, index) => DashboardItemOf(cell, index)).OfType<LiveViewItemDto>().ToList(),
        roleIds?.ToList());

    private bool GridIsEmpty => Cells.All(c => c.AssignedChannel is null && c.Panel?.Content is null);

    /// <summary>
    /// Guarda la grilla actual como tablero nuevo. Si el nombre ya es de uno
    /// que puede cambiar, se ofrece reemplazarlo (en vez de devolver un error
    /// por nombre repetido, que es lo que el operador quiso hacer igual).
    /// </summary>
    [RelayCommand]
    private async Task SaveCurrentViewAsync()
    {
        if (!CanSaveViews) return;
        string name = NewViewName.Trim();
        if (name.Length == 0)
        {
            SavedViewsStatus = "Escriba un nombre para el tablero.";
            return;
        }
        if (GridIsEmpty)
        {
            SavedViewsStatus = "La grilla está vacía: abra lo que quiere guardar (cámaras, recursos, tarjetas o páginas).";
            return;
        }

        bool shared = NewViewShared && CanShareViews;
        var existing = SavedViews.FirstOrDefault(v =>
            string.Equals(v.Name, name, StringComparison.CurrentCultureIgnoreCase) && v.CanEdit && v.Shared == shared);
        if (existing is not null)
        {
            if (Confirm($"Ya existe el tablero \"{existing.Name}\". ¿Reemplazarlo con la grilla actual?",
                    "Reemplazar tablero") is false)
                return;
            await OverwriteViewAsync(existing);
            return;
        }

        await RunAsync(async () =>
        {
            var saved = await _api.CreateLiveViewAsync(SnapshotRequest(name, shared));
            NewViewName = "";
            NewViewShared = false;
            ActiveViewName = saved.Name;
            await LoadSavedViewsAsync();
            SavedViewsStatus = shared
                ? $"Tablero \"{saved.Name}\" compartido con todos (en el panel web, Tableros, se eligen los roles)."
                : $"Tablero \"{saved.Name}\" guardado.";
            StatusMessage = $"Tablero \"{saved.Name}\" guardado con {saved.Items.Count} cuadro(s).";
        });
    }

    /// <summary>Reemplaza el contenido de un tablero con la grilla actual.</summary>
    [RelayCommand]
    private async Task UpdateSavedViewAsync(SavedViewItem item)
    {
        if (item is null) return;
        if (GridIsEmpty)
        {
            SavedViewsStatus = "La grilla está vacía: no hay nada que guardar en el tablero.";
            return;
        }
        if (Confirm($"¿Actualizar el tablero \"{item.Name}\" con lo que hay ahora en la grilla?" +
                    (item.Shared ? "\n\nEs compartido: el cambio lo ven todos los que lo usan." : ""),
                "Actualizar tablero") is false)
            return;
        await OverwriteViewAsync(item);
    }

    /// <summary>Escritura del tablero con la grilla actual, ya confirmada (conserva con quién se comparte).</summary>
    private async Task OverwriteViewAsync(SavedViewItem item)
    {
        await RunAsync(async () =>
        {
            var saved = await _api.UpdateLiveViewAsync(item.Id, SnapshotRequest(item.Name, item.Shared, item.RoleIds));
            NewViewName = "";
            ActiveViewName = saved.Name;
            await LoadSavedViewsAsync();
            SavedViewsStatus = $"Tablero \"{saved.Name}\" actualizado.";
            StatusMessage = $"Tablero \"{saved.Name}\" actualizado con {saved.Items.Count} cuadro(s).";
        });
    }

    [RelayCommand]
    private async Task DeleteSavedViewAsync(SavedViewItem item)
    {
        if (item is null) return;
        if (Confirm($"¿Eliminar el tablero \"{item.Name}\"?" +
                    (item.Shared ? " Es compartido: desaparece para todos los que lo usan." : "") +
                    " Esta acción no se puede deshacer.",
                "Eliminar tablero") is false)
            return;

        await RunAsync(async () =>
        {
            await _api.DeleteLiveViewAsync(item.Id);
            if (ActiveViewName == item.Name) ActiveViewName = "";
            await LoadSavedViewsAsync();
            SavedViewsStatus = $"Tablero \"{item.Name}\" eliminado.";
        });
    }

    /// <summary>Carga un tablero en la grilla (la lista se cierra sola).</summary>
    [RelayCommand]
    private async Task ApplySavedViewAsync(SavedViewItem item)
    {
        if (item is null) return;
        IsViewsPopupOpen = false;
        try
        {
            // El servidor devuelve la versión vigente (pudieron cambiarlo) y de
            // paso registra en la bitácora quién lo cargó.
            var view = await _api.ApplyLiveViewAsync(item.Id);
            await ApplyLiveViewAsync(view);
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo cargar el tablero \"{item.Name}\": {ex.Message}";
        }
    }

    /// <summary>
    /// Arma la grilla de un tablero: primero la división, después las cámaras
    /// por tandas (con el mismo aviso bloqueante que la apertura de un equipo
    /// completo: cada cuadro crea su player y abre un RTSP contra el grabador)
    /// y al final los recursos, tarjetas y páginas web.
    /// </summary>
    private async Task ApplyLiveViewAsync(LiveViewDto view)
    {
        // Canales del inventario por id: los del tablero que ya no existen (o
        // que quedaron deshabilitados) se informan y su cuadro queda libre.
        var byId = Devices.SelectMany(d => d.Channels)
            .GroupBy(c => c.Channel.Id)
            .ToDictionary(g => g.Key, g => g.First());

        IsBulkOpening = true;
        BulkOpeningText = $"Cargando el tablero \"{view.Name}\"…";
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
            // Si el tablero trae una de las divisiones del selector, esa pasa a
            // ser la preferencia local (la próxima sesión abre en ella).
            if (Layouts.Contains(layout) && _settings.LastLayout != layout.Key)
            {
                _settings.LastLayout = layout.Key;
                _settings.Save();
            }

            SelectedCell = null;
            foreach (var cell in Cells)
                cell.Clear();

            var inGrid = view.Items.Where(i => i.CellIndex >= 0 && i.CellIndex < Cells.Count).ToList();
            var targets = inGrid
                .Where(i => DashboardItemKinds.IsCamera(i.Kind) && byId.ContainsKey(i.ChannelId))
                .OrderBy(i => i.CellIndex)
                .ToList();
            var others = inGrid.Where(i => !DashboardItemKinds.IsCamera(i.Kind)).ToList();
            int missing = view.Items.Count - targets.Count - others.Count;

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
                BulkOpeningText = $"Cargando el tablero \"{view.Name}\"… {upTo}/{targets.Count}";
                loading?.Update(BulkOpeningText);
                await BreatheAsync();
            }

            int panels = 0;
            foreach (var item in others)
            {
                if (PanelFor(item.Kind, item.RefId, item.Ref, item.Title, item.Zoom) is { } panel)
                {
                    await Cells[item.CellIndex].ShowPanelAsync(panel);
                    panels++;
                }
                else missing++;
            }

            ActiveViewName = view.Name;
            string shown = panels > 0 ? $"{targets.Count} cámara(s) y {panels} cuadro(s) más" : $"{targets.Count} cámara(s)";
            StatusMessage = missing == 0
                ? $"Tablero \"{view.Name}\": {shown} en pantalla."
                : $"Tablero \"{view.Name}\": {shown} en pantalla; " +
                  $"{missing} cuadro(s) quedaron libres (ya no existen o no están a su alcance).";
        }
        catch (Exception ex)
        {
            // Abrir video toca driver, GPU y red: una falla ahí no puede
            // llevarse la aplicación (el clic de la lista es async void).
            StatusMessage = $"No se pudo cargar todo el tablero \"{view.Name}\": {ex.Message}";
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
        AppMessageBox.Show(Application.Current.MainWindow, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}

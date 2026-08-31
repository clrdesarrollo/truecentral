using System.Windows;
using System.Windows.Controls;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Layouts guardados de un muro: foto del estado (división de cada monitor y
/// cámaras por posición) que se aplica de una vez, para turnos o rondas.
/// </summary>
public partial class WallLayoutsWindow : Window
{
    private readonly ApiClient _api;
    private readonly WallDto _wall;
    /// <summary>Módulo que abrió la ventana (para bloquearlo al aplicar).</summary>
    private WallView? _wallView;

    private sealed record LayoutListItem(int Id, string Name, string Detail);

    public WallLayoutsWindow(ApiClient api, WallDto wall)
    {
        _api = api;
        _wall = wall;
        InitializeComponent();
        TitleText.Text = $"Layouts de \"{wall.Name}\"";
        Loaded += async (_, _) =>
        {
            _wallView = Owner is null ? null : FindWallView(Owner);
            await ReloadAsync();
        };
    }

    /// <summary>Busca el módulo del muro dentro de la ventana que abrió el diálogo.</summary>
    private static WallView? FindWallView(DependencyObject root)
    {
        if (root is WallView view) return view;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            if (FindWallView(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        return null;
    }

    private async Task ReloadAsync()
    {
        try
        {
            var layouts = await _api.GetWallLayoutsAsync(_wall.Id);
            LayoutsList.ItemsSource = layouts
                .Select(l => new LayoutListItem(l.Id, l.Name,
                    $"{l.Items.Count} cámara(s) · guardado {l.CreatedAt.ToLocalTime():g}"))
                .ToList();
            StatusText.Text = layouts.Count == 0 ? "No hay layouts guardados." : "";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "Escriba un nombre para el layout.";
            return;
        }
        try
        {
            await _api.SaveWallLayoutAsync(_wall.Id, name);
            NameBox.Clear();
            await ReloadAsync();
            StatusText.Text = $"Layout \"{name}\" guardado.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id } button) return;

        string name = (button.DataContext as LayoutListItem)?.Name ?? "";
        try
        {
            StatusText.Text = "Aplicando layout…";
            // Bloquear esta ventana y la principal mientras se aplica: un
            // cambio del operador a medio aplicar dejaría el muro a medias.
            IsEnabled = false;
            // El módulo del muro se bloquea mientras tanto: un layout a medio
            // aplicar dejaría el muro a medias si el operador toca algo.
            if (_wallView is not null)
                await _wallView.RunBlockingAsync($"Aplicando layout «{name}»…",
                    () => _api.ApplyWallLayoutAsync(_wall.Id, id));
            else
                await _api.ApplyWallLayoutAsync(_wall.Id, id);
            StatusText.Text = "Layout aplicado.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id } button) return;

        string name = (button.DataContext as LayoutListItem)?.Name ?? "seleccionado";
        if (MessageBox.Show(this,
                $"¿Eliminar el layout \"{name}\"? Esta acción no se puede deshacer.",
                "Eliminar layout", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            await _api.DeleteWallLayoutAsync(_wall.Id, id);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }
}

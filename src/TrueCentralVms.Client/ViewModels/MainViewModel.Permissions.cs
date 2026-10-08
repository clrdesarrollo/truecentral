using System.Windows;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Domain;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Módulos según los roles del usuario: el riel y el inicio muestran solo los
/// que sus permisos admiten. Si un administrador le cambia los roles con la
/// sesión abierta, el servidor avisa ("permissions") y los módulos que ya no le
/// corresponden se cierran.
/// </summary>
public partial class MainViewModel
{
    private static PermissionScope Perms => PermissionScope.Current;

    public bool CanLiveView => Perms.Has(Permissions.LiveView);
    public bool CanPlayback => Perms.Has(Permissions.PlaybackView);
    public bool CanWall => Perms.Has(Permissions.WallOperate);
    public bool CanAlarms => Perms.Has(Permissions.AlarmsMonitor);
    public bool CanCerco => Perms.Has(Permissions.CercoMonitor);
    public bool CanIntercom => Perms.Has(Permissions.IntercomAnswer);
    public bool CanEvents => Perms.Has(Permissions.EventsAttend);
    public bool CanLpr => Perms.Has(Permissions.AnprView);
    public bool CanDownloads => Perms.Has(Permissions.PlaybackExport);

    private static readonly string[] ModuleFlags =
    [
        nameof(CanLiveView), nameof(CanPlayback), nameof(CanWall), nameof(CanAlarms), nameof(CanCerco),
        nameof(CanIntercom), nameof(CanEvents), nameof(CanLpr), nameof(CanDownloads), nameof(RoleLabel),
    ];

    /// <summary>Relee los permisos de la sesión (al entrar y cuando el servidor avisa que cambiaron).</summary>
    private async Task LoadPermissionsAsync()
    {
        if (await _api.GetMyPermissionsAsync() is { } permissions) Perms.Update(permissions);
    }

    /// <summary>Cambiaron los permisos: se rehacen riel e inicio y se cierra lo que ya no le corresponde.</summary>
    private void OnPermissionsChanged() => Application.Current.Dispatcher.InvokeAsync(() =>
    {
        foreach (var name in ModuleFlags) OnPropertyChanged(name);
        if (IsLiveViewOpen && !CanLiveView) CloseLiveViewCommand.Execute(null);
        if (IsPlaybackOpen && !CanPlayback) ClosePlaybackCommand.Execute(null);
        if (IsWallOpen && !CanWall) CloseWallCommand.Execute(null);
        if (IsAlarmsOpen && !CanAlarms) CloseAlarmsCommand.Execute(null);
        if (IsCercoOpen && !CanCerco) CloseCercoCommand.Execute(null);
        if (IsIntercomOpen && !CanIntercom) CloseIntercomCommand.Execute(null);
        if (IsEventsOpen && !CanEvents) CloseEventsCommand.Execute(null);
        if (IsLprOpen && !CanLpr) CloseLprCommand.Execute(null);
    });
}

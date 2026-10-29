using System.Windows;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Módulos según los roles del usuario y la licencia del servidor: el riel y el
/// inicio muestran solo los que sus permisos admiten y la licencia incluye. Si
/// un administrador le cambia los roles con la sesión abierta, o cambia la
/// licencia, el servidor avisa ("permissions" / "license") y los módulos que ya
/// no corresponden se cierran.
/// </summary>
public partial class MainViewModel
{
    private static PermissionScope Perms => PermissionScope.Current;

    public bool CanLiveView => Perms.Has(Permissions.LiveView) && Perms.IsLicensed(LicenseFeatures.ModuleVideo);
    public bool CanPlayback => Perms.Has(Permissions.PlaybackView) && Perms.IsLicensed(LicenseFeatures.ModulePlayback);
    public bool CanWall => Perms.Has(Permissions.WallOperate) && Perms.IsLicensed(LicenseFeatures.ModuleVideowall);
    public bool CanAlarms => Perms.Has(Permissions.AlarmsMonitor) && Perms.IsLicensed(LicenseFeatures.ModuleAlarms);
    public bool CanCerco => Perms.Has(Permissions.CercoMonitor) && Perms.IsLicensed(LicenseFeatures.ModuleFence);
    public bool CanIntercom => Perms.Has(Permissions.IntercomAnswer) && Perms.IsLicensed(LicenseFeatures.ModuleIntercom);
    public bool CanEvents => Perms.Has(Permissions.EventsAttend);
    public bool CanLpr => Perms.Has(Permissions.AnprView) && Perms.IsLicensed(LicenseFeatures.ModuleAnpr);
    public bool CanFleet => Perms.Has(Permissions.FleetLive) && Perms.IsLicensed(LicenseFeatures.ModuleFleet);
    public bool CanFleetMap => Perms.Has(Permissions.FleetView) && Perms.IsLicensed(LicenseFeatures.ModuleFleet);
    public bool CanDownloads => Perms.Has(Permissions.PlaybackExport) && Perms.IsLicensed(LicenseFeatures.ModulePlayback);

    // Acciones dentro de los módulos (además, el recurso tiene que estar en su alcance).
    public bool CanPtz => Perms.Has(Permissions.LivePtz);
    public bool CanSaveViews => Perms.Has(Permissions.LiveViewsManage);
    /// <summary>Guarda y cambia los tableros compartidos (los roles se eligen en el panel web).</summary>
    public bool CanShareViews => Perms.Has(Permissions.DashboardsManage);
    /// <summary>Puede escribir una dirección web cualquiera en un cuadro (no solo las de la lista).</summary>
    public bool CanOpenAnyUrl => Perms.Has(Permissions.DashboardsWebUrl);

    private static readonly string[] ModuleFlags =
    [
        nameof(CanLiveView), nameof(CanPlayback), nameof(CanWall), nameof(CanAlarms), nameof(CanCerco),
        nameof(CanIntercom), nameof(CanEvents), nameof(CanLpr), nameof(CanFleet), nameof(CanFleetMap), nameof(CanDownloads), nameof(RoleLabel),
        nameof(CanPtz), nameof(CanSaveViews), nameof(CanShareViews), nameof(CanOpenAnyUrl),
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
        // Lo que se ofrece dentro de la Vista en vivo (PTZ, órdenes por ubicación, vistas).
        OnOperableChanged();
        if (IsViewsPopupOpen) _ = LoadSavedViewsAsync();
        if (IsLiveViewOpen && !CanLiveView) CloseLiveViewCommand.Execute(null);
        if (IsPlaybackOpen && !CanPlayback) ClosePlaybackCommand.Execute(null);
        if (IsWallOpen && !CanWall) CloseWallCommand.Execute(null);
        if (IsAlarmsOpen && !CanAlarms) CloseAlarmsCommand.Execute(null);
        if (IsCercoOpen && !CanCerco) CloseCercoCommand.Execute(null);
        if (IsIntercomOpen && !CanIntercom) CloseIntercomCommand.Execute(null);
        if (IsEventsOpen && !CanEvents) CloseEventsCommand.Execute(null);
        if (IsLprOpen && !CanLpr) CloseLprCommand.Execute(null);
        if (!CanFleet) _fleetWindow?.Close();
        if (!CanFleetMap) _fleetMapWindow?.Close();
    });
}

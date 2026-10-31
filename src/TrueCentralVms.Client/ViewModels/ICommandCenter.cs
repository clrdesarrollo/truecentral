using System.Collections.ObjectModel;
using System.Windows;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Una grilla del Centro de mando: la de la ventana principal
/// (<see cref="MainViewModel"/>) o la de otro Centro de mando abierto con «+»
/// (<see cref="AuxScreenViewModel"/>, cada uno en su ventana). La misma
/// <see cref="Views.LiveView"/> trabaja con cualquiera: los enlaces del XAML
/// van por nombre y el código de la vista usa esta interfaz. Los tableros
/// también se cargan y guardan sobre la grilla que los pide.
/// </summary>
public interface ICommandCenter
{
    ObservableCollection<VideoCellViewModel> Cells { get; }
    VideoLayout CurrentLayout { get; }
    VideoCellViewModel? SelectedCell { get; set; }
    VideoCellViewModel? DropTargetCell { get; set; }
    bool IsMergeMode { get; set; }
    bool IsDigitalZoomMode { get; set; }
    bool IsGridFullscreen { get; set; }
    bool IsBulkOpening { get; set; }
    string BulkOpeningText { get; set; }
    string StatusMessage { get; set; }
    bool IsVerticalLayouts { get; set; }
    bool LayoutOrientationUnset { get; }

    /// <summary>Ventana donde está la grilla (los avisos de apertura masiva salen ahí).</summary>
    Window? LiveWindow { get; set; }

    /// <summary>Parlantes IP: el mismo panel en todas las grillas.</summary>
    SpeakersViewModel Speakers { get; }

    // PTZ del cuadro seleccionado de ESTA grilla.
    bool IsPtzAvailable { get; }
    bool IsPrecisionMode { get; set; }
    Task PtzAsync(PtzCommand command, bool stop, int? speed = null);
    Task PtzPresetAsync(PtzPresetAction action);

    // Tableros (el desplegable de cada grilla).
    bool IsViewsPopupOpen { get; set; }
    string NewViewName { get; set; }
    bool NewViewShared { get; set; }
    string SavedViewsStatus { get; set; }
    string ActiveViewName { get; set; }

    Task ApplyLayoutAsync(VideoLayout layout, IReadOnlyList<int>? placement = null);
    string? GrowBlockedReason(GridEdge edge);
    Task GrowLayoutAsync(GridEdge edge, int slot, ChannelNode node);
    void PreviewMerge(VideoCellViewModel from, VideoCellViewModel? to);
    Task MergeCellsAsync(VideoCellViewModel from, VideoCellViewModel to);
    Task SplitCellAsync(VideoCellViewModel cell);
    Task SubdivideCellAsync(VideoCellViewModel cell, int across, int down);
    void ToggleMaximize(VideoCellViewModel cell);
    void SwapCells(VideoCellViewModel source, VideoCellViewModel target);
    Task OpenChannelAsync(ChannelNode node);
    Task OpenChannelInCellAsync(ChannelNode node, VideoCellViewModel cell);
    Task OpenDeviceAsync(DeviceNode device);
    Task OpenLocationAsync(LocationNode location);
    Task OpenResourceAsync(ResourceNode node);
    Task OpenResourceInCellAsync(ResourceNode node, VideoCellViewModel cell);
    Task RunLocationCommandAsync(LocationNode node, string command);
    /// <summary>«Vigilar esta ubicación»: un Cuadro vigía filtrado a ella en el cuadro seleccionado.</summary>
    Task WatchLocationAsync(LocationNode location);
}

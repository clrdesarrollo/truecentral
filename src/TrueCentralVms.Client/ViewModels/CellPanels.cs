using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Contenido de un cuadro del Centro de mando que no es video: una puerta, un
/// panel de alarma, un cerco, una tarjeta de reporte, una página web… El
/// cuadro (<see cref="VideoCellViewModel"/>) es el mismo de siempre: lo que
/// cambia es lo que muestra, así que mover, intercambiar, unir y ampliar la
/// grilla funcionan igual con cualquier contenido.
///
/// Cada tipo tiene su DataTemplate en el XAML del cuadro. El panel vive
/// mientras está en un cuadro: al cerrarlo o liberarse el cuadro se llama a
/// <see cref="Dispose"/>, que debe soltar sus suscripciones al hub y sus
/// temporizadores.
/// </summary>
public abstract partial class CellPanel : ObservableObject, IDisposable
{
    /// <summary>Título de la barra del cuadro (el nombre del recurso).</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>Estado breve a la derecha de la barra (como «Conectando…» en las cámaras).</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>Glifo (Segoe MDL2 Assets) que identifica el tipo en la barra y en la lista.</summary>
    public abstract string Glyph { get; }

    /// <summary>Clave del tipo para las vistas guardadas («door», «alarm-panel»…).</summary>
    public abstract string Kind { get; }

    /// <summary>Id del recurso en el servidor (0 si el tipo no tiene uno, como una tarjeta).</summary>
    public virtual int ResourceId => 0;

    /// <summary>Qué es, en el formato de los tableros y de la última sesión (lo fija
    /// quien crea el panel); null = no se guarda.</summary>
    public CellContentRef? Content { get; set; }

    /// <summary>
    /// El panel convive con el video del cuadro (el Cuadro vigía: una franja
    /// arriba y la cámara del evento abajo). Los demás tapan el cuadro entero.
    /// </summary>
    public virtual bool KeepsVideo => false;

    /// <summary>
    /// El cuadro donde quedó el panel. Se llama también al intercambiar o mover
    /// cuadros: el panel viaja con su contenido.
    /// </summary>
    public virtual void AttachTo(VideoCellViewModel cell) { }

    /// <summary>Ese cuadro soltó el panel (si en el intercambio ya lo tomó otro, no cambia nada).</summary>
    public virtual void DetachFrom(VideoCellViewModel cell) { }

    /// <summary>Carga el estado inicial (lo llama el cuadro al recibir el panel).</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>El cuadro muestra el engranaje en su barra (la tarjeta tiene opciones).</summary>
    public virtual bool CanConfigure => false;

    /// <summary>Qué se configura con el engranaje (su ayuda emergente).</summary>
    public virtual string ConfigureHint => "Configurar";

    /// <summary>El engranaje de la barra del cuadro: lo que guarde va en <see cref="Content"/>
    /// (tableros y última sesión).</summary>
    [RelayCommand]
    protected virtual Task ConfigureAsync() => Task.CompletedTask;

    public virtual void Dispose() { }
}

/// <summary>
/// Lo que hay en un cuadro que no es cámara, para guardarlo en un tablero o en
/// la última sesión y volver a armarlo: un recurso (tipo en Ref, id en RefId),
/// una tarjeta (clave en Ref) o una página web (de la lista: RefId; libre: la
/// dirección en Ref). Mismo formato que <c>LiveViewItemDto</c>.
/// </summary>
/// <param name="Zoom">Zoom de una página web en porcentaje (null = 100 %).</param>
public sealed record CellContentRef(string Kind, int? RefId = null, string? Ref = null, string? Title = null,
    int? Zoom = null);

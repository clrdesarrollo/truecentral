using System.Windows;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Los eventos en vivo del puesto (uno por cliente): los recibe del hub, los
/// deduplica por Id (un repetido llega con el mismo Id y el contador subido) y
/// los entrega en el hilo de la interfaz a los Cuadros vigía.
///
/// Al conectar o reconectar pide los recientes al servidor solo para tenerlos
/// en la lista (lo que pasó mientras el puesto no estaba no salta a los vigías
/// como si fuera nuevo).
/// </summary>
public sealed class LiveEventHub
{
    private const int Capacity = 300;

    private readonly ApiClient _api;
    private readonly List<LiveEventDto> _recent = [];

    /// <summary>Llegó un evento (o se repitió uno ya visto: <c>isRepeat</c>), en el hilo de la interfaz.</summary>
    public event Action<LiveEventDto, bool>? Arrived;

    public LiveEventHub(ApiClient api, VmsHubClient hub)
    {
        _api = api;
        hub.LiveEventReceived += dto => Application.Current?.Dispatcher.InvokeAsync(() => Add(dto, notify: true));
        hub.ConnectionStateChanged += connected =>
        {
            if (connected) Application.Current?.Dispatcher.InvokeAsync(() => _ = CatchUpAsync());
        };
    }

    /// <summary>Los últimos eventos recibidos, el más reciente primero.</summary>
    public IReadOnlyList<LiveEventDto> Recent => _recent;

    private void Add(LiveEventDto dto, bool notify)
    {
        int index = _recent.FindIndex(e => e.Id == dto.Id);
        bool repeat = index >= 0;
        if (repeat) _recent.RemoveAt(index);
        _recent.Insert(0, dto);
        if (_recent.Count > Capacity) _recent.RemoveRange(Capacity, _recent.Count - Capacity);
        if (notify) Arrived?.Invoke(dto, repeat);
    }

    private async Task CatchUpAsync()
    {
        try
        {
            var list = await _api.GetRecentLiveEventsAsync();
            // Del más viejo al más nuevo, sin avisar: solo completan la lista.
            for (int i = list.Count - 1; i >= 0; i--)
                if (_recent.All(e => e.Id != list[i].Id)) Add(list[i], notify: false);
            // Lo que llegó por el hub mientras tanto y lo recuperado, en orden de hora.
            _recent.Sort((a, b) => b.At.CompareTo(a.At));
        }
        catch (ApiException) { /* servidor anterior al vigía: solo lo que llegue por el hub */ }
    }
}

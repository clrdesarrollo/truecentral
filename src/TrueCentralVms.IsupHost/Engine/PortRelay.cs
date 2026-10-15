using System.Net;
using System.Net.Sockets;

namespace TrueCentralVms.IsupHost.Engine;

/// <summary>
/// Reenvío TCP de un puerto público a otro local. El SDK ISUP admite un solo puerto de registro (un segundo NET_ECMS_StartListen
/// devuelve el error 46, «máximo alcanzado»), pero los paneles que se registraban en la receptora de Hikvision apuntan al 7661: con
/// esto llegan igual al 7660. En ISUP 5.0 el equipo pregunta después a qué servidor registrarse (DAS) y se le contesta el 7660, así
/// que en la práctica solo el primer contacto pasa por aquí.
/// </summary>
public sealed class PortRelay : IDisposable
{
    readonly TcpListener _listener;
    readonly int _target;
    readonly CancellationTokenSource _cts = new();

    public PortRelay(int port, int target)
    {
        _target = target;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Relay(client));
        }
    }

    async Task Relay(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient())
        {
            try
            {
                client.NoDelay = true;
                await upstream.ConnectAsync(IPAddress.Loopback, _target, _cts.Token);
                upstream.NoDelay = true;
                var a = client.GetStream();
                var b = upstream.GetStream();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                var t1 = a.CopyToAsync(b, cts.Token);
                var t2 = b.CopyToAsync(a, cts.Token);
                await Task.WhenAny(t1, t2);
                cts.Cancel();
            }
            catch (Exception) { /* el equipo o el SDK cerraron: fin del reenvío */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
    }
}

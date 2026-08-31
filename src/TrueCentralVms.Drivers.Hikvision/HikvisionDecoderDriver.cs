using System.Runtime.InteropServices;
using System.Text;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Driver de decodificadores Hikvision sobre HCNetSDK. Soporta dos familias:
///
/// 1. Decoders clásicos (DS-6400HDI, etc.): división de ventanas por salida con
///    NET_DVR_MatrixSet/GetDisplayCfg_V41 y decodificación dinámica por canal.
///
/// 2. Familia video wall (DS-6900UDI, controladores de muro): las salidas se
///    ubican en un muro virtual y cada pane es una VENTANA del muro creada con
///    NET_DVR_SET_VIDEOWALLWINDOWPOSITION; la decodificación dinámica apunta al
///    número de ventana ((muro &lt;&lt; 24) | ventana). El modo se detecta
///    automáticamente consultando NET_DVR_GET_VIDEOWALLDISPLAYNO.
///
/// Las llamadas al SDK son bloqueantes, por eso se envuelven en Task.Run.
/// </summary>
public sealed class HikvisionDecoderDriver : IDecoderDriver, IDecoderDiagnostics
{
    public const string Key = "hikvision-netsdk";

    private int _userId = -1;
    private string? _serialNumber;

    // Contexto de modo video wall (se detecta una vez por sesión).
    private bool? _useWallApi;
    private uint _wallNoHigh = 0x01000000;
    private List<uint> _wallDisplayNos = new();
    private DecoderCapabilities? _capsCache;

    // Geometría del muro reportada por WALL_ABILITY: tamaño de celda por
    // pantalla (windowBaseX/Y) y alineación de ventanas. El DS-6908UDI usa
    // celdas de 1920x1920; otros firmwares pueden variar.
    private uint _cellWidth = 1920;
    private uint _cellHeight = 1920;
    private uint _windowAlignX = 16;
    private uint _windowAlignY = 4;
    private uint _maxWindows = 64; // winNum max reportado por WALL_ABILITY

    // Mapa canal del servidor → número de ventana real del equipo. Se construye
    // al sincronizar y se reconstruye desde el equipo si la sesión es nueva.
    private readonly Dictionary<int, uint> _channelTargets = new();

    // Rectángulos por canal para la pantalla completa instantánea:
    // "home" = sub-celda del mosaico; "cell" = pantalla completa.
    private readonly Dictionary<int, WallSdk.NET_DVR_RECTCFG_EX> _channelHomeRect = new();
    private readonly Dictionary<int, WallSdk.NET_DVR_RECTCFG_EX> _channelCellRect = new();

    // Canales cuya ventana está AGRANDADA (pantalla completa). Su rect real no
    // es su "home", así que el diff del mosaico no puede compararlos contra
    // _channelHomeRect: hay que devolverlos a su sub-celda explícitamente.
    private readonly HashSet<int> _zoomedChannels = new();

    // Canales de las ventanas flotantes en orden de pila (la última arriba):
    // tras cada sincronización se re-aplica SWITCH_WIN_TOP para mantenerlas
    // encima de las ventanas del mosaico.
    private List<int> _floatChannels = new();

    // Modos de división de ventana que declara el muro (WALL_ABILITY windowMode).
    private List<int> _wallWindowModes = new();

    public bool IsConnected => _userId >= 0;

    public Task ConnectAsync(DecoderConnectionInfo info, CancellationToken ct = default) => Task.Run(() =>
    {
        HikvisionSdk.EnsureInitialized();
        if (_userId >= 0) return;

        var deviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();
        int userId = CHCNetSDK.NET_DVR_Login_V30(info.Host, info.Port, info.Username, info.Password, ref deviceInfo);
        if (userId < 0)
            throw HikvisionException.FromLastError($"NET_DVR_Login_V30 ({info.Host}:{info.Port})");

        _userId = userId;
        _useWallApi = null;
        _capsCache = null;
        _serialNumber = deviceInfo.sSerialNumber is null
            ? null
            : Encoding.ASCII.GetString(deviceInfo.sSerialNumber).TrimEnd('\0');
    }, ct);

    public Task DisconnectAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        if (_userId < 0) return;
        CHCNetSDK.NET_DVR_Logout(_userId);
        _userId = -1;
        _useWallApi = null;
        _capsCache = null;
    }, ct);

    public Task<DecoderCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) =>
        Task.Run(GetCapabilitiesCore, ct);

    private DecoderCapabilities GetCapabilitiesCore()
    {
        EnsureConnected();
        if (_capsCache is not null) return _capsCache;

        var ability = new CHCNetSDK.NET_DVR_MATRIX_ABILITY_V41();
        int size = Marshal.SizeOf(ability);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(ability, buffer, false);
            if (!CHCNetSDK.NET_DVR_GetDeviceAbility(
                    _userId, CHCNetSDK.MATRIXDECODER_ABILITY_V41, IntPtr.Zero, 0, buffer, (uint)size))
                throw HikvisionException.FromLastError("NET_DVR_GetDeviceAbility(MATRIXDECODER_ABILITY_V41)");

            ability = Marshal.PtrToStructure<CHCNetSDK.NET_DVR_MATRIX_ABILITY_V41>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        var displays = new List<DisplayOutputInfo>();
        AddDisplays(displays, DisplayOutputType.Vga, ability.struVgaInfo, ability);
        AddDisplays(displays, DisplayOutputType.Bnc, ability.struBncInfo, ability);
        AddDisplays(displays, DisplayOutputType.Hdmi, ability.struHdmiInfo, ability);
        AddDisplays(displays, DisplayOutputType.Dvi, ability.struDviInfo, ability);

        // En la familia video wall la división por pantalla la define el muro
        // (WALL_ABILITY windowMode, ej. hasta 36), no el modo de salida clásico.
        if (UseWallApi() && _wallWindowModes.Count > 0)
            displays = displays.Select(d => d with { WindowModes = _wallWindowModes }).ToList();

        _capsCache = new DecoderCapabilities
        {
            DecodeChannelStart = ability.byStartChan,
            DecodeChannelCount = ability.byDecChanNums,
            Displays = displays,
            SerialNumber = _serialNumber,
        };
        return _capsCache;
    }

    public Task StartDecodingAsync(int decodeChannel, StreamSource source, CancellationToken ct = default) => Task.Run(() =>
    {
        EnsureConnected();

        var cfg = new CHCNetSDK.NET_DVR_PU_STREAM_CFG_V41
        {
            byRes1 = new byte[3],
            byRes2 = new byte[64],
        };
        cfg.dwSize = (uint)Marshal.SizeOf(cfg);

        cfg.uDecStreamMode = source.Mode switch
        {
            StreamSourceMode.DeviceChannel => BuildDeviceChannelUnion(source, out cfg.byStreamMode),
            StreamSourceMode.Url => BuildUrlUnion(source, out cfg.byStreamMode),
            _ => throw new NotSupportedException($"Modo de stream no soportado: {source.Mode}"),
        };

        uint target = ToDeviceChannel(decodeChannel);
        if (!CHCNetSDK.NET_DVR_MatrixStartDynamic_V41(_userId, target, ref cfg))
            throw HikvisionException.FromLastError($"NET_DVR_MatrixStartDynamic_V41 (destino 0x{target:X})");
    }, ct);

    public Task StopDecodingAsync(int decodeChannel, CancellationToken ct = default) => Task.Run(() =>
    {
        EnsureConnected();
        uint target = ToDeviceChannel(decodeChannel);
        if (!CHCNetSDK.NET_DVR_MatrixStopDynamic(_userId, target))
            throw HikvisionException.FromLastError($"NET_DVR_MatrixStopDynamic (destino 0x{target:X})");
    }, ct);

    public Task<IReadOnlyDictionary<int, string?>> ConfigureWallAsync(
        IReadOnlyList<ScreenWindowLayout> screens,
        IReadOnlyList<FloatingWindowLayout>? floating = null, CancellationToken ct = default) => Task.Run(() =>
    {
        EnsureConnected();

        if (!UseWallApi())
        {
            // Decoders clásicos: división por salida (MatrixDisplayCfg). Sin
            // ventanas flotantes: requieren la API de video wall.
            var legacyResults = new Dictionary<int, string?>();
            foreach (var screen in screens)
            {
                try
                {
                    ConfigureLegacyWindows(screen.DisplayChannel, screen.WindowDecodeChannels);
                    legacyResults[screen.DisplayChannel] = null;
                }
                catch (Exception ex) { legacyResults[screen.DisplayChannel] = ex.Message; }
            }
            return (IReadOnlyDictionary<int, string?>)legacyResults;
        }

        return ConfigureWallMosaic(screens, floating ?? Array.Empty<FloatingWindowLayout>());
    }, ct);

    /// <summary>
    /// Modelo de mosaico (el mismo que usa HikCentral/iVMS): cada cámara ocupa
    /// UNA ventana individual del muro, en su sub-celda. Escala hasta el máximo
    /// de ventanas del equipo (winNum, típicamente 64) sin los límites de la
    /// división interna de una sola ventana. Los números de ventana los asigna
    /// el equipo; se redescubren por rectángulo tras abrirlas.
    /// </summary>
    public void InvalidateWallCache()
    {
        _channelTargets.Clear();
        _channelHomeRect.Clear();
        _channelCellRect.Clear();
        _zoomedChannels.Clear();
        _floatChannels.Clear();
    }

    /// <summary>Celda geométrica de una pantalla del muro (grilla anclada en el origen).</summary>
    private WallSdk.NET_DVR_RECTCFG_EX CellRectOf(ScreenWindowLayout screen) => new()
    {
        dwXCoordinate = (uint)screen.Col * _cellWidth,
        dwYCoordinate = (uint)screen.Row * _cellHeight,
        dwWidth = _cellWidth,
        dwHeight = _cellHeight,
        byRes = new byte[4],
    };

    private uint AlignedX(uint v) => v / _windowAlignX * _windowAlignX;
    private uint AlignedY(uint v) => v / _windowAlignY * _windowAlignY;

    /// <summary>
    /// Rect de una ventana flotante: sus unidades de celda (1.0 = un monitor)
    /// se traducen a píxeles del muro con la alineación que exige el firmware.
    /// </summary>
    private WallSdk.NET_DVR_RECTCFG_EX FloatRectOf(FloatingWindowLayout f)
    {
        uint x0 = AlignedX((uint)Math.Max(0, Math.Round(f.X * _cellWidth)));
        uint y0 = AlignedY((uint)Math.Max(0, Math.Round(f.Y * _cellHeight)));
        uint x1 = AlignedX((uint)Math.Max(0, Math.Round((f.X + f.W) * _cellWidth)));
        uint y1 = AlignedY((uint)Math.Max(0, Math.Round((f.Y + f.H) * _cellHeight)));
        return new WallSdk.NET_DVR_RECTCFG_EX
        {
            dwXCoordinate = x0,
            dwYCoordinate = y0,
            dwWidth = Math.Max(_windowAlignX * 4, x1 - x0),
            dwHeight = Math.Max(_windowAlignY * 4, y1 - y0),
            byRes = new byte[4],
        };
    }

    /// <summary>
    /// Re-aplica la capa superior a las ventanas flotantes (en orden de pila).
    /// Va después de cada sincronización: las ventanas del mosaico abiertas más
    /// tarde nacerían con una capa mayor y las taparían.
    /// </summary>
    private void TopFloatingWindows()
    {
        foreach (int channel in _floatChannels)
            if (_channelTargets.TryGetValue(channel, out uint windowNo))
                BringWindowToTop(windowNo);
    }

    /// <summary>
    /// Camino rápido del mosaico: cuando la sesión ya tiene el mapa
    /// canal→ventana, se aplica un DIFF en memoria — cerrar canales
    /// eliminados, mover por roaming los rects que cambiaron y abrir solo lo
    /// nuevo — sin re-escanear las 64 ventanas del equipo (que es lo que hace
    /// lento agrupar/cambiar layout). Devuelve null si no es aplicable o algo
    /// no cuadra: en ese caso se sigue el camino completo con verificación.
    /// </summary>
    private Dictionary<int, string?>? TryApplyMosaicDiff(
        IReadOnlyList<ScreenWindowLayout> screens, IReadOnlyList<FloatingWindowLayout> floating)
    {
        if (_channelTargets.Count == 0) return null;

        var tiles = new List<(int Channel, int DisplayChannel, WallSdk.NET_DVR_RECTCFG_EX Rect, WallSdk.NET_DVR_RECTCFG_EX Cell)>();
        foreach (var screen in screens)
        {
            var cell = CellRectOf(screen);
            foreach (var (channel, rect) in TileCell(cell, screen))
                tiles.Add((channel, screen.DisplayChannel, rect, cell));
        }
        // Las flotantes son ventanas normales del muro con rect libre; su
        // "pantalla completa" es su propio rect (el zoom no les aplica).
        foreach (var f in floating)
        {
            var rect = FloatRectOf(f);
            tiles.Add((f.DecodeChannel, 0, rect, rect));
        }
        if (tiles.Count > _maxWindows) return null; // que el camino completo dé su mensaje de presupuesto

        // Aplicable solo si todo canal ya existente tiene rect conocido.
        foreach (var t in tiles)
            if (_channelTargets.ContainsKey(t.Channel) && !_channelHomeRect.ContainsKey(t.Channel))
                return null;

        try
        {
            // 1) Canales que desaparecieron → cerrar su ventana.
            var currentChannels = tiles.Select(t => t.Channel).ToHashSet();
            foreach (int channel in _channelTargets.Keys.Where(c => !currentChannels.Contains(c)).ToList())
            {
                CloseSingleWindow(_channelTargets[channel]);
                _channelTargets.Remove(channel);
                _channelHomeRect.Remove(channel);
                _channelCellRect.Remove(channel);
                _zoomedChannels.Remove(channel);
            }

            // 2) Canales existentes cuyo rect cambió → recolocar por roaming
            //    (su decodificación no se corta).
            foreach (var t in tiles)
            {
                if (!_channelTargets.TryGetValue(t.Channel, out uint windowNo)) continue;
                // Una ventana en pantalla completa NO está en su sub-celda aunque
                // su "home" no haya cambiado: comparar contra _channelHomeRect la
                // daría por buena y quedaría tapando el monitor entero. El push
                // del mosaico devuelve todo a su sub-celda; quien tenga pantalla
                // completa vigente la re-aplica después (ver WallService).
                if (_zoomedChannels.Remove(t.Channel) || !RectEquals(_channelHomeRect[t.Channel], t.Rect))
                    MoveWindow(windowNo, t.Rect); // si falla, cae al camino completo
                _channelHomeRect[t.Channel] = t.Rect;
                _channelCellRect[t.Channel] = t.Cell;
            }

            // 3) Canales nuevos → abrir y descubrir su número escaneando SOLO
            //    los números de ventana que no tenemos mapeados.
            var missing = tiles.Where(t => !_channelTargets.ContainsKey(t.Channel)).ToList();
            if (missing.Count > 0)
            {
                foreach (var tile in missing) OpenSingleWindow(tile.Rect);
                Thread.Sleep(500); // el equipo materializa las ventanas de forma asíncrona

                var knownNos = _channelTargets.Values.ToHashSet();
                var pendingByKey = missing.ToDictionary(t => RectKey(t.Rect), t => t);
                for (uint i = 1; i <= _maxWindows && pendingByKey.Count > 0; i++)
                {
                    uint windowNo = _wallNoHigh | i;
                    if (knownNos.Contains(windowNo)) continue;
                    var win = TryGetWindow(windowNo);
                    if (win is not { byEnable: 1 } w) continue;
                    if (pendingByKey.Remove(RectKey(w.struRect), out var tile))
                    {
                        _channelTargets[tile.Channel] = windowNo;
                        _channelHomeRect[tile.Channel] = tile.Rect;
                        _channelCellRect[tile.Channel] = tile.Cell;
                    }
                }
                if (pendingByKey.Count > 0) return null; // no aparecieron todas: verificación completa
            }

            var results = new Dictionary<int, string?>();
            foreach (var screen in screens) results[screen.DisplayChannel] = null;
            return results;
        }
        catch
        {
            // Cualquier sorpresa (ventana borrada por otra herramienta, error
            // del equipo) → el camino completo reconcilia contra el estado real.
            return null;
        }
    }

    private IReadOnlyDictionary<int, string?> ConfigureWallMosaic(
        IReadOnlyList<ScreenWindowLayout> screens, IReadOnlyList<FloatingWindowLayout> floating)
    {
        _floatChannels = floating.Select(f => f.DecodeChannel).ToList();

        // Camino rápido: diff en memoria sin re-escanear el equipo.
        if (TryApplyMosaicDiff(screens, floating) is { } fastResults)
        {
            TopFloatingWindows();
            return fastResults;
        }

        var results = new Dictionary<int, string?>();

        // 1) Vincular las salidas físicas a sus celdas del muro.
        var (boundRects, prepErrors, _) = PrepareWallLayout(screens);
        foreach (var (channel, error) in prepErrors)
            results[channel] = error;

        // 2) Calcular los rectángulos de todas las ventanas (una por cámara) y
        //    memorizar por canal su sub-celda (home) y su celda completa (para
        //    la pantalla completa instantánea).
        var tiles = new List<(int Channel, int DisplayChannel, WallSdk.NET_DVR_RECTCFG_EX Rect)>();
        _channelHomeRect.Clear();
        _channelCellRect.Clear();
        foreach (var screen in screens)
        {
            if (results.ContainsKey(screen.DisplayChannel)) continue; // salida con error de vinculación
            if (!boundRects.TryGetValue(screen.DisplayChannel, out var cell))
            {
                results[screen.DisplayChannel] = "La salida no quedó vinculada al muro.";
                continue;
            }
            foreach (var (channel, rect) in TileCell(cell, screen))
            {
                tiles.Add((channel, screen.DisplayChannel, rect));
                _channelHomeRect[channel] = rect;
                _channelCellRect[channel] = cell;
            }
        }
        foreach (var f in floating)
        {
            var rect = FloatRectOf(f);
            tiles.Add((f.DecodeChannel, 0, rect));
            _channelHomeRect[f.DecodeChannel] = rect;
            _channelCellRect[f.DecodeChannel] = rect;
        }

        // 3) Validar el presupuesto total de ventanas del equipo.
        if (tiles.Count > _maxWindows)
        {
            string budget = $"La configuración necesita {tiles.Count} ventanas y el decoder admite {_maxWindows} en total. Reduzca la división de algún monitor.";
            foreach (var screen in screens)
                results.TryAdd(screen.DisplayChannel, budget);
            return results;
        }

        // 4) Reutilizar/cerrar/abrir ventanas y reconstruir el mapa canal→ventana.
        var errorsByDisplay = ApplyMosaic(tiles);
        TopFloatingWindows();
        foreach (var screen in screens)
        {
            if (results.ContainsKey(screen.DisplayChannel)) continue;
            results[screen.DisplayChannel] = errorsByDisplay.TryGetValue(screen.DisplayChannel, out var e) ? e : null;
        }
        return results;
    }

    /// <summary>
    /// Divide una celda en sub-rectángulos alineados, uno por canal. Sin
    /// agrupación cada ventana ocupa una casilla en orden de lectura; con
    /// agrupación (Slots) cada ventana parte en su casilla y abarca
    /// SpanCols×SpanRows casillas de la grilla base (bordes compartidos con
    /// las casillas vecinas, así el mosaico calza exacto).
    /// </summary>
    private List<(int Channel, WallSdk.NET_DVR_RECTCFG_EX Rect)> TileCell(
        WallSdk.NET_DVR_RECTCFG_EX cell, ScreenWindowLayout screen)
    {
        var channels = screen.WindowDecodeChannels;
        int n = channels.Count;
        int baseCount = screen.Slots is not null && screen.BaseMode > 0 ? screen.BaseMode : n;
        int cols = (int)Math.Ceiling(Math.Sqrt(Math.Max(1, baseCount)));
        int rows = (int)Math.Ceiling(baseCount / (double)cols);

        uint AlignX(uint v) => v / _windowAlignX * _windowAlignX;
        uint AlignY(uint v) => v / _windowAlignY * _windowAlignY;
        uint EdgeX(int c) => AlignX(cell.dwXCoordinate + (uint)((ulong)cell.dwWidth * (uint)Math.Clamp(c, 0, cols) / (uint)cols));
        uint EdgeY(int r) => AlignY(cell.dwYCoordinate + (uint)((ulong)cell.dwHeight * (uint)Math.Clamp(r, 0, rows) / (uint)rows));

        var tiles = new List<(int, WallSdk.NET_DVR_RECTCFG_EX)>();
        for (int i = 0; i < n; i++)
        {
            int slot = screen.Slots is not null ? screen.Slots[i].SlotIndex : i;
            int spanCols = screen.Slots is not null ? Math.Max(1, screen.Slots[i].SpanCols) : 1;
            int spanRows = screen.Slots is not null ? Math.Max(1, screen.Slots[i].SpanRows) : 1;
            int col = slot % cols, row = slot / cols;

            uint x0 = EdgeX(col), x1 = EdgeX(col + spanCols);
            uint y0 = EdgeY(row), y1 = EdgeY(row + spanRows);
            tiles.Add((channels[i], new WallSdk.NET_DVR_RECTCFG_EX
            {
                dwXCoordinate = x0,
                dwYCoordinate = y0,
                dwWidth = x1 - x0,
                dwHeight = y1 - y0,
                byRes = new byte[4],
            }));
        }
        return tiles;
    }

    /// <summary>
    /// Aplica el mosaico: conserva las ventanas cuyo rectángulo ya coincide (no
    /// interrumpe su video), recoloca por roaming las que quedaron fuera de sitio
    /// dentro de su propia celda (p. ej. una ventana agrandada por pantalla
    /// completa vuelve a su sub-celda sin cortar su video), cierra las que
    /// sobran y abre las que faltan. Deja _channelTargets con el mapa
    /// canal→número de ventana real.
    /// </summary>
    private Dictionary<int, string> ApplyMosaic(
        List<(int Channel, int DisplayChannel, WallSdk.NET_DVR_RECTCFG_EX Rect)> tiles)
    {
        var errors = new Dictionary<int, string>();
        var desiredByKey = tiles.ToDictionary(t => RectKey(t.Rect), t => t);
        var ourRegions = tiles.Select(t => t.Rect).ToList();

        static bool Overlap(WallSdk.NET_DVR_RECTCFG_EX a, WallSdk.NET_DVR_RECTCFG_EX b) =>
            a.dwXCoordinate < b.dwXCoordinate + b.dwWidth && b.dwXCoordinate < a.dwXCoordinate + a.dwWidth &&
            a.dwYCoordinate < b.dwYCoordinate + b.dwHeight && b.dwYCoordinate < a.dwYCoordinate + a.dwHeight;

        static bool Contains(WallSdk.NET_DVR_RECTCFG_EX outer, WallSdk.NET_DVR_RECTCFG_EX inner) =>
            inner.dwXCoordinate >= outer.dwXCoordinate && inner.dwYCoordinate >= outer.dwYCoordinate &&
            inner.dwXCoordinate + inner.dwWidth <= outer.dwXCoordinate + outer.dwWidth &&
            inner.dwYCoordinate + inner.dwHeight <= outer.dwYCoordinate + outer.dwHeight;

        // Estado actual de las ventanas (1..maxWindows).
        var present = new Dictionary<string, uint>(); // rectKey → windowNo (ya correcto)
        var strays = new List<(uint WindowNo, WallSdk.NET_DVR_RECTCFG_EX Rect)>(); // rect incorrecto en nuestra región
        for (uint i = 1; i <= _maxWindows; i++)
        {
            uint windowNo = _wallNoHigh | i;
            var win = TryGetWindow(windowNo);
            if (win is not { byEnable: 1 } w) continue;
            string key = RectKey(w.struRect);
            if (desiredByKey.ContainsKey(key) && !present.ContainsKey(key))
                present[key] = windowNo;                       // reutilizar tal cual
            else if (ourRegions.Any(r => Overlap(r, w.struRect)))
                strays.Add((windowNo, w.struRect));            // fuera de sitio en nuestra región
        }

        var missing = tiles.Where(t => !present.ContainsKey(RectKey(t.Rect))).ToList();

        static bool SameOrigin(WallSdk.NET_DVR_RECTCFG_EX a, WallSdk.NET_DVR_RECTCFG_EX b) =>
            a.dwXCoordinate == b.dwXCoordinate && a.dwYCoordinate == b.dwYCoordinate;

        // Recolocar por roaming en vez de cerrar y abrir (la decodificación de
        // esa ventana no se interrumpe). Casos: la ventana agrandada de una
        // pantalla completa vuelve a su sub-celda (el hueco cae dentro de su
        // rect), y el ancla de una agrupación crece o se encoge (mismo origen
        // que el hueco). El mismo-origen tiene prioridad para elegir el ancla
        // correcta entre varias candidatas.
        foreach (var stray in strays.ToList())
        {
            int slot = missing.FindIndex(t => SameOrigin(stray.Rect, t.Rect) &&
                (Contains(stray.Rect, t.Rect) || Contains(t.Rect, stray.Rect)));
            if (slot < 0) slot = missing.FindIndex(t => Contains(stray.Rect, t.Rect));
            if (slot < 0) continue;
            try
            {
                MoveWindow(stray.WindowNo, missing[slot].Rect);
                present[RectKey(missing[slot].Rect)] = stray.WindowNo;
                missing.RemoveAt(slot);
                strays.Remove(stray);
            }
            catch
            {
                // Si el roaming falla se sigue el camino clásico: cerrar y reabrir.
            }
        }

        // Cerrar las que sobran.
        foreach (var (windowNo, _) in strays)
            CloseSingleWindow(windowNo);

        // Abrir las que faltan (una por una, como valida la sonda).
        foreach (var tile in missing)
        {
            try { OpenSingleWindow(tile.Rect); }
            catch (Exception ex) { errors[tile.DisplayChannel] = ex.Message; }
        }
        if (missing.Count > 0) Thread.Sleep(500); // el equipo materializa las ventanas de forma asíncrona

        // Reconstruir el mapa canal→ventana. Lo reutilizado/movido ya se conoce;
        // solo hace falta re-escanear el equipo si se abrieron ventanas nuevas
        // (sus números los asigna el equipo y hay que descubrirlos por rect).
        // Los canales que quedaron fuera del layout pierden su mapeo: su
        // ventana ya se cerró como sobrante (o la reutilizó otro canal) y un
        // mapeo viejo haría que CloseWindow cerrara una ventana ajena.
        var tileChannels = tiles.Select(t => t.Channel).ToHashSet();
        foreach (int stale in _channelTargets.Keys.Where(c => !tileChannels.Contains(c)).ToList())
        {
            _channelTargets.Remove(stale);
            _channelHomeRect.Remove(stale);
            _channelCellRect.Remove(stale);
        }
        foreach (var t in tiles) _channelTargets.Remove(t.Channel);
        var mapped = new HashSet<string>(present.Keys);
        foreach (var (key, no) in present)
            if (desiredByKey.TryGetValue(key, out var t0)) _channelTargets[t0.Channel] = no;

        if (missing.Count > 0)
        {
            for (uint i = 1; i <= _maxWindows; i++)
            {
                uint windowNo = _wallNoHigh | i;
                var win = TryGetWindow(windowNo);
                if (win is not { byEnable: 1 } w) continue;
                string key = RectKey(w.struRect);
                if (mapped.Contains(key)) continue;
                if (desiredByKey.TryGetValue(key, out var tile))
                {
                    _channelTargets[tile.Channel] = windowNo;
                    mapped.Add(key);
                }
            }
        }

        // Marcar como error las pantallas cuyas ventanas no se pudieron abrir.
        foreach (var t in tiles)
            if (!_channelTargets.ContainsKey(t.Channel))
                errors.TryAdd(t.DisplayChannel, "No se pudieron abrir todas las ventanas del monitor en el muro.");

        return errors;
    }

    private static string RectKey(WallSdk.NET_DVR_RECTCFG_EX r) =>
        $"{r.dwXCoordinate},{r.dwYCoordinate},{r.dwWidth},{r.dwHeight}";

    /// <summary>Abre una ventana individual (crea) en el muro. Reintenta ante 951/935 transitorios.</summary>
    private void OpenSingleWindow(WallSdk.NET_DVR_RECTCFG_EX rect)
    {
        var win = new WallSdk.NET_DVR_VIDEOWALLWINDOWPOSITION();
        win.Init();
        win.dwSize = (uint)Marshal.SizeOf(win);
        win.byEnable = 1;
        win.byWndOperateMode = 0;
        win.dwWindowNo = _wallNoHigh; // sugerencia; el equipo asigna el número real
        win.struRect = rect;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                SetWindowPositions(new[] { win }, throwOnItemError: true);
                return;
            }
            catch (HikvisionException ex) when ((ex.ErrorCode == 951 || ex.ErrorCode == 935) && attempt < 4)
            {
                Thread.Sleep(400);
            }
        }
    }

    private void CloseSingleWindow(uint windowNo)
    {
        var win = new WallSdk.NET_DVR_VIDEOWALLWINDOWPOSITION();
        win.Init();
        win.dwSize = (uint)Marshal.SizeOf(win);
        win.byEnable = 0;
        win.dwWindowNo = windowNo;
        try { SetWindowPositions(new[] { win }, throwOnItemError: false); }
        catch { /* best-effort */ }
    }

    public Task CloseWindowAsync(int decodeChannel, CancellationToken ct = default) => Task.Run(() =>
    {
        // El ciclo de vida de las ventanas del mosaico lo gestiona la
        // sincronización; tras un push, los canales eliminados ya no tienen
        // mapeo y esto es un no-op. Solo cierra si la sesión aún conoce la
        // ventana del canal (ej. una flotante recién eliminada de la BD).
        if (!IsConnected || !UseWallApi()) return;
        if (!_channelTargets.TryGetValue(decodeChannel, out uint windowNo)) return;
        CloseSingleWindow(windowNo);
        _channelTargets.Remove(decodeChannel);
        _channelHomeRect.Remove(decodeChannel);
        _channelCellRect.Remove(decodeChannel);
        _zoomedChannels.Remove(decodeChannel);
        _floatChannels.Remove(decodeChannel);
    }, ct);

    public Task ZoomWindowAsync(int decodeChannel, bool fullscreen, CancellationToken ct = default) => Task.Run(() =>
    {
        EnsureConnected();
        if (!UseWallApi())
            throw new NotSupportedException("La pantalla completa solo está disponible en equipos de video wall.");

        if (!_channelTargets.TryGetValue(decodeChannel, out uint windowNo) ||
            !_channelHomeRect.TryGetValue(decodeChannel, out var home) ||
            !_channelCellRect.TryGetValue(decodeChannel, out var cell))
            throw new WallSyncRequiredException(
                $"El canal {decodeChannel} no tiene ventana asociada en esta sesión; se requiere sincronizar el wall.");

        // Redimensionar la ventana existente (roaming): instantáneo, sin tocar
        // las demás decodificaciones. Al agrandar cubre toda la pantalla.
        if (fullscreen)
        {
            // Primero traerla al frente: las ventanas del mosaico creadas después
            // tienen una capa superior y quedarían dibujadas encima de la imagen
            // agrandada. Como en su sub-celda no se solapa con nadie, subirla de
            // capa antes de agrandar no produce ningún parpadeo. La pantalla
            // completa queda deliberadamente por encima de las flotantes.
            BringWindowToTop(windowNo);
            MoveWindow(windowNo, cell);
            _zoomedChannels.Add(decodeChannel);
        }
        else
        {
            MoveWindow(windowNo, home);
            _zoomedChannels.Remove(decodeChannel);
            // La ventana restaurada quedó con la capa máxima: devolver las
            // flotantes al frente por si alguna se solapa con su sub-celda.
            TopFloatingWindows();
        }
    }, ct);

    public Task ZoomWindowToWallAsync(int decodeChannel, bool on, CancellationToken ct = default) => Task.Run(() =>
    {
        EnsureConnected();
        if (!UseWallApi())
            throw new NotSupportedException("Ocupar el muro completo solo está disponible en equipos de video wall.");

        if (!_channelTargets.TryGetValue(decodeChannel, out uint windowNo) ||
            !_channelHomeRect.TryGetValue(decodeChannel, out var home) ||
            _channelCellRect.Count == 0)
            throw new WallSyncRequiredException(
                $"El canal {decodeChannel} no tiene ventana asociada en esta sesión; se requiere sincronizar el wall.");

        if (on)
        {
            // El muro entero: bounding box de las celdas de todas las pantallas
            // conocidas por la sesión (la grilla está anclada en el origen).
            // Igual que la pantalla completa por monitor, es un roaming: la
            // ventana solo se agranda, nada se re-decodifica.
            uint x2 = 0, y2 = 0;
            foreach (var cell in _channelCellRect.Values)
            {
                x2 = Math.Max(x2, cell.dwXCoordinate + cell.dwWidth);
                y2 = Math.Max(y2, cell.dwYCoordinate + cell.dwHeight);
            }
            var wallRect = new WallSdk.NET_DVR_RECTCFG_EX
            {
                dwXCoordinate = 0,
                dwYCoordinate = 0,
                dwWidth = x2,
                dwHeight = y2,
                byRes = new byte[4],
            };
            BringWindowToTop(windowNo);
            MoveWindow(windowNo, wallRect);
            _zoomedChannels.Add(decodeChannel);
        }
        else
        {
            MoveWindow(windowNo, home);
            _zoomedChannels.Remove(decodeChannel);
            TopFloatingWindows();
        }
    }, ct);

    /// <summary>
    /// Trae una ventana del muro a la capa superior (mismo comando que usa el
    /// demo oficial para su botón "Top"). Best-effort: si el firmware no lo
    /// soporta, la ventana igual se agranda con su capa actual.
    /// </summary>
    private void BringWindowToTop(uint windowNo)
    {
        IntPtr buffer = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buffer, (int)windowNo);
            WallSdk.NET_DVR_RemoteControl(_userId, WallSdk.NET_DVR_SWITCH_WIN_TOP, buffer, 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Mueve/redimensiona una ventana existente del muro (cond = número de ventana).</summary>
    private void MoveWindow(uint windowNo, WallSdk.NET_DVR_RECTCFG_EX rect)
    {
        var win = new WallSdk.NET_DVR_VIDEOWALLWINDOWPOSITION();
        win.Init();
        win.dwSize = (uint)Marshal.SizeOf(win);
        win.byEnable = 1;
        win.byWndOperateMode = 0; // por coordenadas
        win.dwWindowNo = windowNo;
        win.struRect = rect;
        SetWindowPositions(new[] { win }, throwOnItemError: true, modifyExisting: true);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
            // El logout en dispose no debe propagar errores.
        }
    }

    // -----------------------------------------------------------------------
    // Diagnóstico: vuelca el estado real del equipo para depuración
    // -----------------------------------------------------------------------

    public Task<string> GetDiagnosticsAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        EnsureConnected();
        var sb = new StringBuilder();
        sb.AppendLine($"Serie: {_serialNumber}");

        if (!UseWallApi())
        {
            sb.AppendLine("Modo: decoder clásico (MatrixDisplayCfg).");
            return sb.ToString();
        }

        sb.AppendLine($"Modo: video wall · n.º de muro (byte alto): 0x{_wallNoHigh:X8}");
        sb.AppendLine($"Salidas del muro (1732): {string.Join(", ", _wallDisplayNos.Select(d => $"0x{d:X}"))}");

        foreach (uint displayNo in _wallDisplayNos)
        {
            try
            {
                var pos = GetDisplayPosition(displayNo);
                sb.AppendLine($"Salida 0x{displayNo:X}: enable={pos.byEnable} coord={pos.byCoordinateType} " +
                              $"muro=0x{pos.dwVideoWallNo:X} rect={RectToString(pos.struRectCfg)}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Salida 0x{displayNo:X}: GET posición falló → {ex.Message}");
            }
        }

        sb.AppendLine(DumpWindows());
        return sb.ToString();
    }, ct);

    /// <summary>Enumera las ventanas abiertas del muro (consulta individual 1..32) y su estado.</summary>
    private string DumpWindows()
    {
        var sb = new StringBuilder();
        int itemSize = Marshal.SizeOf<WallSdk.NET_DVR_VIDEOWALLWINDOWPOSITION>();

        IntPtr inBuffer = Marshal.AllocHGlobal(4);
        IntPtr statusBuffer = Marshal.AllocHGlobal(4);
        IntPtr outBuffer = Marshal.AllocHGlobal(itemSize);
        try
        {
            int found = 0;
            for (uint i = 1; i <= _maxWindows; i++)
            {
                uint windowNo = _wallNoHigh | i;
                var template = new WallSdk.NET_DVR_VIDEOWALLWINDOWPOSITION();
                template.Init();
                template.dwSize = (uint)itemSize;

                Marshal.WriteInt32(inBuffer, (int)windowNo);
                Marshal.WriteInt32(statusBuffer, 0);
                Marshal.StructureToPtr(template, outBuffer, false);

                if (!WallSdk.NET_DVR_GetDeviceConfig(
                        _userId, WallSdk.NET_DVR_GET_VIDEOWALLWINDOWPOSITION, 1,
                        inBuffer, 4, statusBuffer, outBuffer, (uint)itemSize))
                    continue;

                var win = Marshal.PtrToStructure<WallSdk.NET_DVR_VIDEOWALLWINDOWPOSITION>(outBuffer);
                if (win.byEnable == 0 && win.struRect.dwWidth == 0) continue;

                found++;
                sb.AppendLine($"Ventana 0x{windowNo:X}: enable={win.byEnable} capa={win.dwLayerIndex} " +
                              $"rect={RectToString(win.struRect)} · {DescribeWindowStatus(windowNo)}");
            }
            if (found == 0)
                sb.AppendLine("Ventanas: el muro no tiene ventanas abiertas (o el GET individual no está soportado).");
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(statusBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
        return sb.ToString();
    }

    private string DescribeWindowStatus(uint windowNo)
    {
        var info = new WallSdk.NET_DVR_WALLWIN_INFO();
        info.Init();
        info.dwSize = (uint)Marshal.SizeOf(info);
        info.dwWinNum = windowNo;
        info.dwSubWinNum = 0;
        info.dwWallNo = _wallNoHigh;

        var status = new WallSdk.NET_DVR_WALL_WIN_STATUS
        {
            byRes1 = new byte[7],
            byRes2 = new byte[31],
        };
        status.dwSize = (uint)Marshal.SizeOf(status);

        int inSize = Marshal.SizeOf(info);
        int outSize = Marshal.SizeOf(status);
        IntPtr inBuffer = Marshal.AllocHGlobal(inSize);
        IntPtr statusBuffer = Marshal.AllocHGlobal(4);
        IntPtr outBuffer = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(info, inBuffer, false);
            Marshal.WriteInt32(statusBuffer, 0);
            Marshal.StructureToPtr(status, outBuffer, false);

            if (!WallSdk.NET_DVR_GetDeviceStatus(
                    _userId, WallSdk.NET_DVR_MATRIX_GETWINSTATUS, 1,
                    inBuffer, (uint)inSize, statusBuffer, outBuffer, (uint)outSize))
                return $"estado: no disponible (err {CHCNetSDK.NET_DVR_GetLastError()})";

            status = Marshal.PtrToStructure<WallSdk.NET_DVR_WALL_WIN_STATUS>(outBuffer);
            return status.byDecodeStatus == 1
                ? $"DECODIFICANDO {status.wImgW}x{status.wImgH} @ {status.byFpsDecV} fps ({status.dwDecodedV} frames)"
                : "sin decodificación";
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(statusBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    // -----------------------------------------------------------------------
    // Detección de modo y contexto de video wall
    // -----------------------------------------------------------------------

    /// <summary>
    /// En modo wall, el "canal" del servidor se traduce al destino de
    /// decodificación del equipo: la sub-ventana correspondiente dentro de la
    /// ventana REAL de su pantalla (el equipo asigna los números de ventana;
    /// no se pueden elegir). El mapa lo construye la sincronización; si no
    /// existe (sesión nueva) el orquestador debe sincronizar y reintentar.
    /// </summary>
    private uint ToDeviceChannel(int decodeChannel)
    {
        if (!UseWallApi()) return (uint)decodeChannel;

        if (_channelTargets.TryGetValue(decodeChannel, out uint target))
            return target;

        throw new WallSyncRequiredException(
            $"El canal {decodeChannel} no tiene ventana asociada en esta sesión; se requiere sincronizar el wall.");
    }

    private WallSdk.NET_DVR_VIDEOWALLWINDOWPOSITION? TryGetWindow(uint windowNo)
    {
        int itemSize = Marshal.SizeOf<WallSdk.NET_DVR_VIDEOWALLWINDOWPOSITION>();
        IntPtr inBuffer = Marshal.AllocHGlobal(4);
        IntPtr statusBuffer = Marshal.AllocHGlobal(4);
        IntPtr outBuffer = Marshal.AllocHGlobal(itemSize);
        try
        {
            var template = new WallSdk.NET_DVR_VIDEOWALLWINDOWPOSITION();
            template.Init();
            template.dwSize = (uint)itemSize;

            Marshal.WriteInt32(inBuffer, (int)windowNo);
            Marshal.WriteInt32(statusBuffer, 0);
            Marshal.StructureToPtr(template, outBuffer, false);

            if (!WallSdk.NET_DVR_GetDeviceConfig(
                    _userId, WallSdk.NET_DVR_GET_VIDEOWALLWINDOWPOSITION, 1,
                    inBuffer, 4, statusBuffer, outBuffer, (uint)itemSize))
                return null;
            return Marshal.PtrToStructure<WallSdk.NET_DVR_VIDEOWALLWINDOWPOSITION>(outBuffer);
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(statusBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    private bool UseWallApi()
    {
        if (_useWallApi is bool cached) return cached;

        // Los equipos de familia video wall responden a GET_VIDEOWALLDISPLAYNO;
        // los decoders clásicos devuelven error (23 u otro).
        var cfg = new WallSdk.NET_DVR_DISPLAYCFG
        {
            struDisplayParam = new WallSdk.NET_DVR_DISPLAYPARAM[WallSdk.MAX_DISPLAY_NUM],
            byRes = new byte[128],
        };
        cfg.dwSize = (uint)Marshal.SizeOf(cfg);
        int size = Marshal.SizeOf(cfg);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(cfg, buffer, false);
            uint returned = 0;
            bool ok = CHCNetSDK.NET_DVR_GetDVRConfig(
                _userId, WallSdk.NET_DVR_GET_VIDEOWALLDISPLAYNO, 0, buffer, (uint)size, ref returned);
            if (!ok)
            {
                _useWallApi = false;
                return false;
            }

            cfg = Marshal.PtrToStructure<WallSdk.NET_DVR_DISPLAYCFG>(buffer);
            _wallDisplayNos = cfg.struDisplayParam
                .Where(p => p.byDispChanType != 0xFF && p.dwDisplayNo != 0)
                .Select(p => p.dwDisplayNo)
                .ToList();
            _useWallApi = true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // Número de muro: se toma de la posición de la primera salida.
        if (_wallDisplayNos.Count > 0)
        {
            try
            {
                var pos = GetDisplayPosition(_wallDisplayNos[0]);
                uint wall = pos.dwVideoWallNo;
                _wallNoHigh = (wall & 0xFF000000) != 0 ? wall & 0xFF000000 : Math.Max(1, wall & 0xFF) << 24;
            }
            catch
            {
                _wallNoHigh = 0x01000000; // muro 1 por defecto
            }
        }

        LoadWallAbility();
        return true;
    }

    /// <summary>
    /// Lee la capacidad de video wall (XML) para conocer la geometría real del
    /// firmware: tamaño de celda por pantalla (windowBaseX/Y) y alineación de
    /// ventanas. Si no está disponible se mantienen los valores por defecto.
    /// </summary>
    private void LoadWallAbility()
    {
        const int cap = 512 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(cap);
        try
        {
            for (int i = 0; i < cap; i++) Marshal.WriteByte(buffer, i, 0);
            if (!CHCNetSDK.NET_DVR_GetDeviceAbility(_userId, (int)WallSdk.WALL_ABILITY, IntPtr.Zero, 0, buffer, cap))
                return;

            string xml = Marshal.PtrToStringAnsi(buffer) ?? "";
            static uint? Extract(string source, string tag)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    source, $"<{tag}>\\s*(\\d+)\\s*</{tag}>");
                return match.Success && uint.TryParse(match.Groups[1].Value, out uint value) ? value : null;
            }

            _cellWidth = Extract(xml, "windowBaseX") ?? _cellWidth;
            _cellHeight = Extract(xml, "windowBaseY") ?? _cellHeight;
            _windowAlignX = Extract(xml, "wndWidthAlignUint") ?? _windowAlignX;
            _windowAlignY = Extract(xml, "wndHeightAlignUint") ?? _windowAlignY;
            if (_windowAlignX == 0) _windowAlignX = 1;
            if (_windowAlignY == 0) _windowAlignY = 1;

            // <winNum min="1" max="64" /> — máximo de ventanas en todo el muro.
            var winNumMatch = System.Text.RegularExpressions.Regex.Match(
                xml, "<winNum[^>]*\\bmax=\"(\\d+)\"");
            if (winNumMatch.Success && uint.TryParse(winNumMatch.Groups[1].Value, out uint maxWin) && maxWin > 0)
                _maxWindows = maxWin;

            // <windowMode opt="1,2,4,6,8,9,12,16,25,36" /> — divisiones soportadas.
            var modeMatch = System.Text.RegularExpressions.Regex.Match(
                xml, "<windowMode\\s+opt=\"([0-9,\\s]+)\"");
            if (modeMatch.Success)
            {
                _wallWindowModes = modeMatch.Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(v => int.TryParse(v, out int m) ? m : 0)
                    .Where(m => m > 0)
                    .OrderBy(m => m)
                    .ToList();
            }
        }
        catch
        {
            // Se mantienen los valores por defecto.
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Traduce el canal de display reportado por el ability (ej. HDMI = 49..56)
    /// al número de salida que usa la API de video wall, mapeando por posición
    /// cuando las numeraciones no coinciden.
    /// </summary>
    private uint ResolveWallDisplayNo(int displayChannel)
    {
        if (_wallDisplayNos.Contains((uint)displayChannel))
            return (uint)displayChannel;

        var displays = GetCapabilitiesCore().Displays;
        int index = displays.ToList().FindIndex(d => d.ChannelNo == displayChannel);
        if (index >= 0 && index < _wallDisplayNos.Count)
            return _wallDisplayNos[index];

        throw new InvalidOperationException(
            $"La salida {displayChannel} no existe en el video wall del equipo " +
            $"(salidas disponibles: {string.Join(", ", _wallDisplayNos)}).");
    }

    private WallSdk.NET_DVR_VIDEOWALLDISPLAYPOSITION GetDisplayPosition(uint wallDisplayNo)
    {
        var position = new WallSdk.NET_DVR_VIDEOWALLDISPLAYPOSITION();
        position.Init();
        position.dwSize = (uint)Marshal.SizeOf(position);

        int outSize = Marshal.SizeOf(position);
        IntPtr inBuffer = Marshal.AllocHGlobal(4);
        IntPtr statusBuffer = Marshal.AllocHGlobal(4);
        IntPtr outBuffer = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.WriteInt32(inBuffer, (int)wallDisplayNo);
            Marshal.WriteInt32(statusBuffer, 0);
            Marshal.StructureToPtr(position, outBuffer, false);

            if (!WallSdk.NET_DVR_GetDeviceConfig(
                    _userId, WallSdk.NET_DVR_GET_VIDEOWALLDISPLAYPOSITION, 1,
                    inBuffer, 4, statusBuffer, outBuffer, (uint)outSize))
                throw HikvisionException.FromLastError($"NET_DVR_GET_VIDEOWALLDISPLAYPOSITION (salida {wallDisplayNo})");

            return Marshal.PtrToStructure<WallSdk.NET_DVR_VIDEOWALLDISPLAYPOSITION>(outBuffer);
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(statusBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    // -----------------------------------------------------------------------
    // División de ventanas: modo video wall (DS-6900UDI)
    // -----------------------------------------------------------------------

    private static string RectToString(WallSdk.NET_DVR_RECTCFG_EX r) =>
        $"({r.dwXCoordinate},{r.dwYCoordinate} {r.dwWidth}x{r.dwHeight})";

    private static bool RectEquals(WallSdk.NET_DVR_RECTCFG_EX a, WallSdk.NET_DVR_RECTCFG_EX b) =>
        a.dwXCoordinate == b.dwXCoordinate && a.dwYCoordinate == b.dwYCoordinate &&
        a.dwWidth == b.dwWidth && a.dwHeight == b.dwHeight;

    /// <summary>
    /// Prepara la geometría del wall en el equipo. TrueCentral es dueño de las
    /// salidas de SU wall: si alguna quedó vinculada con una geometría distinta
    /// (restos de configuraciones anteriores), se desvincula y se vuelve a
    /// vincular con la grilla correcta. La grilla se coloca a la derecha del
    /// espacio ocupado por salidas ajenas para no chocar con otros muros.
    /// </summary>
    private (Dictionary<int, WallSdk.NET_DVR_RECTCFG_EX> Rects, Dictionary<int, string> Errors, string Diag)
        PrepareWallLayout(IReadOnlyList<ScreenWindowLayout> screens)
    {
        var diag = new StringBuilder();
        var errors = new Dictionary<int, string>();
        var rects = new Dictionary<int, WallSdk.NET_DVR_RECTCFG_EX>();

        // 1) Resolver la numeración de muro de nuestras salidas.
        var displayNoOf = new Dictionary<int, uint>();
        foreach (var s in screens)
        {
            try { displayNoOf[s.DisplayChannel] = ResolveWallDisplayNo(s.DisplayChannel); }
            catch (Exception ex) { errors[s.DisplayChannel] = ex.Message; }
        }
        var ourNos = displayNoOf.Values.ToHashSet();

        // 2) Rect deseado de cada salida nuestra, anclado al origen del muro.
        //    TrueCentral es dueño del muro del decoder: la grilla vive en (0,0),
        //    lo que evita la deriva de posición entre sincronizaciones.
        static bool Overlap(WallSdk.NET_DVR_RECTCFG_EX a, WallSdk.NET_DVR_RECTCFG_EX b) =>
            a.dwXCoordinate < b.dwXCoordinate + b.dwWidth && b.dwXCoordinate < a.dwXCoordinate + a.dwWidth &&
            a.dwYCoordinate < b.dwYCoordinate + b.dwHeight && b.dwYCoordinate < a.dwYCoordinate + a.dwHeight;

        var desired = new Dictionary<int, WallSdk.NET_DVR_RECTCFG_EX>();
        foreach (var s in screens.Where(s => displayNoOf.ContainsKey(s.DisplayChannel)))
        {
            desired[s.DisplayChannel] = new WallSdk.NET_DVR_RECTCFG_EX
            {
                dwXCoordinate = (uint)s.Col * _cellWidth,
                dwYCoordinate = (uint)s.Row * _cellHeight,
                dwWidth = _cellWidth,
                dwHeight = _cellHeight,
                byRes = new byte[4],
            };
        }
        var desiredRects = desired.Values.ToList();

        // 3) Muro base del tamaño exacto de nuestra grilla.
        uint neededWidth = (uint)(screens.Max(s => s.Col) + 1) * _cellWidth;
        uint neededHeight = (uint)(screens.Max(s => s.Row) + 1) * _cellHeight;
        diag.Append("origen=(0,0); " + EnsureWallBase(neededWidth, neededHeight));

        // 4) Desvincular salidas AJENAS que se solapen con nuestra grilla
        //    (restos de otras configuraciones que provocarían choques 951/11).
        foreach (uint no in _wallDisplayNos.Where(n => !ourNos.Contains(n)))
        {
            try
            {
                var pos = GetDisplayPosition(no);
                if (pos.byEnable == 1 && pos.struRectCfg.dwWidth > 0 && desiredRects.Any(r => Overlap(r, pos.struRectCfg)))
                    UnbindDisplay(no, diag);
            }
            catch { }
        }

        // 5) Desvincular nuestras salidas mal posicionadas (así no hay choques
        //    transitorios entre la geometría vieja y la nueva).
        var toBind = new List<int>();
        foreach (var (channel, no) in displayNoOf)
        {
            try
            {
                var current = GetDisplayPosition(no);
                if (current.byEnable == 1 && RectEquals(current.struRectCfg, desired[channel]))
                {
                    rects[channel] = current.struRectCfg;
                    diag.Append($"; 0x{no:X} ya correcta");
                    continue;
                }
                if (current.byEnable == 1)
                {
                    UnbindDisplay(no, diag);
                }
                toBind.Add(channel);
            }
            catch (Exception ex)
            {
                errors[channel] = $"{ex.Message} [{diag}]";
            }
        }

        // 6) Vincular con la geometría correcta y verificar.
        foreach (int channel in toBind)
        {
            uint no = displayNoOf[channel];
            try
            {
                rects[channel] = BindDisplayVerified(no, desired[channel], diag);
            }
            catch (Exception ex)
            {
                errors[channel] = ex.Message;
            }
        }

        return (rects, errors, diag.ToString());
    }


    /// <summary>Desvincula una salida del muro (SET enable=0 y, como refuerzo, RESET 1750). Best-effort.</summary>
    private void UnbindDisplay(uint wallDisplayNo, StringBuilder diag)
    {
        var position = new WallSdk.NET_DVR_VIDEOWALLDISPLAYPOSITION();
        position.Init();
        position.dwSize = (uint)Marshal.SizeOf(position);
        position.byEnable = 0;
        position.dwVideoWallNo = _wallNoHigh;
        position.dwDisplayNo = wallDisplayNo;

        bool ok = TrySetDisplayPosition(wallDisplayNo, position, out uint status);
        if (!ok || status != 0)
        {
            // Refuerzo: comando dedicado para cancelar la vinculación.
            IntPtr inBuffer = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(inBuffer, (int)wallDisplayNo);
                ok = WallSdk.NET_DVR_RemoteControl(_userId, 1750 /*RESET_VIDEOWALLDISPLAYPOSITION*/, inBuffer, 4);
                status = ok ? 0 : CHCNetSDK.NET_DVR_GetLastError();
            }
            finally { Marshal.FreeHGlobal(inBuffer); }
        }
        diag.Append($"; 0x{wallDisplayNo:X} desvinculada → " + (ok && status == 0 ? "OK" : $"err {status}"));
    }

    /// <summary>Vincula una salida con reintento de tipo de coordenadas y verificación por GET.</summary>
    private WallSdk.NET_DVR_RECTCFG_EX BindDisplayVerified(uint wallDisplayNo, WallSdk.NET_DVR_RECTCFG_EX rect, StringBuilder diag)
    {
        uint lastStatus = 0;
        foreach (byte coordinateType in new byte[] { 0, 1 })
        {
            var position = new WallSdk.NET_DVR_VIDEOWALLDISPLAYPOSITION();
            position.Init();
            position.dwSize = (uint)Marshal.SizeOf(position);
            position.byEnable = 1;
            position.byCoordinateType = coordinateType;
            position.dwVideoWallNo = _wallNoHigh;
            position.dwDisplayNo = wallDisplayNo;
            position.struRectCfg = rect;

            bool ok = TrySetDisplayPosition(wallDisplayNo, position, out uint status);
            diag.Append($"; 0x{wallDisplayNo:X} SET coord={coordinateType} {RectToString(rect)} → " +
                        (ok && status == 0 ? "OK" : $"err {status}"));
            if (!ok || status != 0) { lastStatus = status; continue; }

            var check = GetDisplayPosition(wallDisplayNo);
            diag.Append($"; verificación enable={check.byEnable} rect={RectToString(check.struRectCfg)}");
            if (check.byEnable == 1 && check.struRectCfg.dwWidth > 0)
                return check.struRectCfg;
        }

        throw new InvalidOperationException(
            $"No se pudo vincular la salida 0x{wallDisplayNo:X} al muro (último error {lastStatus}). [{diag}]");
    }

    private bool TrySetDisplayPosition(uint wallDisplayNo, WallSdk.NET_DVR_VIDEOWALLDISPLAYPOSITION position, out uint status)
    {
        int itemSize = Marshal.SizeOf(position);
        IntPtr inBuffer = Marshal.AllocHGlobal(4);
        IntPtr statusBuffer = Marshal.AllocHGlobal(4);
        IntPtr paramBuffer = Marshal.AllocHGlobal(itemSize);
        try
        {
            Marshal.WriteInt32(inBuffer, (int)wallDisplayNo);
            Marshal.WriteInt32(statusBuffer, 0);
            Marshal.StructureToPtr(position, paramBuffer, false);

            bool ok = WallSdk.NET_DVR_SetDeviceConfig(
                _userId, WallSdk.NET_DVR_SET_VIDEOWALLDISPLAYPOSITION, 1,
                inBuffer, 4, statusBuffer, paramBuffer, (uint)itemSize);
            status = ok ? (uint)Marshal.ReadInt32(statusBuffer) : CHCNetSDK.NET_DVR_GetLastError();
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(statusBuffer);
            Marshal.FreeHGlobal(paramBuffer);
        }
    }

    /// <summary>
    /// Asegura que el muro base exista y sea lo bastante grande. Los comandos
    /// 1730/1731 usan la vía NET_DVR_Get/SetDVRConfig (por canal = n.º de muro),
    /// con reintento de formato de canal según firmware.
    /// </summary>
    private string EnsureWallBase(uint neededWidth, uint neededHeight)
    {
        int size = Marshal.SizeOf<WallSdk.NET_DVR_VIDEOWALLDISPLAYMODE>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            var mode = new WallSdk.NET_DVR_VIDEOWALLDISPLAYMODE();
            mode.Init();
            mode.dwSize = (uint)size;

            // GET con ambos formatos de canal: 0x01000000 y 1.
            int[] channelCandidates = { (int)_wallNoHigh, (int)(_wallNoHigh >> 24) };
            int wallChannel = channelCandidates[0];
            bool getOk = false;
            string diag = "";
            foreach (int channel in channelCandidates)
            {
                Marshal.StructureToPtr(mode, buffer, false);
                uint returned = 0;
                if (CHCNetSDK.NET_DVR_GetDVRConfig(_userId, WallSdk.NET_DVR_GET_VIDEOWALLDISPLAYMODE,
                        channel, buffer, (uint)size, ref returned))
                {
                    mode = Marshal.PtrToStructure<WallSdk.NET_DVR_VIDEOWALLDISPLAYMODE>(buffer);
                    wallChannel = channel;
                    getOk = true;
                    diag = $"muro(ch=0x{channel:X}) GET: enable={mode.byEnable} rect={RectToString(mode.struRect)}";
                    break;
                }
                diag = $"muro GET falló (err {CHCNetSDK.NET_DVR_GetLastError()})";
            }

            if (getOk && mode.byEnable == 1 &&
                mode.struRect.dwWidth >= neededWidth && mode.struRect.dwHeight >= neededHeight)
                return diag;

            if (!getOk)
            {
                mode = new WallSdk.NET_DVR_VIDEOWALLDISPLAYMODE();
                mode.Init();
                mode.dwSize = (uint)size;
            }

            mode.byEnable = 1;
            mode.struRect = new WallSdk.NET_DVR_RECTCFG_EX
            {
                dwXCoordinate = 0,
                dwYCoordinate = 0,
                dwWidth = Math.Max(mode.struRect.dwWidth, neededWidth),
                dwHeight = Math.Max(mode.struRect.dwHeight, neededHeight),
                byRes = new byte[4],
            };
            byte[] name = Encoding.ASCII.GetBytes("CLR TrueCentral");
            Array.Clear(mode.sName!, 0, mode.sName!.Length);
            Array.Copy(name, mode.sName, Math.Min(name.Length, mode.sName.Length - 1));

            bool setOk = false;
            foreach (int channel in getOk ? new[] { wallChannel } : channelCandidates)
            {
                Marshal.StructureToPtr(mode, buffer, false);
                if (CHCNetSDK.NET_DVR_SetDVRConfig(_userId, WallSdk.NET_DVR_SET_VIDEOWALLDISPLAYMODE,
                        channel, buffer, (uint)size))
                {
                    setOk = true;
                    diag += $"; muro SET(ch=0x{channel:X}) {RectToString(mode.struRect)} → OK";
                    break;
                }
            }
            if (!setOk)
                diag += $"; muro SET falló (err {CHCNetSDK.NET_DVR_GetLastError()})";
            return diag;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }


    private void SetWindowPositions(WallSdk.NET_DVR_VIDEOWALLWINDOWPOSITION[] positions, bool throwOnItemError,
        bool modifyExisting = false)
    {
        int n = positions.Length;
        int itemSize = Marshal.SizeOf<WallSdk.NET_DVR_VIDEOWALLWINDOWPOSITION>();
        IntPtr condBuffer = Marshal.AllocHGlobal(4 * n);
        IntPtr statusBuffer = Marshal.AllocHGlobal(4 * n);
        IntPtr paramBuffer = Marshal.AllocHGlobal(itemSize * n);
        try
        {
            for (int i = 0; i < n; i++)
            {
                // Semántica validada contra el DS-6908UDI real:
                // - CREAR (byEnable=1, ventana nueva): condición = número de MURO.
                // - CERRAR (byEnable=0) o MODIFICAR/MOVER una ventana existente:
                //   condición = número de la VENTANA.
                uint condition = (positions[i].byEnable == 0 || modifyExisting)
                    ? positions[i].dwWindowNo : _wallNoHigh;
                Marshal.WriteInt32(condBuffer, 4 * i, (int)condition);
                Marshal.WriteInt32(statusBuffer, 4 * i, 0);
                Marshal.StructureToPtr(positions[i], paramBuffer + i * itemSize, false);
            }

            var inParam = new WallSdk.NET_DVR_IN_PARAM();
            inParam.Init();
            inParam.struCondBuf = new WallSdk.NET_DVR_BUF_INFO { pBuf = condBuffer, nLen = (uint)(4 * n) };
            inParam.struInParamBuf = new WallSdk.NET_DVR_BUF_INFO { pBuf = paramBuffer, nLen = (uint)(itemSize * n) };
            inParam.dwRecvTimeout = 0;

            var outParam = new WallSdk.NET_DVR_OUT_PARAM();
            outParam.Init();
            outParam.struOutBuf = new WallSdk.NET_DVR_BUF_INFO { pBuf = IntPtr.Zero, nLen = 0 };
            outParam.lpStatusList = statusBuffer;

            bool ok = WallSdk.NET_DVR_SetDeviceConfigEx(
                _userId, WallSdk.NET_DVR_SET_VIDEOWALLWINDOWPOSITION, (uint)n, ref inParam, ref outParam);

            if (!ok)
            {
                // Fallback a la variante simple (firmwares antiguos).
                uint exError = CHCNetSDK.NET_DVR_GetLastError();
                for (int i = 0; i < n; i++) Marshal.WriteInt32(statusBuffer, 4 * i, 0);
                ok = WallSdk.NET_DVR_SetDeviceConfig(
                    _userId, WallSdk.NET_DVR_SET_VIDEOWALLWINDOWPOSITION, (uint)n,
                    condBuffer, (uint)(4 * n), statusBuffer, paramBuffer, (uint)(itemSize * n));
                if (!ok)
                    throw new HikvisionException($"NET_DVR_SET_VIDEOWALLWINDOWPOSITION (Ex err {exError})",
                        CHCNetSDK.NET_DVR_GetLastError());
            }

            if (throwOnItemError)
            {
                for (int i = 0; i < n; i++)
                {
                    uint status = (uint)Marshal.ReadInt32(statusBuffer, 4 * i);
                    if (status != 0)
                        throw new HikvisionException(
                            $"Apertura de la ventana 0x{positions[i].dwWindowNo:X}", status);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(condBuffer);
            Marshal.FreeHGlobal(statusBuffer);
            Marshal.FreeHGlobal(paramBuffer);
        }
    }

    // -----------------------------------------------------------------------
    // División de ventanas: decoders clásicos (MatrixSetDisplayCfg_V41)
    // -----------------------------------------------------------------------

    private void ConfigureLegacyWindows(int displayChannel, IReadOnlyList<int> windowDecodeChannels)
    {
        // Leer la configuración actual de la salida para no pisar resolución,
        // formato de video ni audio; solo cambiamos ventanas y sus canales.
        var cfg = new CHCNetSDK.NET_DVR_MATRIX_VOUTCFG
        {
            byJoinDecChan = new byte[CHCNetSDK.MAX_WINDOWS_V41],
            byRes2 = new byte[76],
            struDiff = new CHCNetSDK.NET_DVR_VIDEO_PLATFORM { byRes = new byte[160] },
        };
        cfg.dwSize = (uint)Marshal.SizeOf(cfg);

        if (!CHCNetSDK.NET_DVR_MatrixGetDisplayCfg_V41(_userId, (uint)displayChannel, ref cfg))
            throw HikvisionException.FromLastError($"NET_DVR_MatrixGetDisplayCfg_V41 (display {displayChannel})");

        cfg.dwSize = (uint)Marshal.SizeOf(cfg);
        cfg.dwWindowMode = (uint)windowDecodeChannels.Count;
        cfg.byJoinDecChan ??= new byte[CHCNetSDK.MAX_WINDOWS_V41];
        for (int i = 0; i < CHCNetSDK.MAX_WINDOWS_V41; i++)
            cfg.byJoinDecChan[i] = i < windowDecodeChannels.Count ? (byte)windowDecodeChannels[i] : (byte)0;

        if (!CHCNetSDK.NET_DVR_MatrixSetDisplayCfg_V41(_userId, (uint)displayChannel, ref cfg))
            throw HikvisionException.FromLastError($"NET_DVR_MatrixSetDisplayCfg_V41 (display {displayChannel})");
    }

    // -----------------------------------------------------------------------

    private void EnsureConnected()
    {
        if (_userId < 0)
            throw new InvalidOperationException("El driver no está conectado al decodificador.");
    }

    private static void AddDisplays(List<DisplayOutputInfo> displays, DisplayOutputType type,
        CHCNetSDK.NET_DVR_DISPINFO info, CHCNetSDK.NET_DVR_MATRIX_ABILITY_V41 ability)
    {
        for (int i = 0; i < info.byChanNums; i++)
            displays.Add(new DisplayOutputInfo(type, i + 1, info.byStartChan + i, GetWindowModes(ability, type, i)));
    }

    /// <summary>
    /// Modos de sub-ventanas soportados por una salida según el ability
    /// (struDispMode). Codificación de byDispChanType: 0-BNC, 1-VGA, 2-HDMI, 3-DVI
    /// (la misma que usa NET_DVR_MATRIX_VOUTCFG y el demo oficial).
    /// </summary>
    private static IReadOnlyList<int> GetWindowModes(
        CHCNetSDK.NET_DVR_MATRIX_ABILITY_V41 ability, DisplayOutputType type, int indexInType)
    {
        byte chanType = type switch
        {
            DisplayOutputType.Bnc => 0,
            DisplayOutputType.Vga => 1,
            DisplayOutputType.Hdmi => 2,
            DisplayOutputType.Dvi => 3,
            _ => 0xFF,
        };

        var fallback = new[] { 1, 4, 6, 9, 12, 16 };
        if (ability.struDispMode is null) return fallback;

        int first = Array.FindIndex(ability.struDispMode, m => m.byDispChanType == chanType);
        if (first < 0) return fallback;
        int entryIndex = first + indexInType;
        if (entryIndex >= ability.struDispMode.Length) return fallback;

        var entry = ability.struDispMode[entryIndex];
        if (entry.byDispMode is null) return fallback;

        var modes = entry.byDispMode.Where(m => m != 0).Select(m => (int)m).Distinct().OrderBy(m => m).ToList();
        return modes.Count > 0 ? modes : fallback;
    }

    /// <summary>Fuente por protocolo privado (cámara/DVR/NVR Hikvision): IP, puerto, credenciales y canal.</summary>
    private static CHCNetSDK.NET_DVR_DEC_STREAM_MODE BuildDeviceChannelUnion(StreamSource source, out byte streamMode)
    {
        streamMode = 1; // 1 = stream por IP/dominio

        var dev = new CHCNetSDK.NET_DVR_DEC_STREAM_DEV_EX
        {
            struStreamMediaSvrCfg = new CHCNetSDK.NET_DVR_STREAM_MEDIA_SERVER
            {
                byValid = 0,
                byRes1 = new byte[3],
                byAddress = "",
                byRes2 = new byte[5],
            },
            struDevChanInfo = new CHCNetSDK.NET_DVR_DEV_CHAN_INFO_EX
            {
                byChanType = 0, // canal normal
                byStreamId = "",
                byRes1 = new byte[3],
                dwChannel = (uint)source.Channel,
                byRes2 = new byte[24],
                byAddress = source.Host,
                wDVRPort = (ushort)source.Port,
                byChannel = (byte)Math.Clamp(source.Channel, 0, 255),
                byTransProtocol = (byte)source.TransportProtocol,
                byTransMode = (byte)source.StreamType,
                byFactoryType = 0, // 0 = protocolo Hikvision
                byRes = new byte[2],
                sUserName = ToFixedAnsi(source.Username, CHCNetSDK.NAME_LEN),
                sPassword = ToFixedAnsi(source.Password, CHCNetSDK.PASSWD_LEN),
            },
        };

        return CopyToUnion(dev);
    }

    /// <summary>
    /// Buffer ANSI de tamaño fijo para credenciales del SDK. Sin terminador nulo
    /// obligatorio: un password de exactamente PASSWD_LEN (16) caracteres ocupa
    /// el campo completo.
    /// </summary>
    private static byte[] ToFixedAnsi(string? value, int length)
    {
        var buffer = new byte[length];
        if (!string.IsNullOrEmpty(value))
            Encoding.Latin1.GetBytes(value.AsSpan(0, Math.Min(value.Length, length)), buffer);
        return buffer;
    }

    /// <summary>Fuente por URL (RTSP). Permite decodificar cámaras ONVIF/otras marcas en el decoder Hikvision.</summary>
    private static CHCNetSDK.NET_DVR_DEC_STREAM_MODE BuildUrlUnion(StreamSource source, out byte streamMode)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
            throw new ArgumentException("La fuente en modo URL requiere una URL RTSP.", nameof(source));

        streamMode = 2; // 2 = stream por URL

        var url = new CHCNetSDK.NET_DVR_PU_STREAM_URL
        {
            byEnable = 1,
            strURL = source.Url,
            byTransPortocol = (byte)source.TransportProtocol,
            wIPID = 0,
            byChannel = 0,
            byRes = new byte[7],
        };

        return CopyToUnion(url);
    }

    /// <summary>
    /// Copia una estructura concreta dentro del union NET_DVR_DEC_STREAM_MODE (300 bytes),
    /// rellenando con ceros el espacio sobrante, igual que hace el demo oficial del SDK.
    /// </summary>
    private static CHCNetSDK.NET_DVR_DEC_STREAM_MODE CopyToUnion<T>(T value) where T : struct
    {
        var union = new CHCNetSDK.NET_DVR_DEC_STREAM_MODE();
        union.Init();

        int unionSize = Marshal.SizeOf(union);
        int valueSize = Marshal.SizeOf(value);
        if (valueSize > unionSize)
            throw new InvalidOperationException(
                $"La estructura {typeof(T).Name} ({valueSize} bytes) no cabe en el union ({unionSize} bytes).");

        IntPtr buffer = Marshal.AllocHGlobal(unionSize);
        try
        {
            for (int i = 0; i < unionSize; i++)
                Marshal.WriteByte(buffer, i, 0);

            Marshal.StructureToPtr(value, buffer, false);
            return Marshal.PtrToStructure<CHCNetSDK.NET_DVR_DEC_STREAM_MODE>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>Fábrica del driver Hikvision. Se registra en el DecoderDriverRegistry del servidor.</summary>
public sealed class HikvisionDecoderDriverFactory : IDecoderDriverFactory
{
    public string DriverKey => HikvisionDecoderDriver.Key;
    public string DisplayName => "Hikvision (HCNetSDK)";
    public IDecoderDriver Create() => new HikvisionDecoderDriver();
}

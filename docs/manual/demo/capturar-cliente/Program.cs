// Capturas del cliente de monitoreo para el manual, contra el entorno de demostración.
//
// Abre el cliente de la demo (con su configuración aparte: TCVMS_DEMO_APPDATA),
// lo maneja por UI Automation sin teclado ni mouse, lleva sus ventanas fuera de
// la pantalla para no molestar a quien usa el PC, y por cada captura de
// capturas-cliente.json guarda el PNG (PrintWindow de la ventana más las
// ventanas de video de Flyleaf, que son ventanas aparte) y la posición de sus
// marcas en capturas/marcas.json, igual que el capturador web.
//
// Uso: capturar-cliente <demo.json> <capturas-cliente.json> <carpeta capturas> [filtro…]
//      capturar-cliente <demo.json> --volcar <título de ventana> <archivo>   (árbol UIA, para escribir pasos)

using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace CapturarCliente;

internal static class Program
{
    private static int Main(string[] args)
    {
        Win32.SetProcessDpiAwarenessContext(new IntPtr(-4)); // por monitor v2: coordenadas físicas
        if (args.Length < 3)
        {
            Console.WriteLine("Uso: capturar-cliente <demo.json> <capturas-cliente.json> <carpeta capturas> [filtro…]");
            return 2;
        }
        var demo = new Demo(args[0]);
        using var cliente = new Cliente(demo);

        if (args[1] == "--volcar")
        {
            cliente.Abrir();
            cliente.Ingresar();
            cliente.Volcar(args[2], args[3]);
            return 0;
        }

        var especificacion = JsonNode.Parse(File.ReadAllText(args[1]))!;
        string salida = args[2];
        var filtros = args.Skip(3).ToArray();
        var capturas = especificacion["capturas"]!.AsArray()
            .Where(c => filtros.Length == 0 || filtros.Any(f => c!["archivo"]!.GetValue<string>().Contains(f)))
            .ToList();
        if (capturas.Count == 0) { Console.WriteLine("Ninguna captura coincide."); return 0; }

        int fallas = 0;
        cliente.Abrir();
        foreach (var captura in capturas)
        {
            string archivo = captura!["archivo"]!.GetValue<string>();
            try
            {
                foreach (var paso in captura["pasos"]?.AsArray() ?? [])
                    cliente.Ejecutar(paso!);
                cliente.FueraDePantalla();
                Thread.Sleep(captura["reposo"]?.GetValue<int>() ?? 800);
                cliente.Capturar(captura, Path.Combine(salida, archivo));
                foreach (var paso in captura["despues"]?.AsArray() ?? [])
                    cliente.Ejecutar(paso!);
                Console.WriteLine($"✔ {archivo}");
            }
            catch (Exception ex)
            {
                fallas++;
                Console.WriteLine($"✘ {archivo}: {ex.Message}");
            }
        }
        return fallas == 0 ? 0 : 1;
    }
}

/// <summary>Datos del entorno de demostración (demo.json).</summary>
internal sealed class Demo
{
    public Demo(string ruta)
    {
        var json = JsonNode.Parse(File.ReadAllText(ruta))!;
        Carpeta = Environment.ExpandEnvironmentVariables(json["carpetaDatos"]!.GetValue<string>());
        PuertoWeb = json["puertos"]!["web"]!.GetValue<int>();
        Usuario = json["administrador"]!["usuario"]!.GetValue<string>();
        Clave = json["administrador"]!["clave"]!.GetValue<string>();
    }

    public string Carpeta { get; }
    public int PuertoWeb { get; }
    public string Usuario { get; }
    public string Clave { get; }
    public string Servidor => $"http://127.0.0.1:{PuertoWeb}";
}

/// <summary>El cliente de la demo y lo que se puede hacer con él por UI Automation.</summary>
internal sealed class Cliente : IDisposable
{
    private const string TituloIngreso = "Iniciar sesión";
    private readonly Demo _demo;
    private Process? _proceso;
    private string? _token;
    private static readonly HttpClient Http = new();

    public Cliente(Demo demo) => _demo = demo;

    public void Dispose()
    {
        if (_proceso is { HasExited: false })
        {
            try { _proceso.Kill(entireProcessTree: true); } catch { /* mejor esfuerzo */ }
        }
    }

    // ------------------------------------------------------------------
    // Arranque e ingreso
    // ------------------------------------------------------------------

    public void Abrir()
    {
        string perfil = Path.Combine(_demo.Carpeta, "cliente-perfil");
        string archivos = Path.Combine(_demo.Carpeta, "cliente-archivos");
        Directory.CreateDirectory(Path.Combine(perfil, "CLRTrueCentralVMS"));
        Directory.CreateDirectory(archivos);
        // Configuración limpia en cada corrida: sin usuarios recientes ni claves guardadas.
        var config = new JsonObject
        {
            ["ServerUrl"] = _demo.Servidor,
            ["Username"] = "",
            ["RememberPassword"] = false,
            ["AutoLogin"] = false,
            ["LastLayout"] = "4",
            ["SnapshotFolder"] = Path.Combine(archivos, "Capturas"),
            ["RecordingFolder"] = Path.Combine(archivos, "Grabaciones"),
            ["TreeByLocation"] = false,
        };
        File.WriteAllText(Path.Combine(perfil, "CLRTrueCentralVMS", "client.json"), config.ToJsonString());

        var inicio = new ProcessStartInfo(Path.Combine(_demo.Carpeta, "cliente", "TrueCentralVms.Client.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.Combine(_demo.Carpeta, "cliente"),
        };
        inicio.Environment["TCVMS_DEMO_APPDATA"] = perfil;
        _proceso = Process.Start(inicio)!;
        EsperarVentana(TituloIngreso, 30000);
        FueraDePantalla();
    }

    public void Ingresar()
    {
        var ventana = EsperarVentana(TituloIngreso, 10000);
        Escribir(Buscar(ventana, new JsonObject { ["id"] = "ServerBox" }), _demo.Servidor);
        Escribir(Buscar(ventana, new JsonObject { ["id"] = "UserBox" }), _demo.Usuario);
        var mostrar = Buscar(ventana, new JsonObject { ["id"] = "ShowPasswordToggle" });
        if (((TogglePattern)mostrar.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState != ToggleState.On)
            ((TogglePattern)mostrar.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Escribir(Buscar(ventana, new JsonObject { ["id"] = "PasswordPlain" }), _demo.Clave);
        Clic(Buscar(ventana, new JsonObject { ["id"] = "LoginButton" }));
        // La ventana principal aparece cuando el ingreso termina.
        var fin = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < fin)
        {
            if (Ventanas().Any(v => !Titulo(v).Contains(TituloIngreso))) break;
            Thread.Sleep(300);
        }
        Thread.Sleep(2500);
        FueraDePantalla();
    }

    // ------------------------------------------------------------------
    // Pasos
    // ------------------------------------------------------------------

    public void Ejecutar(JsonNode paso)
    {
        string accion = paso["accion"]!.GetValue<string>();
        // Sin "ventana", se busca en todas las del cliente: los menús desplegables
        // (Popup) de WPF son ventanas aparte.
        AutomationElement Objetivo() => paso["ventana"] is null
            ? BuscarEnTodas(paso, paso["ms"]?.GetValue<int>() ?? 10000)
            : Buscar(VentanaDe(paso), paso, paso["ms"]?.GetValue<int>() ?? 10000);
        switch (accion)
        {
            case "ingresar": Ingresar(); break;
            case "clic": Clic(Objetivo()); break;
            case "escribir": Escribir(Objetivo(), paso["texto"]!.GetValue<string>()); break;
            case "seleccionar":
                ((SelectionItemPattern)Objetivo().GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); break;
            case "expandir":
                ((ExpandCollapsePattern)Objetivo().GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand(); break;
            case "contraer":
                ((ExpandCollapsePattern)Objetivo().GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse(); break;
            case "alternar":
                ((TogglePattern)Objetivo().GetCurrentPattern(TogglePattern.Pattern)).Toggle(); break;
            case "esperar": Objetivo(); break;
            case "esperarVentana": EsperarVentana(paso["titulo"]!.GetValue<string>(), paso["ms"]?.GetValue<int>() ?? 15000); break;
            case "cerrarVentana":
                // "opcional": true = si la ventana no apareció, seguir igual.
                try
                {
                    var ventana = EsperarVentana(paso["titulo"]!.GetValue<string>(), paso["ms"]?.GetValue<int>() ?? 5000);
                    ((WindowPattern)ventana.GetCurrentPattern(WindowPattern.Pattern)).Close();
                }
                catch (InvalidOperationException) when (paso["opcional"]?.GetValue<bool>() == true) { }
                break;
            case "pausa": Thread.Sleep(paso["ms"]!.GetValue<int>()); break;
            case "api": Api(paso); break;
            case "volcar": Volcar(paso["titulo"]!.GetValue<string>(), paso["archivo"]!.GetValue<string>()); break;
            default: throw new InvalidOperationException($"Acción desconocida «{accion}».");
        }
        Thread.Sleep(paso["espera"]?.GetValue<int>() ?? 400);
        FueraDePantalla();
    }

    /// <summary>Llama a la API REST de la demo como administrador (para provocar alertas, por ejemplo).</summary>
    private void Api(JsonNode paso)
    {
        _token ??= Http.PostAsJsonAsync($"{_demo.Servidor}/api/auth/login",
                new { username = _demo.Usuario, password = _demo.Clave }).Result
            .Content.ReadFromJsonAsync<JsonObject>().Result!["token"]!.GetValue<string>();
        var pedido = new HttpRequestMessage(new HttpMethod(paso["metodo"]!.GetValue<string>()),
            $"{_demo.Servidor}{paso["ruta"]!.GetValue<string>()}");
        pedido.Headers.Authorization = new("Bearer", _token);
        if (paso["cuerpo"] is { } cuerpo)
            pedido.Content = new StringContent(cuerpo.ToJsonString(), Encoding.UTF8, "application/json");
        var respuesta = Http.Send(pedido);
        if (!respuesta.IsSuccessStatusCode)
            throw new InvalidOperationException($"API {paso["ruta"]} → {(int)respuesta.StatusCode}");
    }

    private static void Clic(AutomationElement elemento)
    {
        for (var e = elemento; e is not null; e = TreeWalker.ControlViewWalker.GetParent(e))
        {
            if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var invocar)) { ((InvokePattern)invocar).Invoke(); return; }
            if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var elegir)) { ((SelectionItemPattern)elegir).Select(); return; }
            if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var alternar)) { ((TogglePattern)alternar).Toggle(); return; }
            if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandir)) { ((ExpandCollapsePattern)expandir).Expand(); return; }
        }
        throw new InvalidOperationException($"«{elemento.Current.Name}» no se puede pulsar.");
    }

    private static void Escribir(AutomationElement elemento, string texto) =>
        ((ValuePattern)elemento.GetCurrentPattern(ValuePattern.Pattern)).SetValue(texto);

    // ------------------------------------------------------------------
    // Ventanas y elementos
    // ------------------------------------------------------------------

    private List<AutomationElement> Ventanas() =>
        AutomationElement.RootElement
            .FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, _proceso!.Id))
            .Cast<AutomationElement>().ToList();

    private static string Titulo(AutomationElement ventana)
    {
        try { return ventana.Current.Name ?? ""; } catch { return ""; }
    }

    /// <summary>La ventana del paso ("ventana": parte del título) o la principal.</summary>
    private AutomationElement VentanaDe(JsonNode paso) =>
        paso["ventana"] is { } titulo ? EsperarVentana(titulo.GetValue<string>(), 10000) : Principal();

    private AutomationElement Principal() =>
        Ventanas().Where(v => !Titulo(v).Contains(TituloIngreso))
            .OrderByDescending(v => v.Current.BoundingRectangle.Width * v.Current.BoundingRectangle.Height)
            .FirstOrDefault() ?? throw new InvalidOperationException("No está la ventana principal.");

    private AutomationElement EsperarVentana(string titulo, int ms)
    {
        var fin = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < fin)
        {
            var ventana = Ventanas().FirstOrDefault(v => Titulo(v).Contains(titulo));
            if (ventana is not null) return ventana;
            Thread.Sleep(250);
        }
        throw new InvalidOperationException($"No apareció la ventana «{titulo}».");
    }

    /// <summary>Busca en la ventana principal y, si no está, en las demás del cliente.</summary>
    private AutomationElement BuscarEnTodas(JsonNode loc, int ms)
    {
        var fin = DateTime.UtcNow.AddMilliseconds(ms);
        while (true)
        {
            var principal = Ventanas().FirstOrDefault(v => !Titulo(v).Contains(TituloIngreso));
            foreach (var ventana in Ventanas().OrderBy(v => v.Equals(principal) ? 0 : 1))
            {
                try { return Buscar(ventana, loc, 0); }
                catch (InvalidOperationException) { }
            }
            if (DateTime.UtcNow > fin) throw new InvalidOperationException($"No se encontró {loc.ToJsonString()}");
            Thread.Sleep(250);
        }
    }

    /// <summary>
    /// Busca un elemento: id (AutomationId = x:Name), nombre (exacto), contiene
    /// (parte del nombre), ayuda (parte del ToolTip), tipo (Button, Text, TreeItem…)
    /// e indice (el n-ésimo que coincide, desde 0).
    /// </summary>
    private static AutomationElement Buscar(AutomationElement raiz, JsonNode loc, int ms = 10000)
    {
        var condiciones = new List<Condition>();
        if (loc["id"] is { } id) condiciones.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, id.GetValue<string>()));
        if (loc["nombre"] is { } nombre) condiciones.Add(new PropertyCondition(AutomationElement.NameProperty, nombre.GetValue<string>()));
        if (loc["tipo"] is { } tipo)
        {
            var controlType = typeof(ControlType).GetField(tipo.GetValue<string>())?.GetValue(null) as ControlType
                ?? throw new InvalidOperationException($"Tipo desconocido «{tipo}».");
            condiciones.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));
        }
        Condition condicion = condiciones.Count switch
        {
            0 => Condition.TrueCondition,
            1 => condiciones[0],
            _ => new AndCondition([.. condiciones]),
        };
        string? contiene = loc["contiene"]?.GetValue<string>();
        string? ayuda = loc["ayuda"]?.GetValue<string>();
        int indice = loc["indice"]?.GetValue<int>() ?? 0;

        var fin = DateTime.UtcNow.AddMilliseconds(ms);
        while (true)
        {
            var hallados = raiz.FindAll(TreeScope.Descendants, condicion).Cast<AutomationElement>()
                .Where(e =>
                {
                    try
                    {
                        if (e.Current.BoundingRectangle.IsEmpty) return false;
                        if (contiene is not null && !(e.Current.Name ?? "").Contains(contiene)) return false;
                        if (ayuda is not null && !(e.Current.HelpText ?? "").Contains(ayuda)) return false;
                        return true;
                    }
                    catch (ElementNotAvailableException) { return false; }
                })
                .ToList();
            if (hallados.Count > indice) return hallados[indice];
            if (DateTime.UtcNow > fin)
                throw new InvalidOperationException($"No se encontró {loc.ToJsonString()}");
            Thread.Sleep(250);
        }
    }

    /// <summary>
    /// Manda al fondo (debajo de todas las demás ventanas, sin activarlas) las
    /// ventanas del cliente, para no tapar el trabajo de quien usa el PC.
    /// Quedan DENTRO de la pantalla: una ventana WPF o un video de Flyleaf que
    /// queda entero fuera de los monitores deja de dibujarse (el Present de
    /// DirectX se informa "ocluido") y la captura saldría vieja. Tapada por
    /// otras ventanas sí se sigue dibujando, y PrintWindow la captura igual.
    /// </summary>
    public void FueraDePantalla()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        foreach (var hwnd in VentanasWin32())
        {
            if (!Titulo(hwnd).Contains(TituloIngreso) && EsPrincipal(hwnd))
            {
                // La principal además toma un tamaño fijo: capturas parejas entre corridas.
                if (Win32.IsZoomed(hwnd)) Win32.ShowWindow(hwnd, 4); // restaurar sin activar
                double escala = Win32.GetDpiForWindow(hwnd) / 96.0;
                int w = (int)(1440 * escala), h = (int)(900 * escala);
                Win32.SetWindowPos(hwnd, Win32.HwndBottom, area.X + 8, area.Y + 8, w, h, 0x0010);
            }
            else
            {
                Win32.SetWindowPos(hwnd, Win32.HwndBottom, 0, 0, 0, 0, 0x0013); // sin mover ni cambiar tamaño
            }
        }
    }

    private bool EsPrincipal(IntPtr hwnd) => Win32.GetWindow(hwnd, 4) == IntPtr.Zero && Titulo(hwnd).Length > 0;

    private static string Titulo(IntPtr hwnd)
    {
        var texto = new StringBuilder(256);
        Win32.GetWindowText(hwnd, texto, texto.Capacity);
        return texto.ToString();
    }

    /// <summary>Ventanas de primer nivel visibles del cliente, de la de más arriba a la de más abajo.</summary>
    private List<IntPtr> VentanasWin32()
    {
        var lista = new List<IntPtr>();
        uint pid = (uint)_proceso!.Id;
        Win32.EnumWindows((hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint dueño);
            if (dueño == pid && Win32.IsWindowVisible(hwnd) && !Win32.IsIconic(hwnd)) lista.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return lista;
    }

    // ------------------------------------------------------------------
    // Captura
    // ------------------------------------------------------------------

    public void Capturar(JsonNode captura, string destino)
    {
        var ventana = VentanaDe(captura);
        var hwnd = new IntPtr(ventana.Current.NativeWindowHandle);
        Win32.GetWindowRect(hwnd, out var rv);
        var marco = new Rectangle(rv.Left, rv.Top, rv.Right - rv.Left, rv.Bottom - rv.Top);
        double escala = Win32.GetDpiForWindow(hwnd) / 96.0;

        using var imagen = new Bitmap(marco.Width, marco.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(imagen))
        {
            Pintar(g, hwnd, marco);
            // Las ventanas que están por encima (video de Flyleaf, avisos) se pintan
            // de abajo hacia arriba. Las transparentes (WS_EX_LAYERED) se omiten:
            // PrintWindow las entrega con fondo negro.
            // "capas": true las incluye igual (menús desplegables con sombra).
            bool capas = captura["capas"]?.GetValue<bool>() ?? false;
            var encima = VentanasWin32().TakeWhile(h => h != hwnd).Reverse();
            foreach (var otra in encima)
            {
                long estilo = Win32.GetWindowLongPtr(otra, -20).ToInt64();
                if ((estilo & 0x80000) != 0 && !capas) continue;
                Pintar(g, otra, marco);
            }
        }

        AutomationElement Elemento(JsonNode loc) =>
            loc["ventana"] is null ? BuscarEnTodas(loc, 5000) : Buscar(VentanaDe(loc), loc, 5000);

        // Recorte: un elemento (con margen en px lógicos) o la ventana entera.
        var recorte = new Rectangle(0, 0, marco.Width, marco.Height);
        if (captura["recorte"] is { } loc)
        {
            var r = Elemento(loc).Current.BoundingRectangle;
            int m = (int)((captura["margen"]?.GetValue<int>() ?? 0) * escala);
            recorte = Rectangle.Intersect(recorte, new Rectangle(
                (int)r.X - marco.X - m, (int)r.Y - marco.Y - m, (int)r.Width + 2 * m, (int)r.Height + 2 * m));
        }

        var marcas = new JsonArray();
        foreach (var marca in captura["marcas"]?.AsArray() ?? [])
        {
            var r = Elemento(marca!).Current.BoundingRectangle;
            var (x, y) = Punto(r, marca!["en"]?.GetValue<string>() ?? "izq", escala);
            marcas.Add(new JsonObject
            {
                ["x"] = Math.Round((x - marco.X - recorte.X) / recorte.Width, 4),
                ["y"] = Math.Round((y - marco.Y - recorte.Y) / recorte.Height, 4),
                ["etiqueta"] = marca["etiqueta"]!.GetValue<string>(),
            });
        }

        using var final = imagen.Clone(recorte, PixelFormat.Format32bppArgb);
        final.Save(destino, ImageFormat.Png);
        GuardarMarcas(destino, marcas);
    }

    private static void Pintar(Graphics g, IntPtr hwnd, Rectangle marco)
    {
        Win32.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;
        using var parcial = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var gp = Graphics.FromImage(parcial))
        {
            var hdc = gp.GetHdc();
            Win32.PrintWindow(hwnd, hdc, 2); // PW_RENDERFULLCONTENT: incluye contenido DirectX
            gp.ReleaseHdc(hdc);
        }
        g.DrawImageUnscaled(parcial, r.Left - marco.X, r.Top - marco.Y);
    }

    private static (double X, double Y) Punto(System.Windows.Rect r, string en, double escala)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, fuera = 16 * escala;
        return en switch
        {
            "der" => (r.Right, cy),
            "centro" => (cx, cy),
            "arriba" => (cx, r.Top),
            "abajo" => (cx, r.Bottom),
            "arriba-izq" => (r.Left, r.Top),
            "arriba-der" => (r.Right, r.Top),
            "izq-fuera" => (r.Left - fuera, cy),
            "der-fuera" => (r.Right + fuera, cy),
            _ => (r.Left, cy),
        };
    }

    /// <summary>Misma estructura que el capturador web; se relee justo antes de escribir.</summary>
    private static void GuardarMarcas(string destino, JsonArray marcas)
    {
        string ruta = Path.Combine(Path.GetDirectoryName(destino)!, "marcas.json");
        var todas = File.Exists(ruta) ? JsonNode.Parse(File.ReadAllText(ruta))!.AsObject() : new JsonObject();
        todas[Path.GetFileName(destino)] = marcas;
        var ordenado = new JsonObject();
        foreach (var (clave, valor) in todas.OrderBy(p => p.Key, StringComparer.Ordinal).ToList())
            ordenado[clave] = valor?.DeepClone();
        var opciones = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(ruta, ordenado.ToJsonString(opciones) + "\n", new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------
    // Volcado del árbol (para escribir capturas)
    // ------------------------------------------------------------------

    /// <summary>Vuelca el árbol de una ventana ("principal", parte del título o "todas").</summary>
    public void Volcar(string titulo, string archivo)
    {
        var ventanas = titulo switch
        {
            "todas" => Ventanas(),
            "principal" => [Principal()],
            _ => [EsperarVentana(titulo, 10000)],
        };
        var texto = new StringBuilder();
        void Recorrer(AutomationElement e, int nivel)
        {
            if (nivel > 40) return;
            try
            {
                var c = e.Current;
                var r = c.BoundingRectangle;
                texto.Append(' ', nivel * 2)
                    .Append(c.ControlType.ProgrammaticName.Replace("ControlType.", ""))
                    .Append(c.AutomationId is { Length: > 0 } a ? $" id={a}" : "")
                    .Append(c.Name is { Length: > 0 } n ? $" nombre=\"{n}\"" : "")
                    .Append(c.HelpText is { Length: > 0 } h ? $" ayuda=\"{h}\"" : "")
                    .Append(r.IsEmpty ? " (sin área)" : $" [{r.X:0},{r.Y:0} {r.Width:0}×{r.Height:0}]")
                    .AppendLine();
            }
            catch (ElementNotAvailableException) { return; }
            for (var hijo = TreeWalker.ControlViewWalker.GetFirstChild(e); hijo is not null;
                 hijo = TreeWalker.ControlViewWalker.GetNextSibling(hijo))
                Recorrer(hijo, nivel + 1);
        }
        foreach (var ventana in ventanas) Recorrer(ventana, 0);
        File.WriteAllText(archivo, texto.ToString(), new UTF8Encoding(false));
    }
}

internal static class Win32
{
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    public static readonly IntPtr HwndBottom = new(1);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Terminal.Gui;

namespace CLRBuild;

/// <summary>Un instalador/entregable que build-installers.ps1 sabe generar.</summary>
internal sealed record Target(string Key, string Title);

internal static class Program
{
    static readonly Target[] Targets =
    {
        new("Suite",       "Suite completa (servidor + panel + cliente opcional)"),
        new("Client",      "Cliente de escritorio (puestos de operación)"),
        new("Complemento", "Complemento de enrolamiento (lector de huellas)"),
        new("Migrador",    "Migrador desde HikCentral (.zip)"),
    };

    static string Repo = "";
    static string Script => Path.Combine(Repo, "installer", "build-installers.ps1");

    [STAThread]
    static int Main(string[] args)
    {
        var opt = Options.Parse(args);
        if (opt.Error != null) { Console.Error.WriteLine(opt.Error); PrintHelp(); return 2; }
        if (opt.Help) { PrintHelp(); return 0; }
        if (opt.List) { foreach (var t in Targets) Console.WriteLine($"{t.Key,-12} {t.Title}"); return 0; }

        try { Repo = FindRepo(); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }

        // Modo silencioso: sin menú, salida de texto plano, código de salida
        // 0 = ok (pensado para que lo invoque una IA, un script o Jenkins).
        if (opt.Silent) return RunSilent(opt);

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("No hay terminal interactiva: use --silent --only <suite,client,...>.");
            return 2;
        }
        RunMenu(opt);
        return 0;
    }

    // ------------------------------------------------------------------ CLI
    static int RunSilent(Options o)
    {
        if (o.Only.Count == 0)
        {
            Console.Error.WriteLine("Modo silencioso: indique --only <suite,client,complemento,migrador|all>.");
            return 2;
        }
        Console.WriteLine($"[clrbuild] Compilando: {string.Join(", ", o.Only)}" +
                          (o.SkipPublish ? " (sin republicar)" : "") + (o.Version != null ? $" v{o.Version}" : ""));
        var sw = Stopwatch.StartNew();
        int code = RunScript(o, Console.WriteLine);
        Console.WriteLine(code == 0
            ? $"[clrbuild] OK en {sw.Elapsed:mm\\:ss}. Salida: {Path.Combine(Repo, "dist")}"
            : $"[clrbuild] FALLÓ (código {code}) tras {sw.Elapsed:mm\\:ss}.");
        return code;
    }

    /// <summary>Lanza build-installers.ps1 y reenvía cada línea a <paramref name="onLine"/>.</summary>
    static int RunScript(Options o, Action<string> onLine, Action<Process>? onStart = null)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = Repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Script })
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-Solo");
        psi.ArgumentList.Add(string.Join(",", o.Only));
        if (o.Version != null) { psi.ArgumentList.Add("-Version"); psi.ArgumentList.Add(o.Version); }
        if (o.SkipPublish) psi.ArgumentList.Add("-SkipPublish");

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.Start();
        onStart?.Invoke(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }

    // ------------------------------------------------------------------ TUI
    static void RunMenu(Options o)
    {
        Application.Init();
        var top = Application.Top;

        var win = new Window("CLR TrueCentral VMS — Compilador de instaladores")
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
        };

        win.Add(new Label($"Versión del repositorio: {ReadVersion()}   |   Destino: {Path.Combine(Repo, "dist")}")
        { X = 1, Y = 0 });
        win.Add(new Label("¿Qué desea compilar? (Espacio marca/desmarca)") { X = 1, Y = 2 });

        var checks = new List<CheckBox>();
        int y = 3;
        foreach (var t in Targets)
        {
            var cb = new CheckBox(t.Title)
            {
                X = 3, Y = y++,
                Checked = o.Only.Count == 0 ? t.Key is "Suite" or "Client" : o.Only.Contains(t.Key),
            };
            checks.Add(cb);
            win.Add(cb);
        }

        var versionLbl = new Label("Versión (vacío = archivo VERSION):") { X = 1, Y = y + 1 };
        var versionTxt = new TextField(o.Version ?? "") { X = Pos.Right(versionLbl) + 1, Y = y + 1, Width = 12 };
        var skip = new CheckBox("No volver a publicar (reutiliza build\\publish)") { X = 3, Y = y + 2, Checked = o.SkipPublish };
        win.Add(versionLbl, versionTxt, skip);

        var logFrame = new FrameView("Salida") { X = 0, Y = y + 4, Width = Dim.Fill(), Height = Dim.Fill(2) };
        var log = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = false };
        logFrame.Add(log);
        win.Add(logFrame);

        var status = new Label("Listo.") { X = 1, Y = Pos.AnchorEnd(1), Width = 34 };
        var build = new Button("_Compilar", true) { X = 36, Y = Pos.AnchorEnd(1) };
        var all = new Button("_Todos") { X = Pos.Right(build) + 1, Y = Pos.AnchorEnd(1) };
        var none = new Button("_Ninguno") { X = Pos.Right(all) + 1, Y = Pos.AnchorEnd(1) };
        var quit = new Button("_Salir") { X = Pos.Right(none) + 1, Y = Pos.AnchorEnd(1) };
        win.Add(status, build, all, none, quit);

        Process? running = null;
        var buffer = new StringBuilder();

        void SetBusy(bool busy)
        {
            foreach (var c in checks) c.Enabled = !busy;
            versionTxt.Enabled = !busy; skip.Enabled = !busy;
            build.Enabled = !busy; all.Enabled = !busy; none.Enabled = !busy;
        }

        void Append(string line)
        {
            lock (buffer) buffer.AppendLine(line);
            Application.MainLoop.Invoke(() =>
            {
                string text; lock (buffer) text = buffer.ToString();
                log.Text = text;
                log.MoveEnd();
                log.SetNeedsDisplay();
            });
        }

        build.Clicked += () =>
        {
            var selected = Targets.Where((_, i) => checks[i].Checked).Select(t => t.Key).ToList();
            if (selected.Count == 0) { MessageBox.ErrorQuery("Compilador", "Marque al menos un instalador.", "Ok"); return; }
            var ver = versionTxt.Text.ToString()?.Trim();
            if (!string.IsNullOrEmpty(ver) && !Regex.IsMatch(ver, @"^\d+\.\d+\.\d+$"))
            { MessageBox.ErrorQuery("Compilador", "Versión inválida (se espera x.y.z).", "Ok"); return; }

            var opts = new Options { Only = selected, Version = string.IsNullOrEmpty(ver) ? null : ver, SkipPublish = skip.Checked };
            lock (buffer) buffer.Clear();
            log.Text = "";
            SetBusy(true);
            status.Text = "Compilando…";
            var sw = Stopwatch.StartNew();
            Task.Run(() =>
            {
                int code;
                try { code = RunScript(opts, Append, p => running = p); }
                catch (Exception ex) { Append("Error: " + ex.Message); code = -1; }
                running = null;
                Application.MainLoop.Invoke(() =>
                {
                    SetBusy(false);
                    status.Text = code == 0 ? $"OK en {sw.Elapsed:mm\\:ss}." : $"FALLÓ ({code}) tras {sw.Elapsed:mm\\:ss}.";
                    MessageBox.Query("Compilador", code == 0
                        ? $"Instaladores generados en:\n{Path.Combine(Repo, "dist")}"
                        : "La compilación falló. Revise la salida.", "Ok");
                });
            });
        };
        all.Clicked += () => { foreach (var c in checks) c.Checked = true; };
        none.Clicked += () => { foreach (var c in checks) c.Checked = false; };
        quit.Clicked += () =>
        {
            if (running != null && MessageBox.Query("Compilador", "Hay una compilación en curso. ¿Cancelarla y salir?", "Sí", "No") != 0) return;
            try { running?.Kill(true); } catch { }
            Application.RequestStop();
        };

        top.Add(win);
        Application.Run();
        Application.Shutdown();
    }

    // ------------------------------------------------------------- helpers
    static string ReadVersion()
    {
        var f = Path.Combine(Repo, "VERSION");
        return File.Exists(f) ? File.ReadLines(f).FirstOrDefault()?.Trim() ?? "?" : "?";
    }

    static string FindRepo()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "installer", "build-installers.ps1"))) return d.FullName;
        throw new InvalidOperationException("No se encontró installer\\build-installers.ps1: ejecute la herramienta desde el repositorio.");
    }

    static void PrintHelp() => Console.WriteLine("""
        clrbuild — compilador de instaladores de CLR TrueCentral VMS

        Sin argumentos abre el menú interactivo (Terminal.Gui).

        Modo silencioso (sin menú, para scripts / IA / CI):
          clrbuild --silent --only suite,client [--version x.y.z] [--skip-publish]

        Opciones:
          -s, --silent        No abre el menú; salida de texto plano, código de salida 0/distinto de 0.
          -o, --only <lista>  suite, client, complemento, migrador o all (separados por coma).
              --version <v>   Fuerza la versión x.y.z (por defecto, el archivo VERSION).
              --skip-publish  Reutiliza build\publish (no vuelve a publicar).
              --list          Lista los instaladores disponibles.
          -h, --help          Esta ayuda.

        En el menú, --only/--version/--skip-publish sirven de valores iniciales.
        """);
}

internal sealed class Options
{
    public bool Silent, Help, List, SkipPublish;
    public string? Version, Error;
    public List<string> Only { get; set; } = new();

    static readonly string[] Keys = { "Suite", "Client", "Complemento", "Migrador" };

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i].ToLowerInvariant())
            {
                case "-s": case "--silent": o.Silent = true; break;
                case "-h": case "--help": case "-?": o.Help = true; break;
                case "--list": o.List = true; break;
                case "--skip-publish": o.SkipPublish = true; break;
                case "--version":
                    if (++i >= a.Length) return new Options { Error = "--version necesita un valor." };
                    o.Version = a[i]; break;
                case "-o": case "--only":
                    if (++i >= a.Length) return new Options { Error = "--only necesita una lista." };
                    foreach (var part in a[i].Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (part.Equals("all", StringComparison.OrdinalIgnoreCase) || part.Equals("todos", StringComparison.OrdinalIgnoreCase))
                        { o.Only.AddRange(Keys); continue; }
                        var k = Keys.FirstOrDefault(x => x.Equals(part, StringComparison.OrdinalIgnoreCase));
                        if (k == null) return new Options { Error = $"Instalador desconocido: '{part}'." };
                        o.Only.Add(k);
                    }
                    o.Only = o.Only.Distinct().ToList();
                    break;
                default: return new Options { Error = $"Argumento desconocido: '{a[i]}'." };
            }
        }
        if (o.Version != null && !Regex.IsMatch(o.Version, @"^\d+\.\d+\.\d+$"))
            return new Options { Error = "Versión inválida (se espera x.y.z)." };
        return o;
    }
}

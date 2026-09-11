using System.Text;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Migrator.HikCentral;
using TrueCentralVms.Migrator.Migration;
using TrueCentralVms.Migrator.Terminals;
using TrueCentralVms.Migrator.TrueCentral;

namespace TrueCentralVms.Migrator;

/// <summary>
/// Ventana única con cuatro pasos —HikCentral, terminales, TrueCentral,
/// migrar— y, siempre a la vista, la barra de avance y el registro de lo
/// que va pasando. Todo el trabajo corre fuera del hilo de la interfaz y
/// vuelve por <see cref="Progress{T}"/>.
/// </summary>
public sealed class MainForm : Form
{
    private readonly MigratorSettings _settings = MigratorSettings.Load();
    private CancellationTokenSource? _cts;
    private readonly StringBuilder _logText = new();

    // --- Paso 1: HikCentral
    private readonly TextBox _hcpUrl = new();
    private readonly TextBox _hcpKey = new();
    private readonly TextBox _hcpSecret = new() { UseSystemPasswordChar = true };
    private readonly Button _hcpConnect = new() { Text = "Conectar y leer el padrón", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
    private readonly Label _hcpStatus = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(700, 0) };
    private readonly CheckedListBox _orgs = new() { CheckOnClick = true, IntegralHeight = false, MinimumSize = new Size(0, 120) };
    private List<HcpOrganization> _hcpOrgs = [];
    private List<HcpPerson> _hcpPersons = [];
    private string _hcpVersion = "";

    // --- Paso 2: terminales
    private readonly CheckBox _readTerminals = new() { Text = "Leer desde los terminales las plantillas de huella y el legajo con el que conocen a cada persona", AutoSize = true, Checked = true };
    private readonly TextBox _termUser = new() { Width = 140 };
    private readonly TextBox _termPass = new() { Width = 140, UseSystemPasswordChar = true };
    private readonly DataGridView _terminals = new();
    private readonly Label _termStatus = new() { AutoSize = true, ForeColor = Color.DimGray };

    // --- Paso 3: TrueCentral
    private readonly TextBox _tcUrl = new();
    private readonly TextBox _tcUser = new();
    private readonly TextBox _tcPass = new() { UseSystemPasswordChar = true };
    private readonly Button _tcConnect = new() { Text = "Conectar", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
    private readonly Label _tcStatus = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(700, 0) };
    private readonly RadioButton _idTerminal = new() { Text = "El legajo que usan los terminales (recomendado si los equipos Hikvision siguen en uso: evita duplicarlas)", AutoSize = true, Checked = true };
    private readonly RadioButton _idPersonCode = new() { Text = "El código de persona de HikCentral", AutoSize = true };
    private readonly RadioButton _idAssigned = new() { Text = "Dejar que TrueCentral asigne uno nuevo", AutoSize = true };
    private readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _updateExisting = new() { Text = "Completar a las personas que ya existen: agregarles las huellas, tarjetas y foto que les falten (no cambia nombre, vigencia, clave ni niveles)", AutoSize = true, Checked = true };
    private readonly CheckBox _import = new() { Text = "Importar a TrueCentral al terminar de exportar (si se desmarca, solo se guarda el paquete)", AutoSize = true, Checked = true };
    private int _tcExisting = -1;

    // --- Paso 4: migrar
    private readonly Label _summary = new() { AutoSize = true, MaximumSize = new Size(760, 0) };
    private readonly TextBox _folder = new();
    private readonly CheckBox _photos = new() { Text = "Descargar las fotos de carnet (rostro para los terminales con cámara)", AutoSize = true, Checked = true };
    private readonly Button _start = new() { Text = "▶  Iniciar migración", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 6, 16, 6), Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
    private readonly Button _importPackage = new() { Text = "Importar un paquete guardado…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };

    // --- Comunes
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 18, Style = ProgressBarStyle.Continuous, Maximum = 1000 };
    private readonly Label _status = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 3, 0, 3), Text = "Listo." };
    private readonly RichTextBox _log = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(250, 250, 250), Font = new Font("Consolas", 9f), WordWrap = true, DetectUrls = false };
    private readonly Button _back = new() { Text = "◀ Atrás", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
    private readonly Button _next = new() { Text = "Siguiente ▶", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
    private readonly Button _cancel = new() { Text = "Cancelar", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3), Enabled = false };
    private readonly Button _copyLog = new() { Text = "Copiar registro", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
    private readonly Button _saveLog = new() { Text = "Guardar registro…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
    private readonly CheckBox _remember = new() { Text = "Recordar credenciales en este equipo", AutoSize = true };

    public MainForm()
    {
        Text = "Migrador desde HikCentral · CLR TrueCentral VMS";
        Font = new Font("Segoe UI", 9.75f);
        StartPosition = FormStartPosition.CenterScreen;

        // Mismo orden que genera el diseñador: se arma todo a 96 ppp con el
        // diseño suspendido y, al reanudarlo, WinForms escala ventana, anchos
        // y márgenes al DPI real del monitor (PerMonitorV2 en el csproj).
        SuspendLayout();
        Controls.Add(BuildBody());
        Controls.Add(BuildFooter());
        Controls.Add(BuildHeader());
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1120, 840);
        MinimumSize = new Size(1000, 740);
        ResumeLayout(false);
        PerformLayout();

        LoadSettings();
        _tabs.SelectedIndexChanged += (_, _) => OnTabChanged();
        OnTabChanged();
        FormClosing += (_, e) =>
        {
            if (_cts is not null && !_cts.IsCancellationRequested)
            {
                if (MessageBox.Show(this, "Hay una migración en curso. ¿Cancelarla y salir?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) { e.Cancel = true; return; }
                _cts.Cancel();
            }
            SaveSettings();
        };
        Log(LogLevel.Info, $"Migrador desde HikCentral v{TrueCentralClient.Version}. Complete los pasos 1 a 3 y pulse Iniciar en el paso 4.");
    }

    // ==================================================================
    // Construcción de la interfaz
    // ==================================================================

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.FromArgb(24, 33, 48) };
        var logo = new PictureBox { Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(14, 9) };
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("logo.png");
            if (stream is not null) logo.Image = Image.FromStream(stream);
        }
        catch (Exception) { /* sin logo no pasa nada */ }
        var title = new Label
        {
            Text = "Migrador desde HikCentral",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 14f),
            AutoSize = true,
            Margin = new Padding(0),
        };
        var subtitle = new Label
        {
            Text = "Trae al padrón de TrueCentral las personas, fotos, tarjetas y huellas de HikCentral Professional, sin volver a enrolar a nadie.",
            ForeColor = Color.FromArgb(200, 210, 225),
            AutoSize = true,
            Margin = new Padding(1, 0, 0, 0),
        };
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Location = new Point(72, 6), Margin = new Padding(0) };
        text.Controls.AddRange([title, subtitle]);
        header.Controls.AddRange([logo, text]);
        return header;
    }

    private Control BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 6, 10, 6) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        buttons.Controls.AddRange([_back, _next, _cancel, _copyLog, _saveLog, _remember]);
        _remember.Margin = new Padding(24, 6, 0, 0);
        footer.Controls.Add(buttons);
        footer.Controls.Add(_status);
        footer.Controls.Add(_progress);

        _back.Click += (_, _) => _tabs.SelectedIndex = Math.Max(0, _tabs.SelectedIndex - 1);
        _next.Click += (_, _) => _tabs.SelectedIndex = Math.Min(_tabs.TabCount - 1, _tabs.SelectedIndex + 1);
        _cancel.Click += (_, _) => { _cts?.Cancel(); Log(LogLevel.Warning, "Cancelando… se termina la operación en curso."); };
        _copyLog.Click += (_, _) => { if (_logText.Length > 0) Clipboard.SetText(_logText.ToString()); };
        _saveLog.Click += (_, _) => SaveLogDialog();
        return footer;
    }

    private Control BuildBody()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        split.Panel1.Controls.Add(_tabs);
        var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
        logPanel.Controls.Add(_log);
        logPanel.Controls.Add(new Label { Text = "Registro del proceso", Dock = DockStyle.Top, AutoSize = true, Font = new Font("Segoe UI Semibold", 9.75f), Padding = new Padding(0, 4, 0, 3) });
        split.Panel2.Controls.Add(logPanel);
        split.Panel1MinSize = 300;
        split.Panel2MinSize = 140;
        // La proporción se fija una vez escalada la ventana (Load), no en píxeles de diseño.
        Load += (_, _) => split.SplitterDistance = (int)(split.Height * 0.62);

        _tabs.TabPages.Add(BuildHikCentralTab());
        _tabs.TabPages.Add(BuildTerminalsTab());
        _tabs.TabPages.Add(BuildTrueCentralTab());
        _tabs.TabPages.Add(BuildMigrateTab());
        return split;
    }

    private static TableLayoutPanel Grid(int rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = rows, Padding = new Padding(12), AutoScroll = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 4),
    };

    private static Label Hint(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 8), MaximumSize = new Size(760, 0),
    };

    private static void AddRow(TableLayoutPanel grid, int row, string caption, Control control, bool fill = true)
    {
        grid.Controls.Add(Caption(caption), 0, row);
        if (fill) { control.Dock = DockStyle.Fill; control.Margin = new Padding(0, 6, 0, 4); }
        grid.Controls.Add(control, 1, row);
    }

    private TabPage BuildHikCentralTab()
    {
        var page = new TabPage("  1 · HikCentral  ");
        var grid = Grid(7);
        grid.Controls.Add(Hint("Datos de la OpenAPI de HikCentral Professional. La clave y el secreto se crean en HikCentral → Sistema → Integración de terceros → OpenAPI (Integration Partner). Se necesita HikCentral 2.x o 3.x con la OpenAPI habilitada."), 0, 0);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 0)!, 2);
        AddRow(grid, 1, "Dirección del servidor", _hcpUrl);
        _hcpUrl.PlaceholderText = "https://200.55.209.84";
        AddRow(grid, 2, "Clave del socio (AppKey)", _hcpKey);
        AddRow(grid, 3, "Secreto del socio (AppSecret)", _hcpSecret);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 4) };
        buttons.Controls.AddRange([_hcpConnect, _hcpStatus]);
        _hcpStatus.Margin = new Padding(12, 8, 0, 0);
        grid.Controls.Add(buttons, 1, 4);

        var orgBox = new GroupBox { Text = "Departamentos a migrar (se completan al conectar)", Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(0, 8, 0, 0) };
        var orgLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        orgLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        orgLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _orgs.Dock = DockStyle.Fill;
        var orgButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var all = new Button { Text = "Todos", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
        var none = new Button { Text = "Ninguno", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
        all.Click += (_, _) => { for (int i = 0; i < _orgs.Items.Count; i++) _orgs.SetItemChecked(i, true); };
        none.Click += (_, _) => { for (int i = 0; i < _orgs.Items.Count; i++) _orgs.SetItemChecked(i, false); };
        orgButtons.Controls.AddRange([all, none]);
        orgLayout.Controls.Add(_orgs, 0, 0);
        orgLayout.Controls.Add(orgButtons, 0, 1);
        orgBox.Controls.Add(orgLayout);
        grid.Controls.Add(orgBox, 0, 5);
        grid.SetColumnSpan(orgBox, 2);
        grid.RowStyles.Clear();
        for (int i = 0; i < 5; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _hcpConnect.Click += async (_, _) => await ConnectHikCentralAsync();
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildTerminalsTab()
    {
        var page = new TabPage("  2 · Terminales (huellas)  ");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // sin esto la columna se ensancha al ancho preferido de la grilla
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(Hint("HikCentral no entrega las plantillas de huella por su OpenAPI, pero los terminales sí las devuelven por ISAPI. La lista se completa sola con los equipos registrados en HikCentral al conectar en el paso 1; el puerto es el HTTP del equipo (80 por defecto). Hace falta estar en la misma red que los terminales."), 0, 0);
        layout.Controls.Add(_readTerminals, 0, 1);
        _readTerminals.Margin = new Padding(0, 0, 0, 8);

        var creds = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        var apply = new Button { Text = "Aplicar a todos", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
        var probe = new Button { Text = "Probar conexión", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
        var add = new Button { Text = "Agregar", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
        var remove = new Button { Text = "Quitar", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
        creds.Controls.AddRange([
            new Label { Text = "Usuario", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _termUser,
            new Label { Text = "Contraseña", AutoSize = true, Margin = new Padding(10, 6, 4, 0) }, _termPass,
            apply, probe, add, remove, _termStatus,
        ]);
        _termStatus.Margin = new Padding(12, 6, 0, 0);
        layout.Controls.Add(creds, 0, 2);

        _terminals.Dock = DockStyle.Fill;
        _terminals.AllowUserToAddRows = false;
        _terminals.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _terminals.RowHeadersVisible = false;
        _terminals.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _terminals.MultiSelect = false;
        _terminals.BackgroundColor = SystemColors.Window;
        _terminals.Columns.Add(new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "Usar", FillWeight = 8 });
        _terminals.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Nombre", FillWeight = 26 });
        _terminals.Columns.Add(new DataGridViewTextBoxColumn { Name = "host", HeaderText = "Dirección IP", FillWeight = 18 });
        _terminals.Columns.Add(new DataGridViewTextBoxColumn { Name = "port", HeaderText = "Puerto", FillWeight = 8 });
        _terminals.Columns.Add(new DataGridViewTextBoxColumn { Name = "user", HeaderText = "Usuario", FillWeight = 12 });
        _terminals.Columns.Add(new DataGridViewTextBoxColumn { Name = "pass", HeaderText = "Contraseña", FillWeight = 12 });
        _terminals.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "Estado", FillWeight = 26, ReadOnly = true });
        _terminals.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex == _terminals.Columns["pass"]!.Index && e.Value is string s && s.Length > 0)
            {
                e.Value = new string('•', Math.Min(8, s.Length));
                e.FormattingApplied = true;
            }
        };
        _terminals.EditingControlShowing += (_, e) =>
        {
            if (e.Control is TextBox tb)
                tb.UseSystemPasswordChar = _terminals.CurrentCell?.OwningColumn?.Name == "pass";
        };
        layout.Controls.Add(_terminals, 0, 3);

        apply.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in _terminals.Rows)
            {
                row.Cells["user"].Value = _termUser.Text;
                row.Cells["pass"].Value = _termPass.Text;
            }
        };
        add.Click += (_, _) => AddTerminalRow(true, "Terminal nuevo", "", 80, _termUser.Text, _termPass.Text);
        remove.Click += (_, _) => { if (_terminals.CurrentRow is { } row) _terminals.Rows.Remove(row); };
        probe.Click += async (_, _) => await ProbeTerminalsAsync();

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildTrueCentralTab()
    {
        var page = new TabPage("  3 · TrueCentral  ");
        var grid = Grid(10);
        grid.Controls.Add(Hint("Servidor TrueCentral donde se va a cargar el padrón. Hace falta un usuario administrador; cada alta queda en la bitácora del sistema."), 0, 0);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 0)!, 2);
        AddRow(grid, 1, "Dirección del servidor", _tcUrl);
        _tcUrl.PlaceholderText = "http://192.168.10.232:5080";
        AddRow(grid, 2, "Usuario", _tcUser);
        AddRow(grid, 3, "Contraseña", _tcPass);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 4) };
        buttons.Controls.AddRange([_tcConnect, _tcStatus]);
        _tcStatus.Margin = new Padding(12, 8, 0, 0);
        grid.Controls.Add(buttons, 1, 4);
        AddRow(grid, 5, "Nivel de acceso a asignar", _level);
        _level.Items.Add(new LevelItem(null, "(ninguno: asignar después en TrueCentral)"));
        _level.SelectedIndex = 0;

        var idBox = new GroupBox { Text = "Identificador de empleado (es el que los equipos usan para reconocer a la persona)", AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 10, 0, 0) };
        var idLayout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        idLayout.Controls.AddRange([_idTerminal, _idPersonCode, _idAssigned]);
        idBox.Controls.Add(idLayout);
        grid.Controls.Add(idBox, 0, 6);
        grid.SetColumnSpan(idBox, 2);
        grid.Controls.Add(_updateExisting, 0, 7);
        grid.SetColumnSpan(_updateExisting, 2);
        _updateExisting.Margin = new Padding(0, 10, 0, 0);
        grid.Controls.Add(_import, 0, 8);
        grid.SetColumnSpan(_import, 2);
        _import.Margin = new Padding(0, 6, 0, 0);
        grid.Controls.Add(Hint("Con un nivel de acceso asignado, el sincronizador baja a las personas a los equipos de ese nivel apenas se crean. Si no se marca «completar», las personas que ya existan con el mismo identificador se omiten. Una tarjeta que ya sea de otra persona se deja fuera y se avisa en el registro. Las claves de teclado no se pueden recuperar de HikCentral."), 0, 9);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 9)!, 2);
        grid.GetControlFromPosition(0, 9)!.Margin = new Padding(0, 10, 0, 0);

        _tcConnect.Click += async (_, _) => await ConnectTrueCentralAsync();
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildMigrateTab()
    {
        var page = new TabPage("  4 · Migrar  ");
        var grid = Grid(6);
        var summaryBox = new GroupBox { Text = "Resumen", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 0, 0, 10), Dock = DockStyle.Fill };
        // Un contenedor con alto automático dentro del GroupBox: así el cuadro
        // crece con el texto y respeta el espacio del título a cualquier DPI.
        var summaryInner = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 1 };
        _summary.Dock = DockStyle.Fill;
        _summary.Margin = new Padding(0, 4, 0, 0);
        summaryInner.Controls.Add(_summary);
        summaryBox.Controls.Add(summaryInner);
        grid.Controls.Add(summaryBox, 0, 0);
        grid.SetColumnSpan(summaryBox, 2);

        var folderRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 4) };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _folder.Dock = DockStyle.Fill;
        var browse = new Button { Text = "Elegir…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3) };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Carpeta donde guardar el paquete de migración", UseDescriptionForTitle = true, SelectedPath = _folder.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
        };
        folderRow.Controls.Add(_folder, 0, 0);
        folderRow.Controls.Add(browse, 1, 0);
        AddRow(grid, 1, "Carpeta del paquete", folderRow, fill: false);
        folderRow.Dock = DockStyle.Fill;
        grid.Controls.Add(Hint("El paquete (paquete.json + fotos) queda guardado como respaldo y permite importar más tarde en otro servidor con «Importar un paquete guardado». Contiene datos personales y biométricos: bórrelo cuando termine."), 1, 2);
        grid.Controls.Add(_photos, 1, 3);
        _photos.Margin = new Padding(0, 6, 0, 10);

        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 10, 0, 0) };
        actions.Controls.AddRange([_start, _importPackage]);
        _importPackage.Margin = new Padding(16, 10, 0, 0);
        grid.Controls.Add(actions, 1, 4);

        _start.Click += async (_, _) => await RunMigrationAsync();
        _importPackage.Click += async (_, _) => await ImportSavedPackageAsync();
        page.Controls.Add(grid);
        return page;
    }

    // ==================================================================
    // Configuración recordada
    // ==================================================================

    private void LoadSettings()
    {
        var s = _settings;
        _hcpUrl.Text = s.HcpUrl;
        _hcpKey.Text = s.HcpAppKey;
        _hcpSecret.Text = MigratorSettings.Unprotect(s.HcpAppSecretProtected);
        _readTerminals.Checked = s.ReadTerminals;
        _termUser.Text = s.TerminalUsername;
        _termPass.Text = MigratorSettings.Unprotect(s.TerminalPasswordProtected);
        foreach (var t in s.Terminals)
            AddTerminalRow(t.Enabled, t.Name, t.Host, t.Port, t.Username, MigratorSettings.Unprotect(t.PasswordProtected));
        _tcUrl.Text = s.TrueCentralUrl;
        _tcUser.Text = s.TrueCentralUsername;
        _tcPass.Text = MigratorSettings.Unprotect(s.TrueCentralPasswordProtected);
        _idTerminal.Checked = s.EmployeeNoSource == 0;
        _idPersonCode.Checked = s.EmployeeNoSource == 1;
        _idAssigned.Checked = s.EmployeeNoSource == 2;
        _photos.Checked = s.DownloadPhotos;
        _import.Checked = s.ImportToTrueCentral;
        _updateExisting.Checked = s.UpdateExisting;
        _remember.Checked = s.RememberSecrets;
        _folder.Text = string.IsNullOrWhiteSpace(s.PackageFolder) ? DefaultFolder() : s.PackageFolder;
    }

    private static string DefaultFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TrueCentral",
        $"Migracion-HikCentral-{DateTime.Now:yyyyMMdd-HHmm}");

    private void SaveSettings()
    {
        var s = _settings;
        bool remember = _remember.Checked;
        s.HcpUrl = _hcpUrl.Text.Trim();
        s.HcpAppKey = _hcpKey.Text.Trim();
        s.HcpAppSecretProtected = remember ? MigratorSettings.Protect(_hcpSecret.Text) : "";
        s.ReadTerminals = _readTerminals.Checked;
        s.TerminalUsername = _termUser.Text;
        s.TerminalPasswordProtected = remember ? MigratorSettings.Protect(_termPass.Text) : "";
        s.Terminals = TerminalRows().Select(r => new TerminalSetting
        {
            Enabled = r.Enabled, Name = r.Name, Host = r.Host, Port = r.Port, Username = r.Username,
            PasswordProtected = remember ? MigratorSettings.Protect(r.Password) : "",
        }).ToList();
        s.TrueCentralUrl = _tcUrl.Text.Trim();
        s.TrueCentralUsername = _tcUser.Text.Trim();
        s.TrueCentralPasswordProtected = remember ? MigratorSettings.Protect(_tcPass.Text) : "";
        s.EmployeeNoSource = _idPersonCode.Checked ? 1 : _idAssigned.Checked ? 2 : 0;
        s.TrueCentralLevelId = SelectedLevelId() ?? s.TrueCentralLevelId;
        s.DownloadPhotos = _photos.Checked;
        s.ImportToTrueCentral = _import.Checked;
        s.UpdateExisting = _updateExisting.Checked;
        s.RememberSecrets = remember;
        s.PackageFolder = _folder.Text.Trim();
        s.Save();
    }

    // ==================================================================
    // Terminales (grilla)
    // ==================================================================

    private sealed record TerminalRow(bool Enabled, string Name, string Host, int Port, string Username, string Password, DataGridViewRow Row);

    private void AddTerminalRow(bool enabled, string name, string host, int port, string user, string pass)
    {
        int i = _terminals.Rows.Add(enabled, name, host, port.ToString(), user, pass, "");
        _terminals.Rows[i].Tag = null;
    }

    private List<TerminalRow> TerminalRows()
    {
        _terminals.EndEdit();
        var list = new List<TerminalRow>();
        foreach (DataGridViewRow row in _terminals.Rows)
        {
            string host = (row.Cells["host"].Value?.ToString() ?? "").Trim();
            if (host.Length == 0 && row.Cells["name"].Value is null) continue;
            list.Add(new TerminalRow(
                row.Cells["use"].Value is true,
                (row.Cells["name"].Value?.ToString() ?? "").Trim(),
                host,
                int.TryParse(row.Cells["port"].Value?.ToString(), out int port) && port > 0 ? port : 80,
                (row.Cells["user"].Value?.ToString() ?? "").Trim(),
                row.Cells["pass"].Value?.ToString() ?? "",
                row));
        }
        return list;
    }

    private List<TerminalTarget> SelectedTerminals() => !_readTerminals.Checked ? [] :
        TerminalRows().Where(r => r.Enabled && r.Host.Length > 0)
            .Select(r => new TerminalTarget(r.Name.Length > 0 ? r.Name : r.Host, r.Host, r.Port, r.Username, r.Password)).ToList();

    private void MergeTerminalsFromHikCentral(List<HcpAccessDevice> devices)
    {
        var known = TerminalRows().Select(r => r.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var d in devices)
        {
            if (string.IsNullOrWhiteSpace(d.Ip) || known.Contains(d.Ip)) continue;
            // El código del equipo en HCP empieza por el modelo: sirve para reconocer un DS-K1T… a simple vista.
            string name = d.Name.Length > 0 ? d.Name : d.Ip;
            AddTerminalRow(true, name, d.Ip, 80, _termUser.Text, _termPass.Text);
            added++;
        }
        if (added > 0) Log(LogLevel.Info, $"Se agregaron {added} terminal(es) desde HikCentral a la lista del paso 2 (puerto 80; ajústelo si el equipo usa otro).");
    }

    private async Task ProbeTerminalsAsync()
    {
        var rows = TerminalRows().Where(r => r.Enabled && r.Host.Length > 0).ToList();
        if (rows.Count == 0) { _termStatus.Text = "No hay terminales marcados."; return; }
        SetBusy(true, allowCancel: true);
        try
        {
            int ok = 0;
            foreach (var r in rows)
            {
                _cts!.Token.ThrowIfCancellationRequested();
                r.Row.Cells["state"].Value = "probando…";
                try
                {
                    var reader = new TerminalReader(r.Host, r.Port, r.Username, r.Password);
                    var info = await reader.ProbeAsync(_cts.Token);
                    r.Row.Cells["state"].Value = $"OK · {info.Model} · {info.Firmware}";
                    ok++;
                    Log(LogLevel.Success, $"[{r.Name}] {r.Host}: {info.Model}, serie {info.SerialNumber}, firmware {info.Firmware}.");
                }
                catch (DriverException ex)
                {
                    r.Row.Cells["state"].Value = ex.Message;
                    Log(LogLevel.Error, $"[{r.Name}] {r.Host}: {ex.Message}");
                }
            }
            _termStatus.Text = $"Responden {ok} de {rows.Count}.";
            SaveSettings();
        }
        catch (OperationCanceledException) { _termStatus.Text = "Prueba cancelada."; }
        finally { SetBusy(false); }
    }

    // ==================================================================
    // Conexiones de prueba
    // ==================================================================

    private async Task ConnectHikCentralAsync()
    {
        if (!ValidateHikCentralFields()) return;
        SetBusy(true, allowCancel: true);
        _hcpStatus.Text = "Conectando…";
        try
        {
            using var hcp = new HikCentralClient(_hcpUrl.Text, _hcpKey.Text, _hcpSecret.Text);
            var ct = _cts!.Token;
            _hcpVersion = await hcp.GetVersionAsync(ct);
            _hcpOrgs = await hcp.GetOrganizationsAsync(ct);
            _hcpPersons = await hcp.GetPersonsAsync(new Progress<(int Done, int Total)>(p => _hcpStatus.Text = $"Leyendo personas… {p.Done}/{p.Total}"), ct);
            List<HcpAccessDevice> devices = [];
            try { devices = await hcp.GetAccessDevicesAsync(ct); }
            catch (HikCentralException ex) { Log(LogLevel.Warning, $"No se pudo listar los terminales desde HikCentral: {ex.Message}"); }

            FillOrganizations();
            MergeTerminalsFromHikCentral(devices);
            int withFingers = _hcpPersons.Count(p => p.Fingerprints.Count > 0);
            int withCards = _hcpPersons.Count(p => p.Cards.Count > 0);
            int withPhoto = _hcpPersons.Count(p => p.PicUri.Length > 0);
            _hcpStatus.Text = $"{_hcpVersion} · {_hcpPersons.Count} personas · {_hcpOrgs.Count} departamentos · {devices.Count} terminales";
            SaveSettings();
            Log(LogLevel.Success, $"HikCentral: {_hcpVersion}. Personas: {_hcpPersons.Count} (con tarjeta {withCards}, con huellas {withFingers}, con foto {withPhoto}). Terminales registrados: {devices.Count}.");
            if (withFingers > 0 && !_hcpPersons.Any(p => p.Fingerprints.Any(f => f.Data.Length > 0)))
                Log(LogLevel.Info, "Esta versión no entrega las plantillas de huella por OpenAPI: se leerán desde los terminales (paso 2).");
        }
        catch (OperationCanceledException) { _hcpStatus.Text = "Cancelado."; }
        catch (HikCentralException ex)
        {
            _hcpStatus.Text = "No se pudo conectar.";
            Log(LogLevel.Error, ex.Message);
        }
        finally { SetBusy(false); UpdateSummary(); }
    }

    private void FillOrganizations()
    {
        var counts = _hcpPersons.GroupBy(p => p.OrgIndexCode).ToDictionary(g => g.Key, g => g.Count());
        var byCode = _hcpOrgs.ToDictionary(o => o.IndexCode);
        _orgs.Items.Clear();
        foreach (var org in _hcpOrgs.OrderBy(o => o.Name))
        {
            if (org.ParentIndexCode == "0" && counts.GetValueOrDefault(org.IndexCode) == 0) continue; // raíz vacía
            string parent = byCode.TryGetValue(org.ParentIndexCode, out var p) && p.ParentIndexCode != "0" ? $"{p.Name} / " : "";
            _orgs.Items.Add(new OrgItem(org.IndexCode, $"{parent}{org.Name}  ({counts.GetValueOrDefault(org.IndexCode)} personas)"), true);
        }
    }

    private sealed record OrgItem(string IndexCode, string Text)
    {
        public override string ToString() => Text;
    }

    private HashSet<string>? SelectedOrganizations()
    {
        if (_orgs.Items.Count == 0 || _orgs.CheckedItems.Count == _orgs.Items.Count) return null;
        return _orgs.CheckedItems.Cast<OrgItem>().Select(o => o.IndexCode).ToHashSet();
    }

    private async Task ConnectTrueCentralAsync()
    {
        if (!ValidateTrueCentralFields()) return;
        SetBusy(true, allowCancel: true);
        _tcStatus.Text = "Conectando…";
        try
        {
            using var tc = new TrueCentralClient();
            await tc.LoginAsync(_tcUrl.Text, _tcUser.Text, _tcPass.Text, _cts!.Token);
            var persons = await tc.GetPersonsAsync(_cts.Token);
            _tcExisting = persons.Count;
            var levels = await tc.GetLevelsAsync(_cts.Token);
            int? wanted = SelectedLevelId() ?? _settings.TrueCentralLevelId;
            _level.Items.Clear();
            _level.Items.Add(new LevelItem(null, "(ninguno: asignar después en TrueCentral)"));
            foreach (var l in levels.Where(l => l.Enabled).OrderBy(l => l.Name))
                _level.Items.Add(new LevelItem(l.Id, $"{l.Name} · {l.DoorNames.Count} puerta(s) · {l.ScheduleName}"));
            _level.SelectedIndex = Math.Max(0, _level.Items.Cast<LevelItem>().ToList().FindIndex(i => i.Id == wanted));
            Log(LogLevel.Info, $"Niveles de acceso disponibles: {levels.Count}.");
            SaveSettings();
            _tcStatus.Text = $"Conectado como {tc.Username} ({tc.Role}) · el padrón ya tiene {persons.Count} persona(s)";
            Log(LogLevel.Success, $"TrueCentral {tc.BaseUrl}: sesión de {tc.Username}; {persons.Count} persona(s) en el padrón.");
        }
        catch (OperationCanceledException) { _tcStatus.Text = "Cancelado."; }
        catch (TrueCentralException ex)
        {
            _tcStatus.Text = "No se pudo conectar.";
            Log(LogLevel.Error, ex.Message);
        }
        finally { SetBusy(false); UpdateSummary(); }
    }

    // ==================================================================
    // Migración
    // ==================================================================

    private async Task RunMigrationAsync()
    {
        if (!ValidateHikCentralFields()) { _tabs.SelectedIndex = 0; return; }
        if (_import.Checked && !ValidateTrueCentralFields()) { _tabs.SelectedIndex = 2; return; }
        var terminals = SelectedTerminals();
        if (_readTerminals.Checked && terminals.Count == 0 &&
            MessageBox.Show(this, "No hay terminales marcados: las huellas no se van a recuperar. ¿Continuar igual?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (terminals.Any(t => t.Password.Length == 0) &&
            MessageBox.Show(this, "Hay terminales sin contraseña. ¿Continuar igual?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        string folder = _folder.Text.Trim();
        if (folder.Length == 0) { _folder.Text = folder = DefaultFolder(); }

        SaveSettings();
        SetBusy(true, allowCancel: true);
        _tabs.SelectedIndex = 3;
        var ct = _cts!.Token;
        try
        {
            Directory.CreateDirectory(folder);
            Log(LogLevel.Info, "══════════ Comienza la migración ══════════");
            var engine = new MigrationEngine(entry => BeginInvoke(() => Log(entry)), new Progress<ProgressState>(UpdateProgress));
            using var hcp = new HikCentralClient(_hcpUrl.Text, _hcpKey.Text, _hcpSecret.Text);
            var package = await Task.Run(() => engine.ExportAsync(new ExportOptions
            {
                HikCentral = hcp,
                Organizations = SelectedOrganizations(),
                DownloadPhotos = _photos.Checked,
                Terminals = terminals,
                PackageFolder = folder,
            }, ct), ct);

            if (_import.Checked)
            {
                using var tc = new TrueCentralClient();
                await tc.LoginAsync(_tcUrl.Text, _tcUser.Text, _tcPass.Text, ct);
                await Task.Run(() => engine.ImportAsync(package, new ImportOptions
                {
                    TrueCentral = tc,
                    PackageFolder = folder,
                    EmployeeNoSource = CurrentEmployeeNoSource(),
                    LevelId = SelectedLevelId(),
                    LevelName = SelectedLevelName(),
                    UpdateExisting = _updateExisting.Checked,
                }, ct), ct);
            }
            else Log(LogLevel.Info, "No se importó a TrueCentral (opción desmarcada en el paso 3). El paquete queda listo para «Importar un paquete guardado».");

            Log(LogLevel.Success, "══════════ Migración terminada ══════════");
            WriteLogFile(Path.Combine(folder, "registro.txt"));
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Warning, "Migración cancelada por el usuario.");
            UpdateProgress(new ProgressState(0, "Cancelado."));
        }
        catch (Exception ex) when (ex is HikCentralException or TrueCentralException or DriverException or IOException or UnauthorizedAccessException)
        {
            Log(LogLevel.Error, ex.Message);
            UpdateProgress(new ProgressState(0, "La migración se detuvo por un error."));
            MessageBox.Show(this, ex.Message, "La migración se detuvo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private async Task ImportSavedPackageAsync()
    {
        if (!ValidateTrueCentralFields()) { _tabs.SelectedIndex = 2; return; }
        using var dialog = new FolderBrowserDialog { Description = "Carpeta del paquete guardado (la que contiene paquete.json)", UseDescriptionForTitle = true, SelectedPath = _folder.Text };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string folder = dialog.SelectedPath;

        SaveSettings();
        SetBusy(true, allowCancel: true);
        var ct = _cts!.Token;
        try
        {
            var package = await MigrationPackage.LoadAsync(folder, ct);
            Log(LogLevel.Info, $"Paquete de {package.Source} ({package.SourceVersion}) exportado el {package.ExportedAt.ToLocalTime():g} por {package.ExportedBy}: {package.Persons.Count} personas, " +
                               $"{package.Persons.Count(p => p.PhotoFile is not null)} fotos, {package.Persons.Sum(p => p.Fingerprints.Count)} huellas.");
            if (MessageBox.Show(this, $"Se van a dar de alta {package.Persons.Count} personas en {TrueCentralClient.NormalizeUrl(_tcUrl.Text)}. ¿Continuar?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            var engine = new MigrationEngine(entry => BeginInvoke(() => Log(entry)), new Progress<ProgressState>(UpdateProgress));
            using var tc = new TrueCentralClient();
            await tc.LoginAsync(_tcUrl.Text, _tcUser.Text, _tcPass.Text, ct);
            await Task.Run(() => engine.ImportAsync(package, new ImportOptions
            {
                TrueCentral = tc, PackageFolder = folder, EmployeeNoSource = CurrentEmployeeNoSource(),
                LevelId = SelectedLevelId(), LevelName = SelectedLevelName(), UpdateExisting = _updateExisting.Checked,
            }, ct), ct);
            WriteLogFile(Path.Combine(folder, "registro-importacion.txt"));
        }
        catch (OperationCanceledException) { Log(LogLevel.Warning, "Importación cancelada."); }
        catch (Exception ex) when (ex is TrueCentralException or IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log(LogLevel.Error, ex.Message);
            MessageBox.Show(this, ex.Message, "No se pudo importar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private sealed record LevelItem(int? Id, string Text)
    {
        public override string ToString() => Text;
    }

    private int? SelectedLevelId() => (_level.SelectedItem as LevelItem)?.Id;
    private string? SelectedLevelName() => (_level.SelectedItem as LevelItem) is { Id: not null } item ? item.Text : null;

    private EmployeeNoSource CurrentEmployeeNoSource() =>
        _idPersonCode.Checked ? EmployeeNoSource.PersonCode : _idAssigned.Checked ? EmployeeNoSource.Assigned : EmployeeNoSource.Terminal;

    // ==================================================================
    // Validaciones y estado
    // ==================================================================

    private bool ValidateHikCentralFields()
    {
        if (string.IsNullOrWhiteSpace(_hcpUrl.Text)) return Complain("Indique la dirección del servidor HikCentral.", _hcpUrl, 0);
        if (string.IsNullOrWhiteSpace(_hcpKey.Text)) return Complain("Indique la clave del socio de integración (AppKey).", _hcpKey, 0);
        if (string.IsNullOrWhiteSpace(_hcpSecret.Text)) return Complain("Indique el secreto del socio de integración (AppSecret).", _hcpSecret, 0);
        return true;
    }

    private bool ValidateTrueCentralFields()
    {
        if (string.IsNullOrWhiteSpace(_tcUrl.Text)) return Complain("Indique la dirección del servidor TrueCentral.", _tcUrl, 2);
        if (string.IsNullOrWhiteSpace(_tcUser.Text)) return Complain("Indique el usuario de TrueCentral.", _tcUser, 2);
        if (string.IsNullOrWhiteSpace(_tcPass.Text)) return Complain("Indique la contraseña de TrueCentral.", _tcPass, 2);
        return true;
    }

    private bool Complain(string message, Control focus, int tab)
    {
        _tabs.SelectedIndex = tab;
        focus.Focus();
        Log(LogLevel.Warning, message);
        return false;
    }

    private void SetBusy(bool busy, bool allowCancel = false)
    {
        if (busy)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
        }
        else
        {
            _cts?.Dispose();
            _cts = null;
        }
        _cancel.Enabled = busy && allowCancel;
        foreach (var c in new Control[] { _hcpUrl, _hcpKey, _hcpSecret, _hcpConnect, _orgs, _readTerminals, _termUser, _termPass, _terminals,
                     _tcUrl, _tcUser, _tcPass, _tcConnect, _level, _updateExisting, _idTerminal, _idPersonCode, _idAssigned, _import, _folder, _photos, _start, _importPackage })
            c.Enabled = !busy;
        UseWaitCursor = busy;
        if (!busy) _progress.Value = _progress.Value; // fuerza repintado tras el cursor
    }

    private void OnTabChanged()
    {
        _back.Enabled = _tabs.SelectedIndex > 0;
        _next.Enabled = _tabs.SelectedIndex < _tabs.TabCount - 1;
        if (_tabs.SelectedIndex == 3) UpdateSummary();
    }

    private void UpdateSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine(_hcpPersons.Count == 0
            ? "HikCentral: todavía no se leyó el padrón (paso 1, «Conectar»)."
            : $"HikCentral: {_hcpVersion} · {_hcpPersons.Count} personas" +
              (SelectedOrganizations() is { } sel ? $", {_hcpPersons.Count(p => sel.Contains(p.OrgIndexCode))} en los departamentos elegidos" : ", todos los departamentos") + ".");
        var terminals = SelectedTerminals();
        sb.AppendLine(_readTerminals.Checked
            ? $"Terminales: se leerán huellas y legajos de {terminals.Count} equipo(s)."
            : "Terminales: no se leen (las huellas no se recuperan).");
        sb.AppendLine(_import.Checked
            ? $"Destino: {(_tcUrl.Text.Trim().Length > 0 ? TrueCentralClient.NormalizeUrl(_tcUrl.Text) : "(sin definir)")} como {_tcUser.Text}" +
              (_tcExisting >= 0 ? $" · ya hay {_tcExisting} persona(s)" : "") + "."
            : "Destino: solo se guarda el paquete, sin importar.");
        sb.AppendLine("Nivel de acceso: " + (SelectedLevelName() ?? "ninguno (asignar después)."));
        sb.Append("Identificador: " + (_idPersonCode.Checked ? "código de persona de HikCentral." : _idAssigned.Checked ? "lo asigna TrueCentral." : "el legajo de los terminales."));
        _summary.Text = sb.ToString();
    }

    private void UpdateProgress(ProgressState state)
    {
        _progress.Value = Math.Clamp((int)Math.Round(state.Fraction * _progress.Maximum), 0, _progress.Maximum);
        _status.Text = state.Status;
    }

    // ==================================================================
    // Registro
    // ==================================================================

    private void Log(LogLevel level, string message) => Log(new LogEntry(DateTime.Now, level, message));

    private void Log(LogEntry entry)
    {
        string line = $"[{entry.At:HH:mm:ss}] {entry.Message}";
        _logText.AppendLine(line);
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = entry.Level switch
        {
            LogLevel.Success => Color.FromArgb(0, 120, 60),
            LogLevel.Warning => Color.FromArgb(190, 110, 0),
            LogLevel.Error => Color.FromArgb(190, 30, 30),
            _ => Color.FromArgb(40, 40, 40),
        };
        _log.AppendText(line + Environment.NewLine);
        _log.SelectionColor = _log.ForeColor;
        _log.ScrollToCaret();
    }

    private void WriteLogFile(string path)
    {
        try
        {
            File.WriteAllText(path, _logText.ToString(), Encoding.UTF8);
            Log(LogLevel.Info, $"Registro guardado en {path}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(LogLevel.Warning, $"No se pudo guardar el registro: {ex.Message}");
        }
    }

    private void SaveLogDialog()
    {
        using var dialog = new SaveFileDialog { Filter = "Texto (*.txt)|*.txt", FileName = $"migrador-hikcentral-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
        if (dialog.ShowDialog(this) == DialogResult.OK) WriteLogFile(dialog.FileName);
    }
}

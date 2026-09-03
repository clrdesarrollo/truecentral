using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Panel Parlantes IP de la Vista en Vivo: lista de parlantes con casilla
/// (uno o varios), hablar mientras se mantiene presionado el botón, sonidos
/// del servidor (sincronizados entre los marcados), biblioteca del propio
/// parlante y texto a voz. El estado de conexión lo mantiene el servidor y
/// llega por el hub; las órdenes van por la API y quedan auditadas allá.
///
/// Además: elección del micrófono con vúmetro (modo prueba, sin transmitir)
/// y <b>escucha local</b> de los sonidos del servidor y de la biblioteca del
/// parlante en el puesto del operador, para no confundir a la gente en
/// terreno mientras se elige qué reproducir.
/// </summary>
public sealed partial class SpeakersViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly ClientSettings _settings;
    private readonly SpeakerTalkClient _talk;
    private readonly AlertSoundPlayer _preview;
    private MediaPlayer? _libraryPreview;
    private int _libraryOwnerId;

    public SpeakersViewModel(ApiClient api, VmsHubClient hub, ClientSettings settings)
    {
        _api = api;
        _settings = settings;
        _preview = new AlertSoundPlayer(api);
        _talk = new SpeakerTalkClient(api);
        _talk.StatusChanged += text => Application.Current.Dispatcher.InvokeAsync(() => StatusMessage = text);
        _talk.Ended += () => Application.Current.Dispatcher.InvokeAsync(() => IsTalking = false);
        _talk.LevelChanged += level => Application.Current.Dispatcher.InvokeAsync(() => MicLevel = level);

        foreach (var name in SpeakerTalkClient.ListMicrophones()) Microphones.Add(name);
        _selectedMicrophone = Microphones.FirstOrDefault(m => string.Equals(m, settings.MicrophoneDevice, StringComparison.OrdinalIgnoreCase))
                              ?? Microphones.FirstOrDefault();
        HasMicrophones = Microphones.Count > 0;
        _talkPreTone = settings.TalkPreTone;

        hub.ConfigChanged += entity =>
        {
            if (entity == "speakers")
                Application.Current.Dispatcher.InvokeAsync(() => _ = LoadAsync());
        };
        hub.SpeakerStatusChanged += dto => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var item = Speakers.FirstOrDefault(s => s.Dto.Id == dto.Id);
            if (item is not null) item.Dto = dto;
        });
    }

    public ObservableCollection<SpeakerItem> Speakers { get; } = [];
    public ObservableCollection<WorkflowAudioDto> ServerSounds { get; } = [];
    public ObservableCollection<SpeakerAudioItemDto> Library { get; } = [];
    public ObservableCollection<string> Microphones { get; } = [];

    /// <summary>Panel desplegado (minimizado por defecto, como el PTZ).</summary>
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isTalking;
    [ObservableProperty] private bool _hasSpeakers;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _hasSounds;
    [ObservableProperty] private bool _hasLibrary;
    [ObservableProperty] private bool _hasTts;
    [ObservableProperty] private bool _hasMicrophones;
    [ObservableProperty] private string _selectionSummary = "ninguno";
    [ObservableProperty] private string _libraryOwner = "";
    [ObservableProperty] private WorkflowAudioDto? _selectedSound;
    [ObservableProperty] private SpeakerAudioItemDto? _selectedLibraryItem;
    [ObservableProperty] private string _ttsText = "";
    /// <summary>Micrófono elegido (se recuerda en la configuración local).</summary>
    [ObservableProperty] private string? _selectedMicrophone;
    /// <summary>Tono de apertura (dos pitidos) antes de la voz; se recuerda en la configuración local.</summary>
    [ObservableProperty] private bool _talkPreTone;
    /// <summary>Nivel del micrófono 0..100 para el vúmetro.</summary>
    [ObservableProperty] private int _micLevel;
    /// <summary>Modo prueba del micrófono: captura sin transmitir.</summary>
    [ObservableProperty] private bool _isMicTest;
    /// <summary>Hay una escucha local en curso (sonido del servidor o de la biblioteca).</summary>
    [ObservableProperty] private bool _isPreviewing;

    public IReadOnlyList<int> SelectedIds => Speakers.Where(s => s.IsSelected).Select(s => s.Dto.Id).ToList();

    // ------------------------------------------------------------------
    // Volumen de salida de los parlantes marcados (se aplica con un pequeño
    // retardo para no bombardear al equipo mientras se arrastra el control).
    // ------------------------------------------------------------------

    /// <summary>Volumen 0..100 del primer parlante marcado; al moverlo se aplica a todos los marcados.</summary>
    [ObservableProperty] private int _volume = 100;
    private bool _volumeFromDevice;
    private System.Windows.Threading.DispatcherTimer? _volumeTimer;

    partial void OnVolumeChanged(int value)
    {
        if (_volumeFromDevice) return;
        _volumeTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _volumeTimer.Stop();
        _volumeTimer.Tick -= OnVolumeTimerTick;
        _volumeTimer.Tick += OnVolumeTimerTick;
        _volumeTimer.Start();
    }

    private async void OnVolumeTimerTick(object? sender, EventArgs e)
    {
        _volumeTimer?.Stop();
        int volume = Volume;
        var targets = Speakers.Where(s => s.IsSelected).ToList();
        if (targets.Count == 0) return;
        var failed = new List<string>();
        foreach (var target in targets)
        {
            try
            {
                var updated = await _api.SetSpeakerVolumeAsync(target.Dto.Id, volume);
                target.Dto = updated;
            }
            catch (ApiException ex) { failed.Add($"{target.Name}: {ex.Message}"); }
        }
        StatusMessage = failed.Count == 0
            ? $"Volumen {volume} aplicado a {targets.Count} parlante{(targets.Count == 1 ? "" : "s")}."
            : string.Join(" · ", failed);
    }

    private void SyncVolumeFromSelection()
    {
        var first = Speakers.FirstOrDefault(s => s.IsSelected && s.Dto.Volume is not null);
        if (first is null) return;
        _volumeFromDevice = true;
        try { Volume = first.Dto.Volume ?? Volume; }
        finally { _volumeFromDevice = false; }
    }

    partial void OnTalkPreToneChanged(bool value)
    {
        if (_settings.TalkPreTone == value) return;
        _settings.TalkPreTone = value;
        try { _settings.Save(); } catch (Exception) { /* preferencia local: no es crítica */ }
    }

    partial void OnSelectedMicrophoneChanged(string? value)
    {
        if (string.Equals(_settings.MicrophoneDevice, value, StringComparison.Ordinal)) return;
        _settings.MicrophoneDevice = value;
        try { _settings.Save(); } catch (Exception) { /* preferencia local: no es crítica */ }
        // Si estaba probando, reiniciar la captura con el micrófono nuevo.
        if (IsMicTest) _ = RestartMicTestAsync();
    }

    partial void OnIsMicTestChanged(bool value)
    {
        if (value) _ = RestartMicTestAsync();
        else if (!IsTalking) _ = _talk.StopAsync();
    }

    private async Task RestartMicTestAsync()
    {
        if (IsTalking) return;
        try { await _talk.StartMonitorAsync(SelectedMicrophone); }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException)
        {
            IsMicTest = false;
            StatusMessage = $"No se pudo abrir el micrófono: {ex.Message}";
        }
    }

    public async Task InitializeAsync() => await LoadAsync();

    public async Task LoadAsync()
    {
        try
        {
            var speakers = await _api.GetSpeakersAsync();
            var selected = Speakers.Where(s => s.IsSelected).Select(s => s.Dto.Id).ToHashSet();
            foreach (var old in Speakers) old.SelectionChanged -= OnSelectionChanged;
            Speakers.Clear();
            foreach (var dto in speakers.Where(s => s.Enabled))
            {
                var item = new SpeakerItem(dto) { IsSelected = selected.Contains(dto.Id) };
                item.SelectionChanged += OnSelectionChanged;
                Speakers.Add(item);
            }
            // Con un solo parlante no hay nada que elegir: queda marcado.
            if (Speakers.Count == 1 && selected.Count == 0) Speakers[0].IsSelected = true;
            HasSpeakers = Speakers.Count > 0;

            var sounds = await _api.GetSpeakerSoundsAsync();
            string? current = SelectedSound?.DisplayName;
            ServerSounds.Clear();
            foreach (var sound in sounds.Where(s => s.Ready)) ServerSounds.Add(sound);
            HasSounds = ServerSounds.Count > 0;
            SelectedSound = ServerSounds.FirstOrDefault(s => s.DisplayName == current) ?? ServerSounds.FirstOrDefault();
            OnSelectionChanged();
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void OnSelectionChanged()
    {
        var chosen = Speakers.Where(s => s.IsSelected).ToList();
        HasSelection = chosen.Count > 0;
        AllSelected = Speakers.Count > 0 && chosen.Count == Speakers.Count;
        SelectionSummary = chosen.Count switch
        {
            0 => "ninguno",
            1 => chosen[0].Name,
            _ => $"{chosen.Count} parlantes",
        };
        HasTts = chosen.Any(s => s.Dto.SupportsTts);
        SyncVolumeFromSelection();
        // La biblioteca es del primer parlante marcado (los demás se buscan por nombre).
        var owner = chosen.FirstOrDefault(s => s.Dto.SupportsLibrary);
        if (owner is null)
        {
            _libraryOwnerId = 0;
            Library.Clear();
            HasLibrary = false;
            LibraryOwner = "";
            return;
        }
        if (owner.Dto.Id != _libraryOwnerId) _ = LoadLibraryAsync(owner);
    }

    private async Task LoadLibraryAsync(SpeakerItem owner)
    {
        _libraryOwnerId = owner.Dto.Id;
        LibraryOwner = $"Biblioteca de {owner.Name}";
        try
        {
            var items = await _api.GetSpeakerLibraryAsync(owner.Dto.Id);
            if (_libraryOwnerId != owner.Dto.Id) return;   // cambió la selección mientras se leía
            string? current = SelectedLibraryItem?.Name;
            Library.Clear();
            foreach (var item in items) Library.Add(item);
            HasLibrary = Library.Count > 0;
            SelectedLibraryItem = Library.FirstOrDefault(i => i.Name == current) ?? Library.FirstOrDefault();
        }
        catch (ApiException ex)
        {
            HasLibrary = false;
            StatusMessage = ex.Message;
        }
    }

    // ------------------------------------------------------------------
    // Reproducción en los parlantes
    // ------------------------------------------------------------------

    private async Task PlayAsync(SpeakerPlayRequestDto request)
    {
        if (request.SpeakerIds.Count == 0) { StatusMessage = "Marque al menos un parlante."; return; }
        StopPreview();
        IsBusy = true;
        try
        {
            var result = await _api.PlaySpeakersAsync(request);
            StatusMessage = Summarize(result);
        }
        catch (ApiException ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private static string Summarize(SpeakerOperationResultDto result)
    {
        var failed = result.Results.Where(r => !r.Success).Select(r => $"{r.SpeakerName}: {r.Message}").ToList();
        return failed.Count == 0 ? result.Message : $"{result.Message} {string.Join(" · ", failed)}";
    }

    [RelayCommand]
    private Task PlaySound()
    {
        if (SelectedSound is null) { StatusMessage = "Elija un sonido del servidor."; return Task.CompletedTask; }
        return PlayAsync(new SpeakerPlayRequestDto(SelectedIds, SpeakerPlaySources.Server, Sound: SelectedSound.DisplayName));
    }

    [RelayCommand]
    private Task PlayLibrary()
    {
        if (SelectedLibraryItem is null) { StatusMessage = "Elija un audio de la biblioteca."; return Task.CompletedTask; }
        return PlayAsync(new SpeakerPlayRequestDto(SelectedIds, SpeakerPlaySources.Library, LibraryName: SelectedLibraryItem.Name));
    }

    [RelayCommand]
    private Task PlayTts()
    {
        string text = TtsText.Trim();
        if (text.Length == 0) { StatusMessage = "Escriba el texto que debe leer el parlante."; return Task.CompletedTask; }
        return PlayAsync(new SpeakerPlayRequestDto(SelectedIds, SpeakerPlaySources.Tts, Text: text, Language: "spanish", Voice: "female"));
    }

    [RelayCommand]
    private async Task Stop()
    {
        StopPreview();
        var ids = SelectedIds;
        if (ids.Count == 0) { StatusMessage = "Marque al menos un parlante."; return; }
        if (IsTalking) await StopTalkAsync();
        try
        {
            var result = await _api.StopSpeakersAsync(ids);
            StatusMessage = Summarize(result);
        }
        catch (ApiException ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    /// <summary>Marca todos los parlantes; si ya están todos marcados, los desmarca.</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        bool all = Speakers.Count > 0 && Speakers.All(s => s.IsSelected);
        foreach (var speaker in Speakers) speaker.IsSelected = !all;
    }

    [ObservableProperty] private bool _allSelected;

    // ------------------------------------------------------------------
    // Escucha local (en este equipo, NO en los parlantes)
    // ------------------------------------------------------------------

    [RelayCommand]
    private async Task PreviewSound()
    {
        if (SelectedSound is null) { StatusMessage = "Elija un sonido del servidor."; return; }
        if (IsPreviewing) { StopPreview(); return; }
        StopLibraryPreview();
        IsPreviewing = true;
        StatusMessage = $"Escuchando '{SelectedSound.DisplayName}' en este equipo (no suena en los parlantes).";
        await _preview.PlayAsync(SelectedSound.DisplayName, 1);
    }

    [RelayCommand]
    private async Task PreviewLibrary()
    {
        if (SelectedLibraryItem is null || _libraryOwnerId == 0) { StatusMessage = "Elija un audio de la biblioteca."; return; }
        if (IsPreviewing) { StopPreview(); return; }
        _preview.Stop();
        IsPreviewing = true;
        StatusMessage = $"Descargando '{SelectedLibraryItem.Name}' del parlante para escucharlo aquí…";
        try
        {
            var file = await _api.GetSpeakerAudioFileAsync(_libraryOwnerId, SelectedLibraryItem.Id);
            if (file is null) { IsPreviewing = false; StatusMessage = "El parlante no entregó el archivo."; return; }
            string folder = Path.Combine(Path.GetTempPath(), "TrueCentralVMS", "speaker-preview");
            Directory.CreateDirectory(folder);
            string ext = Path.GetExtension(file.Value.FileName) is { Length: > 1 } e ? e : "." + SelectedLibraryItem.Format;
            string path = Path.Combine(folder, $"{_libraryOwnerId}-{SelectedLibraryItem.Id}{ext}");
            await File.WriteAllBytesAsync(path, file.Value.Content);
            // MediaPlayer exige el hilo de interfaz (necesita su Dispatcher).
            _libraryPreview ??= CreateLibraryPlayer();
            _libraryPreview.Open(new Uri(path));
            _libraryPreview.Play();
            StatusMessage = $"Escuchando '{SelectedLibraryItem.Name}' en este equipo (no suena en los parlantes).";
        }
        catch (Exception ex) when (ex is ApiException or IOException or UnauthorizedAccessException)
        {
            IsPreviewing = false;
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Guarda en este equipo el archivo del audio elegido, descargándolo del parlante.</summary>
    [RelayCommand]
    private async Task DownloadLibrary()
    {
        if (SelectedLibraryItem is null || _libraryOwnerId == 0) { StatusMessage = "Elija un audio de la biblioteca."; return; }
        var item = SelectedLibraryItem;
        string ext = item.Format is { Length: > 0 } f ? f : "bin";
        string suggested = item.Name.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase) ? item.Name : $"{item.Name}.{ext}";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Guardar audio del parlante",
            FileName = string.Concat(suggested.Split(Path.GetInvalidFileNameChars())),
            Filter = $"Audio {ext}|*.{ext}|Todos los archivos|*.*",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        StatusMessage = $"Descargando '{item.Name}' del parlante…";
        try
        {
            var file = await _api.GetSpeakerAudioFileAsync(_libraryOwnerId, item.Id);
            if (file is null) { StatusMessage = "El parlante no entregó el archivo."; return; }
            await File.WriteAllBytesAsync(dialog.FileName, file.Value.Content);
            StatusMessage = $"Guardado en {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is ApiException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = ex.Message;
        }
    }

    private MediaPlayer CreateLibraryPlayer()
    {
        var player = new MediaPlayer();
        player.MediaEnded += (_, _) => { player.Stop(); IsPreviewing = false; };
        player.MediaFailed += (_, e) =>
        {
            IsPreviewing = false;
            StatusMessage = $"Este equipo no pudo reproducir el archivo ({e.ErrorException.Message}).";
        };
        return player;
    }

    [RelayCommand]
    private void StopPreview()
    {
        _preview.Stop();
        StopLibraryPreview();
        IsPreviewing = false;
    }

    private void StopLibraryPreview()
    {
        try { _libraryPreview?.Stop(); } catch (Exception) { /* ya detenido */ }
    }

    // ------------------------------------------------------------------
    // Voz en vivo (mantener presionado)
    // ------------------------------------------------------------------

    public async Task StartTalkAsync()
    {
        if (IsTalking) return;
        var ids = SelectedIds;
        if (ids.Count == 0) { StatusMessage = "Marque al menos un parlante para hablar."; return; }
        StopPreview();
        IsTalking = true;
        StatusMessage = "Abriendo el canal de voz…";
        try
        {
            await _talk.StartAsync(ids, SelectedMicrophone, TalkPreTone);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ApiException or System.Net.WebSockets.WebSocketException or NAudio.MmException)
        {
            IsTalking = false;
            StatusMessage = ex.Message;
        }
    }

    public async Task StopTalkAsync()
    {
        if (!IsTalking && !_talk.IsActive) return;
        IsTalking = false;
        await _talk.StopAsync();
        // Si el operador tenía activa la prueba del micrófono, retomarla.
        if (IsMicTest) _ = RestartMicTestAsync();
    }
}

/// <summary>Fila del panel: parlante con su casilla de selección y estado en vivo.</summary>
public sealed partial class SpeakerItem : ObservableObject
{
    public SpeakerItem(SpeakerDto dto)
    {
        _dto = dto;
    }

    public event Action? SelectionChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(IsOnline), nameof(BusyWith), nameof(Tooltip))]
    private SpeakerDto _dto;

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    public string Name => Dto.Name;
    public bool IsOnline => Dto.Status == SpeakerStatus.Online;
    public string BusyWith => Dto.BusyWith is { Length: > 0 } busy ? $"({busy})" : "";

    public string Tooltip
    {
        get
        {
            var parts = new List<string> { $"{Dto.Model ?? Dto.DriverKey} · {Dto.Host}" };
            if (Dto.GroupName is { Length: > 0 } group) parts.Add($"Grupo: {group}");
            parts.Add(Dto.Status switch
            {
                SpeakerStatus.Online => "En línea",
                SpeakerStatus.Offline => $"Sin conexión{(Dto.LastError is null ? "" : $": {Dto.LastError}")}",
                SpeakerStatus.AuthFailed => "Credenciales rechazadas",
                _ => "Estado desconocido",
            });
            if (Dto.Volume is { } volume) parts.Add($"Volumen {volume}");
            return string.Join("\n", parts);
        }
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using VoiceMorph.App.Models;
using VoiceMorph.App.Services;

namespace VoiceMorph.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AudioEngine _engine;
    private readonly DispatcherTimer _timer;
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _previousCpu;
    private long _previousTick = Stopwatch.GetTimestamp();
    private ProfileEntry _selectedProfile;
    private VoiceSettings _settings;
    private double _strength = 1;
    private string _statusTitle = "Готов к работе", _statusDetail = "Выбери голос и запусти обработку";
    private string _errorMessage = "";
    private string _engineButtonText = "Запустить голос", _bypassButtonText = "Исходный голос";
    private string _resourceText = "Замер ресурсов…", _latencyText = "— мс";
    private bool _isRunning, _isBypassed;
    private double _inputLevel, _outputLevel;
    private bool _neuralMode, _neuralRunning;
    private int _neuralLatency;
    private string _neuralResourceText = "";

    public MainViewModel(AudioEngine engine)
    {
        _engine = engine;
        foreach (var preset in VoicePreset.BuiltIns)
            Profiles.Add(new ProfileEntry(preset.Id, preset.Name, preset.Description, true, VoiceSettings.FromPreset(preset)));
        _selectedProfile = Profiles[0];
        _settings = _selectedProfile.Settings;
        ParameterGroups = VoiceParameter.All.Where(parameter => parameter.Key != nameof(VoiceSettings.Strength))
            .GroupBy(parameter => parameter.Group)
            .Select(group => new ParameterGroup(group.Key, group.Select(parameter => new ParameterViewModel(parameter, this)).ToList()))
            .ToList();
        _previousCpu = _process.TotalProcessorTime;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, SampleResources, Dispatcher.CurrentDispatcher);
        _timer.Start();
        _engine.ApplySettings(_settings);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ProfileEntry> Profiles { get; } = [];
    public IReadOnlyList<ParameterGroup> ParameterGroups { get; }
    public VoiceSettings Settings => _settings;
    public ProfileEntry SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value is null || !SetField(ref _selectedProfile, value)) return;
            _settings = value.Settings.Sanitize();
            _strength = _settings.Strength;
            NotifySettings();
            _engine.ApplySettings(_settings);
            if (IsRunning) StatusDetail = $"{value.Name} · звук направляется в выбранный выход";
        }
    }
    public bool CanEdit => !SelectedProfile.IsBuiltIn;
    public string EditingHint => CanEdit
        ? $"{SelectedProfile.Name} · изменения слышны сразу; сохрани профиль после настройки."
        : "Встроенный профиль защищён. Создай копию на вкладке «Голоса», чтобы изменять параметры.";
    public string ProfileSummary => $"Высота {_settings.PitchSemitones:+0.0;-0.0;0.0} st · форманты {_settings.FormantSemitones:+0.0;-0.0;0.0} st\n{VoiceParameter.All.Count} параметр · локальная обработка";
    public double Strength
    {
        get => _strength;
        set
        {
            if (!double.IsFinite(value) || !SetField(ref _strength, Math.Clamp(value, 0, 1))) return;
            if (CanEdit) { _settings = _settings with { Strength = (float)_strength }; _selectedProfile.Settings = _settings; }
            _engine.ApplySettings(_settings with { Strength = (float)_strength });
        }
    }
    public string StatusTitle { get => _statusTitle; set => SetField(ref _statusTitle, value); }
    public string StatusDetail { get => _statusDetail; set => SetField(ref _statusDetail, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetField(ref _errorMessage, value); }
    public string EngineButtonText { get => _engineButtonText; set => SetField(ref _engineButtonText, value); }
    public string BypassButtonText { get => _bypassButtonText; set => SetField(ref _bypassButtonText, value); }
    public string ResourceText { get => _resourceText; set => SetField(ref _resourceText, value); }
    public string LatencyText { get => _latencyText; set => SetField(ref _latencyText, value); }
    public bool IsRunning { get => _isRunning; private set => SetField(ref _isRunning, value); }
    public bool IsBypassed { get => _isBypassed; private set => SetField(ref _isBypassed, value); }
    public double InputLevel { get => _inputLevel; private set => SetField(ref _inputLevel, value); }
    public double OutputLevel { get => _outputLevel; private set => SetField(ref _outputLevel, value); }

    public ProfileEntry AddProfile(UserVoiceProfile profile)
    {
        if (Profiles.Count(entry => !entry.IsBuiltIn) >= 128) throw new InvalidOperationException("Допустимо не более 128 своих профилей.");
        var entry = new ProfileEntry(profile.Id.ToString("D"), profile.Name,
            profile.TargetAnalysis is null ? "Собственный голосовой профиль" : "Подобран по аудиопримеру", false, profile.Settings.Sanitize(), profile);
        Profiles.Add(entry);
        SelectedProfile = entry;
        return entry;
    }
    public void ChangeParameter(VoiceParameter parameter, float value)
    {
        if (!CanEdit) return;
        _settings = parameter.Set(_settings, value).Sanitize();
        _selectedProfile.Settings = _settings;
        OnPropertyChanged(nameof(ProfileSummary));
        _engine.ApplySettings(_settings with { Strength = (float)_strength });
    }
    public IReadOnlyList<UserVoiceProfile> UserProfiles => Profiles.Where(profile => !profile.IsBuiltIn).Select(profile =>
        (profile.UserProfile ?? new UserVoiceProfile(Guid.Parse(profile.Id), profile.Name, profile.Settings, DateTime.UtcNow)) with { Settings = profile.Settings }).ToList();
    public VoiceSettings EffectiveSettings => _settings with { Strength = (float)_strength };
    private string _neuralModelName = "";
    public void SetNeuralMode(bool enabled) { _neuralMode = enabled; if (!enabled) { _neuralRunning = false; _neuralResourceText = ""; } }
    public void ApplyNeuralRunningState(bool running, string modelName, int estimatedLatency = 0)
    {
        ClearError();
        _neuralRunning = running; _neuralLatency = estimatedLatency; _neuralModelName = modelName;
        IsBypassed = false; BypassButtonText = "Исходный голос";
        IsRunning = running; EngineButtonText = running ? "Остановить голос" : "Запустить голос";
        StatusTitle = running ? "RVC-голос активен" : "Режим RVC";
        StatusDetail = running ? modelName + " · локальная нейросетевая обработка" : "Выбери и проверь импортированную модель";
        LatencyText = running ? $"≈ {estimatedLatency} мс" : "— мс";
        if (!running) { InputLevel = 0; OutputLevel = 0; _neuralResourceText = ""; }
    }
    public void ApplyNeuralTelemetry(int latency, string resourceText)
    { _neuralLatency = latency; _neuralResourceText = resourceText; }
    public void ApplyMeter(AudioMeterEventArgs args) { InputLevel = ToVisualLevel(args.Input); OutputLevel = ToVisualLevel(args.Output); }
    public void ApplyRunningState(bool running)
    {
        ClearError();
        IsRunning = running;
        EngineButtonText = running ? "Остановить голос" : "Запустить голос";
        StatusTitle = running ? "Голос активен" : "Обработка остановлена";
        StatusDetail = running ? $"{SelectedProfile.Name} · звук направляется в выбранный выход" : "Выбери профиль и запусти обработку";
        LatencyText = running ? $"≈ {_engine.ReportedLatencyMs} мс" : "— мс";
        if (!running) { InputLevel = 0; OutputLevel = 0; }
    }
    public void ApplyBypassState(bool bypassed)
    {
        IsBypassed = bypassed;
        BypassButtonText = bypassed ? "Вернуть эффект" : "Исходный голос";
        StatusTitle = bypassed ? "Исходный голос" : IsRunning ? (_neuralMode ? "RVC-голос активен" : "Голос активен") : StatusTitle;
        if (IsRunning) StatusDetail = bypassed ? "Передаётся твой голос с защитой громкости" : $"{(_neuralMode ? _neuralModelName : SelectedProfile.Name)} · обработка включена";
    }
    public void ClearError() { ErrorMessage = ""; }
    public void ApplyError(string message)
    { StatusTitle = "Нужна проверка"; StatusDetail = "Причина и следующий шаг — в сообщении ниже"; ErrorMessage = message; }
    public void Dispose() { _timer.Stop(); _process.Dispose(); }
    private void NotifySettings()
    {
        OnPropertyChanged(nameof(Settings)); OnPropertyChanged(nameof(Strength)); OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(EditingHint)); OnPropertyChanged(nameof(ProfileSummary));
        foreach (var parameter in ParameterGroups.SelectMany(group => group.Parameters)) parameter.Refresh();
    }
    private void SampleResources(object? sender, EventArgs args)
    {
        _process.Refresh(); var tick = Stopwatch.GetTimestamp(); var cpu = _process.TotalProcessorTime;
        var wall = Math.Max(.001, Stopwatch.GetElapsedTime(_previousTick, tick).TotalSeconds);
        var percent = Math.Clamp((cpu - _previousCpu).TotalSeconds / wall / Environment.ProcessorCount * 100, 0, 100);
        _previousCpu = cpu; _previousTick = tick;
        ResourceText = $"CPU {percent:0.0}% · RAM {_process.WorkingSet64 / 1048576d:0} МБ · буфер {_engine.BufferedMilliseconds} мс";
        if (_neuralMode)
        {
            ResourceText = $"UI CPU {percent:0.0}% · RAM {_process.WorkingSet64 / 1048576d:0} МБ" + _neuralResourceText;
            if (_neuralRunning) LatencyText = $"≈ {_neuralLatency} мс";
        }
        else if (IsRunning) LatencyText = $"≈ {_engine.ReportedLatencyMs} мс";
    }
    private static double ToVisualLevel(float value) => Math.Clamp((20 * Math.Log10(Math.Max(.0001, value)) + 60) / 60 * 100, 0, 100);
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); return true; }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ProfileEntry(string id, string name, string description, bool isBuiltIn, VoiceSettings settings, UserVoiceProfile? userProfile = null)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public bool IsBuiltIn { get; } = isBuiltIn;
    public string Kind => IsBuiltIn ? "Встроенный · защищён" : "Мой профиль";
    public VoiceSettings Settings { get; set; } = settings;
    public UserVoiceProfile? UserProfile { get; } = userProfile;
}
public sealed record ParameterGroup(string Name, IReadOnlyList<ParameterViewModel> Parameters);
public sealed class ParameterViewModel(VoiceParameter parameter, MainViewModel owner) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name => parameter.Name;
    public float Min => parameter.Min;
    public float Max => parameter.Max;
    public float Step => parameter.Step;
    public bool CanEdit => owner.CanEdit;
    public double Value { get => parameter.Get(owner.Settings); set { if (double.IsFinite(value)) owner.ChangeParameter(parameter, (float)value); Refresh(); } }
    public string DisplayValue => parameter.Unit == "%" ? $"{Value * 100:0.#}%" : $"{Value:0.##} {parameter.Unit}";
    public void Refresh() { foreach (var name in new[] { nameof(Value), nameof(DisplayValue), nameof(CanEdit) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}

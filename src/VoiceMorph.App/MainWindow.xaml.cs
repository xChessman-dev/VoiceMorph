using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceMorph.App.Models;
using VoiceMorph.App.Services;
using VoiceMorph.App.ViewModels;

namespace VoiceMorph.App;

public partial class MainWindow : Window
{
    private static readonly string DataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly string SettingsPath = Path.Combine(DataDirectory, "settings.json");
    private readonly AudioEngine _engine = new();
    private readonly MainViewModel _viewModel;
    private readonly VoiceAnalyzer _analyzer = new();
    private readonly VoiceProfileStore _store = new();
    private readonly PreviewService _preview = new();
    private VoiceAnalysis? _calibration;
    private string? _targetPath, _calibrationPath;
    private CancellationTokenSource? _operation;
    private bool _closed, _changingEngineState, _profilesLoaded;

    public MainWindow()
    {
        InitializeComponent();
        WindowBounds.Attach(this);
        _viewModel = new MainViewModel(_engine); DataContext = _viewModel;
        _engine.MeterUpdated += Engine_MeterUpdated; _engine.EngineFaulted += Engine_EngineFaulted;
        Loaded += MainWindow_Loaded; Closed += MainWindow_Closed;
    }
    private async void MainWindow_Loaded(object sender, RoutedEventArgs args)
    {
        AppSettings? restored = null;
        try
        {
            var data = await _store.LoadAsync();
            _profilesLoaded = true;
            _calibration = data.Calibration;
            foreach (var profile in data.Profiles) _viewModel.AddProfile(profile);
            if (File.Exists(SettingsPath)) restored = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(SettingsPath));
        }
        catch (Exception exception) { _viewModel.ApplyError("Не удалось прочитать профили: " + exception.Message); }
        RefreshDevices(restored?.InputDeviceId, restored?.OutputDeviceId);
        _viewModel.SelectedProfile = _viewModel.Profiles.FirstOrDefault(profile => profile.Id == (restored?.ProfileId ?? restored?.PresetId)) ?? _viewModel.Profiles[0];
        if (restored is not null) _viewModel.Strength = restored.Strength;
        _calibrationPath = restored?.CalibrationPath;
        UpdateCalibrationStatus();
        await InitializeNeuralUiAsync(restored?.NeuralModelId, restored?.NeuralMode ?? false);
        if (App.DiagnosticDirectory is not null)
        {
            try { await UiDiagnostics.RunAsync(this, App.DiagnosticDirectory); Application.Current.Shutdown(0); }
            catch (Exception exception)
            {
                Directory.CreateDirectory(App.DiagnosticDirectory);
                File.WriteAllText(Path.Combine(App.DiagnosticDirectory, "error.txt"), exception.ToString());
                Application.Current.Shutdown(1);
            }
        }
    }
    private void MainWindow_Closed(object? sender, EventArgs args)
    {
        _closed = true; _operation?.Cancel();
        _engine.MeterUpdated -= Engine_MeterUpdated; _engine.EngineFaulted -= Engine_EngineFaulted;
        _neuralClient?.Dispose(); _neuralEngine.StopAsync().GetAwaiter().GetResult();
        _preview.Dispose(); _engine.Dispose(); _viewModel.Dispose();
        if (App.DiagnosticDirectory is not null) return;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new AppSettings(
                (InputDeviceComboBox.SelectedItem as AudioDeviceInfo)?.Id,
                (OutputDeviceComboBox.SelectedItem as AudioDeviceInfo)?.Id,
                _viewModel.SelectedProfile.Id, null, _viewModel.Strength, _calibrationPath,
                IsNeuralMode, SelectedNeuralModel?.Id.ToString("D"))));
            if (_profilesLoaded) _store.SaveAsync(new VoiceProfileData(1, _calibration, _viewModel.UserProfiles)).GetAwaiter().GetResult();
        }
        catch { /* A locked data directory must not prevent exit. */ }
    }
    private async void EngineButton_Click(object sender, RoutedEventArgs args)
    {
        if (_changingEngineState || _operation is not null) return;
        _changingEngineState = true; EngineButton.IsEnabled = false;
        try
        {
            if (_engine.IsRunning) { _engine.Stop(); _viewModel.ApplyRunningState(false); SetDeviceEditing(true); return; }
            _preview.Stop();
            if (InputDeviceComboBox.SelectedItem is not AudioDeviceInfo input || OutputDeviceComboBox.SelectedItem is not AudioDeviceInfo output)
                throw new InvalidOperationException("Выбери микрофон и аудиовыход.");
            if (IsNeuralMode) { await StartOrStopNeuralAsync(input, output); return; }
            _engine.ApplySettings(_viewModel.EffectiveSettings);
            await _engine.StartAsync(input.Id, output.Id);
            _engine.SetBypass(false); _viewModel.ApplyRunningState(true); _viewModel.ApplyBypassState(false); SetDeviceEditing(false);
            if (!output.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                _viewModel.StatusDetail = "Выбран физический выход. Используй наушники, чтобы избежать обратной связи.";
        }
        catch (Exception exception)
        {
            _engine.Stop(); await _neuralEngine.StopAsync(); _neuralClient?.Dispose(); _neuralClient = null;
            if (_closed) return;
            if (IsNeuralMode) _viewModel.ApplyNeuralRunningState(false, SelectedNeuralModel?.Name ?? "");
            else _viewModel.ApplyRunningState(false);
            _viewModel.ApplyError(LocalizeNeuralError(exception.Message)); SetDeviceEditing(true);
            ProcessingModeBox.IsEnabled = true; NeuralModelsList.IsEnabled = true;
        }
        finally { _changingEngineState = false; if (!_closed) UpdateEngineAvailability(); }
    }
    private void SetDeviceEditing(bool enabled)
    { InputDeviceComboBox.IsEnabled = enabled; OutputDeviceComboBox.IsEnabled = enabled; RefreshDevicesButton.IsEnabled = enabled; BypassButton.IsEnabled = !enabled; }
    private void BypassButton_Click(object sender, RoutedEventArgs args)
    {
        if (_neuralEngine.IsRunning) { _neuralEngine.SetBypass(!_neuralEngine.IsBypassed); _viewModel.ApplyBypassState(_neuralEngine.IsBypassed); return; }
        if (!_engine.IsRunning) return; _engine.SetBypass(!_engine.IsBypassed); _viewModel.ApplyBypassState(_engine.IsBypassed);
    }
    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs args) => RefreshDevices();
    private void RefreshDevices(string? inputId = null, string? outputId = null)
    {
        inputId ??= (InputDeviceComboBox.SelectedItem as AudioDeviceInfo)?.Id;
        outputId ??= (OutputDeviceComboBox.SelectedItem as AudioDeviceInfo)?.Id;
        try
        {
            var inputs = AudioEngine.GetInputDevices(); var outputs = AudioEngine.GetOutputDevices();
            InputDeviceComboBox.ItemsSource = inputs; OutputDeviceComboBox.ItemsSource = outputs;
            InputDeviceComboBox.SelectedItem = inputs.FirstOrDefault(device => device.Id == inputId)
                ?? inputs.FirstOrDefault(device => device.Name.Contains("PD200X", StringComparison.OrdinalIgnoreCase))
                ?? inputs.FirstOrDefault(device => device.Name.Contains("Maono", StringComparison.OrdinalIgnoreCase)) ?? inputs.FirstOrDefault();
            OutputDeviceComboBox.SelectedItem = outputs.FirstOrDefault(device => device.Id == outputId)
                ?? outputs.FirstOrDefault(device => device.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) && !device.Name.Contains("16ch", StringComparison.OrdinalIgnoreCase)) ?? outputs.FirstOrDefault();
            if (inputs.Count == 0 || outputs.Count == 0) _viewModel.ApplyError("Подключи микрофон и проверь виртуальный кабель.");
        }
        catch (Exception exception) { _viewModel.ApplyError(exception.Message); }
    }
    private void Engine_MeterUpdated(object? sender, AudioMeterEventArgs args)
    { if (!_closed) Dispatcher.BeginInvoke(() => { if (!_closed) _viewModel.ApplyMeter(args); }); }
    private void Engine_EngineFaulted(object? sender, string message)
    { if (!_closed) Dispatcher.BeginInvoke(() => { if (_closed) return; _engine.Stop(); _viewModel.ApplyRunningState(false); _viewModel.ApplyError(message); SetDeviceEditing(true); }); }
    private void OpenCreate_Click(object sender, RoutedEventArgs args) => MainTabs.SelectedIndex = 1;
    private async void CopyProfile_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var originalName = _viewModel.SelectedProfile.Name;
            var copyName = originalName[..Math.Min(originalName.Length, 50)] + " · моя версия";
            var profile = new UserVoiceProfile(Guid.NewGuid(), copyName, _viewModel.EffectiveSettings, DateTime.UtcNow);
            _viewModel.AddProfile(profile); await SaveProfilesSafelyAsync(); MainTabs.SelectedIndex = 2;
        }
        catch (Exception exception) { _viewModel.ApplyError(exception.Message); }
    }
    private async void SaveProfile_Click(object sender, RoutedEventArgs args)
    { if (await SaveProfilesSafelyAsync()) { _viewModel.StatusTitle = "Профиль сохранён"; _viewModel.StatusDetail = _viewModel.SelectedProfile.Name; } }
    private async Task<bool> SaveProfilesSafelyAsync()
    {
        if (!_profilesLoaded) { _viewModel.ApplyError("Хранилище не удалось прочитать. Оно сохранено без изменений; новый профиль можно экспортировать в отдельный файл."); return false; }
        try { await _store.SaveAsync(new VoiceProfileData(1, _calibration, _viewModel.UserProfiles)); return true; }
        catch (Exception exception) { _viewModel.ApplyError("Не удалось сохранить: " + exception.Message); return false; }
    }
    private async void ImportProfile_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Title = "Импорт голосового профиля", Filter = "Профиль VoiceMorph|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { _viewModel.AddProfile(await _store.ImportAsync(dialog.FileName)); await SaveProfilesSafelyAsync(); }
        catch (Exception exception) { _viewModel.ApplyError("Импорт: " + exception.Message); }
    }
    private async void ExportProfile_Click(object sender, RoutedEventArgs args)
    {
        var selected = _viewModel.SelectedProfile;
        var dialog = new SaveFileDialog { Title = "Экспорт голосового профиля", Filter = "Профиль VoiceMorph|*.json", FileName = "voice-profile.json" };
        if (dialog.ShowDialog(this) != true) return;
        var profile = selected.UserProfile ?? new UserVoiceProfile(Guid.NewGuid(), selected.Name, _viewModel.EffectiveSettings, DateTime.UtcNow);
        try { await _store.ExportAsync(profile with { Settings = _viewModel.EffectiveSettings }, dialog.FileName); _viewModel.StatusDetail = "Профиль экспортирован: " + Path.GetFileName(dialog.FileName); }
        catch (Exception exception) { _viewModel.ApplyError("Экспорт: " + exception.Message); }
    }
    private static OpenFileDialog AudioDialog(string title) => new() { Title = title, CheckFileExists = true, Filter = "Аудио и видео|*.wav;*.mp3;*.flac;*.m4a;*.mp4;*.aac;*.wma|Все файлы|*.*" };
    private async void LoadCalibration_Click(object sender, RoutedEventArgs args)
    {
        var dialog = AudioDialog("Твой исходный голос: от 20 секунд"); if (dialog.ShowDialog(this) != true) return;
        await RunAnalysisOperationAsync(async token =>
        {
            var result = await _analyzer.AnalyzeFileAsync(dialog.FileName, CreateProgress(), token);
            ValidateCalibration(result); _calibration = result; _calibrationPath = dialog.FileName;
            UpdateCalibrationStatus(); await SaveProfilesSafelyAsync(); AnalysisStatus.Text = "Калибровка готова. Выбери целевой голос.";
        });
    }
    private void LoadTarget_Click(object sender, RoutedEventArgs args)
    { var dialog = AudioDialog("Пример голоса: от 2 минут"); if (dialog.ShowDialog(this) != true) return; _targetPath = dialog.FileName; TargetStatus.Text = Path.GetFileName(_targetPath); }
    private async void RecordCalibration_Click(object sender, RoutedEventArgs args)
    {
        if (InputDeviceComboBox.SelectedItem is not AudioDeviceInfo input) { AnalysisStatus.Text = "Выбери микрофон вверху окна."; return; }
        if (_engine.IsRunning || _neuralEngine.IsRunning) { AnalysisStatus.Text = "Останови обработку перед калибровкой, затем запиши свой обычный голос."; return; }
        await RunAnalysisOperationAsync(async token =>
        {
            Directory.CreateDirectory(DataDirectory);
            var path = Path.Combine(DataDirectory, "calibration.wav");
            var pending = Path.Combine(DataDirectory, "pending-calibration-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                var progress = new Progress<double>(fraction => { if (_closed) return; AnalysisProgressBar.Value = fraction; AnalysisStatus.Text = $"Запись {fraction * 30:0}/30 с. Говори обычным голосом, без длинных пауз."; });
                await new CalibrationRecorder().CaptureAsync(input.Id, pending, TimeSpan.FromSeconds(30), progress, token);
                var result = await _analyzer.AnalyzeFileAsync(pending, CreateProgress(), token); ValidateCalibration(result);
                token.ThrowIfCancellationRequested(); File.Move(pending, path, overwrite: true);
                _calibration = result; _calibrationPath = path; UpdateCalibrationStatus(); await SaveProfilesSafelyAsync(); AnalysisStatus.Text = "Калибровка готова. Теперь выбери голос для ориентира.";
            }
            finally { try { if (File.Exists(pending)) File.Delete(pending); } catch (IOException) { } }
        });
    }
    private async void AnalyzeProfile_Click(object sender, RoutedEventArgs args)
    {
        if (_calibration is null) { AnalysisStatus.Text = "Сначала запиши или загрузи свой обычный голос."; return; }
        if (_targetPath is null) { AnalysisStatus.Text = "Выбери файл с целевым голосом."; return; }
        var name = ProfileNameBox.Text.Trim(); if (name.Length == 0) { AnalysisStatus.Text = "Введи название профиля."; ProfileNameBox.Focus(); return; }
        var strength = float.Parse((string)((ComboBoxItem)FitStrengthBox.SelectedItem).Tag, CultureInfo.InvariantCulture);
        var source = _calibration; var targetPath = _targetPath;
        await RunAnalysisOperationAsync(async token =>
        {
            var target = await _analyzer.AnalyzeFileAsync(targetPath, CreateProgress(), token);
            var fit = _analyzer.FitProfile(source, target, strength);
            var profile = new UserVoiceProfile(Guid.NewGuid(), name, fit.Settings, DateTime.UtcNow, source, target, fit.Notes);
            _viewModel.AddProfile(profile);
            if (await SaveProfilesSafelyAsync()) AnalysisStatus.Text = "Профиль сохранён. " + target.Summary + "\n" + string.Join("\n", fit.Notes);
        });
    }
    private IProgress<AnalysisProgress> CreateProgress() => new Progress<AnalysisProgress>(progress =>
    { if (_closed) return; AnalysisProgressBar.Value = progress.Fraction; AnalysisStatus.Text = progress.Message; });
    private async Task RunAnalysisOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_operation is not null) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        SetAnalysisBusy(true);
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { if (!_closed) AnalysisStatus.Text = "Операция отменена. Профиль не создан."; }
        catch (Exception exception) { if (!_closed) AnalysisStatus.Text = exception.Message; }
        finally { _operation = null; if (!_closed) SetAnalysisBusy(false); }
    }
    private void SetAnalysisBusy(bool busy)
    {
        if (busy) _preview.Stop();
        PreviewDryButton.IsEnabled = !busy && _calibrationPath is not null && File.Exists(_calibrationPath);
        PreviewWetButton.IsEnabled = PreviewDryButton.IsEnabled;
        AnalyzeButton.IsEnabled = !busy; RecordCalibrationButton.IsEnabled = !busy; LoadCalibrationButton.IsEnabled = !busy;
        LoadTargetButton.IsEnabled = !busy; EngineButton.IsEnabled = !busy;
        CancelAnalysisButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AnalysisProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AnalysisProgressBar.Value = 0;
    }
    private static void ValidateCalibration(VoiceAnalysis analysis)
    { if (analysis.DurationSeconds < 20 || analysis.VoicedSeconds < 8) throw new InvalidOperationException("Нужно хотя бы 20 секунд образца с 8 секундами различимой речи. Запиши калибровку ещё раз."); }
    private void UpdateCalibrationStatus()
    {
        CalibrationStatus.Text = _calibration is null ? "Калибровка ещё не выполнена" : _calibration.Summary;
        var available = _calibrationPath is not null && File.Exists(_calibrationPath);
        PreviewDryButton.IsEnabled = available; PreviewWetButton.IsEnabled = available;
        if (available) PreviewStatus.Text = "Первые 10 секунд твоей записи. Слушай в наушниках; звук не отправляется в кабель.";
    }
    private void PreviewDry_Click(object sender, RoutedEventArgs args) => PlayPreview(false);
    private void PreviewWet_Click(object sender, RoutedEventArgs args) => PlayPreview(true);
    private async void PlayPreview(bool processed)
    {
        if (_calibrationPath is null || !File.Exists(_calibrationPath)) { UpdateCalibrationStatus(); return; }
        if (_engine.IsRunning) { PreviewStatus.Text = "Останови голос перед сравнением, чтобы записи не смешивались."; return; }
        StopPreviewButton.Visibility = Visibility.Visible;
        PreviewDryButton.IsEnabled = false; PreviewWetButton.IsEnabled = false;
        PreviewStatus.Text = processed ? "Слушаем обработанный вариант…" : "Слушаем исходную запись…";
        try { await _preview.PlayAsync(_calibrationPath, _viewModel.EffectiveSettings, processed); if (!_closed) PreviewStatus.Text = "Готово. Выход: " + _preview.LastOutputDeviceName; }
        catch (OperationCanceledException) { if (!_closed) PreviewStatus.Text = "Прослушивание остановлено."; }
        catch (Exception exception) { if (!_closed) PreviewStatus.Text = exception.Message; }
        finally { if (!_closed) { StopPreviewButton.Visibility = Visibility.Collapsed; PreviewDryButton.IsEnabled = true; PreviewWetButton.IsEnabled = true; } }
    }
    private void StopPreview_Click(object sender, RoutedEventArgs args) => _preview.Stop();
    private void CancelAnalysis_Click(object sender, RoutedEventArgs args) => _operation?.Cancel();
    private async void ConfigureObs_Click(object sender, RoutedEventArgs args)
    {
        ConfigureObsButton.IsEnabled = false;
        try
        {
            var result = await new ObsIntegrationService().ConfigureAsync((InputDeviceComboBox.SelectedItem as AudioDeviceInfo)?.Id);
            if (!_closed) ObsStatus.Text = result.Message;
        }
        catch (Exception exception) { if (!_closed) ObsStatus.Text = "OBS: " + exception.Message; }
        finally { if (!_closed) ConfigureObsButton.IsEnabled = true; }
    }
    private void MinimizeButton_Click(object sender, RoutedEventArgs args) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs args) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs args) => Close();
    private sealed record AppSettings(string? InputDeviceId, string? OutputDeviceId, string? ProfileId, string? PresetId, double Strength = 1, string? CalibrationPath = null, bool NeuralMode = false, string? NeuralModelId = null);
}

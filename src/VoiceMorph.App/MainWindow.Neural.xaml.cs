using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VoiceMorph.App.Models;
using VoiceMorph.App.Services;

namespace VoiceMorph.App;

public partial class MainWindow
{
    private static readonly string NeuralModelDirectory = Environment.GetEnvironmentVariable("VOICEMORPH_MODEL_ROOT")
        ?? Path.Combine(AppContext.BaseDirectory, "data", "neural-models");
    private readonly NeuralModelStore _neuralStore = new();
    private readonly ObservableCollection<NeuralVoiceModel> _neuralModels = [];
    private readonly NeuralAudioEngine _neuralEngine = new();
    private NeuralRuntimeClient? _neuralClient;
    private bool _neuralUiInitialized, _neuralLibraryLoaded, _switchingMode;
    private bool? _runtimeProbeReady;
    private NeuralVoiceModel? SelectedNeuralModel => NeuralModelsList.SelectedItem as NeuralVoiceModel;
    private bool IsNeuralMode => ProcessingModeBox.SelectedIndex == 1;
    private NeuralRuntimeClient NeuralClient => _neuralClient ??= new NeuralRuntimeClient();
    private static bool NeuralRuntimeFilesPresent => new[]
    {
        @"python\python.exe", @"python\Lib\site-packages\torch\__init__.py",
        @"assets\hubert_base\config.json", @"assets\hubert_base\preprocessor_config.json",
        @"assets\hubert_base\pytorch_model.bin", @"assets\rmvpe\rmvpe.pt",
        @"vendor\infer\module\models.py", @"vendor\infer\rmvpe.py"
    }.All(relative => File.Exists(Path.Combine(NeuralRuntimeClient.DefaultRuntimeRoot, relative)));

    private void UpdateEngineAvailability()
    {
        if (!_neuralUiInitialized || _closed) return;
        EngineButton.IsEnabled = _operation is null && !_changingEngineState && !_switchingMode &&
            (_viewModel.IsRunning || !IsNeuralMode || (SelectedNeuralModel?.IsAvailable == true && NeuralRuntimeFilesPresent && _runtimeProbeReady != false));
        EngineButton.ToolTip = IsNeuralMode && !NeuralRuntimeFilesPresent
            ? "Сначала установи движок RVC и нажми «Проверить движок»." : null;
    }
    private static string LocalizeNeuralError(string message)
    {
        if (message.Contains("Optional RVC runtime", StringComparison.OrdinalIgnoreCase))
            return "Движок RVC не установлен или установка не завершена. Установи его и нажми «Проверить движок».";
        if (message.Contains("Legacy pickle checkpoints", StringComparison.OrdinalIgnoreCase))
            return "Модель сохранена в старом pickle-формате. Он не загружается из соображений безопасности: нужен стандартный экспорт RVC в современном tensor-формате.";
        if (message.Contains("Safe tensor loading failed", StringComparison.OrdinalIgnoreCase))
            return "Не удалось безопасно прочитать веса модели. Нестандартные pickle-объекты не поддерживаются; выбери другой стандартный экспорт RVC.";
        if (message.Contains("CUDA is unavailable", StringComparison.OrdinalIgnoreCase))
            return "GPU-движок не видит CUDA. Нажми «Проверить движок», чтобы увидеть состояние видеокарты.";
        return message;
    }

    private async Task InitializeNeuralUiAsync(string? selectedId = null, bool restoreNeuralMode = false)
    {
        NeuralModelsList.ItemsSource = _neuralModels;
        try
        {
            var library = await _neuralStore.LoadAsync();
            foreach (var model in library.Models) _neuralModels.Add(model);
            _neuralLibraryLoaded = true;
            NeuralModelsList.SelectedItem = _neuralModels.FirstOrDefault(model => model.Id.ToString("D") == selectedId)
                ?? _neuralModels.FirstOrDefault();
        }
        catch (Exception exception) { ModelImportStatus.Text = "Библиотека сохранена без изменений: " + exception.Message; }
        _neuralEngine.MeterUpdated += Engine_MeterUpdated;
        _neuralEngine.EngineFaulted += NeuralEngine_Faulted;
        _neuralEngine.TelemetryUpdated += NeuralEngine_Telemetry;
        _neuralUiInitialized = true;
        UpdateSelectedNeuralModel();
        if (restoreNeuralMode) ProcessingModeBox.SelectedIndex = 1;
        UpdateEngineAvailability();
    }

    private async void ProcessingMode_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_neuralUiInitialized || _switchingMode || _closed) return;
        _switchingMode = true; EngineButton.IsEnabled = false; ProcessingModeBox.IsEnabled = false;
        try
        {
            _preview.Stop(); _engine.Stop();
            await _neuralEngine.StopAsync();
            // Kill our optional worker too, releasing its RAM and GPU, not just its model.
            _neuralClient?.Dispose(); _neuralClient = null;
            _viewModel.SetNeuralMode(IsNeuralMode);
            SetDeviceEditing(true);
            SetNeuralBusy(false);
            DspVoicesTab.IsEnabled = !IsNeuralMode; DspCreateTab.IsEnabled = !IsNeuralMode; DspSettingsTab.IsEnabled = !IsNeuralMode;
            if (IsNeuralMode)
            {
                _viewModel.ApplyNeuralRunningState(false, SelectedNeuralModel?.Name ?? "");
                MainTabs.SelectedItem = NeuralModelsTab;
                if (!NeuralRuntimeFilesPresent)
                {
                    _viewModel.StatusTitle = "RVC-движок не установлен";
                    _viewModel.StatusDetail = "Импорт моделей доступен. Для голоса нужен локальный движок RVC.";
                    NeuralRuntimeStatus.Text = "Установи движок RVC и укажи VOICEMORPH_RUNTIME_ROOT, затем нажми «Проверить движок».";
                }
            }
            else { _viewModel.ApplyRunningState(false); MainTabs.SelectedItem = DspVoicesTab; }
        }
        catch (Exception exception) { _viewModel.ApplyError(exception.Message); }
        finally { _switchingMode = false; if (!_closed) { ProcessingModeBox.IsEnabled = true; UpdateEngineAvailability(); } }
    }

    private void NeuralModel_Changed(object sender, SelectionChangedEventArgs args)
    { if (_neuralUiInitialized) UpdateSelectedNeuralModel(); }

    private void UpdateSelectedNeuralModel()
    {
        var model = SelectedNeuralModel;
        ModelEmptyText.Visibility = _neuralModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedModelTitle.Text = model?.Name ?? "Модель не выбрана";
        SelectedModelDetails.Text = model is null ? "Импортируй ZIP или выбери .pth." : model.Summary +
            (model.IsAvailable ? "\n" + (model.IndexPath is null ? "Без индекса" : "Индекс добавлен · включается отдельно") : "\nФайлы перемещены или отсутствуют");
        TrustIndexCheckBox.IsChecked = false;
        TrustIndexCheckBox.IsEnabled = model?.IndexPath is not null;
        UpdateNeuralParameterLabels();
        UpdateEngineAvailability();
    }

    private void NeuralParameters_Changed(object sender, RoutedEventArgs args)
    { if (!_neuralUiInitialized) return; UpdateNeuralParameterLabels(); if (_neuralEngine.IsRunning) _neuralEngine.ApplyOptions(CurrentNeuralOptions()); }
    private void UpdateNeuralParameterLabels()
    {
        var supportsPitch = SelectedNeuralModel?.Inspection?.UsesF0 != false;
        NeuralPitchSlider.IsEnabled = supportsPitch;
        NeuralPitchValue.Text = supportsPitch ? $"{NeuralPitchSlider.Value:+0;-0;0} полутонов" : "Без F0 · недоступно";
        var indexAllowed = TrustIndexCheckBox.IsChecked == true && SelectedNeuralModel?.IndexPath is not null;
        NeuralIndexSlider.IsEnabled = indexAllowed;
        NeuralIndexValue.Text = indexAllowed ? $"{NeuralIndexSlider.Value:P0}" : "Выключен";
    }
    private NeuralRuntimeOptions CurrentNeuralOptions() => new((int)Math.Round(NeuralPitchSlider.Value),
        (float)NeuralIndexSlider.Value, .33f, "auto", 0, TrustIndexCheckBox.IsChecked == true);
    private int CurrentNeuralBlockMs => int.Parse((string)((ComboBoxItem)NeuralBlockBox.SelectedItem).Tag, CultureInfo.InvariantCulture);

    private async void ImportModelArchive_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Title = "Архив RVC-модели", Filter = "ZIP с .pth и .index|*.zip", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        await RunNeuralOperationAsync(async token =>
        {
            foreach (var path in dialog.FileNames)
            {
                foreach (var selection in await _neuralStore.InspectArchiveAsync(path, token))
                {
                    NeuralOperationStatus.Text = "Импорт: " + selection.Name;
                    var files = await _neuralStore.ExtractArchiveAsync(path, selection, NeuralModelDirectory, token);
                    NeuralVoiceModel? registered = null;
                    try
                    {
                        registered = await _neuralStore.RegisterAsync(selection.Name, files.ModelPath, files.IndexPath, cancellationToken: token);
                        if (!await AddNeuralModelAsync(registered, token)) CleanupNewModelFiles(files.ModelPath, files.IndexPath);
                    }
                    catch
                    {
                        // Never remove a pair that made it into the atomic library, even if a later UI update failed.
                        var retained = false;
                        try { retained = (await _neuralStore.LoadAsync()).Models.Any(item => item.ModelPath == files.ModelPath); }
                        catch { retained = true; }
                        if (!retained) CleanupNewModelFiles(files.ModelPath, files.IndexPath);
                        throw;
                    }
                }
            }
            NeuralOperationStatus.Text = "Импорт готов. Файлы скопированы в локальную библиотеку, содержимое модели ещё не запускалось.";
        });
    }

    private async void ImportModelFiles_Click(object sender, RoutedEventArgs args)
    {
        var modelDialog = new OpenFileDialog { Title = "RVC-модель .pth", Filter = "RVC-модель|*.pth" };
        if (modelDialog.ShowDialog(this) != true) return;
        var indexDialog = new OpenFileDialog { Title = "Индекс .index — необязательно, можно отменить", Filter = "FAISS-индекс|*.index", InitialDirectory = Path.GetDirectoryName(modelDialog.FileName) };
        var indexPath = indexDialog.ShowDialog(this) == true ? indexDialog.FileName : null;
        await RunNeuralOperationAsync(async token =>
        {
            await AddNeuralModelAsync(await _neuralStore.RegisterAsync(Path.GetFileNameWithoutExtension(modelDialog.FileName), modelDialog.FileName, indexPath, cancellationToken: token), token);
            NeuralOperationStatus.Text = "Модель добавлена по исходному пути. Не перемещай эти файлы после импорта.";
        });
    }

    private static void CleanupNewModelFiles(string modelPath, string? indexPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(modelPath));
        var name = Path.GetFileName(folder) ?? "";
        if (folder is null || !string.Equals(Path.GetDirectoryName(folder), NeuralModelDirectory, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith("rvc-", StringComparison.Ordinal) || !Guid.TryParseExact(name[4..], "N", out _) ||
            Path.GetFileName(modelPath) != "voice.pth" || (indexPath is not null && Path.GetFullPath(indexPath) != Path.Combine(folder, "voice.index"))) return;
        try
        {
            File.Delete(modelPath); if (indexPath is not null) File.Delete(indexPath);
            Directory.Delete(folder, recursive: false);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<bool> AddNeuralModelAsync(NeuralVoiceModel model, CancellationToken token)
    {
        if (!_neuralLibraryLoaded) throw new InvalidOperationException("Библиотеку не удалось прочитать. Сначала восстанови её файл; существующие модели не перезаписываются.");
        if (_neuralModels.Any(existing => existing.ModelSha256 == model.ModelSha256 && existing.IndexSha256 == model.IndexSha256))
        { NeuralModelsList.SelectedItem = _neuralModels.First(existing => existing.ModelSha256 == model.ModelSha256 && existing.IndexSha256 == model.IndexSha256); return false; }
        var updated = _neuralModels.Append(model).ToArray();
        await _neuralStore.SaveAsync(new(1, updated), token);
        _neuralModels.Add(model); NeuralModelsList.SelectedItem = model;
        return true;
    }

    private async void CheckNeuralRuntime_Click(object sender, RoutedEventArgs args) => await RunNeuralOperationAsync(async token =>
    {
        var status = await NeuralClient.ProbeAsync(token);
        _runtimeProbeReady = status.Available;
        NeuralRuntimeStatus.Text = status.Message + (status.CudaAvailable ? "\nGPU: " + status.DeviceName : "");
        NeuralOperationStatus.Text = status.Available ? "Движок готов. Теперь проверь выбранную модель." : "Импорт доступен, но преобразование ждёт установки локального движка RVC.";
        if (!status.Available) { _neuralClient?.Dispose(); _neuralClient = null; }
    });

    private async Task<NeuralVoiceModel> ValidateSelectedModelAsync(CancellationToken token)
    {
        var model = SelectedNeuralModel ?? throw new InvalidOperationException("Выбери модель слева.");
        if (!await _neuralStore.VerifyAssetsAsync(model, token)) throw new InvalidDataException("Файлы модели изменились или отсутствуют. Импортируй её повторно.");
        return model;
    }
    private async void CheckNeuralModel_Click(object sender, RoutedEventArgs args) => await RunNeuralOperationAsync(async token =>
    {
        var model = await ValidateSelectedModelAsync(token);
        var inspection = await NeuralClient.InspectAsync(model.ModelPath, cancellationToken: token);
        var checkedModel = model with { Inspection = inspection };
        var position = _neuralModels.IndexOf(model);
        var snapshot = _neuralModels.Select(item => item.Id == model.Id ? checkedModel : item).ToArray();
        await _neuralStore.SaveAsync(new(1, snapshot), token);
        _neuralModels[position] = checkedModel; NeuralModelsList.SelectedItem = checkedModel;
        NeuralOperationStatus.Text = "Архитектура совместима: " + checkedModel.Summary + ". Это не проверка происхождения модели.";
    });

    private async void PreviewNeuralModel_Click(object sender, RoutedEventArgs args)
    {
        if (_calibrationPath is null || !File.Exists(_calibrationPath)) { NeuralOperationStatus.Text = "Сначала запиши свой голос в DSP → Создать профиль."; return; }
        await RunNeuralOperationAsync(async token =>
        {
            var model = await ValidateSelectedModelAsync(token);
            _preview.Stop();
            var pending = Path.Combine(DataDirectory, "rvc-preview-" + Guid.NewGuid().ToString("N") + ".wav");
            Directory.CreateDirectory(DataDirectory);
            try
            {
                await NeuralClient.LoadModelAsync(model, CurrentNeuralOptions(), token);
                NeuralOperationStatus.Text = "Преобразование калибровки…";
                var result = await NeuralClient.ConvertFileAsync(_calibrationPath, pending, CurrentNeuralOptions(), token);
                NeuralOperationStatus.Text = $"Готово за {result.ProcessingSeconds:0.0} с. Слушаем первые 10 секунд в наушниках…";
                await _preview.PlayAsync(pending, VoiceSettings.Neutral, false, token);
                NeuralOperationStatus.Text = "Проба завершена. Для микрофона выбери режим RVC и нажми «Запустить голос».";
            }
            finally { try { if (File.Exists(pending)) File.Delete(pending); } catch (IOException) { } }
        });
    }

    private async void ConvertNeuralFile_Click(object sender, RoutedEventArgs args)
    {
        var input = AudioDialog("Исходный голос для RVC"); if (input.ShowDialog(this) != true) return;
        var output = new SaveFileDialog { Title = "Обработанный голос", Filter = "WAV|*.wav", FileName = "voice-rvc.wav", InitialDirectory = NeuralModelDirectory };
        if (output.ShowDialog(this) != true) return;
        if (Path.GetFullPath(input.FileName).Equals(Path.GetFullPath(output.FileName), StringComparison.OrdinalIgnoreCase))
        { NeuralOperationStatus.Text = "Исходник и результат должны быть разными файлами."; return; }
        await RunNeuralOperationAsync(async token =>
        {
            var model = await ValidateSelectedModelAsync(token);
            await NeuralClient.LoadModelAsync(model, CurrentNeuralOptions(), token);
            var result = await NeuralClient.ConvertFileAsync(input.FileName, output.FileName, CurrentNeuralOptions(), token);
            NeuralOperationStatus.Text = $"Сохранено: {result.OutputPath}\nАудио {result.AudioSeconds:0.0} с · обработка {result.ProcessingSeconds:0.0} с · {result.Device}";
        });
    }

    private async Task RunNeuralOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_operation is not null || _changingEngineState || _switchingMode) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        SetNeuralBusy(true); NeuralOperationStatus.Text = "Выполняется…";
        try
        {
            if (_engine.IsRunning || _neuralEngine.IsRunning) throw new InvalidOperationException("Останови голос перед импортом, проверкой или обработкой файла.");
            await action(cancellation.Token);
        }
        catch (OperationCanceledException) { if (!_closed) NeuralOperationStatus.Text = "Операция отменена."; }
        catch (Exception exception) { if (!_closed) NeuralOperationStatus.Text = LocalizeNeuralError(exception.Message); }
        finally
        {
            _operation = null;
            // Offline tests need no resident GPU worker after their result has been returned.
            _neuralClient?.Dispose(); _neuralClient = null;
            if (!_closed) SetNeuralBusy(false);
        }
    }

    private void SetNeuralBusy(bool busy)
    {
        foreach (var control in new Control[] { ImportArchiveButton, ImportPthButton, CheckRuntimeButton, CheckModelButton,
            NeuralPreviewButton, NeuralConvertFileButton, NeuralUnloadButton, NeuralModelsList, NeuralPitchSlider, TrustIndexCheckBox,
            NeuralBlockBox, EngineButton, ProcessingModeBox, InputDeviceComboBox, OutputDeviceComboBox, RefreshDevicesButton }) control.IsEnabled = !busy;
        NeuralProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelNeuralButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy)
        {
            var running = _neuralEngine.IsRunning;
            NeuralModelsList.IsEnabled = !running; NeuralBlockBox.IsEnabled = !running;
            TrustIndexCheckBox.IsEnabled = !running && SelectedNeuralModel?.IndexPath is not null;
            foreach (var control in new Control[] { ImportArchiveButton, ImportPthButton, CheckRuntimeButton, CheckModelButton,
                NeuralPreviewButton, NeuralConvertFileButton }) control.IsEnabled = !running;
            UpdateNeuralParameterLabels(); SetDeviceEditing(!_viewModel.IsRunning);
            UpdateEngineAvailability();
        }
    }
    private void CancelNeural_Click(object sender, RoutedEventArgs args) => _operation?.Cancel();
    private async void UnloadNeuralRuntime_Click(object sender, RoutedEventArgs args)
    {
        if (_operation is not null || _changingEngineState || _switchingMode) return;
        _changingEngineState = true; SetNeuralBusy(true); CancelNeuralButton.Visibility = Visibility.Collapsed;
        try
        {
            await _neuralEngine.StopAsync(); _neuralClient?.Dispose(); _neuralClient = null;
            if (IsNeuralMode) _viewModel.ApplyNeuralRunningState(false, SelectedNeuralModel?.Name ?? "");
            SetNeuralBusy(false); NeuralRuntimeStatus.Text = "Движок выгружен. GPU и память процесса RVC освобождены.";
        }
        catch (Exception exception) { NeuralOperationStatus.Text = exception.Message; }
        finally { _changingEngineState = false; if (!_closed) SetNeuralBusy(false); }
    }

    private async Task StartOrStopNeuralAsync(AudioDeviceInfo input, AudioDeviceInfo output)
    {
        if (_neuralEngine.IsRunning)
        {
            await _neuralEngine.StopAsync(); _neuralClient?.Dispose(); _neuralClient = null;
            _viewModel.ApplyNeuralRunningState(false, SelectedNeuralModel?.Name ?? ""); SetDeviceEditing(true);
            SetNeuralBusy(false); return;
        }
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        SetNeuralBusy(true);
        try
        {
            var model = await ValidateSelectedModelAsync(cancellation.Token);
            _viewModel.StatusTitle = "Загрузка RVC-модели…";
            _viewModel.StatusDetail = "Первый запуск занимает время; GPU-движок запускается отдельно.";
            ProcessingModeBox.IsEnabled = false; NeuralModelsList.IsEnabled = false;
            await _neuralEngine.StartAsync(input.Id, output.Id, model, CurrentNeuralOptions(), CurrentNeuralBlockMs, NeuralClient, cancellation.Token);
            if (!_neuralEngine.IsRunning) throw new InvalidOperationException("RVC остановился во время запуска. Проверь движок и аудиоустройства.");
            _viewModel.ApplyNeuralRunningState(true, model.Name, _neuralEngine.ReportedLatencyMs);
            SetDeviceEditing(false); ProcessingModeBox.IsEnabled = true;
        }
        finally
        {
            _operation = null;
            if (!_closed) SetNeuralBusy(false);
        }
    }

    private void NeuralEngine_Faulted(object? sender, string message)
    {
        if (_closed) return;
        var failedClient = _neuralClient;
        Dispatcher.BeginInvoke(async () =>
        {
            if (_closed || !IsNeuralMode || !ReferenceEquals(failedClient, _neuralClient) || _changingEngineState || _switchingMode) return;
            _changingEngineState = true; SetNeuralBusy(true); CancelNeuralButton.Visibility = Visibility.Collapsed;
            try
            {
                await _neuralEngine.StopAsync(); failedClient?.Dispose(); _neuralClient = null;
                if (_closed) return;
                _viewModel.ApplyNeuralRunningState(false, SelectedNeuralModel?.Name ?? ""); _viewModel.ApplyError(message);
            }
            catch (Exception exception) { if (!_closed) _viewModel.ApplyError(exception.Message); }
            finally { _changingEngineState = false; if (!_closed) SetNeuralBusy(false); }
        });
    }
    private void NeuralEngine_Telemetry(object? sender, NeuralAudioTelemetry telemetry)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _viewModel.ApplyNeuralTelemetry(_neuralEngine.ReportedLatencyMs,
                $" · RVC RAM {(_neuralClient?.WorkerWorkingSetBytes ?? 0) / 1048576d:0} МБ · {_neuralEngine.LastProcessingMs:0} мс/блок");
            NeuralOperationStatus.Text = $"Обработка {telemetry.ProcessingMilliseconds:0} мс / блок {telemetry.BlockMilliseconds} мс · " +
                $"очередь {telemetry.QueueMilliseconds:0} мс · пропуски {_neuralEngine.DroppedBlocks}" +
                (telemetry.CannotKeepUp ? "\nДвижок не успевает. Увеличь блок после остановки голоса или выбери более лёгкую модель." : "");
        });
    }
}

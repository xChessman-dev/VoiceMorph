using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;

namespace VoiceMorph.App.Services;

public enum ObsIntegrationStatus
{
    Configured,
    WebSocketDisabled,
    NotRunning,
    AuthenticationFailed,
    OutputActive,
    CableMissing,
    Failed,
}

public sealed record ObsIntegrationResult(
    ObsIntegrationStatus Status,
    string Message,
    string? SourceName = null,
    int MutedMicrophones = 0)
{
    public bool Success => Status == ObsIntegrationStatus.Configured;
}

/// <summary>Configures the local OBS v5 WebSocket without replacing scenes or changing video settings.</summary>
public sealed class ObsIntegrationService
{
    public async Task<ObsIntegrationResult> ConfigureAsync(
        string? rawMicrophoneDeviceId = null,
        bool muteRawMicrophone = true,
        CancellationToken cancellationToken = default)
    {
        var configuration = ReadConfiguration();
        if (!configuration.Enabled)
        {
            return new(ObsIntegrationStatus.WebSocketDisabled,
                "В OBS: Инструменты → Настройки сервера WebSocket → включить сервер. Затем нажмите подключение ещё раз.");
        }

        var cableDeviceId = FindCableCaptureDevice();
        if (cableDeviceId is null)
        {
            return new(ObsIntegrationStatus.CableMissing,
                "Не найден CABLE Output. Установите VB-CABLE и обновите устройства.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await using var connection = new ObsConnection();
            await connection.ConnectAsync(configuration.Port, configuration.Password, timeout.Token);
            var stream = await connection.RequestAsync("GetStreamStatus", null, timeout.Token);
            var recording = await connection.RequestAsync("GetRecordStatus", null, timeout.Token);
            if (stream.GetProperty("outputActive").GetBoolean() || recording.GetProperty("outputActive").GetBoolean())
            {
                return new(ObsIntegrationStatus.OutputActive,
                    "OBS сейчас передаёт эфир или записывает видео. Остановите вывод перед изменением аудио.");
            }

            var inputList = await connection.RequestAsync("GetInputList", null, timeout.Token);
            var inputs = new List<(string Name, string? DeviceId)>();
            foreach (var input in inputList.GetProperty("inputs").EnumerateArray())
            {
                if (input.GetProperty("inputKind").GetString() is not "wasapi_input_capture")
                {
                    continue;
                }

                var name = input.GetProperty("inputName").GetString()!;
                var settings = await connection.RequestAsync("GetInputSettings", new { inputName = name }, timeout.Token);
                var deviceId = settings.GetProperty("inputSettings").TryGetProperty("device_id", out var device)
                    ? device.GetString() : null;
                inputs.Add((name, deviceId));
            }

            // Reuse an existing global CABLE source (including VoiceBridge) to avoid hearing it twice.
            var cableInput = inputs.FirstOrDefault(input =>
                string.Equals(input.DeviceId, cableDeviceId, StringComparison.OrdinalIgnoreCase));
            var sourceName = cableInput.Name;
            if (sourceName is null)
            {
                sourceName = "VoiceMorph";
                var existingNames = inputList.GetProperty("inputs").EnumerateArray()
                    .Select(input => input.GetProperty("inputName").GetString())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                for (var suffix = 2; existingNames.Contains(sourceName); suffix++)
                {
                    sourceName = $"VoiceMorph {suffix}";
                }

                var currentScene = await connection.RequestAsync("GetCurrentProgramScene", null, timeout.Token);
                await connection.RequestAsync("CreateInput", new
                {
                    sceneName = currentScene.GetProperty("currentProgramSceneName").GetString(),
                    inputName = sourceName,
                    inputKind = "wasapi_input_capture",
                    inputSettings = new { device_id = cableDeviceId },
                    sceneItemEnabled = true,
                }, timeout.Token);
            }

            await connection.RequestAsync("SetInputMute", new { inputName = sourceName, inputMuted = false }, timeout.Token);
            await connection.RequestAsync("SetInputAudioMonitorType", new
            {
                inputName = sourceName,
                monitorType = "OBS_MONITORING_TYPE_NONE",
            }, timeout.Token);

            var muted = 0;
            if (muteRawMicrophone && !string.IsNullOrWhiteSpace(rawMicrophoneDeviceId))
            {
                foreach (var input in inputs.Where(input =>
                    input.Name != sourceName &&
                    string.Equals(input.DeviceId, rawMicrophoneDeviceId, StringComparison.OrdinalIgnoreCase)))
                {
                    await connection.RequestAsync("SetInputMute", new { inputName = input.Name, inputMuted = true }, timeout.Token);
                    muted++;
                }
            }

            return new(ObsIntegrationStatus.Configured,
                muted > 0
                    ? $"OBS получает CABLE Output через «{sourceName}». Прямой микрофон выключен, двойного голоса нет."
                    : $"OBS получает CABLE Output через «{sourceName}». Проверьте, что прямой микрофон не звучит одновременно.",
                sourceName, muted);
        }
        catch (ObsAuthenticationException)
        {
            return new(ObsIntegrationStatus.AuthenticationFailed,
                "OBS отклонил подключение. Проверьте пароль сервера WebSocket в OBS и повторите.");
        }
        catch (WebSocketException)
        {
            return new(ObsIntegrationStatus.NotRunning,
                "OBS не отвечает. Запустите OBS и включите сервер WebSocket в меню «Инструменты».");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ObsIntegrationStatus.NotRunning,
                "OBS не ответил за 8 секунд. Проверьте сервер WebSocket и повторите подключение.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or ObsRequestException or InvalidOperationException)
        {
            return new(ObsIntegrationStatus.Failed, $"Не удалось настроить OBS: {exception.Message}");
        }
    }

    private static string? FindCableCaptureDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                if (device.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) &&
                    !device.FriendlyName.Contains("16ch", StringComparison.OrdinalIgnoreCase))
                {
                    return device.ID;
                }
            }
        }

        return null;
    }

    private static (bool Enabled, int Port, string Password) ReadConfiguration()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "obs-studio", "plugin_config", "obs-websocket", "config.json");
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            var enabled = root.TryGetProperty("server_enabled", out var value) && value.GetBoolean();
            var port = root.TryGetProperty("server_port", out value) ? value.GetInt32() : 4455;
            var password = root.TryGetProperty("server_password", out value) ? value.GetString() ?? "" : "";
            return (enabled, port, password);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return (false, 4455, "");
        }
    }

    private sealed class ObsConnection : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public async Task ConnectAsync(int port, string password, CancellationToken token)
        {
            await _socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), token);
            var hello = await ReceiveAsync(token);
            if (hello.GetProperty("op").GetInt32() != 0)
            {
                throw new ObsRequestException("Неожиданный ответ сервера WebSocket.");
            }

            string? authentication = null;
            if (hello.GetProperty("d").TryGetProperty("authentication", out var challenge))
            {
                var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(
                    password + challenge.GetProperty("salt").GetString())));
                authentication = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(
                    secret + challenge.GetProperty("challenge").GetString())));
            }

            await SendAsync(new { op = 1, d = new { rpcVersion = 1, authentication, eventSubscriptions = 0 } }, token);
            try
            {
                var response = await ReceiveAsync(token);
                if (response.GetProperty("op").GetInt32() != 2)
                {
                    throw new ObsAuthenticationException();
                }
            }
            catch (WebSocketException) when (authentication is not null)
            {
                throw new ObsAuthenticationException();
            }
        }

        public async Task<JsonElement> RequestAsync(string requestType, object? requestData, CancellationToken token)
        {
            var requestId = Guid.NewGuid().ToString("N");
            await SendAsync(new { op = 6, d = new { requestType, requestId, requestData } }, token);
            while (true)
            {
                var response = await ReceiveAsync(token);
                if (response.GetProperty("op").GetInt32() != 7)
                {
                    continue;
                }

                var data = response.GetProperty("d");
                if (data.GetProperty("requestId").GetString() != requestId)
                {
                    continue;
                }

                var status = data.GetProperty("requestStatus");
                if (!status.GetProperty("result").GetBoolean())
                {
                    throw new ObsRequestException(status.TryGetProperty("comment", out var comment)
                        ? comment.GetString() ?? requestType : requestType);
                }

                return data.TryGetProperty("responseData", out var result)
                    ? result.Clone() : JsonSerializer.SerializeToElement(new { });
            }
        }

        private Task SendAsync(object message, CancellationToken token) => _socket.SendAsync(
            new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions)), WebSocketMessageType.Text, true, token);

        private async Task<JsonElement> ReceiveAsync(CancellationToken token)
        {
            using var stream = new MemoryStream();
            var buffer = new byte[8192];
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (_socket.CloseStatus == (WebSocketCloseStatus)4009)
                    {
                        throw new ObsAuthenticationException();
                    }

                    throw new ObsRequestException("OBS закрыл соединение.");
                }

                stream.Write(buffer, 0, result.Count);
                if (stream.Length > 2_000_000)
                {
                    throw new ObsRequestException("Слишком большой ответ OBS.");
                }
            } while (!result.EndOfMessage);
            using var json = JsonDocument.Parse(stream.ToArray());
            return json.RootElement.Clone();
        }

        public ValueTask DisposeAsync()
        {
            _socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ObsAuthenticationException : Exception { }
    private sealed class ObsRequestException(string message) : Exception(message) { }
}

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceMorph.App.Models;

namespace VoiceMorph.App.Services;

/// <summary>Portable, versioned JSON. Audio references are never embedded in profiles.</summary>
public sealed class VoiceProfileStore
{
    private const int SchemaVersion = 1;
    private const long MaximumJsonBytes = 1024 * 1024;
    private readonly string _directory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public VoiceProfileStore(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? Path.Combine(AppContext.BaseDirectory, "data"));
    }

    public async Task<VoiceProfileData> LoadAsync(CancellationToken cancellationToken = default)
    {
        // Windows replacement can briefly mark the previous file for deletion. Keep local
        // readers out of the commit, rather than racing a new open against that transition.
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_directory, "voice-profiles.json");
            if (!File.Exists(path)) return VoiceProfileData.Empty;
            var document = await ReadAsync<VoiceProfileData>(path, cancellationToken).ConfigureAwait(false);
            return ValidateData(document);
        }
        finally { _writeLock.Release(); }
    }

    public async Task SaveAsync(VoiceProfileData data, CancellationToken cancellationToken = default)
    {
        var validated = ValidateData(data);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(Path.Combine(_directory, "voice-profiles.json"), validated, cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<UserVoiceProfile> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var document = await ReadAsync<ExportDocument>(path, cancellationToken).ConfigureAwait(false);
        if (document.Version != SchemaVersion)
            throw new InvalidDataException("Версия файла профиля не поддерживается.");
        // Import always creates a new user profile and cannot overwrite a built-in or existing profile.
        return SanitizeProfile(document.Profile) with { Id = Guid.NewGuid(), CreatedUtc = DateTime.UtcNow };
    }

    public Task ExportAsync(UserVoiceProfile profile, string path, CancellationToken cancellationToken = default) =>
        WriteAsync(Path.GetFullPath(path), new ExportDocument(SchemaVersion, SanitizeProfile(profile)), cancellationToken);

    private static VoiceProfileData ValidateData(VoiceProfileData data)
    {
        if (data is null || data.Version != SchemaVersion)
            throw new InvalidDataException("Версия хранилища голосов не поддерживается.");
        var calibration = SnapshotAnalysis(data.Calibration);
        if (data.Profiles is null || data.Profiles.Count > 128)
            throw new InvalidDataException("Допустимо не более 128 пользовательских профилей.");
        var profiles = data.Profiles.Select(SanitizeProfile).ToArray();
        if (profiles.Select(profile => profile.Id).Distinct().Count() != profiles.Length)
            throw new InvalidDataException("В хранилище есть повторяющиеся идентификаторы профилей.");
        return new(SchemaVersion, calibration, profiles);
    }

    private static UserVoiceProfile SanitizeProfile(UserVoiceProfile profile)
    {
        if (profile is null || profile.Settings is null)
            throw new InvalidDataException("В файле отсутствуют настройки голоса.");
        var name = (profile.Name ?? "").Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl))
            throw new InvalidDataException("Название профиля должно содержать от 1 до 64 обычных символов.");
        var source = SnapshotAnalysis(profile.SourceAnalysis);
        var target = SnapshotAnalysis(profile.TargetAnalysis);
        if (profile.Notes?.Count > 20) throw new InvalidDataException("В профиле слишком много примечаний.");
        var notes = profile.Notes?.Select(note =>
        {
            if (note is null || note.Length > 600)
                throw new InvalidDataException("Некорректное описание профиля.");
            return note;
        }).ToArray() ?? [];
        return profile with
        {
            Id = profile.Id == Guid.Empty ? Guid.NewGuid() : profile.Id,
            Name = name,
            Settings = profile.Settings.Sanitize(),
            SourceAnalysis = source,
            TargetAnalysis = target,
            Notes = notes,
        };
    }

    private static VoiceAnalysis? SnapshotAnalysis(VoiceAnalysis? analysis)
    {
        if (analysis is null) return null;
        if (analysis.BandEnergyFractions is not { Length: 5 } ||
            analysis.Warnings is null || analysis.Warnings.Count > 20)
            throw new InvalidDataException("Некорректные акустические измерения профиля.");
        var copy = analysis with
        {
            BandEnergyFractions = analysis.BandEnergyFractions.ToArray(),
            Warnings = analysis.Warnings.ToArray(),
        };
        VoiceAnalyzer.ValidateMeasurements(copy);
        return copy;
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Deletion sharing lets a writer replace this file atomically while this reader
            // finishes reading the complete previous snapshot. Opening first removes a size-check race.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumJsonBytes)
                throw new InvalidDataException("Файл профиля слишком большой. Допустим JSON до 1 МБ.");
            using var content = new MemoryStream((int)stream.Length);
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (content.Length + count > MaximumJsonBytes)
                    throw new InvalidDataException("Файл профиля превышает 1 МБ.");
                content.Write(buffer, 0, count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = content.GetBuffer();
            var length = (int)content.Length;
            var offset = length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return JsonSerializer.Deserialize<T>(bytes.AsSpan(offset, length - offset), JsonOptions)
                ?? throw new InvalidDataException("Файл профиля пуст.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Не удалось прочитать JSON профиля: неверный формат.", exception);
        }
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > MaximumJsonBytes)
            throw new InvalidDataException("Профиль или хранилище превышает допустимый размер 1 МБ.");
        var temporary = Path.Combine(directory, ".voicemorph-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            // Cancellation before this commit preserves the previous successful snapshot.
            // Once the atomic rename commits, the operation has succeeded even if cancellation races it.
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (Exception exception) when (attempt < 5 &&
                    exception is IOException or UnauthorizedAccessException &&
                    (exception.HResult & 0xFFFF) is 5 or 32 or 33)
                {
                    // A scanner or another reader may hold a short Windows sharing lock.
                    // Retry the same atomic commit; never delete/truncate the good snapshot.
                    await Task.Delay(20 * (1 << attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* Preserve the original result; a leftover unique temp can be removed later. */ }
            catch (UnauthorizedAccessException) { /* Preserve the original result. */ }
        }
    }

    private sealed record ExportDocument(int Version, UserVoiceProfile Profile);
}

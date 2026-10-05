using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceMorph.App.Models;

namespace VoiceMorph.App.Services;

/// <summary>Safe raw-file import; .pth/pickle contents are NEVER loaded in the UI process.</summary>
public sealed class NeuralModelStore
{
    public const long MaximumAssetBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumModels = 64;
    private const int MaximumJsonBytes = 1024 * 1024;
    private readonly string _directory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public NeuralModelStore(string? directory = null) =>
        _directory = Path.GetFullPath(directory ?? Path.Combine(AppContext.BaseDirectory, "data"));

    public async Task<NeuralModelLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_directory, "neural-models.json");
        if (!File.Exists(path)) return NeuralModelLibrary.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumJsonBytes) throw new InvalidDataException("Хранилище моделей превышает 1 МБ.");
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (memory.Length + count > MaximumJsonBytes) throw new InvalidDataException("Хранилище моделей превышает 1 МБ.");
            memory.Write(buffer, 0, count);
        }
        try
        {
            return Validate(JsonSerializer.Deserialize<NeuralModelLibrary>(memory.ToArray(), JsonOptions)
                ?? throw new InvalidDataException("Хранилище моделей пусто."));
        }
        catch (JsonException exception) { throw new InvalidDataException("Неверный формат хранилища RVC.", exception); }
    }

    public async Task SaveAsync(NeuralModelLibrary library, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Validate(library), JsonOptions);
        if (bytes.Length > MaximumJsonBytes) throw new InvalidDataException("Хранилище моделей превышает 1 МБ.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(_directory);
            temporary = Path.Combine(_directory, ".neural-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(_directory, "neural-models.json"), overwrite: true);
        }
        finally
        {
            if (temporary is not null) TryDelete(temporary);
            _writeLock.Release();
        }
    }

    /// <summary>Registers paths and streams SHA-256; does not move or execute user files.</summary>
    public async Task<NeuralVoiceModel> RegisterAsync(string name, string modelPath, string? indexPath = null,
        NeuralModelInspection? inspection = null, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        modelPath = ValidateAssetPath(modelPath, ".pth");
        indexPath = string.IsNullOrWhiteSpace(indexPath) ? null : ValidateAssetPath(indexPath, ".index");
        if (inspection is not null) ValidateInspection(inspection);
        var modelAsset = await HashAssetAsync(modelPath, cancellationToken).ConfigureAwait(false);
        var indexAsset = indexPath is null ? (0L, (string?)null)
            : await HashAssetAsync(indexPath, cancellationToken).ConfigureAwait(false);
        if (modelAsset.Item1 + indexAsset.Item1 > MaximumAssetBytes)
            throw new InvalidDataException("Пара .pth и .index превышает 2 ГБ.");
        return new(Guid.NewGuid(), name, modelPath, indexPath, modelAsset.Item1, indexAsset.Item1,
            modelAsset.Item2, indexAsset.Item2, DateTime.UtcNow, inspection);
    }

    public Task<IReadOnlyList<NeuralArchiveModel>> InspectArchiveAsync(string archivePath,
        CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<NeuralArchiveModel>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = OpenArchive(archivePath);
        ValidateArchive(archive);
        var models = archive.Entries.Where(entry => entry.FullName.EndsWith(".pth", StringComparison.OrdinalIgnoreCase)).ToArray();
        var indices = archive.Entries.Where(entry => entry.FullName.EndsWith(".index", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (models.Length == 0) throw new InvalidDataException("В ZIP нет файла модели .pth.");
        if (models.Length > MaximumModels) throw new InvalidDataException("В ZIP слишком много моделей.");
        return models.Select(model =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modelName = Path.GetFileNameWithoutExtension(model.Name);
            var modelFolder = EntryFolder(model.FullName);
            var sameFolder = indices.Where(index => EntryFolder(index.FullName).Equals(modelFolder, StringComparison.OrdinalIgnoreCase)).ToArray();
            // Never silently associate one of several unrelated indices.
            var named = sameFolder.Where(index => Path.GetFileNameWithoutExtension(index.Name).Equals(modelName, StringComparison.OrdinalIgnoreCase)).ToArray();
            var index = named.Length == 1 ? named[0] : sameFolder.Length == 1 ? sameFolder[0] : indices.Length == 1 ? indices[0] : null;
            return new NeuralArchiveModel(modelName[..Math.Min(64, modelName.Length)], model.FullName,
                index?.FullName, model.Length, index?.Length ?? 0);
        }).ToArray();
    }, cancellationToken);

    /// <summary>Extracts just a selected pair into a fresh child directory of an explicit destination.</summary>
    public async Task<(string ModelPath, string? IndexPath)> ExtractArchiveAsync(string archivePath,
        NeuralArchiveModel selection, string destinationRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = OpenArchive(archivePath);
        ValidateArchive(archive);
        var model = GetSelectedEntry(archive, selection.ModelEntry, ".pth");
        var index = selection.IndexEntry is null ? null : GetSelectedEntry(archive, selection.IndexEntry, ".index");
        if (model.Length + (index?.Length ?? 0) > MaximumAssetBytes)
            throw new InvalidDataException("Пара .pth и .index превышает 2 ГБ.");
        var root = Path.GetFullPath(destinationRoot);
        var directory = Path.Combine(root, "rvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var modelPath = Path.Combine(directory, "voice.pth");
        var indexPath = index is null ? null : Path.Combine(directory, "voice.index");
        try
        {
            await ExtractEntryAsync(model, modelPath, cancellationToken).ConfigureAwait(false);
            if (index is not null) await ExtractEntryAsync(index, indexPath!, cancellationToken).ConfigureAwait(false);
            return (modelPath, indexPath);
        }
        catch
        {
            TryDelete(modelPath);
            if (indexPath is not null) TryDelete(indexPath);
            try { Directory.Delete(directory, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public async Task<bool> VerifyAssetsAsync(NeuralVoiceModel model, CancellationToken cancellationToken = default)
    {
        ValidateModel(model);
        if (!model.IsAvailable) return false;
        var main = await HashAssetAsync(model.ModelPath, cancellationToken).ConfigureAwait(false);
        if (main.Item1 != model.ModelBytes || !main.Item2.Equals(model.ModelSha256, StringComparison.OrdinalIgnoreCase)) return false;
        if (model.IndexPath is null) return true;
        var index = await HashAssetAsync(model.IndexPath, cancellationToken).ConfigureAwait(false);
        return index.Item1 == model.IndexBytes && index.Item2.Equals(model.IndexSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static NeuralModelLibrary Validate(NeuralModelLibrary library)
    {
        if (library is null || library.Version != 1 || library.Models is null || library.Models.Count > MaximumModels)
            throw new InvalidDataException("Неподдерживаемая версия или размер библиотеки RVC.");
        var models = library.Models.Select(ValidateModel).ToArray();
        if (models.Select(model => model.Id).Distinct().Count() != models.Length)
            throw new InvalidDataException("Повторяющиеся идентификаторы моделей RVC.");
        return new(1, models);
    }

    private static NeuralVoiceModel ValidateModel(NeuralVoiceModel model)
    {
        if (model is null || model.Id == Guid.Empty) throw new InvalidDataException("Некорректная модель RVC.");
        var name = ValidateName(model.Name);
        var modelPath = ValidateAssetPath(model.ModelPath, ".pth");
        var indexPath = model.IndexPath is null ? null : ValidateAssetPath(model.IndexPath, ".index");
        if (model.ModelBytes is <= 0 or > MaximumAssetBytes || model.IndexBytes is < 0 or > MaximumAssetBytes ||
            model.ModelBytes + model.IndexBytes > MaximumAssetBytes || !ValidHash(model.ModelSha256) ||
            (indexPath is null ? model.IndexBytes != 0 || model.IndexSha256 is not null
                : model.IndexBytes == 0 || !ValidHash(model.IndexSha256)))
            throw new InvalidDataException("Некорректный размер или контрольная сумма модели RVC.");
        if (model.Inspection is not null) ValidateInspection(model.Inspection);
        return model with { Name = name, ModelPath = modelPath, IndexPath = indexPath };
    }

    public static void ValidateInspection(NeuralModelInspection inspection)
    {
        if (inspection.Version is not ("v1" or "v2") || inspection.SampleRate is not (32000 or 40000 or 48000) ||
            inspection.SpeakerCount is < 1 or > 1024 || inspection.FeatureDimension != (inspection.Version == "v1" ? 256 : 768))
            throw new InvalidDataException("Неподдерживаемая архитектура RVC. Нужна модель v1/v2 с частотой 32/40/48 кГц.");
    }

    private static string ValidateName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl))
            throw new InvalidDataException("Название модели: от 1 до 64 обычных символов.");
        return name;
    }

    private static string ValidateAssetPath(string path, string extension)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !Path.IsPathFullyQualified(path) ||
            !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Ожидается полный локальный путь к {extension}.");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full[2..].Contains(':'))
            throw new InvalidDataException("Сетевые пути и альтернативные потоки файлов не поддерживаются.");
        return full;
    }

    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private static async Task<(long, string)> HashAssetAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumAssetBytes) throw new InvalidDataException("Файл модели должен быть больше 0 байт и не больше 2 ГБ.");
        var size = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long countTotal = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            countTotal += count;
            if (countTotal > MaximumAssetBytes) throw new InvalidDataException("Файл модели вырос выше лимита 2 ГБ.");
            hash.AppendData(buffer, 0, count);
        }
        if (countTotal != size) throw new IOException("Размер модели изменился во время импорта. Повторите операцию.");
        return (size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static ZipArchive OpenArchive(string path)
    {
        if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Поддерживается ZIP. Для RAR/7z сначала распакуйте .pth и .index.");
        var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length > MaximumAssetBytes) throw new InvalidDataException("Архив превышает 2 ГБ.");
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch { stream.Dispose(); throw; }
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > 512) throw new InvalidDataException("В архиве слишком много файлов.");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length is < 1 or > 512 || name.StartsWith('/') || name.Contains(':') || name.Any(char.IsControl) ||
                name.Split('/').Any(part => part is ".." or ".") || !names.Add(name) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Архив содержит небезопасные или повторяющиеся пути.");
            if (entry.Length < 0 || entry.Length > MaximumAssetBytes || total > MaximumAssetBytes - entry.Length)
                throw new InvalidDataException("Распакованный архив превышает 2 ГБ.");
            total += entry.Length;
            if (entry.Length > 1024 * 1024 && (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > 200))
                throw new InvalidDataException("Подозрительно высокая степень сжатия архива.");
        }
    }

    private static ZipArchiveEntry GetSelectedEntry(ZipArchive archive, string name, string extension)
    {
        var matches = archive.Entries.Where(entry => entry.FullName.Equals(name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || !matches[0].FullName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || matches[0].Length == 0)
            throw new InvalidDataException("Выбранный файл отсутствует или изменился в архиве.");
        return matches[0];
    }

    private static string EntryFolder(string entry) => entry.Replace('\\', '/')[..(entry.Replace('\\', '/').LastIndexOf('/') + 1)];

    private static async Task ExtractEntryAsync(ZipArchiveEntry entry, string path, CancellationToken cancellationToken)
    {
        using var source = entry.Open();
        using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
            if (total > entry.Length || total > MaximumAssetBytes) throw new InvalidDataException("Распакованный файл превышает заявленный размер.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        if (total != entry.Length) throw new InvalidDataException("Архив повреждён: размер файла не совпадает.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

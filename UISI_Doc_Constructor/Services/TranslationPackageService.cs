using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using UISI_Doc_Constructor.Models;

namespace UISI_Doc_Constructor.Services;

public sealed class TranslationPackageService
{
    public const string PackageExtension = ".uisi-translation";
    public const string PackageSaveDialogFilter =
        "Перевод UISI (*.uisi-translation)|*.uisi-translation";
    public const string PackageOpenDialogFilter =
        "Пакеты перевода UISI (*.uisi-translation;*.zip)|*.uisi-translation;*.zip|Все файлы (*.*)|*.*";

    private const int CurrentFormatVersion = 2;
    private const long MaximumManifestSize = 1024 * 1024;
    private const long MaximumDocumentSize = 250L * 1024 * 1024;

    private readonly OpenXmlDocumentService _documentService;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public TranslationPackageService(
        OpenXmlDocumentService? documentService = null,
        string? sessionRoot = null)
    {
        _documentService = documentService ?? new OpenXmlDocumentService();
        SessionRoot = string.IsNullOrWhiteSpace(sessionRoot)
            ? Path.Combine(
                Path.GetTempPath(),
                "UISI Doc Constructor",
                "Translation Sessions")
            : Path.GetFullPath(sessionRoot);
        Directory.CreateDirectory(SessionRoot);
    }

    public string SessionRoot { get; }

    public void CreatePackage(
        DocumentTemplate template,
        string templatePath,
        IEnumerable<FieldValueItem> fieldValues,
        string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(template);

        var fields = fieldValues.ToList();
        var settings = ValidateTemplateConfiguration(template, fields);
        var valuesByTag = fields.ToDictionary(field => field.Tag, StringComparer.Ordinal);
        var sourceValues = settings
            .Where(item => item.Role is TranslationFieldRole.Source or TranslationFieldRole.Shared)
            .ToDictionary(
                item => item.Tag,
                item => valuesByTag[item.Tag].Value,
                StringComparer.Ordinal);
        foreach (var mirroredTarget in settings.Where(item =>
                     item.Role == TranslationFieldRole.Target &&
                     item.PairMode == TranslationPairMode.SameValue &&
                     item.SourceFieldTag is not null))
        {
            sourceValues[mirroredTarget.Tag] = valuesByTag[mirroredTarget.SourceFieldTag!].Value;
        }

        var emptySource = settings
            .Where(item => item.Role is TranslationFieldRole.Source or TranslationFieldRole.Shared)
            .Select(item => valuesByTag[item.Tag])
            .FirstOrDefault(item => string.IsNullOrWhiteSpace(item.Value));
        if (emptySource is not null)
        {
            throw new InvalidOperationException(
                $"Заполните украинское или общее поле «{emptySource.DisplayName}».");
        }

        var translationTargets = settings
            .Where(item =>
                item.Role == TranslationFieldRole.Target &&
                item.PairMode == TranslationPairMode.Translation)
            .ToList();
        if (translationTargets.Count == 0)
        {
            throw new InvalidOperationException(
                "В шаблоне нет английских полей, которые должен заполнить переводчик.");
        }

        var sessionDirectory = CreateSessionDirectory();
        var workingDocumentPath = Path.Combine(sessionDirectory, "working.docx");
        try
        {
            var replacements = _documentService.GenerateDocument(
                templatePath,
                workingDocumentPath,
                sourceValues,
                settings.Where(item => item.Role is TranslationFieldRole.Source or TranslationFieldRole.Target)
                    .Select(item => item.Tag).ToHashSet(StringComparer.Ordinal));
            if (replacements == 0)
            {
                throw new InvalidDataException(
                    "В шаблоне не найдено ни одного украинского или общего поля.");
            }

            var now = DateTime.UtcNow;
            var manifest = new TranslationPackageManifest
            {
                FormatVersion = CurrentFormatVersion,
                PackageId = Guid.NewGuid(),
                TemplateId = template.Id,
                TemplateName = template.Name,
                Revision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                WorkingDocumentSha256 = CalculateSha256(workingDocumentPath),
                Fields = fields.Select(field =>
                {
                    var fieldSettings = settings.Single(item =>
                        string.Equals(item.Tag, field.Tag, StringComparison.Ordinal));
                    return new TranslationPackageField
                    {
                        Tag = field.Tag,
                        DisplayName = field.DisplayName,
                        Role = fieldSettings.Role,
                        SourceFieldTag = fieldSettings.SourceFieldTag,
                        PairId = fieldSettings.PairId,
                        PairMode = fieldSettings.PairMode,
                        Value = fieldSettings.Role == TranslationFieldRole.Target
                            ? fieldSettings.PairMode == TranslationPairMode.SameValue &&
                              fieldSettings.SourceFieldTag is not null
                                ? valuesByTag[fieldSettings.SourceFieldTag].Value
                                : string.Empty
                            : field.Value
                    };
                }).ToList()
            };

            ValidateWorkingDocument(manifest, workingDocumentPath);
            WritePackage(manifest, workingDocumentPath, destinationPath);
        }
        finally
        {
            DeleteSessionDirectory(sessionDirectory);
        }
    }

    public TranslationPackageSession OpenPackage(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("Файл перевода не найден.", fullSourcePath);
        }

        TranslationPackageManifest manifest;
        var sessionDirectory = CreateSessionDirectory();
        var workingDocumentPath = Path.Combine(sessionDirectory, "working.docx");
        try
        {
            using var archive = ZipFile.OpenRead(fullSourcePath);
            var manifestEntry = FindSingleEntry(archive, "manifest.json");
            var documentEntry = FindSingleEntry(archive, "working.docx");
            ValidateEntrySizes(manifestEntry, documentEntry);

            using (var manifestStream = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<TranslationPackageManifest>(
                               manifestStream,
                               _jsonOptions)
                           ?? throw new InvalidDataException("Описание перевода повреждено.");
            }

            ValidateManifest(manifest);
            using (var documentStream = documentEntry.Open())
            using (var destinationStream = new FileStream(
                       workingDocumentPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                documentStream.CopyTo(destinationStream);
            }

            if (!string.Equals(
                    CalculateSha256(workingDocumentPath),
                    manifest.WorkingDocumentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Рабочий документ внутри файла перевода повреждён.");
            }

            // Validate the original checksum first; repair only the extracted working copy.
            _documentService.RepairTemplateFields(workingDocumentPath, createBackup: false);
            ValidateWorkingDocument(manifest, workingDocumentPath);
        }
        catch
        {
            DeleteSessionDirectory(sessionDirectory);
            throw;
        }

        return new TranslationPackageSession
        {
            SourcePath = fullSourcePath,
            WorkingDirectory = sessionDirectory,
            WorkingDocumentPath = workingDocumentPath,
            Manifest = manifest
        };
    }

    public IReadOnlyList<TranslationFieldValueItem> CreateTargetItems(
        TranslationPackageSession session)
    {
        // A source can be paired with multiple translations. All rows share one editable value.
        var sourcesByTag = session.Manifest.Fields
            .Where(field => field.Role == TranslationFieldRole.Source)
            .ToDictionary(field => field.Tag, field => new FieldValueItem
            {
                Tag = field.Tag,
                DisplayName = field.DisplayName,
                Value = field.Value
            }, StringComparer.Ordinal);

        return session.Manifest.Fields
            .Where(field =>
                field.Role == TranslationFieldRole.Target &&
                field.PairMode == TranslationPairMode.Translation)
            .Select(field =>
            {
                var source = field.SourceFieldTag is not null &&
                             sourcesByTag.TryGetValue(field.SourceFieldTag, out var pairedSource)
                    ? pairedSource
                    : null;
                return new TranslationFieldValueItem
                {
                    Tag = field.Tag,
                    DisplayName = field.DisplayName,
                    SourceDisplayName = source?.DisplayName ?? "Без связанного украинского поля",
                    Source = source,
                    CanEditSource = session.Manifest.FormatVersion >= 2 && source is not null,
                    Value = field.Value
                };
            })
            .ToList();
    }

    public void SavePackage(
        TranslationPackageSession session,
        IEnumerable<TranslationFieldValueItem> targetValues,
        string destinationPath)
    {
        var manifest = BuildUpdatedManifest(session, targetValues);
        manifest.Revision++;
        manifest.UpdatedAtUtc = DateTime.UtcNow;
        var temporaryDocument = Path.Combine(session.WorkingDirectory, $"{Guid.NewGuid():N}.docx");
        try
        {
            var workingPath = session.WorkingDocumentPath;
            if (manifest.FormatVersion >= 2)
            {
                ValidateWorkingDocument(manifest, workingPath);
                _documentService.GenerateDocument(workingPath, temporaryDocument,
                    GetSourceValues(manifest), GetWorkingFieldTags(manifest));
                workingPath = temporaryDocument;
            }

            manifest.WorkingDocumentSha256 = CalculateSha256(workingPath);
            WritePackage(manifest, workingPath, destinationPath);
            if (File.Exists(temporaryDocument))
            {
                File.Move(temporaryDocument, session.WorkingDocumentPath, overwrite: true);
            }
            session.Manifest = manifest;
        }
        finally
        {
            if (File.Exists(temporaryDocument))
            {
                File.Delete(temporaryDocument);
            }
        }
    }

    public int GenerateFinalDocument(
        TranslationPackageSession session,
        IEnumerable<TranslationFieldValueItem> targetValues,
        string destinationPath)
    {
        var manifest = BuildUpdatedManifest(session, targetValues);
        var values = manifest.Fields.Where(field =>
            field.Role == TranslationFieldRole.Target && field.PairMode == TranslationPairMode.Translation).ToList();
        var emptyField = values.FirstOrDefault(field => string.IsNullOrWhiteSpace(field.Value));
        if (emptyField is not null)
        {
            throw new InvalidOperationException(
                $"Заполните английское поле «{emptyField.DisplayName}».");
        }

        var valuesByTag = values.ToDictionary(field => field.Tag, field => field.Value, StringComparer.Ordinal);
        if (manifest.FormatVersion >= 2)
        {
            emptyField = manifest.Fields.FirstOrDefault(field =>
                field.Role == TranslationFieldRole.Source && string.IsNullOrWhiteSpace(field.Value));
            if (emptyField is not null)
            {
                throw new InvalidOperationException($"Заполните украинское поле «{emptyField.DisplayName}».");
            }
            foreach (var value in GetSourceValues(manifest))
            {
                valuesByTag[value.Key] = value.Value;
            }
        }

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (string.Equals(fullDestinationPath, Path.GetFullPath(session.WorkingDocumentPath), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullDestinationPath, Path.GetFullPath(session.SourcePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Сохраните итоговый документ в отдельный файл.");
        }
        var directory = Path.GetDirectoryName(fullDestinationPath)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.docx");
        try
        {
            ValidateWorkingDocument(manifest, session.WorkingDocumentPath);
            var replacements = _documentService.GenerateDocument(session.WorkingDocumentPath, temporaryPath, valuesByTag);
            if (replacements == 0 || _documentService.ReadFields(temporaryPath).Count > 0)
            {
                throw new InvalidDataException("В итоговом документе остались незаполненные поля. Проверьте пакет перевода.");
            }
            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
            session.Manifest = manifest;
            return replacements;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static IReadOnlyList<TranslationFieldSettings> ValidateTemplateConfiguration(
        DocumentTemplate template,
        IReadOnlyList<FieldValueItem> fields)
    {
        var fieldTags = fields.Select(field => field.Tag).ToHashSet(StringComparer.Ordinal);
        var settings = template.TranslationFields
            .Where(item => fieldTags.Contains(item.Tag))
            .GroupBy(item => item.Tag, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (settings.Count != fields.Count ||
            settings.Any(item => item.Role == TranslationFieldRole.Unassigned))
        {
            throw new InvalidOperationException(
                "Сначала назначьте языковую роль каждому полю шаблона.");
        }

        if (!settings.Any(item => item.Role == TranslationFieldRole.Source) ||
            !settings.Any(item => item.Role == TranslationFieldRole.Target))
        {
            throw new InvalidOperationException(
                "Для перевода нужны украинские и английские поля.");
        }

        var sourceTags = settings
            .Where(item => item.Role == TranslationFieldRole.Source)
            .Select(item => item.Tag)
            .ToHashSet(StringComparer.Ordinal);
        if (settings.Any(item =>
                item.Role == TranslationFieldRole.Target &&
                item.SourceFieldTag is not null &&
                !sourceTags.Contains(item.SourceFieldTag)))
        {
            throw new InvalidOperationException(
                "Одно из английских полей связано не с украинским оригиналом.");
        }

        if (settings.Any(item =>
                item.Role == TranslationFieldRole.Target &&
                item.PairMode == TranslationPairMode.SameValue &&
                item.SourceFieldTag is null))
        {
            throw new InvalidOperationException(
                "Для одинакового значения выберите родственное украинское поле.");
        }

        return settings;
    }

    private void WritePackage(
        TranslationPackageManifest manifest,
        string workingDocumentPath,
        string destinationPath)
    {
        var fullDestinationPath = EnsurePackageExtension(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("Не удалось определить папку сохранения.");
        if (!Directory.Exists(destinationDirectory))
        {
            throw new DirectoryNotFoundException("Выбранная папка сохранения не найдена.");
        }

        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fileStream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using (var manifestStream = manifestEntry.Open())
                {
                    JsonSerializer.Serialize(manifestStream, manifest, _jsonOptions);
                }

                var documentEntry = archive.CreateEntry("working.docx", CompressionLevel.Optimal);
                using var packageDocumentStream = documentEntry.Open();
                using var sourceDocumentStream = new FileStream(
                    workingDocumentPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                sourceDocumentStream.CopyTo(packageDocumentStream);
            }

            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void ValidateWorkingDocument(
        TranslationPackageManifest manifest,
        string workingDocumentPath)
    {
        var targetTags = GetWorkingFieldTags(manifest);
        var remainingFields = _documentService.ReadFields(workingDocumentPath);

        if (remainingFields.Count == 0 ||
            remainingFields.Any(field => !targetTags.Contains(field.Tag)) ||
            targetTags.Any(tag => remainingFields.All(field =>
                !string.Equals(field.Tag, tag, StringComparison.Ordinal))))
        {
            throw new InvalidDataException(
                "Рабочий документ не соответствует полям файла перевода.");
        }
    }

    private static void ValidateManifest(TranslationPackageManifest manifest)
    {
        if (manifest.FormatVersion is < 1 or > CurrentFormatVersion ||
            manifest.PackageId == Guid.Empty ||
            string.IsNullOrWhiteSpace(manifest.TemplateName) ||
            manifest.Fields.Count is 0 or > 5000 ||
            string.IsNullOrWhiteSpace(manifest.WorkingDocumentSha256))
        {
            throw new InvalidDataException(
                "Этот файл перевода повреждён или создан более новой версией программы.");
        }

        var uniqueTags = manifest.Fields
            .Select(field => field.Tag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (uniqueTags != manifest.Fields.Count ||
            manifest.Fields.Any(field =>
                field.Role == TranslationFieldRole.Unassigned ||
                string.IsNullOrWhiteSpace(field.DisplayName)) ||
            !manifest.Fields.Any(field =>
                field.Role == TranslationFieldRole.Target &&
                field.PairMode == TranslationPairMode.Translation))
        {
            throw new InvalidDataException("Описание полей перевода повреждено.");
        }

        var sourceTags = manifest.Fields
            .Where(field => field.Role == TranslationFieldRole.Source)
            .Select(field => field.Tag)
            .ToHashSet(StringComparer.Ordinal);
        if (manifest.Fields.Any(field =>
                field.Role == TranslationFieldRole.Target &&
                field.SourceFieldTag is not null &&
                !sourceTags.Contains(field.SourceFieldTag)))
        {
            throw new InvalidDataException("Связи между полями перевода повреждены.");
        }
    }

    private TranslationPackageManifest BuildUpdatedManifest(
        TranslationPackageSession session,
        IEnumerable<TranslationFieldValueItem> targetValues)
    {
        // Failed saves must not mutate the session or lose the previous revision.
        var manifest = JsonSerializer.Deserialize<TranslationPackageManifest>(
            JsonSerializer.Serialize(session.Manifest, _jsonOptions), _jsonOptions)!;
        var values = targetValues.ToList();
        var targets = manifest.Fields.Where(field =>
            field.Role == TranslationFieldRole.Target && field.PairMode == TranslationPairMode.Translation)
            .ToDictionary(field => field.Tag, StringComparer.Ordinal);
        if (values.Count != targets.Count || values.Select(value => value.Tag).Distinct(StringComparer.Ordinal).Count() != values.Count ||
            values.Any(value => !targets.ContainsKey(value.Tag)))
        {
            throw new InvalidDataException("Список английских полей не соответствует пакету перевода.");
        }
        var sources = manifest.Fields.Where(field => field.Role == TranslationFieldRole.Source)
            .ToDictionary(field => field.Tag, StringComparer.Ordinal);
        var corrections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var target = targets[value.Tag];
            target.Value = value.Value;
            if (target.SourceFieldTag is null)
            {
                continue;
            }
            if (value.Source is not null && !string.Equals(value.Source.Tag, target.SourceFieldTag, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Украинский текст связан с другим полем перевода.");
            }
            var source = sources[target.SourceFieldTag];
            if (manifest.FormatVersion < 2 && !string.Equals(source.Value, value.SourceValue, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("В старом пакете украинский текст недоступен для изменения. Создайте новый пакет из существующего шаблона.");
            }
            if (corrections.TryGetValue(source.Tag, out var previous) && !string.Equals(previous, value.SourceValue, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Для одного украинского поля указаны разные исправления.");
            }
            corrections[source.Tag] = value.SourceValue;
        }
        foreach (var correction in corrections)
        {
            sources[correction.Key].Value = correction.Value;
        }
        foreach (var mirroredTarget in manifest.Fields.Where(field =>
                     field.Role == TranslationFieldRole.Target && field.PairMode == TranslationPairMode.SameValue))
        {
            if (mirroredTarget.SourceFieldTag is null || !sources.TryGetValue(mirroredTarget.SourceFieldTag, out var source))
            {
                throw new InvalidDataException("Связь одинаковых значений повреждена.");
            }
            mirroredTarget.Value = source.Value;
        }
        return manifest;
    }

    private static HashSet<string> GetWorkingFieldTags(TranslationPackageManifest manifest) =>
        manifest.Fields.Where(field => manifest.FormatVersion >= 2
                ? field.Role is TranslationFieldRole.Source or TranslationFieldRole.Target
                : field.Role == TranslationFieldRole.Target && field.PairMode == TranslationPairMode.Translation)
            .Select(field => field.Tag).ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> GetSourceValues(TranslationPackageManifest manifest) =>
        manifest.Fields.Where(field => field.Role == TranslationFieldRole.Source ||
                field.Role == TranslationFieldRole.Target && field.PairMode == TranslationPairMode.SameValue)
            .ToDictionary(field => field.Tag, field => field.Value, StringComparer.Ordinal);

    private static void ValidateEntrySizes(
        ZipArchiveEntry manifestEntry,
        ZipArchiveEntry documentEntry)
    {
        if (manifestEntry.Length is <= 0 or > MaximumManifestSize)
        {
            throw new InvalidDataException("Описание файла перевода имеет недопустимый размер.");
        }

        if (documentEntry.Length is <= 0 or > MaximumDocumentSize)
        {
            throw new InvalidDataException("Документ внутри файла перевода имеет недопустимый размер.");
        }
    }

    private static ZipArchiveEntry FindSingleEntry(ZipArchive archive, string name)
    {
        var matches = archive.Entries
            .Where(entry => string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"В файле перевода отсутствует обязательная часть «{name}».");
    }

    private string CreateSessionDirectory()
    {
        var path = Path.Combine(SessionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteSessionDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string EnsurePackageExtension(string path) =>
        string.Equals(Path.GetExtension(path), PackageExtension, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path + PackageExtension);

    private static string CalculateSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

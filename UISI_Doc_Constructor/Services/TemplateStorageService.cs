using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UISI_Doc_Constructor.Models;

namespace UISI_Doc_Constructor.Services;

public sealed class TemplateStorageService
{
    public const string PackageExtension = ".uisi-template";

    private const int CurrentPackageVersion = 1;
    private const long MaximumManifestSize = 1024 * 1024;
    private const long MaximumDocumentSize = 250L * 1024 * 1024;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly List<DocumentTemplate> _templates;
    private bool _catalogNeedsRewrite;

    public TemplateStorageService(string? rootPath = null)
    {
        RootPath = string.IsNullOrWhiteSpace(rootPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UISI Doc Constructor")
            : Path.GetFullPath(rootPath);
        TemplatesPath = Path.Combine(RootPath, "Templates");
        DeletedTemplatesPath = Path.Combine(RootPath, "Deleted Templates");
        CatalogPath = Path.Combine(RootPath, "templates.json");

        Directory.CreateDirectory(TemplatesPath);

        _templates = LoadCatalog();
        if (DiscoverUnregisteredTemplates() || _catalogNeedsRewrite)
        {
            SaveCatalog();
        }
    }

    public string RootPath { get; }

    public string TemplatesPath { get; }

    public string DeletedTemplatesPath { get; }

    public string CatalogPath { get; }

    public IReadOnlyList<DocumentTemplate> GetTemplates() =>
        _templates
            .OrderBy(template => template.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public DocumentTemplate Import(
        string sourcePath,
        string templateName,
        bool? hasTranslation = null)
    {
        using var package = IsTemplatePackage(sourcePath)
            ? ReadPackage(sourcePath)
            : null;

        if (package is null)
        {
            ValidateSourceDocument(sourcePath);
        }

        var normalizedName = NormalizeTemplateName(templateName);
        if (_templates.Any(template => string.Equals(
                template.Name,
                normalizedName,
                StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Шаблон с названием «{normalizedName}» уже существует. Укажите другое название.");
        }

        var template = new DocumentTemplate
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            FileName = $"{Guid.NewGuid():N}.docx",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            HasTranslation = hasTranslation ??
                             (package is not null &&
                              (package.Manifest.HasTranslation ||
                               package.Manifest.TranslationFields.Count > 0)),
            FieldOrder = package?.Manifest.FieldOrder
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.Ordinal)
                .Take(5000)
                .ToList()
                ?? [],
            TranslationFields = NormalizeTranslationFields(
                package?.Manifest.TranslationFields ?? [],
                package?.Manifest.FieldOrder ?? [])
        };

        var destinationPath = GetTemplatePath(template);
        try
        {
            if (package is null)
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
            }
            else
            {
                using var packageStream = package.DocumentEntry.Open();
                using var destinationStream = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                packageStream.CopyTo(destinationStream);
            }

            new OpenXmlDocumentService().RepairTemplateFields(destinationPath, createBackup: false);
            ValidateDocxContents(destinationPath);
            _templates.Add(template);
            SaveCatalog();
            return template;
        }
        catch
        {
            _templates.Remove(template);
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            throw;
        }
    }

    public string GetSuggestedTemplateName(string sourcePath)
    {
        if (!IsTemplatePackage(sourcePath))
        {
            ValidateSourceDocument(sourcePath);
            return Path.GetFileNameWithoutExtension(sourcePath);
        }

        using var package = ReadPackage(sourcePath);
        return NormalizeTemplateName(package.Manifest.Name);
    }

    public bool GetSuggestedTranslationMode(string sourcePath)
    {
        if (!IsTemplatePackage(sourcePath))
        {
            ValidateSourceDocument(sourcePath);
            return false;
        }

        using var package = ReadPackage(sourcePath);
        return package.Manifest.HasTranslation ||
               package.Manifest.TranslationFields.Count > 0;
    }

    public void Export(Guid templateId, string destinationPath)
    {
        var template = _templates.FirstOrDefault(item => item.Id == templateId)
            ?? throw new InvalidOperationException("Выбранный шаблон отсутствует в каталоге.");
        var sourcePath = GetTemplatePath(template);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Файл выбранного шаблона не найден.", sourcePath);
        }

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (!string.Equals(
                Path.GetExtension(fullDestinationPath),
                PackageExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            fullDestinationPath += PackageExtension;
        }

        var destinationDirectory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("Не удалось определить папку экспорта.");
        if (!Directory.Exists(destinationDirectory))
        {
            throw new DirectoryNotFoundException("Выбранная папка экспорта не найдена.");
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
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var manifest = new TemplatePackageManifest
                {
                    FormatVersion = CurrentPackageVersion,
                    Name = template.Name,
                    HasTranslation = template.HasTranslation,
                    FieldOrder = template.FieldOrder.ToList(),
                    TranslationFields = template.TranslationFields
                        .Select(CloneTranslationField)
                        .ToList()
                };

                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using (var manifestStream = manifestEntry.Open())
                {
                    JsonSerializer.Serialize(manifestStream, manifest, _jsonOptions);
                }

                var documentEntry = archive.CreateEntry("template.docx", CompressionLevel.Optimal);
                using var documentStream = documentEntry.Open();
                using var sourceStream = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                sourceStream.CopyTo(documentStream);
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

    public DocumentTemplate Rename(Guid templateId, string templateName)
    {
        var template = _templates.FirstOrDefault(item => item.Id == templateId)
            ?? throw new InvalidOperationException("Выбранный шаблон отсутствует в каталоге.");
        var normalizedName = NormalizeTemplateName(templateName);

        if (_templates.Any(item =>
                item.Id != templateId &&
                string.Equals(
                    item.Name,
                    normalizedName,
                    StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Шаблон с названием «{normalizedName}» уже существует. Укажите другое название.");
        }

        if (string.Equals(template.Name, normalizedName, StringComparison.CurrentCulture))
        {
            return template;
        }

        template.Name = normalizedName;
        template.UpdatedAtUtc = DateTime.UtcNow;
        SaveCatalog();
        return template;
    }

    public DocumentTemplate Delete(Guid templateId)
    {
        var template = _templates.FirstOrDefault(item => item.Id == templateId)
            ?? throw new InvalidOperationException("Выбранный шаблон отсутствует в каталоге.");
        var sourcePath = GetTemplatePath(template);
        string? deletedPath = null;

        if (File.Exists(sourcePath))
        {
            Directory.CreateDirectory(DeletedTemplatesPath);
            deletedPath = Path.Combine(
                DeletedTemplatesPath,
                CreateDeletedTemplateFileName(template));
            File.Move(sourcePath, deletedPath, overwrite: false);
        }

        _templates.Remove(template);

        try
        {
            SaveCatalog();
            return template;
        }
        catch
        {
            _templates.Add(template);

            if (deletedPath is not null && File.Exists(deletedPath) && !File.Exists(sourcePath))
            {
                File.Move(deletedPath, sourcePath, overwrite: false);
            }

            throw;
        }
    }

    public string GetTemplatePath(DocumentTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var fileName = Path.GetFileName(template.FileName);
        if (!string.Equals(fileName, template.FileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("В каталоге шаблонов найдено недопустимое имя файла.");
        }

        return Path.Combine(TemplatesPath, fileName);
    }

    public void SaveFieldOrder(Guid templateId, IEnumerable<string> fieldTags)
    {
        var template = _templates.FirstOrDefault(item => item.Id == templateId)
            ?? throw new InvalidOperationException("Текущий шаблон отсутствует в каталоге.");

        var normalizedOrder = fieldTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var normalizedTranslationFields = NormalizeTranslationFields(
            template.TranslationFields,
            normalizedOrder);
        var fieldOrderChanged = !template.FieldOrder.SequenceEqual(
            normalizedOrder,
            StringComparer.Ordinal);
        var translationFieldsChanged = !TranslationFieldsEqual(
            template.TranslationFields,
            normalizedTranslationFields);

        if (!fieldOrderChanged && !translationFieldsChanged)
        {
            return;
        }

        template.FieldOrder = normalizedOrder;
        template.TranslationFields = normalizedTranslationFields;
        template.UpdatedAtUtc = DateTime.UtcNow;
        SaveCatalog();
    }

    public void SaveTranslationConfiguration(
        Guid templateId,
        IEnumerable<TranslationFieldSettings> settings)
    {
        var template = _templates.FirstOrDefault(item => item.Id == templateId)
            ?? throw new InvalidOperationException("Текущий шаблон отсутствует в каталоге.");
        var normalized = NormalizeTranslationFields(settings, template.FieldOrder);

        if (normalized.Count != template.FieldOrder.Count ||
            normalized.Any(item => item.Role == TranslationFieldRole.Unassigned))
        {
            throw new InvalidOperationException(
                "Назначьте языковую роль каждому полю текущего шаблона.");
        }

        if (!normalized.Any(item => item.Role == TranslationFieldRole.Source) ||
            !normalized.Any(item => item.Role == TranslationFieldRole.Target))
        {
            throw new InvalidOperationException(
                "Для перевода нужны хотя бы одно украинское и одно английское поле.");
        }

        var sourceTags = normalized
            .Where(item => item.Role == TranslationFieldRole.Source)
            .Select(item => item.Tag)
            .ToHashSet(StringComparer.Ordinal);
        if (normalized.Any(item =>
                item.Role == TranslationFieldRole.Target &&
                item.SourceFieldTag is not null &&
                !sourceTags.Contains(item.SourceFieldTag)))
        {
            throw new InvalidOperationException(
                "Английское поле можно связать только с полем украинского оригинала.");
        }

        var repeatedSource = normalized
            .Where(item =>
                item.Role == TranslationFieldRole.Target &&
                item.SourceFieldTag is not null)
            .GroupBy(item => item.SourceFieldTag!, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (repeatedSource is not null)
        {
            throw new InvalidOperationException(
                "Одно украинское поле можно связать только с одним английским полем.");
        }

        if (normalized.Any(item =>
                item.Role == TranslationFieldRole.Target &&
                item.PairMode == TranslationPairMode.SameValue &&
                item.SourceFieldTag is null))
        {
            throw new InvalidOperationException(
                "Для одинакового значения выберите родственное украинское поле.");
        }

        template.TranslationFields = normalized;
        template.HasTranslation = true;
        template.UpdatedAtUtc = DateTime.UtcNow;
        SaveCatalog();
    }

    public void SaveTranslationPair(
        Guid templateId,
        string sourceTag,
        string targetTag,
        TranslationPairMode pairMode = TranslationPairMode.Translation)
    {
        var template = _templates.FirstOrDefault(item => item.Id == templateId)
            ?? throw new InvalidOperationException("Текущий шаблон отсутствует в каталоге.");
        if (!template.HasTranslation)
        {
            throw new InvalidOperationException("Для этого шаблона не включён режим перевода.");
        }

        var fieldTags = template.FieldOrder.ToHashSet(StringComparer.Ordinal);
        if (string.Equals(sourceTag, targetTag, StringComparison.Ordinal) ||
            !fieldTags.Contains(sourceTag) ||
            !fieldTags.Contains(targetTag))
        {
            throw new InvalidOperationException("Не удалось связать поля: одно из них отсутствует в шаблоне.");
        }

        var settings = template.TranslationFields
            .Where(item =>
                !string.Equals(item.Tag, sourceTag, StringComparison.Ordinal) &&
                !string.Equals(item.Tag, targetTag, StringComparison.Ordinal))
            .Select(CloneTranslationField)
            .ToList();
        foreach (var item in settings.Where(item =>
                     string.Equals(item.SourceFieldTag, sourceTag, StringComparison.Ordinal) ||
                     string.Equals(item.SourceFieldTag, targetTag, StringComparison.Ordinal)))
        {
            item.SourceFieldTag = null;
            item.PairId = null;
            item.PairMode = TranslationPairMode.Translation;
        }

        var pairId = Guid.NewGuid();

        settings.Add(new TranslationFieldSettings
        {
            Tag = sourceTag,
            Role = TranslationFieldRole.Source,
            PairId = pairId,
            PairMode = pairMode
        });
        settings.Add(new TranslationFieldSettings
        {
            Tag = targetTag,
            Role = TranslationFieldRole.Target,
            SourceFieldTag = sourceTag,
            PairId = pairId,
            PairMode = pairMode
        });

        template.TranslationFields = NormalizeTranslationFields(settings, template.FieldOrder);
        template.UpdatedAtUtc = DateTime.UtcNow;
        SaveCatalog();
    }

    public void SetTranslationMode(Guid templateId, bool isEnabled)
    {
        var template = _templates.FirstOrDefault(item => item.Id == templateId)
            ?? throw new InvalidOperationException("Текущий шаблон отсутствует в каталоге.");
        if (template.HasTranslation == isEnabled)
        {
            return;
        }

        template.HasTranslation = isEnabled;
        template.UpdatedAtUtc = DateTime.UtcNow;
        SaveCatalog();
    }

    public int MigrateLegacyTranslationPairs(
        Guid templateId,
        IEnumerable<TemplateField> fields)
    {
        var template = _templates.FirstOrDefault(item => item.Id == templateId)
            ?? throw new InvalidOperationException("Текущий шаблон отсутствует в каталоге.");
        if (!template.HasTranslation)
        {
            return 0;
        }

        var fieldList = fields.ToList();
        var fieldsByName = fieldList
            .GroupBy(field => field.DisplayName.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.CurrentCultureIgnoreCase);
        var settingsByTag = template.TranslationFields
            .Select(CloneTranslationField)
            .ToDictionary(item => item.Tag, StringComparer.Ordinal);
        foreach (var field in fieldList)
        {
            settingsByTag.TryAdd(field.Tag, new TranslationFieldSettings
            {
                Tag = field.Tag,
                Role = TranslationFieldRole.Unassigned
            });
        }

        var migratedCount = 0;
        foreach (var targetField in fieldList)
        {
            if (!TryGetOriginalFieldName(targetField.DisplayName, out var originalName) ||
                !fieldsByName.TryGetValue(originalName, out var sourceField) ||
                string.Equals(sourceField.Tag, targetField.Tag, StringComparison.Ordinal))
            {
                continue;
            }

            var source = settingsByTag[sourceField.Tag];
            var target = settingsByTag[targetField.Tag];
            if (target.SourceFieldTag is not null)
            {
                continue;
            }

            var isSameValue = source.Role == TranslationFieldRole.Shared &&
                              target.Role == TranslationFieldRole.Shared;
            var isUnassigned = source.Role == TranslationFieldRole.Unassigned &&
                               target.Role == TranslationFieldRole.Unassigned;
            var isUnlinkedTranslation = source.Role == TranslationFieldRole.Source &&
                                        target.Role == TranslationFieldRole.Target;
            if (!isSameValue && !isUnassigned && !isUnlinkedTranslation)
            {
                continue;
            }

            var pairId = CreateStablePairId(source.Tag, target.Tag);
            var pairMode = isSameValue
                ? TranslationPairMode.SameValue
                : TranslationPairMode.Translation;
            source.Role = TranslationFieldRole.Source;
            source.PairId = pairId;
            source.PairMode = pairMode;
            target.Role = TranslationFieldRole.Target;
            target.SourceFieldTag = source.Tag;
            target.PairId = pairId;
            target.PairMode = pairMode;
            migratedCount++;
        }

        if (migratedCount == 0)
        {
            return 0;
        }

        template.TranslationFields = NormalizeTranslationFields(
            settingsByTag.Values,
            template.FieldOrder);
        template.UpdatedAtUtc = DateTime.UtcNow;
        SaveCatalog();
        return migratedCount;
    }

    private List<DocumentTemplate> LoadCatalog()
    {
        if (!File.Exists(CatalogPath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(CatalogPath, Encoding.UTF8);
            var templates = JsonSerializer.Deserialize<List<DocumentTemplate>>(
                                json,
                                _jsonOptions)
                            ?? [];

            foreach (var template in templates)
            {
                template.FieldOrder ??= [];
                template.TranslationFields = NormalizeTranslationFields(
                    template.TranslationFields ?? [],
                    template.FieldOrder);
            }

            var validTemplates = templates
                .Where(template =>
                    template.Id != Guid.Empty &&
                    !string.IsNullOrWhiteSpace(template.Name) &&
                    !string.IsNullOrWhiteSpace(template.FileName) &&
                    !IsTemporaryWordFile(template.FileName) &&
                    File.Exists(Path.Combine(TemplatesPath, Path.GetFileName(template.FileName))))
                .GroupBy(template => template.Id)
                .Select(group => group.First())
                .ToList();

            _catalogNeedsRewrite = validTemplates.Count != templates.Count;
            return validTemplates;
        }
        catch (JsonException)
        {
            PreserveBrokenCatalog();
            return [];
        }
        catch (IOException)
        {
            PreserveBrokenCatalog();
            return [];
        }
    }

    private bool DiscoverUnregisteredTemplates()
    {
        var registeredFileNames = _templates
            .Select(template => template.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var path in Directory.EnumerateFiles(TemplatesPath, "*.docx"))
        {
            var fileName = Path.GetFileName(path);
            if (IsTemporaryWordFile(fileName) || registeredFileNames.Contains(fileName))
            {
                continue;
            }

            var templateName = CreateUniqueImportedName(
                RemoveLegacyUniqueSuffix(Path.GetFileNameWithoutExtension(path)));
            var createdAt = File.GetCreationTimeUtc(path);
            var updatedAt = File.GetLastWriteTimeUtc(path);

            _templates.Add(new DocumentTemplate
            {
                Id = Guid.NewGuid(),
                Name = templateName,
                FileName = fileName,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = updatedAt
            });

            registeredFileNames.Add(fileName);
            changed = true;
        }

        return changed;
    }

    private string CreateUniqueImportedName(string proposedName)
    {
        var baseName = string.IsNullOrWhiteSpace(proposedName) ? "Импортированный шаблон" : proposedName;
        var candidate = baseName;
        var suffix = 2;

        while (_templates.Any(template => string.Equals(
                   template.Name,
                   candidate,
                   StringComparison.CurrentCultureIgnoreCase)))
        {
            candidate = $"{baseName} ({suffix})";
            suffix++;
        }

        return candidate;
    }

    private static string CreateDeletedTemplateFileName(DocumentTemplate template)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var safeName = new string(template.Name
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');

        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "template";
        }

        if (safeName.Length > 60)
        {
            safeName = safeName[..60].TrimEnd();
        }

        return $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{safeName}-{template.Id:N}.docx";
    }

    private static string RemoveLegacyUniqueSuffix(string fileNameWithoutExtension)
    {
        var separatorIndex = fileNameWithoutExtension.LastIndexOf('_');
        if (separatorIndex < 0 || separatorIndex == fileNameWithoutExtension.Length - 1)
        {
            return fileNameWithoutExtension;
        }

        var possibleSuffix = fileNameWithoutExtension[(separatorIndex + 1)..];
        return possibleSuffix.Length == 8 &&
               possibleSuffix.All(Uri.IsHexDigit)
            ? fileNameWithoutExtension[..separatorIndex]
            : fileNameWithoutExtension;
    }

    private void SaveCatalog()
    {
        var temporaryPath = $"{CatalogPath}.tmp";
        var json = JsonSerializer.Serialize(_templates, _jsonOptions);

        File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, CatalogPath, overwrite: true);
    }

    private void PreserveBrokenCatalog()
    {
        if (!File.Exists(CatalogPath))
        {
            return;
        }

        var backupPath = Path.Combine(
            RootPath,
            $"templates.broken-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

        try
        {
            File.Move(CatalogPath, backupPath, overwrite: false);
        }
        catch (IOException)
        {
            // Рабочие DOCX остаются на месте и будут обнаружены автоматически.
        }
    }

    private static void ValidateSourceDocument(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Выбранный документ не найден.", sourcePath);
        }

        if (!string.Equals(Path.GetExtension(sourcePath), ".docx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "На первом этапе поддерживаются только документы DOCX.");
        }

        if (IsTemporaryWordFile(Path.GetFileName(sourcePath)))
        {
            throw new InvalidOperationException(
                "Это служебный файл Microsoft Word. Закройте документ в Word и выберите исходный DOCX без префикса «~$».");
        }

        ValidateDocxContents(sourcePath);
    }

    private static void ValidateDocxContents(string documentPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(documentPath);
            var hasContentTypes = archive.Entries.Any(entry => string.Equals(
                entry.FullName,
                "[Content_Types].xml",
                StringComparison.OrdinalIgnoreCase));
            var hasMainDocument = archive.Entries.Any(entry => string.Equals(
                entry.FullName,
                "word/document.xml",
                StringComparison.OrdinalIgnoreCase));

            if (!hasContentTypes || !hasMainDocument)
            {
                throw new InvalidDataException(
                    "Выбранный файл не содержит корректный документ Word DOCX.");
            }
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                "Выбранный документ DOCX повреждён или имеет неподдерживаемый формат.",
                exception);
        }
    }

    private static bool IsTemplatePackage(string sourcePath) =>
        string.Equals(
            Path.GetExtension(sourcePath),
            PackageExtension,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsTemporaryWordFile(string fileName) =>
        fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase);

    private PackageArchive ReadPackage(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Выбранный файл шаблона не найден.", sourcePath);
        }

        ZipArchive? archive = null;
        try
        {
            archive = ZipFile.OpenRead(sourcePath);
            var manifestEntry = FindSinglePackageEntry(archive, "manifest.json");
            var documentEntry = FindSinglePackageEntry(archive, "template.docx");

            if (manifestEntry.Length is <= 0 or > MaximumManifestSize)
            {
                throw new InvalidDataException("Файл описания шаблона имеет недопустимый размер.");
            }

            if (documentEntry.Length is <= 0 or > MaximumDocumentSize)
            {
                throw new InvalidDataException("Документ в шаблоне имеет недопустимый размер.");
            }

            TemplatePackageManifest? manifest;
            using (var manifestStream = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<TemplatePackageManifest>(
                    manifestStream,
                    _jsonOptions);
            }

            if (manifest is null ||
                manifest.FormatVersion != CurrentPackageVersion ||
                string.IsNullOrWhiteSpace(manifest.Name))
            {
                throw new InvalidDataException(
                    "Этот файл не является поддерживаемым шаблоном UISI или создан более новой версией программы.");
            }

            manifest.FieldOrder ??= [];
            manifest.TranslationFields = NormalizeTranslationFields(
                manifest.TranslationFields ?? [],
                manifest.FieldOrder);
            return new PackageArchive(archive, manifest, documentEntry);
        }
        catch (InvalidDataException)
        {
            archive?.Dispose();
            throw;
        }
        catch (JsonException exception)
        {
            archive?.Dispose();
            throw new InvalidDataException("Описание шаблона UISI повреждено.", exception);
        }
        catch
        {
            archive?.Dispose();
            throw;
        }
    }

    private static ZipArchiveEntry FindSinglePackageEntry(ZipArchive archive, string name)
    {
        var matches = archive.Entries
            .Where(entry => string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"В файле шаблона отсутствует обязательная часть «{name}».");
    }

    private static string NormalizeTemplateName(string templateName)
    {
        var normalizedName = templateName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new InvalidOperationException("Введите название шаблона.");
        }

        return normalizedName.Length <= 100
            ? normalizedName
            : normalizedName[..100];
    }

    private static List<TranslationFieldSettings> NormalizeTranslationFields(
        IEnumerable<TranslationFieldSettings> settings,
        IEnumerable<string> fieldOrder)
    {
        var orderedTags = fieldOrder
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .Take(5000)
            .ToList();
        var allowedTags = orderedTags.ToHashSet(StringComparer.Ordinal);
        var settingsByTag = settings
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Tag) &&
                allowedTags.Contains(item.Tag))
            .GroupBy(item => item.Tag, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var normalized = new List<TranslationFieldSettings>();
        foreach (var tag in orderedTags)
        {
            if (!settingsByTag.TryGetValue(tag, out var item))
            {
                continue;
            }

            var role = Enum.IsDefined(item.Role)
                ? item.Role
                : TranslationFieldRole.Unassigned;
            var sourceFieldTag = role == TranslationFieldRole.Target &&
                                 item.SourceFieldTag is not null &&
                                 allowedTags.Contains(item.SourceFieldTag) &&
                                 !string.Equals(item.SourceFieldTag, tag, StringComparison.Ordinal)
                ? item.SourceFieldTag
                : null;
            var pairId = item.PairId is { } candidatePairId &&
                         candidatePairId != Guid.Empty
                ? (Guid?)candidatePairId
                : null;
            var pairMode = Enum.IsDefined(item.PairMode)
                ? item.PairMode
                : TranslationPairMode.Translation;

            normalized.Add(new TranslationFieldSettings
            {
                Tag = tag,
                Role = role,
                SourceFieldTag = sourceFieldTag,
                PairId = role == TranslationFieldRole.Target ? pairId : null,
                PairMode = role == TranslationFieldRole.Target
                    ? pairMode
                    : TranslationPairMode.Translation
            });
        }

        var normalizedByTag = normalized.ToDictionary(item => item.Tag, StringComparer.Ordinal);
        foreach (var target in normalized.Where(item =>
                     item.Role == TranslationFieldRole.Target &&
                     item.SourceFieldTag is not null))
        {
            if (!normalizedByTag.TryGetValue(target.SourceFieldTag!, out var source) ||
                source.Role != TranslationFieldRole.Source)
            {
                continue;
            }

            var pairId = target.PairId ??
                         source.PairId ??
                         CreateStablePairId(source.Tag, target.Tag);
            target.PairId = pairId;
            source.PairId = pairId;
            source.PairMode = target.PairMode;
        }

        return normalized;
    }

    private static TranslationFieldSettings CloneTranslationField(
        TranslationFieldSettings settings) =>
        new()
        {
            Tag = settings.Tag,
            Role = settings.Role,
            SourceFieldTag = settings.SourceFieldTag,
            PairId = settings.PairId,
            PairMode = settings.PairMode
        };

    private static bool TranslationFieldsEqual(
        IReadOnlyList<TranslationFieldSettings> left,
        IReadOnlyList<TranslationFieldSettings> right) =>
        left.Count == right.Count && left
            .Zip(right)
            .All(pair =>
                string.Equals(pair.First.Tag, pair.Second.Tag, StringComparison.Ordinal) &&
                pair.First.Role == pair.Second.Role &&
                pair.First.PairId == pair.Second.PairId &&
                pair.First.PairMode == pair.Second.PairMode &&
                string.Equals(
                    pair.First.SourceFieldTag,
                    pair.Second.SourceFieldTag,
                    StringComparison.Ordinal));

    private static Guid CreateStablePairId(string sourceTag, string targetTag)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceTag}\n{targetTag}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static bool TryGetOriginalFieldName(string targetName, out string originalName)
    {
        var normalized = targetName.Trim();
        string[] suffixes = [" — EN", " – EN", " - EN", " (EN)"];
        foreach (var suffix in suffixes)
        {
            if (!normalized.EndsWith(suffix, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            originalName = normalized[..^suffix.Length].Trim();
            return originalName.Length > 0;
        }

        originalName = string.Empty;
        return false;
    }

    private sealed class TemplatePackageManifest
    {
        public int FormatVersion { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool HasTranslation { get; set; }

        public List<string> FieldOrder { get; set; } = [];

        public List<TranslationFieldSettings> TranslationFields { get; set; } = [];
    }

    private sealed class PackageArchive(
        ZipArchive archive,
        TemplatePackageManifest manifest,
        ZipArchiveEntry documentEntry) : IDisposable
    {
        public TemplatePackageManifest Manifest { get; } = manifest;

        public ZipArchiveEntry DocumentEntry { get; } = documentEntry;

        public void Dispose() => archive.Dispose();
    }
}

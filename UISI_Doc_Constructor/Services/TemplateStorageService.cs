using System.IO;
using System.IO.Compression;
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
        if (DiscoverUnregisteredTemplates())
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

    public DocumentTemplate Import(string sourcePath, string templateName)
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
            FieldOrder = package?.Manifest.FieldOrder
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.Ordinal)
                .Take(5000)
                .ToList()
                ?? []
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
                    FieldOrder = template.FieldOrder.ToList()
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

        if (template.FieldOrder.SequenceEqual(normalizedOrder, StringComparer.Ordinal))
        {
            return;
        }

        template.FieldOrder = normalizedOrder;
        template.UpdatedAtUtc = DateTime.UtcNow;
        SaveCatalog();
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

            return templates
                .Where(template =>
                    template.Id != Guid.Empty &&
                    !string.IsNullOrWhiteSpace(template.Name) &&
                    !string.IsNullOrWhiteSpace(template.FileName) &&
                    File.Exists(Path.Combine(TemplatesPath, Path.GetFileName(template.FileName))))
                .GroupBy(template => template.Id)
                .Select(group => group.First())
                .ToList();
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
            if (registeredFileNames.Contains(fileName))
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

    private sealed class TemplatePackageManifest
    {
        public int FormatVersion { get; set; }

        public string Name { get; set; } = string.Empty;

        public List<string> FieldOrder { get; set; } = [];
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

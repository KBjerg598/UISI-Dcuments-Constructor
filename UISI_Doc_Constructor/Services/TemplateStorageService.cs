using System.IO;
using System.Text;
using System.Text.Json;
using UISI_Doc_Constructor.Models;

namespace UISI_Doc_Constructor.Services;

public sealed class TemplateStorageService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
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

    public string CatalogPath { get; }

    public IReadOnlyList<DocumentTemplate> GetTemplates() =>
        _templates
            .OrderBy(template => template.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public DocumentTemplate Import(string sourcePath, string templateName)
    {
        ValidateSourceDocument(sourcePath);

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
            UpdatedAtUtc = DateTime.UtcNow
        };

        var destinationPath = GetTemplatePath(template);
        File.Copy(sourcePath, destinationPath, overwrite: false);

        try
        {
            _templates.Add(template);
            SaveCatalog();
            return template;
        }
        catch
        {
            _templates.Remove(template);
            File.Delete(destinationPath);
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
}

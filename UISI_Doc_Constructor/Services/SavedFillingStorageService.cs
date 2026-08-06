using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UISI_Doc_Constructor.Models;

namespace UISI_Doc_Constructor.Services;

public sealed class SavedFillingStorageService
{
    private const int CurrentFormatVersion = 1;
    private const string FileSuffix = "сохранённые_данные";

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public SavedFillingStorageService(string? rootPath = null)
    {
        RootPath = string.IsNullOrWhiteSpace(rootPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UISI Doc Constructor",
                "Saved Fillings")
            : Path.GetFullPath(rootPath);

        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public IReadOnlyList<SavedFilling> GetAll()
    {
        var fillings = new List<SavedFilling>();
        foreach (var path in Directory.EnumerateFiles(RootPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var filling = JsonSerializer.Deserialize<SavedFilling>(json, _jsonOptions);
                if (!IsValid(filling))
                {
                    continue;
                }

                filling!.StoragePath = Path.GetFullPath(path);
                filling.ManualMappings ??= [];
                fillings.Add(filling);
            }
            catch (JsonException)
            {
                // A damaged entry is ignored without blocking the rest of the local library.
            }
            catch (IOException)
            {
                // A temporarily unavailable entry is ignored for this refresh.
            }
            catch (UnauthorizedAccessException)
            {
                // The library remains usable even if one file has restricted permissions.
            }
        }

        return fillings
            .GroupBy(filling => filling.Id)
            .Select(group => group.OrderByDescending(item => item.UpdatedAtUtc).First())
            .OrderByDescending(filling => filling.LastUsedAtUtc)
            .ThenBy(filling => filling.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public SavedFillingSaveResult SaveAfterDocumentCreated(
        DocumentTemplate template,
        IEnumerable<FieldValueItem> fields,
        string documentPath)
    {
        ArgumentNullException.ThrowIfNull(template);

        var savedFields = fields
            .Select(field => new SavedFillingField
            {
                Tag = field.Tag,
                DisplayName = field.DisplayName.Trim(),
                Value = field.Value
            })
            .ToList();
        if (savedFields.Count == 0)
        {
            throw new InvalidOperationException("Нельзя сохранить заполнение без полей.");
        }

        var now = DateTime.UtcNow;
        var contentHash = CalculateContentHash(template.Id, savedFields);
        var duplicate = GetAll().FirstOrDefault(filling =>
            filling.TemplateId == template.Id &&
            string.Equals(filling.ContentHash, contentHash, StringComparison.Ordinal));

        if (duplicate is not null)
        {
            duplicate.LastUsedAtUtc = now;
            duplicate.UpdatedAtUtc = now;
            duplicate.LastDocumentName = Path.GetFileName(documentPath);
            duplicate.TemplateName = template.Name;
            Write(duplicate, duplicate.StoragePath);
            return new SavedFillingSaveResult(duplicate, WasCreated: false);
        }

        var localNow = now.ToLocalTime();
        var filling = new SavedFilling
        {
            FormatVersion = CurrentFormatVersion,
            Id = Guid.NewGuid(),
            TemplateId = template.Id,
            TemplateName = template.Name,
            Name = $"{template.Name} — {localNow:dd.MM.yyyy HH:mm}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastUsedAtUtc = now,
            LastDocumentName = Path.GetFileName(documentPath),
            ContentHash = contentHash,
            Fields = savedFields
        };

        var fileName = $"{CreateSafeFileName(template.Name)}_{localNow:yyyy-MM-dd_HH-mm-ss}_{FileSuffix}.json";
        filling.StoragePath = CreateUniquePath(fileName);
        Write(filling, filling.StoragePath);
        return new SavedFillingSaveResult(filling, WasCreated: true);
    }

    public SavedFilling Rename(Guid id, string newName)
    {
        var filling = Find(id);
        var normalizedName = newName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new InvalidOperationException("Введите название сохранённого заполнения.");
        }

        if (GetAll().Any(item =>
                item.Id != id &&
                string.Equals(item.Name, normalizedName, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new InvalidOperationException("Сохранённое заполнение с таким названием уже существует.");
        }

        filling.Name = normalizedName.Length <= 120
            ? normalizedName
            : normalizedName[..120];
        filling.UpdatedAtUtc = DateTime.UtcNow;
        Write(filling, filling.StoragePath);

        var localDate = filling.CreatedAtUtc.ToLocalTime();
        var renamedPath = CreateUniquePath(
            $"{CreateSafeFileName(filling.Name)}_{localDate:yyyy-MM-dd_HH-mm-ss}_{FileSuffix}.json",
            filling.StoragePath);
        if (!string.Equals(renamedPath, filling.StoragePath, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(filling.StoragePath, renamedPath, overwrite: false);
            filling.StoragePath = renamedPath;
        }

        return filling;
    }

    public void Delete(Guid id)
    {
        var filling = Find(id);
        File.Delete(filling.StoragePath);
    }

    public int DeleteAll()
    {
        var fillings = GetAll();
        foreach (var filling in fillings)
        {
            File.Delete(filling.StoragePath);
        }

        return fillings.Count;
    }

    public void MarkUsed(Guid id)
    {
        var filling = Find(id);
        filling.LastUsedAtUtc = DateTime.UtcNow;
        filling.UpdatedAtUtc = filling.LastUsedAtUtc;
        Write(filling, filling.StoragePath);
    }

    public void RememberMappings(
        Guid savedFillingId,
        Guid targetTemplateId,
        IEnumerable<SavedFillingManualMapping> mappings)
    {
        var filling = Find(savedFillingId);
        var newMappings = mappings.ToList();
        foreach (var mapping in newMappings)
        {
            filling.ManualMappings.RemoveAll(existing =>
                existing.TargetTemplateId == targetTemplateId &&
                string.Equals(
                    existing.SourceFieldTag,
                    mapping.SourceFieldTag,
                    StringComparison.Ordinal));
            filling.ManualMappings.Add(mapping);
        }

        filling.UpdatedAtUtc = DateTime.UtcNow;
        Write(filling, filling.StoragePath);
    }

    private SavedFilling Find(Guid id) =>
        GetAll().FirstOrDefault(filling => filling.Id == id)
        ?? throw new InvalidOperationException("Сохранённое заполнение больше не найдено.");

    private void Write(SavedFilling filling, string path)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureInsideRoot(fullPath);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            var json = JsonSerializer.Serialize(filling, _jsonOptions);
            File.WriteAllText(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, fullPath, overwrite: true);
            filling.StoragePath = fullPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string CreateUniquePath(string fileName, string? currentPath = null)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(RootPath, fileName);
        var suffix = 2;

        while (File.Exists(candidate) &&
               !string.Equals(candidate, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.Combine(RootPath, $"{baseName}_{suffix}{extension}");
            suffix++;
        }

        return Path.GetFullPath(candidate);
    }

    private void EnsureInsideRoot(string path)
    {
        var root = Path.GetFullPath(RootPath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Недопустимый путь к сохранённому заполнению.");
        }
    }

    private static bool IsValid(SavedFilling? filling) =>
        filling is
        {
            FormatVersion: CurrentFormatVersion,
            Id: var id,
            TemplateId: var templateId,
            Fields.Count: > 0
        } &&
        id != Guid.Empty &&
        templateId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(filling.Name) &&
        !string.IsNullOrWhiteSpace(filling.TemplateName);

    private static string CalculateContentHash(
        Guid templateId,
        IEnumerable<SavedFillingField> fields)
    {
        var builder = new StringBuilder(templateId.ToString("D"));
        foreach (var field in fields.OrderBy(field => field.Tag, StringComparer.Ordinal))
        {
            builder.Append('\n');
            builder.Append(field.Tag.Trim());
            builder.Append('\u001f');
            builder.Append(field.Value.Replace("\r\n", "\n", StringComparison.Ordinal));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string CreateSafeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var safeName = new string(value
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');

        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "Заполнение";
        }

        return safeName.Length <= 80
            ? safeName
            : safeName[..80].TrimEnd();
    }
}

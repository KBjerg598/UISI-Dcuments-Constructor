using System.Text.Json.Serialization;

namespace UISI_Doc_Constructor.Models;

public sealed class SavedFilling
{
    public int FormatVersion { get; set; } = 1;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TemplateId { get; set; }

    public string TemplateName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastUsedAtUtc { get; set; } = DateTime.UtcNow;

    public string LastDocumentName { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public List<SavedFillingField> Fields { get; set; } = [];

    public List<SavedFillingManualMapping> ManualMappings { get; set; } = [];

    [JsonIgnore]
    public string StoragePath { get; set; } = string.Empty;

    [JsonIgnore]
    public string FieldsCountText => Fields.Count switch
    {
        1 => "1 поле",
        >= 2 and <= 4 => $"{Fields.Count} поля",
        _ => $"{Fields.Count} полей"
    };

    [JsonIgnore]
    public string LastUsedDisplay
    {
        get
        {
            var localDate = LastUsedAtUtc.ToLocalTime();
            return localDate.Date == DateTime.Today
                ? $"Сегодня, {localDate:HH:mm}"
                : localDate.ToString("dd.MM.yyyy, HH:mm");
        }
    }
}

public sealed class SavedFillingField
{
    public string Tag { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class SavedFillingManualMapping
{
    public Guid TargetTemplateId { get; set; }

    public string SourceFieldTag { get; set; } = string.Empty;

    public string SourceFieldName { get; set; } = string.Empty;

    public string TargetFieldTag { get; set; } = string.Empty;

    public string TargetFieldName { get; set; } = string.Empty;
}

public sealed record SavedFillingSaveResult(SavedFilling Filling, bool WasCreated);

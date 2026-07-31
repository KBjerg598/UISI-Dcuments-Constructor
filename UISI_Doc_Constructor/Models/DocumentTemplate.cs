namespace UISI_Doc_Constructor.Models;

public sealed class DocumentTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<string> FieldOrder { get; set; } = [];

    public string FieldCountText => FieldOrder.Count switch
    {
        0 => "Поля не добавлены",
        1 => "1 поле",
        >= 2 and <= 4 => $"{FieldOrder.Count} поля",
        _ => $"{FieldOrder.Count} полей"
    };

    public string UpdatedAtDisplay
    {
        get
        {
            var localDate = UpdatedAtUtc.ToLocalTime();
            var today = DateTime.Today;

            if (localDate.Date == today)
            {
                return $"Изменён сегодня, {localDate:HH:mm}";
            }

            if (localDate.Date == today.AddDays(-1))
            {
                return $"Изменён вчера, {localDate:HH:mm}";
            }

            return $"Изменён {localDate:dd.MM.yyyy}";
        }
    }
}

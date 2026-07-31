namespace UISI_Doc_Constructor.Models;

public sealed class FieldValueItem
{
    public required string Tag { get; init; }

    public required string DisplayName { get; init; }

    public string Value { get; set; } = string.Empty;
}

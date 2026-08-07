namespace UISI_Doc_Constructor.Models;

public enum TranslationFieldRole
{
    Unassigned = 0,
    Source = 1,
    Target = 2,
    Shared = 3
}

public enum TranslationPairMode
{
    Translation = 0,
    SameValue = 1
}

public sealed class TranslationFieldSettings
{
    public string Tag { get; set; } = string.Empty;

    public TranslationFieldRole Role { get; set; }

    public string? SourceFieldTag { get; set; }

    public Guid? PairId { get; set; }

    public TranslationPairMode PairMode { get; set; }
}

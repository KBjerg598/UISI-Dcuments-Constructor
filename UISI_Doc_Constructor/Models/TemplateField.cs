namespace UISI_Doc_Constructor.Models;

public sealed class TemplateField
{
    public required string Tag { get; init; }

    public required string DisplayName { get; init; }

    public int Occurrences { get; init; }

    public TranslationFieldRole TranslationRole { get; init; }

    public string? SourceFieldTag { get; init; }

    public Guid? PairId { get; init; }

    public TranslationPairMode PairMode { get; init; }

    public bool CanAddTranslationPair { get; init; }

    public string TranslationRoleText => TranslationRole switch
    {
        TranslationFieldRole.Source when PairMode == TranslationPairMode.SameValue =>
            "Одно значение UA + EN",
        TranslationFieldRole.Source => "Украинский оригинал",
        TranslationFieldRole.Target when PairMode == TranslationPairMode.SameValue =>
            "Связанное значение",
        TranslationFieldRole.Target => "Английский перевод",
        TranslationFieldRole.Shared => "Общее значение",
        _ => "Язык не настроен"
    };

    public string OccurrencesText => Occurrences switch
    {
        1 => "1 вхождение",
        >= 2 and <= 4 => $"{Occurrences} вхождения",
        _ => $"{Occurrences} вхождений"
    };
}

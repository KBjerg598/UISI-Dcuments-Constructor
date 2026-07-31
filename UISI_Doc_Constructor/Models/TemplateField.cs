namespace UISI_Doc_Constructor.Models;

public sealed class TemplateField
{
    public required string Tag { get; init; }

    public required string DisplayName { get; init; }

    public int Occurrences { get; init; }

    public string OccurrencesText => Occurrences switch
    {
        1 => "1 вхождение",
        >= 2 and <= 4 => $"{Occurrences} вхождения",
        _ => $"{Occurrences} вхождений"
    };
}

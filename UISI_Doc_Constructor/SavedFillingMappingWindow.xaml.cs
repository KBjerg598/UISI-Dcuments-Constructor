using System.Collections.ObjectModel;
using System.Windows;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;

namespace UISI_Doc_Constructor;

public partial class SavedFillingMappingWindow : Window
{
    private readonly SavedFillingMatchResult _matchResult;

    public SavedFillingMappingWindow(
        SavedFilling filling,
        SavedFillingMatchResult matchResult,
        SavedFillingMatcher matcher)
    {
        InitializeComponent();

        _matchResult = matchResult;
        Mappings = [];

        foreach (var source in matchResult.UnmatchedSourceFields)
        {
            var options = new List<FieldMappingTargetOption>
            {
                new() { Label = "Не использовать" }
            };
            options.AddRange(matchResult.UnmatchedTargetFields.Select(field =>
                new FieldMappingTargetOption
                {
                    Field = field,
                    Label = field.DisplayName
                }));

            var suggestion = matcher.FindSuggestion(source, matchResult.UnmatchedTargetFields);
            Mappings.Add(new SavedFieldMappingItem
            {
                Source = source,
                TargetOptions = options,
                SelectedTarget = options[0],
                SuggestionText = suggestion is null
                    ? string.Empty
                    : $"Возможно подходит: {suggestion.DisplayName}"
            });
        }

        DataContext = this;
        DescriptionText.Text =
            $"В заполнении «{filling.Name}» найдены поля, которых нет в текущем шаблоне под теми же именами.";
        AutomaticCountText.Text = matchResult.AutomaticMatches.Count.ToString();
        ManualCountText.Text = matchResult.UnmatchedSourceFields.Count.ToString();
        EmptyCountText.Text = matchResult.UnmatchedTargetFields.Count.ToString();
        EmptyFieldsText.Text = matchResult.UnmatchedTargetFields.Count == 0
            ? "В текущем шаблоне нет новых незаполненных полей."
            : $"Без сохранённых данных: {string.Join(", ", matchResult.UnmatchedTargetFields.Select(field => field.DisplayName))}";
    }

    public ObservableCollection<SavedFieldMappingItem> Mappings { get; }

    public IReadOnlyList<SavedFillingFieldMatch> ManualMatches { get; private set; } = [];

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var selectedMappings = Mappings
            .Where(item => item.SelectedTarget?.Field is not null)
            .ToList();
        var duplicatedTargets = selectedMappings
            .GroupBy(item => item.SelectedTarget!.Field!.Tag, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.First().SelectedTarget!.Field!.DisplayName)
            .ToList();
        if (duplicatedTargets.Count > 0)
        {
            ValidationText.Text =
                $"Одно поле нельзя заполнить несколькими значениями: {string.Join(", ", duplicatedTargets)}.";
            return;
        }

        ManualMatches = selectedMappings
            .Select(item => new SavedFillingFieldMatch(
                item.Source,
                item.SelectedTarget!.Field!,
                SavedFillingMatchKind.Manual))
            .ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

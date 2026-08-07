using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using UISI_Doc_Constructor.Models;

namespace UISI_Doc_Constructor;

public partial class TranslationSetupWindow : Window
{
    private readonly ICollectionView _fieldsView;

    public TranslationSetupWindow(
        IEnumerable<TemplateField> fields,
        IEnumerable<TranslationFieldSettings> existingSettings)
    {
        InitializeComponent();

        var fieldList = fields.ToList();
        var settingsByTag = existingSettings
            .GroupBy(item => item.Tag, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Fields = new ObservableCollection<TranslationSetupItem>(fieldList.Select(field =>
        {
            settingsByTag.TryGetValue(field.Tag, out var settings);
            return new TranslationSetupItem(field, settings);
        }));

        ApplySuggestedLegacyPairs();

        foreach (var item in Fields)
        {
            item.PropertyChanged += Field_PropertyChanged;
        }

        _fieldsView = CollectionViewSource.GetDefaultView(Fields);
        _fieldsView.SortDescriptions.Add(new SortDescription(
            nameof(TranslationSetupItem.RoleSortOrder),
            ListSortDirection.Ascending));
        _fieldsView.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(TranslationSetupItem.RoleGroupName)));
        RefreshSourceOptions();

        DataContext = this;
    }

    public ObservableCollection<TranslationSetupItem> Fields { get; }

    public IReadOnlyList<TranslationFieldSettings> Settings { get; private set; } = [];

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Fields.Any(item => item.Role == TranslationFieldRole.Unassigned))
        {
            ShowValidation("Назначьте роль каждому полю шаблона.");
            return;
        }

        if (!Fields.Any(item => item.Role == TranslationFieldRole.Source) ||
            !Fields.Any(item => item.Role == TranslationFieldRole.Target))
        {
            ShowValidation("Нужны хотя бы одно украинское и одно английское поле.");
            return;
        }

        var sourceTags = Fields
            .Where(item => item.Role == TranslationFieldRole.Source)
            .Select(item => item.Tag)
            .ToHashSet(StringComparer.Ordinal);
        var invalidPair = Fields.FirstOrDefault(item =>
            item.Role == TranslationFieldRole.Target &&
            item.SourceFieldTag is not null &&
            !sourceTags.Contains(item.SourceFieldTag));
        if (invalidPair is not null)
        {
            ShowValidation(
                $"Для поля «{invalidPair.DisplayName}» выберите именно украинский оригинал.");
            return;
        }

        var repeatedSource = Fields
            .Where(item =>
                item.Role == TranslationFieldRole.Target &&
                item.SourceFieldTag is not null)
            .GroupBy(item => item.SourceFieldTag!, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (repeatedSource is not null)
        {
            ShowValidation(
                "Каждому украинскому полю можно назначить только одно родственное английское поле.");
            return;
        }

        var unpairedSameValue = Fields.FirstOrDefault(item =>
            item.Role == TranslationFieldRole.Target &&
            item.PairMode == TranslationPairMode.SameValue &&
            item.SourceFieldTag is null);
        if (unpairedSameValue is not null)
        {
            ShowValidation(
                $"Для поля «{unpairedSameValue.DisplayName}» выберите родственное украинское поле.");
            return;
        }

        var settingsByTag = Fields.ToDictionary(
            item => item.Tag,
            item => new TranslationFieldSettings
            {
                Tag = item.Tag,
                Role = item.Role
            },
            StringComparer.Ordinal);
        foreach (var target in Fields.Where(item =>
                     item.Role == TranslationFieldRole.Target &&
                     item.SourceFieldTag is not null))
        {
            var pairId = target.PairId ?? Guid.NewGuid();
            var targetSettings = settingsByTag[target.Tag];
            targetSettings.SourceFieldTag = target.SourceFieldTag;
            targetSettings.PairId = pairId;
            targetSettings.PairMode = target.PairMode;

            var sourceSettings = settingsByTag[target.SourceFieldTag!];
            sourceSettings.PairId = pairId;
            sourceSettings.PairMode = target.PairMode;
        }

        Settings = Fields.Select(item => settingsByTag[item.Tag]).ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowValidation(string message) => ValidationText.Text = message;

    private void Field_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TranslationSetupItem.Role))
        {
            if (e.PropertyName is nameof(TranslationSetupItem.SourceFieldTag) or
                nameof(TranslationSetupItem.PairMode))
            {
                ValidationText.Text = string.Empty;
            }

            return;
        }

        RefreshSourceOptions();
        _fieldsView.Refresh();
        ValidationText.Text = string.Empty;
    }

    private void RefreshSourceOptions()
    {
        var sourceFields = Fields
            .Where(item => item.Role == TranslationFieldRole.Source)
            .Select(item => new TranslationSourceOption(item.Tag, item.DisplayName))
            .ToList();
        foreach (var item in Fields)
        {
            item.SetSourceOptions(sourceFields.Where(source =>
                !string.Equals(source.Tag, item.Tag, StringComparison.Ordinal)));
        }
    }

    private void ApplySuggestedLegacyPairs()
    {
        var fieldsByName = Fields
            .GroupBy(item => item.DisplayName.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.CurrentCultureIgnoreCase);

        foreach (var target in Fields)
        {
            if (target.SourceFieldTag is not null ||
                !TryGetOriginalFieldName(target.DisplayName, out var originalName) ||
                !fieldsByName.TryGetValue(originalName, out var source) ||
                string.Equals(source.Tag, target.Tag, StringComparison.Ordinal))
            {
                continue;
            }

            var isLegacySameValue =
                source.Role == TranslationFieldRole.Shared &&
                target.Role == TranslationFieldRole.Shared;
            var canSuggestTranslation =
                (source.Role == TranslationFieldRole.Unassigned &&
                 target.Role == TranslationFieldRole.Unassigned) ||
                (source.Role == TranslationFieldRole.Source &&
                 target.Role == TranslationFieldRole.Target);
            if (!isLegacySameValue && !canSuggestTranslation)
            {
                continue;
            }

            source.Role = TranslationFieldRole.Source;
            target.Role = TranslationFieldRole.Target;
            target.SourceFieldTag = source.Tag;
            target.PairId ??= Guid.NewGuid();
            target.PairMode = isLegacySameValue
                ? TranslationPairMode.SameValue
                : TranslationPairMode.Translation;
        }
    }

    private static bool TryGetOriginalFieldName(string targetName, out string originalName)
    {
        var normalized = targetName.Trim();
        string[] suffixes = [" — EN", " – EN", " - EN", " (EN)"];
        foreach (var suffix in suffixes)
        {
            if (!normalized.EndsWith(suffix, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            originalName = normalized[..^suffix.Length].Trim();
            return originalName.Length > 0;
        }

        originalName = string.Empty;
        return false;
    }
}

public sealed class TranslationSetupItem : INotifyPropertyChanged
{
    private TranslationFieldRole _role;
    private string? _sourceFieldTag;
    private Guid? _pairId;
    private TranslationPairMode _pairMode;

    public TranslationSetupItem(
        TemplateField field,
        TranslationFieldSettings? settings)
    {
        Tag = field.Tag;
        DisplayName = field.DisplayName;
        _role = settings?.Role ?? TranslationFieldRole.Unassigned;
        _sourceFieldTag = settings?.SourceFieldTag;
        _pairId = settings?.PairId;
        _pairMode = settings?.PairMode ?? TranslationPairMode.Translation;
        RoleOptions =
        [
            new(TranslationFieldRole.Unassigned, "Выберите роль"),
            new(TranslationFieldRole.Source, "Украинский оригинал"),
            new(TranslationFieldRole.Target, "Английский перевод"),
            new(TranslationFieldRole.Shared, "Общее поле без пары")
        ];
        PairModeOptions =
        [
            new(TranslationPairMode.Translation, "Переводит переводчик"),
            new(TranslationPairMode.SameValue, "Одинаковое значение")
        ];
        SourceOptions = [];
    }

    public string Tag { get; }

    public string DisplayName { get; }

    public IReadOnlyList<TranslationRoleOption> RoleOptions { get; }

    public ObservableCollection<TranslationSourceOption> SourceOptions { get; }

    public IReadOnlyList<TranslationPairModeOption> PairModeOptions { get; }

    public TranslationFieldRole Role
    {
        get => _role;
        set
        {
            if (_role == value)
            {
                return;
            }

            _role = value;
            if (_role != TranslationFieldRole.Target)
            {
                _sourceFieldTag = null;
                _pairId = null;
                OnPropertyChanged(nameof(SourceFieldTag));
                OnPropertyChanged(nameof(PairId));
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSelectSource));
            OnPropertyChanged(nameof(RoleDescription));
            OnPropertyChanged(nameof(RoleGroupName));
            OnPropertyChanged(nameof(RoleSortOrder));
        }
    }

    public string? SourceFieldTag
    {
        get => _sourceFieldTag;
        set
        {
            if (string.Equals(_sourceFieldTag, value, StringComparison.Ordinal))
            {
                return;
            }

            _sourceFieldTag = value;
            if (_sourceFieldTag is not null)
            {
                _pairId ??= Guid.NewGuid();
                OnPropertyChanged(nameof(PairId));
            }
            OnPropertyChanged();
        }
    }

    public Guid? PairId
    {
        get => _pairId;
        set
        {
            if (_pairId == value)
            {
                return;
            }

            _pairId = value;
            OnPropertyChanged();
        }
    }

    public TranslationPairMode PairMode
    {
        get => _pairMode;
        set
        {
            if (_pairMode == value)
            {
                return;
            }

            _pairMode = value;
            OnPropertyChanged();
        }
    }

    public bool CanSelectSource => Role == TranslationFieldRole.Target;

    public string RoleDescription => Role switch
    {
        TranslationFieldRole.Source => "Значение вводит автор документа.",
        TranslationFieldRole.Shared => "Одиночное поле без отдельной языковой пары.",
        TranslationFieldRole.Target => "Выберите соответствующий украинский оригинал.",
        _ => "Сначала выберите назначение этого поля."
    };

    public string RoleGroupName => Role switch
    {
        TranslationFieldRole.Source => "Украинские поля",
        TranslationFieldRole.Target => "Английские поля",
        TranslationFieldRole.Shared => "Общие поля без пары",
        _ => "Нужно распределить"
    };

    public int RoleSortOrder => (int)Role;

    public void SetSourceOptions(IEnumerable<TranslationSourceOption> sourceOptions)
    {
        var options = sourceOptions.ToList();
        var selectedSourceIsValid = _sourceFieldTag is null ||
                                    options.Any(option => string.Equals(
                                        option.Tag,
                                        _sourceFieldTag,
                                        StringComparison.Ordinal));

        SourceOptions.Clear();
        SourceOptions.Add(new TranslationSourceOption(null, "Не связывать"));
        foreach (var option in options)
        {
            SourceOptions.Add(option);
        }

        if (!selectedSourceIsValid)
        {
            SourceFieldTag = null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record TranslationRoleOption(TranslationFieldRole Role, string Label);

public sealed record TranslationSourceOption(string? Tag, string Label);

public sealed record TranslationPairModeOption(TranslationPairMode Mode, string Label);

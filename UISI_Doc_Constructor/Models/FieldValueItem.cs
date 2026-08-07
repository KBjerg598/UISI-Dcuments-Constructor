using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UISI_Doc_Constructor.Models;

public sealed class FieldValueItem : INotifyPropertyChanged
{
    private string _value = string.Empty;
    private TranslationFieldRole _translationRole;
    private string? _sourceFieldTag;
    private Guid? _pairId;
    private TranslationPairMode _pairMode;

    public required string Tag { get; init; }

    public required string DisplayName { get; init; }

    public TranslationFieldRole TranslationRole
    {
        get => _translationRole;
        set
        {
            if (_translationRole == value)
            {
                return;
            }

            _translationRole = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TranslationRoleText));
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
            OnPropertyChanged(nameof(TranslationRoleText));
        }
    }

    public string TranslationRoleText => TranslationRole switch
    {
        TranslationFieldRole.Source when PairMode == TranslationPairMode.SameValue =>
            "UA + EN",
        TranslationFieldRole.Source => "UA",
        TranslationFieldRole.Target when PairMode == TranslationPairMode.SameValue =>
            "СВЯЗАНО",
        TranslationFieldRole.Target => "EN",
        TranslationFieldRole.Shared => "ОБЩЕЕ",
        _ => string.Empty
    };

    public string Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal))
            {
                return;
            }

            _value = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

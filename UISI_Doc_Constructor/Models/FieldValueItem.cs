using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UISI_Doc_Constructor.Models;

public sealed class FieldValueItem : INotifyPropertyChanged
{
    private string _value = string.Empty;

    public required string Tag { get; init; }

    public required string DisplayName { get; init; }

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

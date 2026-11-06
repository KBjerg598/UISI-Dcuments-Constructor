using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace UISI_Doc_Constructor.Models;

public sealed class TranslationPackageManifest
{
    public int FormatVersion { get; set; } = 1;

    public Guid PackageId { get; set; } = Guid.NewGuid();

    public Guid TemplateId { get; set; }

    public string TemplateName { get; set; } = string.Empty;

    public string SourceLanguage { get; set; } = "uk";

    public string TargetLanguage { get; set; } = "en";

    public int Revision { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public string WorkingDocumentSha256 { get; set; } = string.Empty;

    public List<TranslationPackageField> Fields { get; set; } = [];
}

public sealed class TranslationPackageField
{
    public string Tag { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public TranslationFieldRole Role { get; set; }

    public string? SourceFieldTag { get; set; }

    public Guid? PairId { get; set; }

    public TranslationPairMode PairMode { get; set; }

    public string Value { get; set; } = string.Empty;
}

public sealed class TranslationFieldValueItem : INotifyPropertyChanged
{
    private string _value = string.Empty;
    private string _sourceValue = string.Empty;
    private FieldValueItem? _source;

    public required string Tag { get; init; }

    public required string DisplayName { get; init; }

    public string SourceDisplayName { get; init; } = string.Empty;

    public FieldValueItem? Source
    {
        get => _source;
        init
        {
            _source = value;
            if (_source is not null)
            {
                _source.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(FieldValueItem.Value))
                    {
                        OnPropertyChanged(nameof(SourceValue));
                    }
                };
            }
        }
    }

    public bool CanEditSource { get; init; }

    public bool IsSourceReadOnly => !CanEditSource;

    public string SourceValue
    {
        get => _source?.Value ?? _sourceValue;
        set
        {
            if (_source is not null)
            {
                _source.Value = value;
            }
            else if (!string.Equals(_sourceValue, value, StringComparison.Ordinal))
            {
                _sourceValue = value;
                OnPropertyChanged();
            }
        }
    }

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

public sealed class TranslationPackageSession : IDisposable
{
    public required string SourcePath { get; init; }

    public required string WorkingDirectory { get; init; }

    public required string WorkingDocumentPath { get; init; }

    public required TranslationPackageManifest Manifest { get; set; }

    public void Dispose()
    {
        if (!Directory.Exists(WorkingDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(WorkingDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary Word handle may delay cleanup until the operating system clears it.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary cleanup must never hide a successfully saved translation.
        }
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;

namespace UISI_Doc_Constructor;

public partial class SavedFillingsWindow : Window
{
    private readonly SavedFillingStorageService _storageService;
    private readonly Guid _currentTemplateId;
    private readonly string _currentTemplateName;
    private IReadOnlyList<SavedFilling> _allFillings = [];

    public SavedFillingsWindow(
        SavedFillingStorageService storageService,
        Guid currentTemplateId,
        string currentTemplateName)
    {
        InitializeComponent();

        _storageService = storageService;
        _currentTemplateId = currentTemplateId;
        _currentTemplateName = currentTemplateName;
        Fillings = [];
        DataContext = this;

        CurrentTemplateText.Text = $"Можно загрузить данные в шаблон «{currentTemplateName}»";
        Reload();
    }

    public ObservableCollection<SavedFilling> Fillings { get; }

    public SavedFilling? SelectedFilling { get; private set; }

    private void Reload(Guid? selectedId = null)
    {
        _allFillings = _storageService.GetAll();
        ApplyFilter(selectedId);
    }

    private void ApplyFilter(Guid? selectedId = null)
    {
        var query = SearchTextBox.Text.Trim();
        var onlyCurrentTemplate = OnlyCurrentTemplateCheckBox.IsChecked == true;
        var filtered = _allFillings.Where(filling =>
            (!onlyCurrentTemplate || filling.TemplateId == _currentTemplateId) &&
            (query.Length == 0 ||
             filling.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             filling.TemplateName.Contains(query, StringComparison.CurrentCultureIgnoreCase)));

        Fillings.Clear();
        foreach (var filling in filtered)
        {
            Fillings.Add(filling);
        }

        CountText.Text = $"{Fillings.Count} из {_allFillings.Count}";
        EmptyPanel.Visibility = Fillings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteAllButton.IsEnabled = _allFillings.Count > 0;

        if (selectedId is not null)
        {
            FillingsList.SelectedItem = Fillings.FirstOrDefault(item => item.Id == selectedId);
            if (FillingsList.SelectedItem is not null)
            {
                FillingsList.ScrollIntoView(FillingsList.SelectedItem);
            }
        }

        UpdateUseButton();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Fillings is not null)
        {
            ApplyFilter();
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (Fillings is not null)
        {
            ApplyFilter();
        }
    }

    private void FillingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateUseButton();
    }

    private void FillingsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FillingsList.SelectedItem is SavedFilling)
        {
            UseSelectedFilling();
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedFilling filling })
        {
            return;
        }

        var dialog = new SavedFillingNameWindow(
            filling.Name,
            _allFillings.Where(item => item.Id != filling.Id).Select(item => item.Name))
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        RunSafely(() =>
        {
            var renamed = _storageService.Rename(filling.Id, dialog.SavedName);
            Reload(renamed.Id);
        });
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedFilling filling })
        {
            return;
        }

        var confirmation = new ConfirmationWindow(
            "Удалить заполнение?",
            $"Заполнение «{filling.Name}» будет удалено из локальной библиотеки. Созданные ранее документы не изменятся.",
            "Удалить",
            "Это действие удалит только сохранённые значения полей.")
        {
            Owner = this
        };
        if (confirmation.ShowDialog() != true)
        {
            return;
        }

        RunSafely(() =>
        {
            _storageService.Delete(filling.Id);
            Reload();
        });
    }

    private void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (_allFillings.Count == 0)
        {
            return;
        }

        var confirmation = new ConfirmationWindow(
            "Удалить все заполнения?",
            $"Будут удалены все сохранённые наборы данных: {_allFillings.Count}. Созданные документы и шаблоны останутся без изменений.",
            "Удалить все",
            "Восстановить удалённые значения полей будет невозможно.")
        {
            Owner = this
        };
        if (confirmation.ShowDialog() != true)
        {
            return;
        }

        RunSafely(() =>
        {
            _storageService.DeleteAll();
            Reload();
        });
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        UseSelectedFilling();
    }

    private void UseSelectedFilling()
    {
        if (FillingsList.SelectedItem is not SavedFilling filling)
        {
            return;
        }

        SelectedFilling = filling;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void UpdateUseButton()
    {
        if (UseButton is not null)
        {
            UseButton.IsEnabled = FillingsList?.SelectedItem is SavedFilling;
        }
    }

    private void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Сохранённые заполнения",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}

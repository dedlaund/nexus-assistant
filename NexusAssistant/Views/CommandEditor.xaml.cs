using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NexusAssistant.Services;

namespace NexusAssistant.Views;

public partial class CommandEditor : UserControl
{
    private Guid? _editing;
    private CustomCommand? _deleting;

    public CommandEditor()
    {
        InitializeComponent();
        ActionBox.SelectedIndex = 0;
    }

    private void Action_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TargetLabel is null || ActionBox.SelectedItem is not CustomActionOption option) return;
        TargetLabel.Text = option.TargetLabel;
        TargetHint.Text = option.Hint;
        Pickers.Visibility = option.Action == CustomAction.OpenPath ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PickFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите файл или приложение", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) TargetBox.Text = dialog.FileName;
    }

    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) TargetBox.Text = dialog.FolderName;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AssistantViewModel model && ActionBox.SelectedItem is CustomActionOption option &&
            await model.SaveCommandAsync(_editing, PhraseBox.Text, option.Action, TargetBox.Text))
            ResetEditor();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CustomCommand item }) return;
        ResetDelete();
        _editing = item.Id;
        EditorTitle.Text = "Редактирование команды";
        SaveButton.Content = "Сохранить изменения";
        CancelEditButton.Visibility = Visibility.Visible;
        PhraseBox.Text = item.Phrase;
        ActionBox.SelectedItem = CustomCommands.Options.First(option => option.Action == item.Action);
        TargetBox.Text = item.Target;
        PhraseBox.BringIntoView();
        PhraseBox.Focus();
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => ResetEditor();

    private void ResetEditor()
    {
        _editing = null;
        EditorTitle.Text = "Новая команда";
        SaveButton.Content = "Добавить команду";
        CancelEditButton.Visibility = Visibility.Collapsed;
        PhraseBox.Clear();
        ActionBox.SelectedIndex = 0;
        TargetBox.Clear();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CustomCommand item }) return;
        _deleting = item;
        DeleteMessage.Text = $"Удалить команду «{item.Phrase}»?";
        DeletePrompt.Visibility = Visibility.Visible;
        DeletePrompt.BringIntoView();
    }

    private void CancelDelete_Click(object sender, RoutedEventArgs e) => ResetDelete();

    private async void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AssistantViewModel model || _deleting is not { } item) return;
        ResetDelete();
        await model.DeleteCommandAsync(item.Id);
        if (_editing == item.Id && !model.UserCommands.Any(command => command.Id == item.Id)) ResetEditor();
    }

    private void ResetDelete()
    {
        _deleting = null;
        DeletePrompt.Visibility = Visibility.Collapsed;
    }
}

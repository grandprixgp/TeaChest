using System.Windows;
using Microsoft.Win32;
using TeaChest.ViewModels;

namespace TeaChest.Services;

/// <summary>The real dialogs, owned by the main window so they open on top of it.</summary>
public sealed class WpfDialogService : IDialogService
{
    private readonly Window? _owner;

    public WpfDialogService(Window? owner)
    {
        _owner = owner;
    }

    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultExtension, string fileName, string initialDirectory)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            DefaultExt = defaultExtension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = fileName,
            InitialDirectory = initialDirectory
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public bool Confirm(string message, string title, ConfirmButtons buttons, DialogIcon icon)
    {
        if (buttons == ConfirmButtons.YesNo)
        {
            return ShowMessage(message, title, MessageBoxButton.YesNo, icon) == MessageBoxResult.Yes;
        }
        return ShowMessage(message, title, MessageBoxButton.OKCancel, icon) == MessageBoxResult.OK;
    }

    public void Show(string message, string title, DialogIcon icon)
        => ShowMessage(message, title, MessageBoxButton.OK, icon);

    private MessageBoxResult ShowMessage(string message, string title, MessageBoxButton buttons, DialogIcon icon)
    {
        MessageBoxImage image = icon switch
        {
            DialogIcon.Warning => MessageBoxImage.Warning,
            DialogIcon.Question => MessageBoxImage.Question,
            _ => MessageBoxImage.Information
        };
        return _owner != null
            ? MessageBox.Show(_owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
    }
}

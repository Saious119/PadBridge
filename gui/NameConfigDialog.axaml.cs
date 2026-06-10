using Avalonia.Controls;
using Avalonia.Interactivity;
using PadBridge.Gui.Core;

namespace PadBridge.Gui;

/// <summary>Modal prompt for naming a config; returns the name or null.</summary>
public partial class NameConfigDialog : Window
{
    public NameConfigDialog()
    {
        InitializeComponent();
        Opened += (_, _) => NameBox.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0)
        {
            ShowError("Enter a name.");
            return;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("That name contains characters that can't be used in a filename.");
            return;
        }
        if (File.Exists(ConfigStore.PathFor(name)))
        {
            ShowError($"A config named '{name}' already exists.");
            return;
        }
        Close(name);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}

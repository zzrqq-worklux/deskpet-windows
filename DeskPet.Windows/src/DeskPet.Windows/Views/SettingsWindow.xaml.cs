using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace DeskPet.Windows.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        SettingsNav.SelectedIndex = 0;
    }

    private void OnSettingsNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SectionTitle.Text = SettingsNav.SelectedItem is ListBoxItem item
            ? item.Content?.ToString() ?? string.Empty
            : string.Empty;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (System.Windows.Application.Current.Dispatcher.HasShutdownStarted) return;
        e.Cancel = true;
        Hide();
    }
}

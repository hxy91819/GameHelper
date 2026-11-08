using System.Windows;
using System.Windows.Controls;

namespace GameHelper.Desktop;

public partial class JournalEditor : Window
{
    internal JournalEditor(JournalEditorViewModel model) { InitializeComponent(); DataContext = model; }
    private void OnStatusClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: JournalStatusOption option } box) return;
        ((JournalEditorViewModel)DataContext).Toggle(option.Name, box.IsChecked == true);
    }
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try { if (await ((JournalEditorViewModel)DataContext).SaveAsync()) DialogResult = true; }
        finally { IsEnabled = true; }
    }
}

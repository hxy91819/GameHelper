using System.Windows;

namespace GameHelper.Desktop;

public partial class JournalEditor : Window
{
    internal JournalEditor(JournalEditorViewModel model) { InitializeComponent(); DataContext = model; }
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try { if (await ((JournalEditorViewModel)DataContext).SaveAsync()) DialogResult = true; }
        finally { IsEnabled = true; }
    }
}

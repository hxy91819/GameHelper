using System.Windows;

namespace GameHelper.Desktop;

public partial class JournalEditor : Window
{
    internal JournalEditor(JournalEditorViewModel model) { InitializeComponent(); DataContext = model; }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (((JournalEditorViewModel)DataContext).Save()) DialogResult = true;
    }
}

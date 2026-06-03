using System.Windows;
using System.Windows.Input;

namespace AshenTasker.Views.Dialogs;

public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string value = "")
    {
        InitializeComponent();
        Title = title;
        DialogTitleText.Text = title;
        PromptText.Text = prompt;
        ValueTextBox.Text = value;
        ValueTextBox.SelectAll();
        ValueTextBox.Focus();
    }

    public string ResponseText => ValueTextBox.Text.Trim();

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }
}

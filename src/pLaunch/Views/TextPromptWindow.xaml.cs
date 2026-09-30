using System.Windows;

namespace pLaunch.Views;

/// <summary>Asks for one line of text; <c>validate</c> returns an error message, or null when the text is fine.</summary>
public partial class TextPromptWindow : Window
{
    readonly Func<string, string?> _validate;

    public TextPromptWindow(string title, string message, string initial, Func<string, string?> validate)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        InputBox.Text = initial;
        _validate = validate;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public string Value => InputBox.Text.Trim();

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_validate(Value) is { } error)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            InputBox.Focus();
            return;
        }
        DialogResult = true;
    }
}

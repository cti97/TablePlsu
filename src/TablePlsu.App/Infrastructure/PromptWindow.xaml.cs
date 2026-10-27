using System.Windows;
using System.Windows.Media;

namespace TablePlsu.App.Infrastructure;

/// <summary>A themed message / text / password prompt. Use through <see cref="Dialogs"/>.</summary>
public partial class PromptWindow : Window
{
    public enum InputKind { None, Text, Password, Multiline }

    public PromptWindow(string title, string message, InputKind input, string okText, string? cancelText, string? detail = null, bool danger = false)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        if (cancelText is null) CancelButton.Visibility = Visibility.Collapsed;
        else CancelButton.Content = cancelText;
        if (danger) OkButton.SetResourceReference(BackgroundProperty, "c.app.error");

        switch (input)
        {
            case InputKind.Text:
                InputBox.Visibility = Visibility.Visible;
                break;
            case InputKind.Multiline:
                InputBox.Visibility = Visibility.Visible;
                InputBox.AcceptsReturn = true;
                InputBox.TextWrapping = TextWrapping.Wrap;
                InputBox.MinHeight = 90;
                InputBox.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
                OkButton.IsDefault = false;
                break;
            case InputKind.Password:
                PasswordInput.Visibility = Visibility.Visible;
                break;
        }
        if (detail is not null)
        {
            DetailBox.Text = detail;
            DetailBox.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            if (InputBox.Visibility == Visibility.Visible) { InputBox.Focus(); InputBox.SelectAll(); }
            else if (PasswordInput.Visibility == Visibility.Visible) PasswordInput.Focus();
            else OkButton.Focus();
        };
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
    }

    public string InputText
    {
        get => InputBox.Visibility == Visibility.Visible ? InputBox.Text : PasswordInput.Password;
        set
        {
            InputBox.Text = value;
            PasswordInput.Password = value;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}

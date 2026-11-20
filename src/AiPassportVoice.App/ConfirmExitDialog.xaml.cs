using System.Windows;
using System.Windows.Input;

namespace AiPassportVoice.App;

public partial class ConfirmExitDialog : Window
{
    public enum Choice
    {
        Cancel,
        Minimize,
        Exit,
    }

    public Choice Result { get; private set; } = Choice.Cancel;

    public ConfirmExitDialog()
    {
        InitializeComponent();
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        Result = Choice.Minimize;
        DialogResult = true;
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Result = Choice.Exit;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = Choice.Cancel;
        DialogResult = false;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }
}

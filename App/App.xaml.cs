namespace PdfCustode.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
#if WINDOWS
        UserAppTheme = AppTheme.Light;
#endif
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new ReaderPage()) { Title = "PDF Custode" };
        return window;
    }
}

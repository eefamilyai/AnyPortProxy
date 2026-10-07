namespace AnyPortProxy.Gui;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Errors anywhere in the app show a message instead of closing it.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) ShowError(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void ShowError(Exception ex)
    {
        try
        {
            MessageBox.Show(ex.GetBaseException().Message + "\n\nThe app will keep running. If this keeps happening, try the Health check.",
                "AnyPortProxy — something went wrong", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
        }
    }
}

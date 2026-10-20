using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MozChecker.Core;

namespace MozChecker.Desktop;

public partial class App : Application
{
    private FileStream? instanceLock;
    private Store? store;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Contains("--smoke");
        var smokeDirectory = smoke ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MozChecker-smoke-" + Guid.NewGuid().ToString("N")) : null;
        Window? loading = null;
        var loadingClosed = false;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var directory = smokeDirectory ?? Store.DataDirectory;
            Directory.CreateDirectory(directory);
            // A shared file handle excludes a second .NET instance before opening SQLite.
            instanceLock = new FileStream(System.IO.Path.Combine(directory, "desktop-dotnet.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!smoke && File.Exists(System.IO.Path.Combine(directory, "desktop.lock")))
                throw new IOException("Tutup aplikasi Python lama sebelum membuka versi .NET; jika sudah ditutup, periksa sisa desktop.lock.");
            if (!smoke)
            {
                loading = new Window { Title = "Moz Checker", Width = 460, Height = 130, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = new System.Windows.Controls.TextBlock { Text = "Membuka dan menyiapkan data lokal…", Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center } };
                loading.Closed += (_, _) => loadingClosed = true;
                loading.Show();
            }
            store = await Task.Run(() => new Store(System.IO.Path.Combine(directory, "checker.sqlite3")));
            if (loadingClosed)
            {
                Shutdown(0);
                return;
            }
            var keys = new ApiKeys();
            var vaultWarning = "";
            if (!smoke)
            {
                try
                {
                    keys = CredentialVault.Load();
                }
                catch (System.ComponentModel.Win32Exception) { vaultWarning = "Credential Manager tidak dapat dibaca. Pemeriksaan tetap bisa berjalan tanpa key."; }
            }
            var window = new MainWindow(store, keys, smoke);
            MainWindow = window;
            window.Show();
            loading?.Close();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (vaultWarning.Length > 0)
                MessageBox.Show(window, vaultWarning, "API key");
            if (smoke)
            {
                window.ContentRendered += (_, _) => Dispatcher.BeginInvoke(async () =>
                {
                    var code = 0;
                    try
                    {
                        var screenshot = e.Args.SkipWhile(x => x != "--screenshot").Skip(1).FirstOrDefault();
                        window.ScreenshotDirectory = screenshot is null ? null : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(screenshot));
                        window.VerifySmoke();
                        File.WriteAllText(System.IO.Path.Combine(directory, "smoke-report.json"), JsonData.Write(new
                        {
                            Ui = "WPF",
                            Checks = new[] { "IDN/PSL", "eight columns", "available-only results", "registered log", "search", "reset", "CSV button states", "immediate CSV parsing", "large-list import preview", "single full-list session", "aggregate progress", "queue/event separation", "persistent timestamped log", "resume workspace", "checkpoint resume guidance" },
                            Status = "passed"
                        }));
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        window.Width = 1040;
                        window.Height = 700;
                        window.UpdateLayout();
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        window.Width = 1440;
                        window.Height = 900;
                        window.UpdateLayout();
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        image.Render(window);
                        var path = e.Args.SkipWhile(x => x != "--screenshot").Skip(1).FirstOrDefault();
                        if (path is not null)
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(image));
                            using var output = File.Create(path);
                            encoder.Save(output);
                        }
                    }
                    catch (Exception ex) { code = 1; File.WriteAllText(System.IO.Path.Combine(directory, "smoke-error.txt"), ex.ToString()); }
                    await window.StopAsync();
                    window.Close();
                    Shutdown(code);
                }, DispatcherPriority.ApplicationIdle);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or ArgumentException or System.Text.Json.JsonException)
        {
            if (!smoke)
                MessageBox.Show("Workspace belum dapat dibuka. Pastikan aplikasi lain ditutup dan folder data dapat diakses.\n\n" + ex.GetType().Name, "Moz Checker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        store?.Dispose();
        instanceLock?.Dispose();
        base.OnExit(e);
    }
}

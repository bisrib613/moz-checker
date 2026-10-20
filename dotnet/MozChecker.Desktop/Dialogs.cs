using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MozChecker.Core;

namespace MozChecker.Desktop;

internal static class Forms
{
    public static TextBox Text(string value = "") => new() { Text = value, Margin = new(0, 0, 0, 8) };
    public static void Field(Panel panel, string label, Control input)
    {
        var caption = new Label { Content = label, Target = input };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        panel.Children.Add(caption);
        panel.Children.Add(input);
    }
    public static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8), Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("MutedBrush") };
    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, Margin = new(8, 0, 0, 0) };
        if (primary)
            button.Style = (Style)Application.Current.FindResource("PrimaryButton");
        button.Click += (_, _) => action();
        return button;
    }
    public static int Int(TextBox box)
    {
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0)
        {
            box.Focus();
            throw new ArgumentException("Isi angka bulat nol atau lebih pada kolom terkait.");
        }
        return n;
    }
    public static double Number(TextBox box)
    {
        if (!double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < 0)
        {
            box.Focus();
            throw new ArgumentException("Isi angka nol atau lebih pada kolom terkait.");
        }
        return n;
    }
    public static Grid Layout(Window window, UIElement body, UIElement footer)
    {
        window.Style = (Style)Application.Current.FindResource(typeof(Window));
        var root = new Grid { Margin = new(24) };
        root.RowDefinitions.Add(new()
        {
            Height = new(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        root.Children.Add(body);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        window.Content = root;
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { window.DialogResult = false; e.Handled = true; } };
        return root;
    }
}

internal sealed class SettingsWindow : Window
{
    public SettingsWindow(Store store)
    {
        Title = "Pengaturan API & aturan";
        Width = 660;
        Height = 700;
        MinWidth = 520;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var settings = store.GetSettings();
        var keysPanel = new StackPanel();
        var rulesPanel = new StackPanel();
        keysPanel.Children.Add(Forms.Hint("Kunci disimpan di Windows Credential Manager. Kosongkan untuk menghapus. Kunci hanya dikirim ke penyedia masing-masing."));
        var opr = new PasswordBox();
        var mozId = new PasswordBox();
        var mozSecret = new PasswordBox();
        Forms.Field(keysPanel, "Open PageRank API key", opr);
        Forms.Field(keysPanel, "Moz access ID (Links API v2)", mozId);
        Forms.Field(keysPanel, "Moz secret key (Links API v2)", mozSecret);
        keysPanel.Children.Add(Forms.Hint("Moz memakai endpoint legacy Links API v2, bukan token v3. Pastikan akun Anda mendukung endpoint ini. Aplikasi tetap bisa mengecek RDAP, DNS, dan Wayback tanpa kunci."));
        var error = Forms.Hint("");
        error.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("ErrorBrush");
        var loaded = true;
        try
        {
            var keys = CredentialVault.Load();
            opr.Password = keys.OprKey;
            mozId.Password = keys.MozId;
            mozSecret.Password = keys.MozSecret;
        }
        catch (System.ComponentModel.Win32Exception) { loaded = false; error.Text = "Credential Manager tidak dapat dibaca. Tutup dan coba lagi; kunci belum diubah."; }
        var budget = Forms.Text(settings.MozBudget.ToString());
        var reserve = Forms.Text(settings.MozReserve.ToString());
        var period = Forms.Text(settings.MozPeriod);
        var samples = Forms.Text(settings.Samples.ToString());
        var min = Forms.Text(settings.OprMin.ToString(CultureInfo.InvariantCulture));
        Forms.Field(rulesPanel, "Anggaran Moz lokal (rows per periode)", budget);
        Forms.Field(rulesPanel, "Cadangan rows", reserve);
        Forms.Field(rulesPanel, "Periode khusus (kosong = bulan UTC berjalan)", period);
        rulesPanel.Children.Add(Forms.Hint("Penghitung ini lokal, bukan saldo provider. Jangan mengganti periode untuk menganggap kuota provider bertambah."));
        Forms.Field(rulesPanel, "Sampel snapshot per domain (1–20)", samples);
        Forms.Field(rulesPanel, "OPR minimum (0–10)", min);
        var empty = new CheckBox { Content = "Tanpa riwayat dianggap lolos", IsChecked = settings.EmptyValid };
        rulesPanel.Children.Add(empty);
        var keywords = Forms.Text(string.Join('\n', settings.Keywords));
        keywords.AcceptsReturn = true;
        keywords.Height = 140;
        keywords.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Forms.Field(rulesPanel, "Kata kunci risiko (satu per baris)", keywords);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "API key", Content = new ScrollViewer { Content = keysPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 12, 8, 0) } });
        tabs.Items.Add(new TabItem { Header = "Aturan hunting", Content = new ScrollViewer { Content = rulesPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 12, 8, 0) } });
        var footer = new StackPanel();
        footer.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(actions);
        actions.Children.Add(Forms.Button("Batal", () => DialogResult = false));
        var save = Forms.Button("Simpan", () =>
        {
            try
            {
                var next = new Settings { MozBudget = Forms.Int(budget), MozReserve = Forms.Int(reserve), MozPeriod = period.Text.Trim(), Samples = Forms.Int(samples), OprMin = Forms.Number(min), EmptyValid = empty.IsChecked == true, Keywords = keywords.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
                next.Validate();
                CredentialVault.Save("opr_key", opr.Password.Trim());
                CredentialVault.Save("moz_id", mozId.Password.Trim());
                CredentialVault.Save("moz_secret", mozSecret.Password.Trim());
                store.Configure(next);
                DialogResult = true;
            }
            catch (ArgumentException ex) { error.Text = ex.Message; }
            catch (System.ComponentModel.Win32Exception) { error.Text = "Penyimpanan Credential Manager gagal. Periksa kembali kunci dan coba simpan lagi."; }
        }, true);
        save.IsEnabled = loaded;
        actions.Children.Add(save);
        Forms.Layout(this, tabs, footer);
    }
}

internal sealed record FilterValues(string Tld = "", string History = "", int MaxLength = 0, double Opr = 0, double Da = 0, int Snapshots = 0)
{
    public bool Active => Tld.Length > 0 || History.Length > 0 || MaxLength > 0 || Opr > 0 || Da > 0 || Snapshots > 0;
    public bool Matches(Result r) => (Tld.Length == 0 || r.Domain.EndsWith("." + Tld.TrimStart('.'), StringComparison.OrdinalIgnoreCase)) && (History.Length == 0 || r.History == History) && (MaxLength == 0 || r.Domain.Split('.')[0].Length <= MaxLength) && (Opr == 0 || r.Opr >= Opr) && (Da == 0 || r.Da >= Da) && (Snapshots == 0 || r.Snapshots >= Snapshots);
}
internal sealed class FilterWindow : Window
{
    public FilterValues Values
    {
        get; private set;
    }
    public FilterWindow(FilterValues values)
    {
        Values = values;
        Title = "Filter domain available";
        Width = 470;
        Height = 650;
        MinHeight = 400;
        MinWidth = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel();
        var tld = Forms.Text(values.Tld);
        var history = new ComboBox { ItemsSource = new[] { "Semua riwayat", "valid", "no_history", "invalid", "unchecked" }, MinHeight = 38 };
        history.SelectedIndex = values.History.Length == 0 ? 0 : Array.IndexOf(new[] { "", "valid", "no_history", "invalid", "unchecked" }, values.History);
        var length = Forms.Text(values.MaxLength.ToString());
        var opr = Forms.Text(values.Opr.ToString(CultureInfo.InvariantCulture));
        var da = Forms.Text(values.Da.ToString(CultureInfo.InvariantCulture));
        var snapshots = Forms.Text(values.Snapshots.ToString());
        Forms.Field(body, "TLD (contoh: co.id)", tld);
        Forms.Field(body, "Riwayat", history);
        Forms.Field(body, "Panjang nama maksimum (0 = semua)", length);
        Forms.Field(body, "OPR minimum", opr);
        Forms.Field(body, "Moz DA minimum", da);
        Forms.Field(body, "Snapshot minimum", snapshots);
        var error = Forms.Hint("");
        error.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("ErrorBrush");
        var footer = new StackPanel();
        footer.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(actions);
        actions.Children.Add(Forms.Button("Batal", () => DialogResult = false));
        actions.Children.Add(Forms.Button("Terapkan", () => { try { var o = Forms.Number(opr); var d = Forms.Number(da); if (o > 10 || d > 100) throw new ArgumentException("OPR maksimal 10; DA maksimal 100."); Values = new(tld.Text.Trim().ToLowerInvariant(), history.SelectedIndex <= 0 ? "" : (string)history.SelectedItem, Forms.Int(length), o, d, Forms.Int(snapshots)); DialogResult = true; } catch (ArgumentException ex) { error.Text = ex.Message; } }, true));
        Forms.Layout(this, new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, footer);
    }
}
internal sealed class EvidenceWindow : Window
{
    public EvidenceWindow(Result result)
    {
        Title = "Bukti · " + result.Domain;
        Width = 760;
        Height = 680;
        MinWidth = 500;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new Grid();
        body.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        body.RowDefinitions.Add(new()
        {
            Height = new(1, GridUnitType.Star)
        });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = result.Domain, FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(Forms.Hint($"{result.Status} · {result.Readiness}\nRDAP HTTP {result.RdapHttp} · DNS {result.Dns}\nRegistrar: {result.Registrar}\nKedaluwarsa: {result.Expires}"));
        body.Children.Add(header);
        var options = new System.Text.Json.JsonSerializerOptions(JsonData.Options) { WriteIndented = true };
        var evidence = new TextBox { IsReadOnly = true, Text = System.Text.Json.JsonSerializer.Serialize(result, options), FontFamily = new("Consolas"), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(evidence, "Bukti dan waktu pemeriksaan semua sumber");
        Grid.SetRow(evidence, 1);
        body.Children.Add(evidence);
        var footer = new StackPanel();
        footer.Children.Add(Forms.Hint("Arsip ditelaah sebagai teks sampel. Isi halaman tidak dirender di aplikasi."));
        var matches = new ComboBox { ItemsSource = result.Matches, DisplayMemberPath = "Snapshot", MinHeight = 36, SelectedIndex = result.Matches.Count > 0 ? 0 : -1 };
        System.Windows.Automation.AutomationProperties.SetName(matches, "Snapshot dengan kata kunci yang cocok");
        if (result.Matches.Count > 0)
            footer.Children.Add(matches);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        footer.Children.Add(actions);
        if (result.Matches.Count > 0)
            actions.Children.Add(Forms.Button("Buka snapshot di browser", () =>
        {
            if (matches.SelectedItem is HistoryMatch match && Uri.TryCreate(match.Snapshot, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "web.archive.org" && uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.AbsolutePath.StartsWith("/web/"))
                try
                {
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception) { MessageBox.Show(this, "Browser belum dapat dibuka."); }
        }));
        actions.Children.Add(Forms.Button("Tutup", Close));
        Forms.Layout(this, body, footer);
    }
}

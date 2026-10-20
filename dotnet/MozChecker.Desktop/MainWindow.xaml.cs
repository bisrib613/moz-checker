using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using MozChecker.Core;

namespace MozChecker.Desktop;

public partial class MainWindow : Window
{
    private sealed record Scope(string? Id, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record Snapshot(List<Job> Jobs, WorkspaceCounts Counts, ResultPage? Results, EventPage? Events, Settings? Settings = null, int Used = 0);
    private readonly Store store;
    private readonly Transport transport = new();
    private Engine engine;
    private readonly bool smokeMode;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource inputCancel = new();
    private bool initialView = true;
    private bool closing, closedAfterStop, updating, refreshing, preparing;
    private Task? inputWork, refreshWork;
    private ImportResult? preparedImport;
    private string? scope;
    private FilterValues filters = new();
    private int page;
    private const int PageSize = 200;
    private long rowCount;
    private long? eventAnchor;
    private string sort = "", lastJobs = "";
    private bool descending;
    private long request;
    public MainWindow(Store store, ApiKeys keys, bool smoke = false)
    {
        this.store = store;
        smokeMode = smoke;
        engine = NewEngine(keys);
        InitializeComponent();
        ResultTabs.SelectedIndex = 0;
        ConfigureColumns();
        timer.Tick += async (_, _) => await RefreshAsync();
        if (!smoke)
            timer.Start();
        ResultsGrid.SelectionChanged += (_, _) => EvidenceButton.IsEnabled = ResultsGrid.SelectedItem is Result;
        AddShortcut(Key.F, () => Search.Focus());
        AddShortcut(Key.Enter, () => { if (ResumeButton.IsEnabled && DomainInput.Text.Length == 0) ResumeClick(this, new()); else StartClick(this, new()); });
        AddShortcut(Key.O, () => ImportClick(this, new()));
        AddShortcut(Key.OemComma, () => SettingsClick(this, new()));
        Closing += OnClosing;
        SizeChanged += (_, _) => UpdateDensity();
        if (!smoke)
            Loaded += async (_, _) => await RefreshAsync();
        if (!smoke)
            DomainInput.Focus();
    }
    private void UpdateDensity()
    {
        var compact = ActualHeight < 780;
        LayoutRoot.Margin = compact ? new Thickness(16, 12, 16, 10) : new Thickness(24, 20, 24, 14);
        HeaderBar.Margin = new Thickness(0, 0, 0, compact ? 10 : 18);
        ResumeBanner.Margin = new Thickness(0, 0, 0, compact ? 8 : 14);
        InputPanel.Margin = new Thickness(0, 0, 0, compact ? 8 : 14);
        ProgressPanel.Margin = new Thickness(0, 0, 0, compact ? 8 : 16);
        ResumeHint.Visibility = ResultSummary.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
    private Engine NewEngine(ApiKeys keys) => new(store, keys, new Providers(store, keys, transport));
    private void AddShortcut(Key key, Action action)
    {
        var command = new RoutedCommand();
        CommandBindings.Add(new(command, (_, _) => action()));
        InputBindings.Add(new KeyBinding(command, key, ModifierKeys.Control));
    }
    private ResultFilter QueryFilter() => new(Search.Text.Trim(), filters.Tld, filters.History, filters.MaxLength, filters.Opr, filters.Da, filters.Snapshots);
    private void ConfigureColumns()
    {
        if (ResultsGrid is null)
            return;
        ResultsGrid.Columns.Clear();
        void Column(string title, string property, double width = 100, string? format = null)
        {
            var binding = new Binding(property) { TargetNullValue = "—" };
            if (format is not null)
                binding.StringFormat = format;
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            textStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(0, 2, 0, 0)));
            if (property == "Domain")
                textStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            ResultsGrid.Columns.Add(new DataGridTextColumn { Header = title, Binding = binding, ElementStyle = textStyle, SortMemberPath = property, Width = new DataGridLength(width) });
        }
        Column("Domain", "Domain", 190);
        if (ResultTabs.SelectedIndex == 0)
        {
            Column("OPR", "Opr", 75, "F2");
            Column("Moz DA", "Da", 80, "F0");
            Column("Moz PA", "Pa", 80, "F0");
            Column("Spam", "Spam", 75, "F0");
            Column("Riwayat", "History", 110);
            Column("Snapshot", "Snapshots", 85);
            Column("Kesiapan", "Readiness", 210);
        }
        else if (ResultTabs.SelectedIndex == 1)
        {
            Column("Status", "StatusText", 120);
            Column("RDAP", "RdapHttp", 65);
            Column("DNS", "Dns", 110);
            Column("Waktu cek UTC", "AvailabilityAt", 220);
            Column("Registrar", "Registrar", 170);
            Column("Kedaluwarsa", "Expires", 200);
            Column("Tahap", "Stage", 120);
        }
        else
        {
            Column("Keadaan antrean", "WorkState", 125);
            Column("Tahap terakhir", "Stage", 125);
            Column("Status terakhir", "StatusText", 150);
            Column("Waktu cek terakhir UTC", "AvailabilityAt", 230);
            Column("Pemeriksaan", "JobId", 240);
        }
    }
    private async Task RefreshAsync()
    {
        if (closing || preparing || refreshing)
            return;
        refreshing = true;
        var selected = scope;
        var tab = ResultTabs.SelectedIndex;
        var selectedPage = page;
        var filter = QueryFilter();
        var anchor = eventAnchor;
        var selectedSort = sort;
        var desc = descending;
        var revision = request;
        try
        {
            var work = Task.Run(() => store.ReadConsistent(s =>
            {
                var settings = s.GetSettings();
                return new Snapshot(s.Jobs(), s.Counts(selected), tab == 3 ? null : s.Page(selected, tab, filter, selectedPage, PageSize, selectedSort, desc), tab == 3 ? s.Events(selected, filter.Search, selectedPage, anchor, PageSize) : null, settings, s.Used(settings.Period));
            }));
            refreshWork = work;
            var snapshot = await work;
            if (closing || preparing || revision != request)
                return;
            var total = snapshot.Events?.Total ?? snapshot.Results?.Total ?? 0;
            if (page > 0 && (long)page * PageSize >= total)
            {
                page = total == 0 ? 0 : (int)((total - 1) / PageSize);
                request++;
                return;
            }
            Render(snapshot);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException) { if (!closing) ViewStatus.Text = "Pembacaan data gagal; coba muat ulang. " + ex.GetType().Name; }
        finally
        {
            refreshing = false;
            if (!closing && !preparing && revision != request)
                _ = Dispatcher.BeginInvoke(async () => await RefreshAsync());
        }
    }
    private void Render(Snapshot snapshot)
    {
        updating = true;
        var jobsSignature = JsonData.Write(snapshot.Jobs);
        if (lastJobs != jobsSignature)
        {
            Jobs.ItemsSource = new[] { new Scope(null, "Semua pemeriksaan (progres dan hasil gabungan)") }.Concat(snapshot.Jobs.AsEnumerable().Reverse().Select(j => new Scope(j.Id, j.ToString()))).ToList();
            Jobs.SelectedItem = ((IEnumerable<Scope>)Jobs.ItemsSource).FirstOrDefault(s => s.Id == scope) ?? ((IEnumerable<Scope>)Jobs.ItemsSource).First();
            lastJobs = jobsSignature;
        }
        var c = snapshot.Counts;
        if (initialView && !smokeMode)
        {
            initialView = false;
            InputPanel.IsExpanded = c.Total == 0;
            if (c.Total > 0) Search.Focus(); else DomainInput.Focus();
        }
        var scopedJobs = snapshot.Jobs.Where(j => scope is null || j.Id == scope).ToList();
        var resumable = scopedJobs.Count(j => j.State is "paused" or "queued");
        ResumeBanner.Visibility = !engine.Running && c.Pending > 0 && resumable > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResumeTitle.Text = $"{c.Pending:N0} domain siap dilanjutkan";
        ResumeHint.Text = $"{resumable:N0} pemeriksaan tersimpan · {c.Done:N0} domain selesai tetap disimpan. Tidak perlu impor ulang.";
        ProgressPercent.Text = c.Total == 0 ? "" : $"{100.0 * c.Done / c.Total:F1}%";
        Progress.Maximum = Math.Max(1, c.Total);
        Progress.Value = c.Done;
        ProgressText.Text = $"Total {c.Total:N0} · Selesai {c.Done:N0} · Belum selesai {c.Pending:N0} · Menunggu {Math.Max(0, c.Pending - c.Active):N0} · Diproses {c.Active:N0}";
        var running = snapshot.Jobs.Where(j => j.State == "running").Select(j => j.Id[..8]).ToArray();
        SessionStatus.Text = engine.Failure.Length > 0 ? engine.Failure : $"Aktif: {(running.Length == 0 ? "tidak ada" : string.Join(", ", running))} · Pemeriksaan selesai {snapshot.Jobs.Count(j => j.State == "completed"):N0}, menunggu {snapshot.Jobs.Count(j => j.State == "queued"):N0}, dijeda {snapshot.Jobs.Count(j => j.State == "paused"):N0}";
        ResultSummary.Text = $"Dalam tampilan ini: diperiksa {c.Checked:N0} · Available {c.Available:N0} · Registered {c.Registered:N0} · Riwayat lolos {c.Passed:N0}";
        AvailableTab.Header = $"Available ({c.Available:N0})";
        OtherTab.Header = $"Hasil lainnya ({c.Other:N0})";
        QueueTab.Header = $"Antrean / proses ({c.Pending:N0})";
        EventsTab.Header = $"Log kejadian ({c.Events:N0})";
        var selection = ResultsGrid.SelectedItem as Result;
        var logView = ResultTabs.SelectedIndex == 3;
        ResultsGrid.Visibility = logView ? Visibility.Collapsed : Visibility.Visible;
        LogGrid.Visibility = logView ? Visibility.Visible : Visibility.Collapsed;
        if (logView)
        {
            LogGrid.ItemsSource = snapshot.Events!.Rows;
            rowCount = snapshot.Events.Total;
            var last = snapshot.Events.Rows.FirstOrDefault();
            ViewStatus.Text = last is null ? "Belum ada kejadian pada cakupan ini. Baris antrean tidak ditulis sebagai log." : $"Terbaru di atas · kejadian terakhir dalam halaman: #{last.Sequence:N0}, {last.LocalTime} · baris menunggu bukan log. " + (eventAnchor.HasValue ? "Riwayat dikunci; klik Terbaru untuk mengikuti kejadian baru." : "Mengikuti kejadian baru.");
        }
        else
        {
            ResultsGrid.ItemsSource = snapshot.Results!.Rows;
            ResultsGrid.SelectedItem = snapshot.Results.Rows.FirstOrDefault(r => r.Domain == selection?.Domain && r.JobId == selection?.JobId);
            rowCount = snapshot.Results.Total;
            ViewStatus.Text = ResultTabs.SelectedIndex == 2 ? "Antrean berisi domain belum selesai. Status 'Belum diperiksa' berarti belum ada hasil RDAP/DNS; tahap yang tersimpan bukan bukti proses sedang aktif." : ResultTabs.SelectedIndex == 1 ? "Hasil pemeriksaan tersimpan: registered, redemption, pending delete dan unknown. Waktu cek tersedia per baris." : "Available dari seluruh cakupan / filter; ekspor mencakup semua halaman. Perpindahan halaman tidak membatasi jumlah domain yang diperiksa.";
        }
        EmptyState.Visibility = rowCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Text = (filters.Active || Search.Text.Length > 0) ? "Tidak ada baris yang cocok dengan filter." : logView ? "Belum ada kejadian. Antrean tidak dimasukkan ke log." : ResultTabs.SelectedIndex == 2 ? "Tidak ada domain yang belum selesai." : "Belum ada hasil yang cocok.";
        PageText.Text = rowCount == 0 ? "0 baris" : $"{(long)page * PageSize + 1:N0}–{Math.Min(rowCount, ((long)page + 1) * PageSize):N0} dari {rowCount:N0} · halaman {page + 1:N0}";
        PreviousButton.IsEnabled = page > 0;
        NextButton.IsEnabled = ((long)page + 1) * PageSize < rowCount;
        ExportButton.IsEnabled = !preparing && !logView && ResultTabs.SelectedIndex == 0 && rowCount > 0;
        EvidenceButton.IsEnabled = !logView && ResultsGrid.SelectedItem is Result;
        PauseButton.IsEnabled = engine.Running;
        ResumeButton.IsEnabled = !preparing && !engine.Running && snapshot.Jobs.Any(j => (scope is null || j.Id == scope) && j.State is "paused" or "queued");
        RetryButton.IsEnabled = !preparing && !engine.Running && c.Total > 0;
        SettingsButton.IsEnabled = !preparing && !engine.Running;
        LatestButton.Visibility = logView ? Visibility.Visible : Visibility.Collapsed;
        FilterButton.IsEnabled = !logView;
        var settings = snapshot.Settings ?? new Settings();
        QuotaText.Text = $"Moz lokal {snapshot.Used}/{settings.MozBudget} · cadangan {settings.MozReserve} · RDAP/DNS ×{settings.AvailabilityWorkers}";
        StartButton.Content = engine.Running ? "Antrekan daftar baru" : "Mulai pemeriksaan";
        updating = false;
    }
    private async void JobsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (smokeMode || updating || !IsLoaded)
            return;
        scope = (Jobs.SelectedItem as Scope)?.Id;
        ResetPage();
        await RefreshAsync();
    }
    private void ResetPage()
    {
        page = 0;
        eventAnchor = null;
        request++;
    }
    private async void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (smokeMode || !IsLoaded || updating)
            return;
        ResetPage();
        await RefreshAsync();
    }
    private async void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (smokeMode || e.Source != ResultTabs || !IsLoaded || updating)
            return;
        ResetPage();
        sort = "";
        descending = false;
        ConfigureColumns();
        await RefreshAsync();
    }
    private async void SortChanged(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        sort = e.Column.SortMemberPath;
        descending = e.Column.SortDirection != ListSortDirection.Descending && e.Column.SortDirection == ListSortDirection.Ascending;
        foreach (var c in ResultsGrid.Columns)
            c.SortDirection = null;
        e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        ResetPage();
        await RefreshAsync();
    }
    private async void PreviousClick(object sender, RoutedEventArgs e)
    {
        if (page > 0)
        {
            page--;
            request++;
            await RefreshAsync();
        }
    }
    private async void NextClick(object sender, RoutedEventArgs e)
    {
        if (((long)page + 1) * PageSize >= rowCount)
            return;
        if (ResultTabs.SelectedIndex == 3 && eventAnchor is null && LogGrid.ItemsSource is List<EventRow> rows)
            eventAnchor = rows.FirstOrDefault()?.Sequence;
        page++;
        request++;
        await RefreshAsync();
    }
    private async void LatestClick(object sender, RoutedEventArgs e)
    {
        ResetPage();
        await RefreshAsync();
    }
    private void SetPreparing(bool value, string? message = null)
    {
        preparing = value;
        StartButton.IsEnabled = ImportButton.IsEnabled = DomainInput.IsEnabled = NewInputButton.IsEnabled = GenerateButton.IsEnabled = !value;
        ResumeButton.IsEnabled = RetryButton.IsEnabled = SettingsButton.IsEnabled = !value && !engine.Running;
        ExportButton.IsEnabled = !value && ResultTabs.SelectedIndex == 0 && rowCount > 0;
        if (message is not null)
            OperationStatus.Text = InputFeedback.Text = message;
    }
    private void InputChanged(object sender, TextChangedEventArgs e)
    {
        if (!updating)
            preparedImport = null;
    }
    private void ClearInputClick(object sender, RoutedEventArgs e)
    {
        if (preparing)
            return;
        preparedImport = null;
        DomainInput.IsReadOnly = false;
        DomainInput.Clear();
        InputPanel.IsExpanded = true;
        InputPanel.Header = "Input domain";
        InputLabel.Text = "Tempel domain, atau impor file. Satu daftar menjadi satu pemeriksaan.";
        InputFeedback.Text = "Belum ada daftar input.";
        DomainInput.Focus();
    }
    private void ApplyImport(ImportResult parsed)
    {
        if (parsed.Domains.Count == 0)
            throw new ArgumentException($"Tidak ada domain valid ({parsed.Invalid.Count:N0} tidak valid). Daftar sebelumnya dipertahankan.");
        updating = true;
        preparedImport = parsed;
        DomainInput.Text = string.Join('\n', parsed.Domains.Take(200));
        DomainInput.IsReadOnly = true;
        InputLabel.Text = $"Pratinjau {Math.Min(200, parsed.Domains.Count):N0} dari {parsed.Domains.Count:N0} domain; seluruh daftar akan diperiksa. Pilih Input baru untuk mengganti.";
        InputFeedback.Text = $"{parsed.Domains.Count:N0} valid · {parsed.Duplicates:N0} duplikat dibuang · {parsed.Invalid.Count:N0} tidak valid · satu pemeriksaan, tanpa pecah per 1.000.";
        InputPanel.Header = $"Input: {parsed.Domains.Count:N0} domain siap";
        InputPanel.IsExpanded = true;
        updating = false;
    }
    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        if (preparing)
            return;
        var dialog = new OpenFileDialog { Filter = "Daftar domain (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|Semua file|*.*" };
        if (dialog.ShowDialog(this) != true)
            return;
        SetPreparing(true, "Membaca dan memisahkan domain…");
        try
        {
            var work = Task.Run(() => Domains.Parse(File.ReadAllText(dialog.FileName)));
            inputWork = work;
            var parsed = await work;
            if (!closing)
                ApplyImport(parsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { if (!closing) InputFeedback.Text = ex is ArgumentException ? ex.Message : "File tidak dapat dibaca; daftar sebelumnya dipertahankan."; }
        finally { if (!closing) SetPreparing(false); }
    }
    private async void StartClick(object sender, RoutedEventArgs e)
    {
        if (preparing)
            return;
        var cached = preparedImport;
        var text = DomainInput.Text;
        SetPreparing(true, "Menyimpan seluruh daftar pemeriksaan…");
        try
        {
            var work = Task.Run(() => { var parsed = cached ?? Domains.Parse(text); if (parsed.Domains.Count == 0) throw new ArgumentException("Masukkan minimal satu domain valid."); return (Job: store.NewJob(parsed.Domains, inputCancel.Token), Count: parsed.Domains.Count); });
            inputWork = work;
            var created = await work;
            if (closing)
                return;
            scope = created.Job;
            lastJobs = "";
            ResetPage();
            OperationStatus.Text = InputFeedback.Text = $"{created.Count:N0} domain disimpan; pemeriksaan dimulai.";
            InputPanel.IsExpanded = false;
            engine.Start();
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex) when (ex is ArgumentException or Microsoft.Data.Sqlite.SqliteException or IOException) { if (!closing) OperationStatus.Text = InputFeedback.Text = ex is ArgumentException ? ex.Message : "Daftar belum dapat disimpan. Tidak ada pemeriksaan parsial dibuat."; }
        finally { if (!closing) { SetPreparing(false); await RefreshAsync(); } }
    }
    private async void GenerateClick(object sender, RoutedEventArgs e)
    {
        if (preparing)
            return;
        var keywords = Keywords.Text;
        var tlds = Tlds.Text;
        SetPreparing(true, "Membuat daftar…");
        try
        {
            var work = Task.Run(() => Domains.Parse(string.Join('\n', Domains.Generate(keywords, tlds))));
            inputWork = work;
            var parsed = await work;
            if (!closing)
                ApplyImport(parsed);
        }
        catch (ArgumentException ex) { if (!closing) InputFeedback.Text = ex.Message; }
        finally { if (!closing) SetPreparing(false); }
    }
    private void PauseClick(object sender, RoutedEventArgs e)
    {
        engine.Pause();
        SessionStatus.Text = "Menjeda; menunggu worker berhenti dan menyimpan hasil…";
    }
    private async void ResumeClick(object sender, RoutedEventArgs e)
    {
        if (engine.Running || preparing)
            return;
        var selected = scope;
        SetPreparing(true, "Melanjutkan tahap yang tersimpan…");
        try
        {
            inputWork = Task.Run(() => store.Resume(selected));
            await inputWork;
            if (!closing)
            {
                InputPanel.IsExpanded = false;
                engine.Start(selected);
                OperationStatus.Text = "Pemeriksaan dilanjutkan dari tahap tersimpan. Domain selesai tidak diulang.";
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { if (!closing) OperationStatus.Text = "Belum dapat melanjutkan. Data tetap tersimpan; coba lagi."; }
        finally { if (!closing) { SetPreparing(false); request++; await RefreshAsync(); } }
    }
    private async void RetryClick(object sender, RoutedEventArgs e)
    {
        if (engine.Running || preparing)
            return;
        SetPreparing(true, "Menyiapkan sumber gagal untuk dicoba ulang…");
        try
        {
            var selected = scope;
            var work = Task.Run(() => store.RetryFailed(selected));
            inputWork = work;
            var changed = await work;
            if (!closing)
            {
                if (changed > 0) engine.Start(selected);
                OperationStatus.Text = changed > 0 ? $"{changed:N0} domain dijadwalkan ulang mulai dari sumber yang perlu diperiksa. Moz yang mungkin ditagih tidak dikirim ulang." : "Tidak ada sumber gagal yang bisa diulang otomatis pada cakupan ini. Gunakan Lanjutkan untuk pekerjaan yang dijeda.";
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { if (!closing) OperationStatus.Text = "Penjadwalan ulang gagal. Data tetap tersimpan; coba lagi."; }
        finally { if (!closing) { SetPreparing(false); request++; await RefreshAsync(); } }
    }
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (preparing || ResultTabs.SelectedIndex != 0 || rowCount == 0)
            return;
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "MozChecker-available.csv" };
        if (dialog.ShowDialog(this) != true)
            return;
        var selected = scope;
        var filter = QueryFilter();
        var selectedSort = sort;
        var desc = descending;
        SetPreparing(true, "Mengekspor seluruh hasil sesuai filter, semua halaman…");
        try
        {
            var work = Task.Run(() => CsvExport.Save(dialog.FileName, store.ExportAvailable(selected, filter, selectedSort, desc)));
            inputWork = work;
            await work;
            if (!closing)
                OperationStatus.Text = InputFeedback.Text = "Ekspor seluruh hasil sesuai filter selesai.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { if (!closing) MessageBox.Show(this, "CSV belum dapat disimpan. Periksa folder tujuan.", "Ekspor"); }
        finally { if (!closing) { SetPreparing(false); await RefreshAsync(); } }
    }
    private void EvidenceClick(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is Result r)
            new EvidenceWindow(r) { Owner = this }.ShowDialog();
    }
    private void GridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            EvidenceClick(sender, new());
            e.Handled = true;
        }
    }
    private async void FilterClick(object sender, RoutedEventArgs e)
    {
        var dialog = new FilterWindow(filters) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            filters = dialog.Values;
            FilterButton.Content = filters.Active ? "Filter aktif…" : "Filter…";
            ResetPage();
            await RefreshAsync();
        }
    }
    private void ResetFilters()
    {
        updating = true;
        filters = new();
        Search.Clear();
        FilterButton.Content = "Filter…";
        updating = false;
        ResetPage();
    }
    private async void ResetClick(object sender, RoutedEventArgs e)
    {
        ResetFilters();
        await RefreshAsync();
    }
    private async void SettingsClick(object sender, RoutedEventArgs e)
    {
        if (engine.Running || preparing)
        {
            MessageBox.Show(this, "Jeda pemeriksaan sebelum mengubah pengaturan.", "Pengaturan");
            return;
        }
        if (new SettingsWindow(store) { Owner = this }.ShowDialog() == true)
        {
            try
            {
                engine = NewEngine(CredentialVault.Load());
                await RefreshAsync();
            }
            catch (System.ComponentModel.Win32Exception) { MessageBox.Show(this, "Credential Manager belum dapat dibaca."); }
        }
    }
    public async Task StopAsync()
    {
        timer.Stop();
        engine.Pause();
        inputCancel.Cancel();
        if (inputWork is { } input)
        {
            try
            {
                await input;
            }
            catch { }
        }
        if (engine.Completion is { } task)
        {
            try { await task; }
            catch (Exception) { /* Closing still releases resources after a worker/database failure. */ }
        }
        if (refreshWork is { } refresh)
        {
            try
            {
                await refresh;
            }
            catch { }
        }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closedAfterStop)
            return;
        e.Cancel = true;
        if (closing)
            return;
        closing = true;
        IsEnabled = false;
        await StopAsync();
        transport.Dispose();
        inputCancel.Dispose();
        closedAfterStop = true;
        _ = Dispatcher.BeginInvoke(Close);
    }
    internal string? ScreenshotDirectory { get; set; }
    private void CaptureSmoke(string name)
    {
        if (ScreenshotDirectory is null) return;
        UpdateLayout();
        if (ResultsGrid.ActualHeight < 100 || ResumeButton.ActualWidth < 100)
            throw new InvalidOperationException("Resume workspace layout clipped");
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(this);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(ScreenshotDirectory, name + ".png"));
        encoder.Save(output);
    }
    internal void VerifySmoke()
    {
        timer.Stop();
        if (Domains.Normalize("https://www.bücher.de/path") != "xn--bcher-kva.de" || ResultsGrid.Columns.Count != 8)
            throw new InvalidOperationException($"Empty workspace failed: columns={ResultsGrid.Columns.Count}");
        ApplyImport(Domains.Parse("score;Domain Name\n90;https://www.google.com/path\n80;GOOGLE.com\n70;co.id"));
        if (preparedImport?.Domains.Single() != "google.com" || !InputFeedback.Text.Contains("1 duplikat"))
            throw new InvalidOperationException("Immediate import failed");
        ApplyImport(Domains.Parse(string.Join('\n', Enumerable.Range(0, 2501).Select(i => $"import{i}.com"))));
        if (preparedImport?.Domains.Count != 2501 || DomainInput.LineCount > 200 || !InputLabel.Text.Contains("2,501") && !InputLabel.Text.Contains("2.501"))
            throw new InvalidOperationException("Large-list preview failed");
        var job = store.NewJob(preparedImport.Domains);
        if (store.Counts(job).Total != 2501)
            throw new InvalidOperationException("Single full-list job failed");
        store.State(job, "running");
        store.Save(job, new Result { Domain = "import0.com", Status = "registered", Stage = "done", AvailabilityAt = JsonData.Now() });
        store.Save(job, new Result { Domain = "import1.com", Status = "available", Stage = "done", AvailabilityAt = JsonData.Now(), History = "valid" });
        scope = null;
        ResultTabs.SelectedIndex = 0;
        ConfigureColumns();
        Render(new(store.Jobs(), store.Counts(null), store.Page(null, 0, new()), null));
        if (((List<Result>)ResultsGrid.ItemsSource).Single().Domain != "import1.com" || !ExportButton.IsEnabled || !ProgressText.Text.Contains("2,501") && !ProgressText.Text.Contains("2.501"))
            throw new InvalidOperationException("Aggregate available results failed");
        Search.Text = "absent";
        Render(new(store.Jobs(), store.Counts(null), store.Page(null, 0, QueryFilter()), null));
        if (ExportButton.IsEnabled)
            throw new InvalidOperationException("Search/export failed");
        ResetFilters();
        ResultTabs.SelectedIndex = 3;
        var events = store.Events(null);
        Render(new(store.Jobs(), store.Counts(null), null, events));
        if (events.Rows.Any(r => r.Stage == "queued") || !events.Rows.Any(r => r.Status == "registered") || ExportButton.IsEnabled)
            throw new InvalidOperationException("Timestamped event log failed");
        store.State(job, "paused");
        ResultTabs.SelectedIndex = 0;
        ConfigureColumns();
        Render(new(store.Jobs(), store.Counts(null), store.Page(null, 0, new()), null));
        if (!ResumeButton.IsEnabled || ResumeBanner.Visibility != Visibility.Visible || !ResumeTitle.Text.Contains("2,499") && !ResumeTitle.Text.Contains("2.499"))
            throw new InvalidOperationException("Resume from stored work failed");
        // Capture a separate populated resume workspace before the empty-state screenshot.
        InputPanel.IsExpanded = false;
        CaptureSmoke("resume");
        var oldWidth = Width;
        var oldHeight = Height;
        Width = 1040; Height = 700;
        CaptureSmoke("resume-compact");
        Width = oldWidth; Height = oldHeight;
        // Only synthetic fixtures in this temporary database are used in screenshots.
        scope = "empty";
        ResultTabs.SelectedIndex = 0;
        ConfigureColumns();
        ClearInputClick(this, new());
        InputPanel.Header = "Input domain";
        lastJobs = "";
        Render(new([], new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), new([], 0), null));
    }
}

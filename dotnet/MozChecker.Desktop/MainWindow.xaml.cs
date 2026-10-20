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
    private readonly Store store;
    private readonly Transport transport = new();
    private Engine engine;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private List<Result> results = [];
    private string signature = "";
    private string jobsSignature = "";
    private bool updating;
    private bool closing;
    private bool closedAfterStop;
    private FilterValues filters = new();
    private string? CurrentJob => (Jobs.SelectedItem as Job)?.Id;
    private ICollectionView? view;
    public MainWindow(Store store, ApiKeys keys, bool smoke = false)
    {
        this.store = store; engine = NewEngine(keys); InitializeComponent();
        timer.Tick += (_, _) => Refresh(); timer.Start();
        ResultsGrid.SelectionChanged += (_, _) => EvidenceButton.IsEnabled = ResultsGrid.SelectedItem is Result;
        CommandBindings.Add(new(ApplicationCommands.Find, (_, _) => Search.Focus()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        AddShortcut(Key.Enter, () => StartClick(this, new())); AddShortcut(Key.O, () => ImportClick(this, new())); AddShortcut(Key.OemComma, () => SettingsClick(this, new()));
        Closing += OnClosing; Refresh();
        if (!smoke) DomainInput.Focus();
    }
    private Engine NewEngine(ApiKeys keys) => new(store, keys, new Providers(store, keys, transport));
    private void AddShortcut(Key key, Action action)
    {
        var command = new RoutedCommand(); CommandBindings.Add(new(command, (_, _) => action())); InputBindings.Add(new KeyBinding(command, key, ModifierKeys.Control));
    }
    private void Refresh(bool force = false)
    {
        if (updating || closing) return;
        var selected = CurrentJob;
        var jobs = store.Jobs(); var jobSig = JsonData.Write(jobs);
        if (jobSig != jobsSignature)
        {
            updating = true; Jobs.ItemsSource = jobs.AsEnumerable().Reverse().ToList(); Jobs.SelectedItem = jobs.FirstOrDefault(x => x.Id == selected) ?? jobs.LastOrDefault(); updating = false; jobsSignature = jobSig;
        }
        var all = CurrentJob is { } id ? store.Results(id) : [];
        var nextSignature = JsonData.Write(all) + string.Join('|', all.Select(x => x.Readiness));
        if (force || nextSignature != signature)
        {
            var selection = (ResultsGrid.SelectedItem as Result)?.Domain;
            results = all; signature = nextSignature;
            // A private view keeps sorting/filter state separate from the log and job list.
            var sort = view?.SortDescriptions.ToArray() ?? [];
            view = new ListCollectionView(results.Where(x => x.Status == "available").ToList()); view.Filter = Matches;
            foreach (var s in sort) view.SortDescriptions.Add(s);
            ResultsGrid.ItemsSource = view; ResultsGrid.SelectedItem = results.FirstOrDefault(x => x.Domain == selection && x.Status == "available");
            Log.Text = results.Count == 0 ? "Belum ada pemeriksaan." : string.Join('\n', results.Select(r => $"{r.Domain} · {r.Status} · {r.Stage}" + (r.Errors.Count == 0 ? "" : "\n  " + string.Join(" | ", r.Errors.Select(p => p.Key + ": " + p.Value)))));
        }
        AvailableStat.Text = results.Count(x => x.Status == "available").ToString(); CheckedStat.Text = results.Count(x => x.AvailabilityAt.Length > 0).ToString(); HistoryStat.Text = results.Count(x => x.Status == "available" && x.History is "valid" or "no_history").ToString(); RegisteredStat.Text = results.Count(x => x.Status == "registered").ToString();
        var completed = results.Count(x => x.Stage == "done"); Progress.Maximum = Math.Max(1, results.Count); Progress.Value = completed;
        ProgressText.Text = engine.Failure.Length > 0 ? engine.Failure : results.Count > 0 ? $"{completed} / {results.Count} domain selesai · {((Job?)Jobs.SelectedItem)?.State} · RDAP → DNS → OPR → Wayback → Moz" : "Workspace siap. Masukkan daftar domain untuk mulai.";
        var settings = store.GetSettings(); var used = store.Used(settings.Period);
        QuotaText.Text = $"{used} / {settings.MozBudget} rows terpakai\nCadangan {settings.MozReserve} · tersedia {Math.Max(0, settings.MozBudget - settings.MozReserve - used)}\nPeriode {settings.Period}";
        StartButton.Content = engine.Running ? "Tambahkan ke antrean" : "Mulai hunting";
        PauseButton.IsEnabled = engine.Running; ResumeButton.IsEnabled = !engine.Running && Jobs.SelectedItem is Job { State: "paused" or "queued" }; RetryButton.IsEnabled = !engine.Running && results.Any(x => x.Errors.Count > 0 || x.History == "unchecked" || x.Status == "unknown");
        EvidenceButton.IsEnabled = ResultsGrid.SelectedItem is Result;
        UpdateEmpty();
    }
    private bool Matches(object item) => item is Result r && r.Domain.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase) && filters.Matches(r);
    private void UpdateEmpty()
    {
        if (ResultsGrid is null || ExportButton is null) return;
        var count = view?.Cast<Result>().Count() ?? 0;
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = results.Any(x => x.Status == "available") ? "Tidak ada yang cocok" : "Shortlist masih kosong";
        EmptyDescription.Text = results.Any(x => x.Status == "available") ? "Ubah pencarian atau reset filter." : results.Count > 0 ? "Registered dan hasil lainnya ada di log pemeriksaan." : "Mulai hunting untuk menemukan domain available.";
        ExportButton.IsEnabled = ResultTabs.SelectedIndex == 0 && count > 0;
    }
    private void JobsChanged(object sender, SelectionChangedEventArgs e) { if (!updating && IsLoaded) { signature = ""; Refresh(true); } }
    private void SearchChanged(object sender, TextChangedEventArgs e) { view?.Refresh(); UpdateEmpty(); }
    private void TabChanged(object sender, SelectionChangedEventArgs e) { if (e.Source == ResultTabs) UpdateEmpty(); }
    private void StartClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var parsed = Domains.Parse(DomainInput.Text); InputFeedback.Text = $"{parsed.Domains.Count} unik · {parsed.Duplicates} duplikat · {parsed.Invalid.Count} tidak valid";
            if (parsed.Domains.Count == 0) throw new ArgumentException("Masukkan minimal satu domain yang valid.");
            var job = store.NewJob(parsed.Domains); Refresh(); Jobs.SelectedItem = ((IEnumerable<Job>)Jobs.ItemsSource).First(x => x.Id == job); Refresh(true); engine.Start();
        }
        catch (ArgumentException ex) { InputFeedback.Text = ex.Message; DomainInput.Focus(); }
    }
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Daftar domain (*.csv;*.txt)|*.csv;*.txt|Semua file|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try { if (new FileInfo(dialog.FileName).Length > 5_000_000) throw new ArgumentException("File maksimal 5 MB."); DomainInput.Text = File.ReadAllText(dialog.FileName); InputFeedback.Text = "Daftar diimpor. Tekan Mulai hunting untuk memeriksa."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { InputFeedback.Text = ex is ArgumentException ? ex.Message : "File tidak dapat dibaca."; }
    }
    private void GenerateClick(object sender, RoutedEventArgs e)
    {
        try { var parsed = Domains.Parse(string.Join('\n', Domains.Generate(Keywords.Text, Tlds.Text))); DomainInput.Text = string.Join('\n', parsed.Domains); InputFeedback.Text = $"{parsed.Domains.Count} domain dibuat · {parsed.Invalid.Count} kombinasi tidak valid"; }
        catch (ArgumentException ex) { InputFeedback.Text = ex.Message; }
    }
    private void PauseClick(object sender, RoutedEventArgs e) { engine.Pause(); ProgressText.Text = "Menjeda dan menyimpan progres…"; }
    private void ResumeClick(object sender, RoutedEventArgs e) { if (CurrentJob is { } id && !engine.Running) { store.State(id, "queued"); engine.Start(); Refresh(); } }
    private void RetryClick(object sender, RoutedEventArgs e) { if (CurrentJob is { } id && !engine.Running) { store.RetryFailed(id); engine.Start(); Refresh(); } }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var rows = view?.Cast<Result>().ToArray() ?? []; if (ResultTabs.SelectedIndex != 0 || rows.Length == 0) return;
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "MozChecker-available.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { CsvExport.Save(dialog.FileName, rows); ProgressText.Text = $"{rows.Length} domain diekspor."; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "CSV belum dapat disimpan. Periksa folder dan tutup file yang sedang digunakan.", "Ekspor"); }
    }
    private void EvidenceClick(object sender, RoutedEventArgs e) { if (ResultsGrid.SelectedItem is Result result) new EvidenceWindow(result) { Owner = this }.ShowDialog(); }
    private void GridKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { EvidenceClick(sender, new()); e.Handled = true; } }
    private void FilterClick(object sender, RoutedEventArgs e)
    {
        var dialog = new FilterWindow(filters) { Owner = this };
        if (dialog.ShowDialog() == true) { filters = dialog.Values; FilterButton.Content = filters.Active ? "Filter aktif" : "Filter"; view?.Refresh(); UpdateEmpty(); }
    }
    private void ResetClick(object sender, RoutedEventArgs e) { filters = new(); FilterButton.Content = "Filter"; Search.Clear(); view?.Refresh(); UpdateEmpty(); }
    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        if (engine.Running) { MessageBox.Show(this, "Jeda hunting sebelum mengubah kunci atau aturan.", "Pengaturan"); return; }
        var dialog = new SettingsWindow(store) { Owner = this };
        if (dialog.ShowDialog() == true) { try { engine = NewEngine(CredentialVault.Load()); Refresh(); } catch (System.ComponentModel.Win32Exception) { MessageBox.Show(this, "Credential Manager belum dapat dibaca.", "Pengaturan"); } }
    }
    public async Task StopAsync() { timer.Stop(); engine.Pause(); if (engine.Completion is { } task) await task; }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closedAfterStop) return;
        e.Cancel = true; if (closing) return; closing = true; IsEnabled = false;
        await StopAsync(); transport.Dispose(); closedAfterStop = true; Close();
    }
    internal void VerifySmoke()
    {
        if (Domains.Normalize("https://www.bücher.de/path") != "xn--bcher-kva.de" || ResultsGrid.Columns.Count != 8 || ExportButton.IsEnabled) throw new InvalidOperationException("Empty workspace smoke failed");
        var job = store.NewJob(["google.com", "sample-example.com"]);
        var registered = new Result { Domain = "google.com", Status = "registered", Stage = "done", AvailabilityAt = JsonData.Now() };
        var available = new Result { Domain = "sample-example.com", Status = "available", Stage = "done", AvailabilityAt = JsonData.Now(), History = "no_history" };
        store.Save(job, registered); store.Save(job, available); Refresh(); Jobs.SelectedItem = ((IEnumerable<Job>)Jobs.ItemsSource).Single(x => x.Id == job); Refresh(true);
        if (view?.Cast<Result>().Single().Domain != available.Domain || !Log.Text.Contains("google.com · registered") || !ExportButton.IsEnabled) throw new InvalidOperationException("Available-only smoke failed");
        Search.Text = "absent"; if (view!.Cast<Result>().Any() || ExportButton.IsEnabled) throw new InvalidOperationException("Search smoke failed");
        ResetClick(this, new()); ResultTabs.SelectedIndex = 1; if (ExportButton.IsEnabled) throw new InvalidOperationException("Log export smoke failed");
        ResultTabs.SelectedIndex = 0;
        // Screenshots show the unpopulated workspace; test fixtures live only in this temporary DB.
        ResultsGrid.ItemsSource = null; results = []; view = null; Jobs.ItemsSource = null; Log.Text = "Belum ada pemeriksaan."; AvailableStat.Text = CheckedStat.Text = HistoryStat.Text = RegisteredStat.Text = "0"; Progress.Value = 0; ProgressText.Text = "Workspace siap. Masukkan daftar domain untuk mulai."; UpdateEmpty();
    }
}

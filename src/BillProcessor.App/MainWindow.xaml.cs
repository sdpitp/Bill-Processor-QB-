using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BillProcessor.Core.Abstractions;
using BillProcessor.Core.Models;
using BillProcessor.Core.Services;
using BillProcessor.Infrastructure.Import;
using BillProcessor.Infrastructure.Logging;
using BillProcessor.Infrastructure.Persistence;
using BillProcessor.Infrastructure.QuickBooks;
using Microsoft.Win32;

namespace BillProcessor.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<BillRecord> _bills = [];
    private readonly BillWorkflowEngine _workflowEngine = new();
    private readonly CsvBillImporter _csvImporter = new();
    private readonly SecureFileBillRepository _repository = new();
    private readonly SafeAuditLogger _auditLogger = new();
    private IQuickBooksGateway _quickBooksGateway = null!;
    private QuickBooksPostingCoordinator _quickBooksCoordinator = null!;
    private bool _suppressModeChangeMessage;

    public MainWindow()
    {

        InitializeComponent();
        BillsGrid.ItemsSource = _bills;
        _suppressModeChangeMessage = true;
        ConfigureQuickBooksGateway();
        _suppressModeChangeMessage = false;
        Loaded += MainWindowLoaded;
    }

    private async void MainWindowLoaded(object sender, RoutedEventArgs e)
    {
        await ReloadFromDiskAsync();
    }

    private async void ImportCsvClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                Title = "Import Vendor Bills"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var imported = await _csvImporter.ImportAsync(dialog.FileName);
            foreach (var bill in imported)
            {
                _bills.Add(bill);
            }

            UpdateStatus($"Imported {imported.Count} bill(s) from {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception exception)
        {
            ShowError("CSV import failed.", exception);
        }
    }

    private void AddBlankBillClick(object sender, RoutedEventArgs e)
    {
        var bill = new BillRecord
        {
            Status = BillProcessingStatus.Imported,
            ExpenseAccountName = "Uncategorized Expense"
        };
        bill.AddAudit("created", "Created manually in UI.");
        _bills.Add(bill);
        UpdateStatus($"Added blank bill. Current grid count: {_bills.Count}.");
    }

    private async void NormalizeAndValidateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidates = _bills.Where(bill =>
                bill.Status is BillProcessingStatus.Imported
                    or BillProcessingStatus.Normalized
                    or BillProcessingStatus.NeedsReview
                    or BillProcessingStatus.ReadyToPost
                    or BillProcessingStatus.PostingFailed);

            foreach (var bill in candidates)
            {
                _workflowEngine.Process(bill);
                await _auditLogger.LogProcessAsync(bill);
            }

            BillsGrid.Items.Refresh();
            var readyToPostCount = _bills.Count(bill => bill.Status == BillProcessingStatus.ReadyToPost);
            var needsReviewCount = _bills.Count(bill => bill.Status == BillProcessingStatus.NeedsReview);

            UpdateStatus(
                $"Processed {_bills.Count} bill(s): {readyToPostCount} ready to post, {needsReviewCount} need review.");
        }
        catch (Exception exception)
        {
            ShowError("Normalize/validate failed.", exception);
        }
    }

    private async void QueueToQuickBooksClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var sessionContext = BuildQuickBooksSessionContext();
            var summary = await _quickBooksCoordinator.QueueBillsAsync(_bills, sessionContext);
            await _repository.SaveAsync(_bills);
            BillsGrid.Items.Refresh();

            UpdateStatus(
                $"QB queue complete: eligible {summary.EligibleCount}, queued {summary.QueuedCount}, duplicates {summary.DuplicateCount}, skipped {summary.SkippedCount}.");
        }
        catch (Exception exception)
        {
            ShowError("Queue to QuickBooks failed.", exception);
        }
    }

    private async void VerifyQuickBooksResultsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var summary = await _quickBooksCoordinator.ApplyVerificationResultsAsync(_bills);
            await _repository.SaveAsync(_bills);
            BillsGrid.Items.Refresh();

            UpdateStatus(
                $"QB verify complete: read {summary.TotalResultsRead}, posted {summary.PostedCount}, failed {summary.FailedCount}, retry {summary.RecoverableFailuresScheduledForRetry}, unmatched {summary.UnmatchedResults}.");
        }
        catch (Exception exception)
        {
            ShowError("Verify QuickBooks results failed.", exception);
        }
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _repository.SaveAsync(_bills);
            UpdateStatus($"Saved {_bills.Count} bill(s) to encrypted storage.");
        }
        catch (Exception exception)
        {
            ShowError("Save failed.", exception);
        }
    }

    private async void ReloadClick(object sender, RoutedEventArgs e)
    {
        await ReloadFromDiskAsync();
    }

    private void ClearGridClick(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            "Clear all rows from the grid? Unsaved edits will be lost.",
            "Confirm",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _bills.Clear();
        UpdateStatus("Cleared grid.");
    }

    private async Task ReloadFromDiskAsync()
    {
        try
        {
            var loadedBills = await _repository.LoadAsync();
            _bills.Clear();
            foreach (var bill in loadedBills)
            {
                _bills.Add(bill);
            }

            UpdateStatus(
                loadedBills.Count == 0
                    ? "No saved bills found yet."
                    : $"Loaded {loadedBills.Count} bill(s) from encrypted storage.");
        }
        catch (Exception exception)
        {
            ShowError("Reload failed.", exception);
        }
    }

    private void UpdateStatus(string message)
    {
        StatusTextBlock.Text = $"{DateTime.Now:HH:mm:ss} - {message}";
    }

    private void ShowError(string title, Exception exception)
    {
        UpdateStatus($"{title} {exception.Message}");
        MessageBox.Show(
            $"{title}\n\n{exception.Message}",
            "Vendor Bill Processor",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private QuickBooksSessionContext BuildQuickBooksSessionContext()
    {
        return new QuickBooksSessionContext
        {
            IsPostingAuthorizedForSession = AuthorizePostingCheckBox.IsChecked == true,
            AccessIntent = QuickBooksAccessIntent.PostBills,
            CompanyFileIdentifier = CompanyFileTextBox.Text?.Trim() ?? string.Empty,
            RequestedBy = Environment.UserName
        };
    }

    private void QuickBooksModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ConfigureQuickBooksGateway();
    }

    private void ConfigureQuickBooksGateway()
    {
        var mode = GetSelectedTransportMode();
        _quickBooksGateway = QuickBooksGatewayFactory.Create(mode);
        _quickBooksCoordinator = new QuickBooksPostingCoordinator(_quickBooksGateway);

        var modeLabel = mode == QuickBooksTransportMode.DirectDesktopSdk
            ? "Direct Desktop SDK"
            : "File Drop Bridge";
        QuickBooksPathsTextBlock.Text =
            $"{modeLabel} | Outbox: {_quickBooksCoordinator.GetOutboxPath()}  Inbox: {_quickBooksCoordinator.GetInboxPath()}";

        if (!_suppressModeChangeMessage)
        {
            UpdateStatus($"QuickBooks transport switched to {modeLabel}.");
        }
    }

    private QuickBooksTransportMode GetSelectedTransportMode()
    {
        if (QuickBooksModeComboBox.SelectedItem is ComboBoxItem selected &&
            selected.Tag is string modeTag &&
            Enum.TryParse<QuickBooksTransportMode>(modeTag, out var parsedMode))
        {
            return parsedMode;
        }

        return QuickBooksTransportMode.FileDropBridge;
    }
}

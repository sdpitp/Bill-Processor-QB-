using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BillProcessor.Core.Abstractions;
using BillProcessor.Core.Models;
using BillProcessor.Core.Services;
using BillProcessor.Infrastructure.Persistence;
using BillProcessor.Infrastructure.QuickBooks;

namespace BillProcessor.App;

public partial class MainWindow : Window
{
    private const int DueSoonWindowDays = 3;
    private readonly ObservableCollection<BillRecord> _bills = [];
    private readonly BillPayPlanner _billPayPlanner = new();
    private readonly SecureFileBillRepository _repository = new();
    private IQuickBooksGateway _quickBooksGateway = null!;
    private QuickBooksPostingCoordinator _quickBooksCoordinator = null!;
    private ICollectionView _billsView = null!;
    private bool _suppressModeChangeMessage;
    private BillDueBucket? _activeDueFilter;
    private decimal _operatingAccountBalance;

    public MainWindow()
    {
        InitializeComponent();

        _billsView = CollectionViewSource.GetDefaultView(_bills);
        _billsView.Filter = BillPassesActiveFilter;
        _billsView.SortDescriptions.Add(new SortDescription(nameof(BillRecord.DueDate), ListSortDirection.Ascending));
        BillsGrid.ItemsSource = _billsView;

        _suppressModeChangeMessage = true;
        ConfigureQuickBooksGateway();
        _suppressModeChangeMessage = false;

        Loaded += MainWindowLoaded;
    }

    private async void MainWindowLoaded(object sender, RoutedEventArgs e)
    {
        await ReloadFromDiskAsync();
    }

    private async void SyncBillPaySnapshotClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var request = new QuickBooksBillPaySyncRequest
            {
                CompanyFileIdentifier = CompanyFileTextBox.Text?.Trim() ?? string.Empty,
                OperatingAccountName = OperatingAccountTextBox.Text?.Trim() ?? "Operating Account",
                DueSoonDays = DueSoonWindowDays,
                MaxBillsToReturn = 1000,
                AsOfDate = DateTime.Today
            };

            var snapshot = await _quickBooksGateway.GetBillPaySnapshotAsync(request);
            _bills.Clear();
            foreach (var bill in snapshot.Bills)
            {
                bill.ApprovedForPrint = false;
                _bills.Add(bill);
            }

            _operatingAccountBalance = snapshot.OperatingAccountBalance;
            ApplyDueClassification();
            ApproveAllHeaderCheckBox.IsChecked = false;
            UpdateBalancePanel();

            var message = $"Synced {_bills.Count} unpaid bill(s). Operating balance: {_operatingAccountBalance:C2}.";
            if (!string.IsNullOrWhiteSpace(snapshot.WarningMessage))
            {
                message += $" Warning: {snapshot.WarningMessage}";
            }

            UpdateStatus(message);
        }
        catch (Exception exception)
        {
            ShowError("QuickBooks sync failed.", exception);
        }
    }

    private void FilterAllClick(object sender, RoutedEventArgs e)
    {
        _activeDueFilter = null;
        _billsView.Refresh();
        UpdateStatus("Filter set to: All unpaid bills.");
    }

    private void FilterOverdueClick(object sender, RoutedEventArgs e)
    {
        _activeDueFilter = BillDueBucket.Overdue;
        _billsView.Refresh();
        UpdateStatus("Filter set to: Overdue bills.");
    }

    private void FilterDueSoonClick(object sender, RoutedEventArgs e)
    {
        _activeDueFilter = BillDueBucket.DueSoon;
        _billsView.Refresh();
        UpdateStatus("Filter set to: Due within 3 days.");
    }

    private void FilterUpcomingClick(object sender, RoutedEventArgs e)
    {
        _activeDueFilter = BillDueBucket.Upcoming;
        _billsView.Refresh();
        UpdateStatus("Filter set to: Upcoming bills.");
    }

    private void ApproveAllHeaderClick(object sender, RoutedEventArgs e)
    {
        var shouldApprove = ApproveAllHeaderCheckBox.IsChecked == true;
        foreach (var bill in _billsView.Cast<BillRecord>())
        {
            bill.ApprovedForPrint = shouldApprove;
        }

        BillsGrid.Items.Refresh();
        UpdateBalancePanel();
    }

    private void BillApprovalChanged(object sender, RoutedEventArgs e)
    {
        UpdateBalancePanel();
    }

    private void PrintApprovedChecksClick(object sender, RoutedEventArgs e)
    {
        var approvedBills = _bills
            .Where(bill => bill.ApprovedForPrint)
            .OrderBy(bill => bill.DueDate ?? DateTime.MaxValue)
            .ThenBy(bill => bill.VendorName)
            .ToList();

        if (approvedBills.Count == 0)
        {
            MessageBox.Show(
                "No bills are currently approved for printing.",
                "Print Approved Checks",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var summaryText = BuildCheckRunSummary(approvedBills);
        var summaryWindow = new CheckRunSummaryWindow(summaryText)
        {
            Owner = this
        };

        summaryWindow.ShowDialog();
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
        _operatingAccountBalance = 0m;
        UpdateBalancePanel();
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

            ApplyDueClassification();
            UpdateBalancePanel();
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

    private void ApplyDueClassification()
    {
        _billPayPlanner.ClassifyDueBuckets(_bills, DateTime.Today, DueSoonWindowDays);
        _billsView.Refresh();
        BillsGrid.Items.Refresh();
    }

    private bool BillPassesActiveFilter(object item)
    {
        if (item is not BillRecord bill)
        {
            return false;
        }

        return !_activeDueFilter.HasValue || bill.DueBucket == _activeDueFilter.Value;
    }

    private void UpdateBalancePanel()
    {
        var approvedTotal = _billPayPlanner.CalculateApprovedCheckTotal(_bills);
        var balanceAfter = _billPayPlanner.CalculateBalanceAfterApprovedChecks(_operatingAccountBalance, _bills);

        BalanceBeforeValueTextBlock.Text = _operatingAccountBalance.ToString("C2", CultureInfo.CurrentCulture);
        ApprovedTotalValueTextBlock.Text = approvedTotal.ToString("C2", CultureInfo.CurrentCulture);
        BalanceAfterValueTextBlock.Text = balanceAfter.ToString("C2", CultureInfo.CurrentCulture);
        BalanceAfterValueTextBlock.Foreground = balanceAfter < 0m ? Brushes.OrangeRed : Brushes.White;
    }

    private string BuildCheckRunSummary(IReadOnlyList<BillRecord> approvedBills)
    {
        var approvedTotal = _billPayPlanner.CalculateApprovedCheckTotal(approvedBills);
        var balanceAfter = _billPayPlanner.CalculateBalanceAfterApprovedChecks(_operatingAccountBalance, approvedBills);
        var builder = new StringBuilder();
        builder.AppendLine("Vendor Bill Pay - Approved Check Run Summary");
        builder.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Operating Account: {OperatingAccountTextBox.Text?.Trim() ?? "Operating Account"}");
        builder.AppendLine($"Balance Before: {_operatingAccountBalance:C2}");
        builder.AppendLine($"Approved Check Total: {approvedTotal:C2}");
        builder.AppendLine($"Balance After: {balanceAfter:C2}");
        builder.AppendLine(new string('-', 80));
        builder.AppendLine("Vendor                          Invoice #       Due Date      Amount");
        builder.AppendLine(new string('-', 80));

        foreach (var bill in approvedBills)
        {
            builder.AppendLine(
                $"{Truncate(bill.VendorName, 30),-30} {Truncate(bill.InvoiceNumber, 14),-14} {(bill.DueDate?.ToString("yyyy-MM-dd") ?? "N/A"),-12} {bill.Amount,12:C2}");
        }

        builder.AppendLine(new string('-', 80));
        builder.AppendLine($"Total Approved: {approvedTotal:C2}");
        return builder.ToString();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
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
            "Vendor Bill Pay Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
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
            Enum.TryParse(modeTag, out QuickBooksTransportMode parsedMode))
        {
            return parsedMode;
        }

        return QuickBooksTransportMode.FileDropBridge;
    }
}

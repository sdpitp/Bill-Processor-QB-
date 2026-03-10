using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace BillProcessor.App;

public partial class CheckRunSummaryWindow : Window
{
    public CheckRunSummaryWindow(string summaryText)
    {
        InitializeComponent();
        SummaryTextBox.Text = summaryText ?? string.Empty;
    }

    private void PrintClick(object sender, RoutedEventArgs e)
    {
        var printDialog = new PrintDialog();
        if (printDialog.ShowDialog() != true)
        {
            return;
        }

        var document = new FlowDocument(new Paragraph(new Run(SummaryTextBox.Text)))
        {
            PagePadding = new Thickness(40),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11
        };

        var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
        printDialog.PrintDocument(paginator, "Approved Check Run Summary");
    }
}

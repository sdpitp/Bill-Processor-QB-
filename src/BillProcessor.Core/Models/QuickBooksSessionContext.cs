namespace BillProcessor.Core.Models;

public sealed class QuickBooksSessionContext
{
    public bool IsPostingAuthorizedForSession { get; set; }
    public string CompanyFileIdentifier { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public QuickBooksAccessIntent AccessIntent { get; set; } = QuickBooksAccessIntent.PostBills;
}

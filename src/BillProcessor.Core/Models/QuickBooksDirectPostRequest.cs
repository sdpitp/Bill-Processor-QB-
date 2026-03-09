namespace BillProcessor.Core.Models;

public sealed class QuickBooksDirectPostRequest
{
    public string RequestId { get; set; } = string.Empty;
    public string CompanyFileIdentifier { get; set; } = string.Empty;
    public string QbXmlPayload { get; set; } = string.Empty;
}

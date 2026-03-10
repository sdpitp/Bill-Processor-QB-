using BillProcessor.Core.Models;

namespace BillProcessor.Core.Abstractions;

public interface IQuickBooksDesktopTransport
{
    Task<QuickBooksPostResult> SubmitBillAsync(
        QuickBooksDirectPostRequest request,
        CancellationToken cancellationToken = default);
    Task<QuickBooksBillPaySnapshot> ReadBillPaySnapshotAsync(
        QuickBooksBillPaySyncRequest request,
        CancellationToken cancellationToken = default);

    string GetTransportName();
}

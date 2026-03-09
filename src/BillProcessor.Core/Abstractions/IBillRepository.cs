using BillProcessor.Core.Models;

namespace BillProcessor.Core.Abstractions;

public interface IBillRepository
{
    Task<IReadOnlyList<BillRecord>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IEnumerable<BillRecord> bills, CancellationToken cancellationToken = default);
}

namespace BillProcessor.Core.Models;

public sealed class BillValidationResult
{
    public List<string> Errors { get; } = [];
    public bool IsValid => Errors.Count == 0;
}

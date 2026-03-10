using BillProcessor.Core.Models;

namespace BillProcessor.Core.Services;

public sealed class BillPayPlanner
{
    public void ClassifyDueBuckets(
        IEnumerable<BillRecord> bills,
        DateTime asOfDate,
        int dueSoonDays = 3)
    {
        ArgumentNullException.ThrowIfNull(bills);
        dueSoonDays = Math.Max(0, dueSoonDays);

        foreach (var bill in bills)
        {
            if (!bill.DueDate.HasValue)
            {
                bill.DaysUntilDue = int.MaxValue;
                bill.DueBucket = BillDueBucket.Unknown;
                continue;
            }

            var dueDate = bill.DueDate.Value.Date;
            var dayDelta = (dueDate - asOfDate.Date).Days;
            bill.DaysUntilDue = dayDelta;

            if (dayDelta < 0)
            {
                bill.DueBucket = BillDueBucket.Overdue;
            }
            else if (dayDelta <= dueSoonDays)
            {
                bill.DueBucket = BillDueBucket.DueSoon;
            }
            else
            {
                bill.DueBucket = BillDueBucket.Upcoming;
            }
        }
    }

    public decimal CalculateApprovedCheckTotal(IEnumerable<BillRecord> bills)
    {
        ArgumentNullException.ThrowIfNull(bills);
        return bills.Where(bill => bill.ApprovedForPrint).Sum(bill => bill.Amount);
    }

    public decimal CalculateBalanceAfterApprovedChecks(decimal operatingBalanceBefore, IEnumerable<BillRecord> bills)
    {
        ArgumentNullException.ThrowIfNull(bills);
        return operatingBalanceBefore - CalculateApprovedCheckTotal(bills);
    }
}

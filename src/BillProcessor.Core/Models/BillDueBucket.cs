namespace BillProcessor.Core.Models;

public enum BillDueBucket
{
    Unknown = 0,
    Overdue = 1,
    DueSoon = 2,
    Upcoming = 3
}

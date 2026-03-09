using BillProcessor.Core.Abstractions;
using BillProcessor.Core.Models;

namespace BillProcessor.Infrastructure.QuickBooks;

public static class QuickBooksGatewayFactory
{
    public static IQuickBooksGateway Create(QuickBooksTransportMode mode)
    {
        return mode switch
        {
            QuickBooksTransportMode.DirectDesktopSdk => new DirectQuickBooksGateway(new QbXmlRp2DesktopTransport()),
            _ => new FileDropQuickBooksGateway()
        };
    }
}

using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Domain;

namespace Enrichment.Example.Ports;

// «Внешние ресурсы»: в реальном проекте за этими интерфейсами EF Core/Dapper/HTTP-клиенты.
// Валидаторы обращаются к ним при проверке — и кладут результат в payload,
// чтобы хендлер больше не дёргал эти же интерфейсы.

public interface IOrderRepository
{
  ValueTask<Order?> FindOrderAsync(string orderId, CancellationToken cancellationToken);
}

public interface ICustomerApi
{
  ValueTask<Customer?> FindCustomerAsync(string customerId, CancellationToken cancellationToken);
}

public interface IPaymentGateway
{
  ValueTask<PaymentMethod?> FindMethodAsync(string code, CancellationToken cancellationToken);
  ValueTask<RefundPolicy> GetRefundPolicyAsync(Order order, CancellationToken cancellationToken);
}

public interface IInventoryApi
{
  ValueTask<Inventory?> FindFlightAsync(string flightNumber, CancellationToken cancellationToken);
}

public interface IPassengerApi
{
  ValueTask<Passenger?> FindPassengerAsync(string passengerId, CancellationToken cancellationToken);
}

public interface INotificationApi
{
  ValueTask<string> NotifyCancellationAsync(string orderId, CancellationToken cancellationToken);
}
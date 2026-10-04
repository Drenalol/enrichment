using System;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Domain;
using Enrichment.Example.Ports;

namespace Enrichment.Example.Infrastructure;

// Имитации «внешних ресурсов» со счётчиками вызовов: демо на их примере показывает,
// что в «БД/API» ходят только энричеры, а валидаторы и хендлер живут на данных из Enrichment.

public sealed class FakeOrderRepository : IOrderRepository
{
  public int Calls { get; private set; }

  public ValueTask<Order?> FindOrderAsync(string orderId, CancellationToken cancellationToken)
  {
    Calls++;
    Console.WriteLine($"      [db] SELECT * FROM orders WHERE id = '{orderId}'");

    var order = orderId switch
    {
      "ORD-100" => new Order { Id = "ORD-100", CustomerId = "CUS-7", TotalAmount = 12_500m, PaidAmount = 0m, Status = "Reserved" },
      "ORD-200" => new Order { Id = "ORD-200", CustomerId = "CUS-9", TotalAmount = 8_400m, PaidAmount = 3_000m, Status = "PartiallyPaid" },
      "ORD-300" => new Order { Id = "ORD-300", CustomerId = "CUS-4", TotalAmount = 5_000m, PaidAmount = 5_000m, Status = "Paid" },
      _ => null,
    };

    return ValueTask.FromResult(order);
  }
}

public sealed class FakeCustomerApi : ICustomerApi
{
  public int Calls { get; private set; }

  public ValueTask<Customer?> FindCustomerAsync(string customerId, CancellationToken cancellationToken)
  {
    Calls++;
    Console.WriteLine($"      [api] GET /customers/{customerId}");

    var customer = customerId switch
    {
      "CUS-7" => new Customer { Id = "CUS-7", Email = "ivanov@example.com", BonusBalance = 21_000 },
      "CUS-9" => new Customer { Id = "CUS-9", Email = "petrov@example.com", BonusBalance = 300 },
      "CUS-4" => new Customer { Id = "CUS-4", Email = "sidorov@example.com", BonusBalance = 1_200 },
      _ => null,
    };

    return ValueTask.FromResult(customer);
  }
}

public sealed class FakePaymentGateway : IPaymentGateway
{
  public int Calls { get; private set; }

  public ValueTask<PaymentMethod?> FindMethodAsync(string code, CancellationToken cancellationToken)
  {
    Calls++;
    Console.WriteLine($"      [api] GET /payment-methods/{code}");

    var method = code switch
    {
      "CARD" => new PaymentMethod { Code = "CARD", Name = "банковская карта", FeePercent = 1.9m },
      "CASH" => new PaymentMethod { Code = "CASH", Name = "наличные", FeePercent = 0m },
      _ => null,
    };

    return ValueTask.FromResult(method);
  }

  public ValueTask<RefundPolicy> GetRefundPolicyAsync(Order order, CancellationToken cancellationToken)
  {
    Calls++;
    Console.WriteLine($"      [api] GET /orders/{order.Id}/refund-policy");

    return ValueTask.FromResult(new RefundPolicy { FeePercent = 2m, DaysWindow = 30 });
  }
}

public sealed class FakeInventoryApi : IInventoryApi
{
  public int Calls { get; private set; }

  public ValueTask<Inventory?> FindFlightAsync(string flightNumber, CancellationToken cancellationToken)
  {
    Calls++;
    Console.WriteLine($"      [api] GET /flights/{flightNumber}/seats");

    var flight = flightNumber switch
    {
      "SU-123" => new Inventory { FlightNumber = "SU-123", SeatsLeft = 18, AircraftType = "Airbus A320" },
      "SU-777" => new Inventory { FlightNumber = "SU-777", SeatsLeft = 0, AircraftType = "Boeing 777" },
      _ => null,
    };

    return ValueTask.FromResult(flight);
  }
}

public sealed class FakePassengerApi : IPassengerApi
{
  public int Calls { get; private set; }

  public ValueTask<Passenger?> FindPassengerAsync(string passengerId, CancellationToken cancellationToken)
  {
    Calls++;
    Console.WriteLine($"      [api] GET /passengers/{passengerId}");

    var passenger = passengerId switch
    {
      "PAX-1" => new Passenger { Id = "PAX-1", FullName = "Иванов И.И.", BonusMiles = 150_000 },
      "PAX-2" => new Passenger { Id = "PAX-2", FullName = "Петров П.П.", BonusMiles = 20_000 },
      _ => null,
    };

    return ValueTask.FromResult(passenger);
  }
}

public sealed class FakeNotificationApi : INotificationApi
{
  public int Calls { get; private set; }

  public ValueTask<string> NotifyCancellationAsync(string orderId, CancellationToken cancellationToken)
  {
    Calls++;
    var noticeId = $"N-{Calls}";
    Console.WriteLine($"      [api] POST /notifications отмена заказа {orderId} -> {noticeId}");

    return ValueTask.FromResult(noticeId);
  }
}
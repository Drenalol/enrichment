using System;
using System.Threading.Tasks;
using Enrichment.Example.Contracts;
using Enrichment.Example.Infrastructure;
using Enrichment.Example.Ports;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Enrichment.Example;

public static class Program
{
  public static async Task Main()
  {
    // Имитации «внешних ресурсов» со счётчиками вызовов.
    var orders = new FakeOrderRepository();
    var customers = new FakeCustomerApi();
    var gateway = new FakePaymentGateway();
    var inventory = new FakeInventoryApi();
    var passengers = new FakePassengerApi();
    var notifications = new FakeNotificationApi();

    var provider = new ServiceCollection()
        .AddSingleton<IOrderRepository>(orders)
        .AddSingleton<ICustomerApi>(customers)
        .AddSingleton<IPaymentGateway>(gateway)
        .AddSingleton<IInventoryApi>(inventory)
        .AddSingleton<IPassengerApi>(passengers)
        .AddSingleton<INotificationApi>(notifications)

        // Энричеры и валидаторы собираются Scrutor-сканированием сборки — ровно теми же
        // закрытыми IDataEnricher<TRequest>/IEnrichingValidator<TRequest>, что DI инжектит хендлерам.
        // Генератору безразлично, откуда DI берёт инъект: форму Enrichment
        // он собирает по типам энричеров, видимым на компиляции.
        .AddEnrichment(typeof(Program).Assembly)

        .AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly))
        .BuildServiceProvider();

    var sender = provider.GetRequiredService<ISender>();

    Console.WriteLine("Enrichment — демо");
    Console.WriteLine("Конвейер: чистые правила (I/O = 0) → энричеры грузят из «БД/API» →");
    Console.WriteLine("контекстные правила проверяют на загруженном → source generator уже собрал");
    Console.WriteLine("из payload'ов генерируемый Enrichment, и хендлер живёт на этих же данных.");

    Case("1. CreateOrder — запрос валиден");
    var created = await sender.Send(new CreateOrderRequest { OrderId = "ORD-100", CustomerId = "CUS-7", Currency = "RUB", Items = ["TKT-1", "SEAT-2A"] });
    Console.WriteLine($"      [ответ] {created.OrderId}: {created.Status}, итого {created.Total:0.##}");
    Counters(orders, customers, gateway, inventory, passengers, notifications);

    Case("2. CreateOrder — запрос невалиден, чистые правила гасят его до обращения в «БД»");
    try
    {
      await sender.Send(new CreateOrderRequest { OrderId = "ord-bad", CustomerId = "CUS-7", Currency = "RUB", Items = ["TKT-1"] });
    }
    catch (ValidationException e)
    {
      foreach (var failure in e.Errors)
        Console.WriteLine($"      [отказ] {failure.ErrorMessage}");
    }

    Counters(orders, customers, gateway, inventory, passengers, notifications);

    Case("3. PayOrder — запрос валиден");
    var paid = await sender.Send(new PayOrderRequest { OrderId = "ORD-200", Amount = 5_000m, MethodCode = "CARD" });
    Console.WriteLine($"      [ответ] {paid.PaymentId}, списано {paid.ChargedAmount:0.##}");
    Counters(orders, customers, gateway, inventory, passengers, notifications);

    Case("4. PayOrder — сумма со комиссией больше остатка: хендлер смотрит в Enrichment");
    try
    {
      await sender.Send(new PayOrderRequest { OrderId = "ORD-200", Amount = 5_400m, MethodCode = "CARD" });
    }
    catch (InvalidOperationException e)
    {
      Console.WriteLine($"      [отказ хендлера] {e.Message}");
    }

    Counters(orders, customers, gateway, inventory, passengers, notifications);

    Case("5. CancelOrder — request-энричеры + response-энричер");
    var cancelled = await sender.Send(new CancelOrderRequest { OrderId = "ORD-300", Reason = "клиент отказался" });
    Console.WriteLine($"      [ответ] {cancelled.OrderId}, к возврату {cancelled.RefundAmount:0.##}");
    Console.WriteLine("      [response-сторона] ResponseEnrichment.NoticeId/SentAt наполнены после хендлера (см. вызов notify API выше)");
    Counters(orders, customers, gateway, inventory, passengers, notifications);

    Case("6. IssueTicket — данные двух «API» переиспользованы хендлером");
    var ticket = await sender.Send(new IssueTicketRequest { OrderId = "ORD-100", FlightNumber = "SU-123", PassengerId = "PAX-1", Seat = "12A" });
    Console.WriteLine($"      [ответ] билет {ticket.TicketNumber}, место {ticket.Seat}, статус {ticket.LoyaltyTier}");
    Counters(orders, customers, gateway, inventory, passengers, notifications);

    Case("7. IssueTicket — рейс без мест: контекстное правило отказывает до хендлера");
    try
    {
      await sender.Send(new IssueTicketRequest { OrderId = "ORD-100", FlightNumber = "SU-777", PassengerId = "PAX-2", Seat = "05B" });
    }
    catch (ValidationException e)
    {
      foreach (var failure in e.Errors)
        Console.WriteLine($"      [отказ валидатора] {failure.ErrorMessage}");
    }

    Counters(orders, customers, gateway, inventory, passengers, notifications);

    Case("8. CreateOrder — ORD-999: «не найден» — ValidationFailure правила, а не исключение загрузчика");
    try
    {
      await sender.Send(new CreateOrderRequest { OrderId = "ORD-999", CustomerId = "CUS-4", Currency = "RUB", Items = ["TKT-9"] });
    }
    catch (ValidationException e)
    {
      foreach (var failure in e.Errors)
        Console.WriteLine($"      [отказ контекстного правила] {failure.ErrorMessage}");
    }

    Counters(orders, customers, gateway, inventory, passengers, notifications);

    Console.WriteLine();
    Console.WriteLine("══ ИТОГО ══");
    Console.WriteLine("В «БД/API» ходили только энричеры (счётчики выше), валидаторы и хендлеры — только по данным из Enrichment.");
    Console.WriteLine("Хендлеры не получают порты в конструкторах; data-проверки — ValidationFailure, а не throw из загрузчика.");
  }

  private static void Case(string title)
  {
    Console.WriteLine();
    Console.WriteLine($"── {title} ".PadRight(78, '─'));
  }

  private static void Counters(
      FakeOrderRepository orders,
      FakeCustomerApi customers,
      FakePaymentGateway gateway,
      FakeInventoryApi inventory,
      FakePassengerApi passengers,
      FakeNotificationApi notifications
  )
  {
    Console.WriteLine($"      [счётчики] orders: {orders.Calls}, customers: {customers.Calls}, gateway: {gateway.Calls}, inventory: {inventory.Calls}, passengers: {passengers.Calls}, notify: {notifications.Calls}");
  }
}
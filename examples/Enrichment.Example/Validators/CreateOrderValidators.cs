using System;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Domain;
using Enrichment.Example.Ports;
using FluentValidation;

namespace Enrichment.Example.Validators;

/// <summary>
/// Валидатор-чекер: только правила, payload нет — в Enrichment не попадает.
/// Правила FluentValidation исполняются базовым мостом ДО загрузки данных,
/// поэтому на невалидном запросе в репозиторий никто не сходит.
/// </summary>
public class OrderRequestRulesValidator : EnrichingValidator<Contracts.CreateOrderRequest>
{
  public OrderRequestRulesValidator()
  {
    RuleFor(x => x.OrderId).NotEmpty().Matches(@"^ORD-\d+$").WithMessage("идентификатор заказа должен выглядеть как ORD-123");
    RuleFor(x => x.CustomerId).NotEmpty().Matches(@"^CUS-\d+$");
    RuleFor(x => x.Currency).Equal("RUB");
    RuleFor(x => x.Items).NotEmpty();
  }
}

/// <summary>
/// «БД-шный» валидатор: достаёт заказ и отдаёт его в payload.
/// Хендлер увидит заказ как Enrichment.Order, не дёргая IOrderRepository повторно.
/// </summary>
public class OrderExistsValidator : EnrichingValidator<Contracts.CreateOrderRequest, (Order Order, decimal ItemsTotal)>
{
  private readonly IOrderRepository _orders;

  public OrderExistsValidator(IOrderRepository orders)
  {
    _orders = orders;
    RuleFor(x => x.OrderId).NotEmpty();
  }

  protected override async ValueTask<(Order Order, decimal ItemsTotal)> EnrichAsync(Contracts.CreateOrderRequest request, CancellationToken cancellationToken)
  {
    var order = await _orders.FindOrderAsync(request.OrderId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"заказ {request.OrderId} не найден");

    return (order, order.TotalAmount);
  }
}

/// <summary>«API-шный» валидатор: клиент из внешней системы + расчётный признак VIP.</summary>
public class CustomerValidator : EnrichingValidator<Contracts.CreateOrderRequest, (Customer Customer, bool IsVip)>
{
  private readonly ICustomerApi _customers;

  public CustomerValidator(ICustomerApi customers)
  {
    _customers = customers;
    RuleFor(x => x.CustomerId).NotEmpty();
  }

  protected override async ValueTask<(Customer Customer, bool IsVip)> EnrichAsync(Contracts.CreateOrderRequest request, CancellationToken cancellationToken)
  {
    var customer = await _customers.FindCustomerAsync(request.CustomerId, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"клиент {request.CustomerId} не найден");

    return (customer, customer.BonusBalance > 10_000);
  }
}
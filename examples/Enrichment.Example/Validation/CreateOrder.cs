using System;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Domain;
using Enrichment.Example.Handlers;
using Enrichment.Example.Ports;
using FluentValidation;

namespace Enrichment.Example.Validation;

// ─── фаза 1: чистые правила — ни одного похода в «БД» на мусорном запросе ───

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

// ─── фаза 2: энричеры только грузят данные, правил у них нет ───

public class OrderEnricher : Enricher<Contracts.CreateOrderRequest, (Order? Order, decimal ItemsTotal)>
{
  private readonly IOrderRepository _orders;

  public OrderEnricher(IOrderRepository orders) => _orders = orders;

  protected override async ValueTask<(Order?, decimal)> LoadAsync(Contracts.CreateOrderRequest request, CancellationToken cancellationToken)
  {
    var order = await _orders.FindOrderAsync(request.OrderId, cancellationToken).ConfigureAwait(false);

    return (order, order?.TotalAmount ?? 0m);
  }
}

public class CustomerEnricher : Enricher<Contracts.CreateOrderRequest, (Customer? Customer, bool IsVip)>
{
  private readonly ICustomerApi _customers;

  public CustomerEnricher(ICustomerApi customers) => _customers = customers;

  protected override async ValueTask<(Customer?, bool)> LoadAsync(Contracts.CreateOrderRequest request, CancellationToken cancellationToken)
  {
    var customer = await _customers.FindCustomerAsync(request.CustomerId, cancellationToken).ConfigureAwait(false);

    return (customer, customer?.BonusBalance > 10_000);
  }
}

// ─── фаза 3: контекстные правила; TNeeds — генерируемый ICreateOrderEnrichment ───

public class OrderExistsValidator : EnrichingValidator<Contracts.CreateOrderRequest, ICreateOrderEnrichment>
{
  public OrderExistsValidator()
  {
    RuleFor(x => x.OrderId).Custom((orderId, context) =>
    {
      if (GetEnrichment(context).Order is null)
        context.AddFailure($"заказ {orderId} не найден");
    });
  }
}

public class CustomerExistsValidator : EnrichingValidator<Contracts.CreateOrderRequest, ICreateOrderEnrichment>
{
  public CustomerExistsValidator()
  {
    RuleFor(x => x.CustomerId).Custom((customerId, context) =>
    {
      if (GetEnrichment(context).Customer is null)
        context.AddFailure($"клиент {customerId} не найден");
    });
  }
}
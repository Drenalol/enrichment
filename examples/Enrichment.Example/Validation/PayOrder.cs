using System;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Domain;
using Enrichment.Example.Handlers;
using Enrichment.Example.Ports;
using FluentValidation;

namespace Enrichment.Example.Validation;

// ─── фаза 1: чистые правила ───

public class PayOrderRequestRulesValidator : EnrichingValidator<Contracts.PayOrderRequest>
{
  public PayOrderRequestRulesValidator()
  {
    RuleFor(x => x.Amount).GreaterThan(0);
    RuleFor(x => x.MethodCode).NotEmpty();
  }
}

// ─── фаза 2: энричеры ───

public class OrderDebtEnricher : Enricher<Contracts.PayOrderRequest, (Order? Order, decimal RemainingAmount)>
{
  private readonly IOrderRepository _orders;

  public OrderDebtEnricher(IOrderRepository orders) => _orders = orders;

  protected override async ValueTask<(Order?, decimal)> LoadAsync(Contracts.PayOrderRequest request, CancellationToken cancellationToken)
  {
    var order = await _orders.FindOrderAsync(request.OrderId, cancellationToken).ConfigureAwait(false);

    return (order, order?.RemainingAmount ?? 0m);
  }
}

public class PaymentMethodEnricher : Enricher<Contracts.PayOrderRequest, (PaymentMethod? Method, decimal AcquirerFee)>
{
  private readonly IPaymentGateway _gateway;

  public PaymentMethodEnricher(IPaymentGateway gateway) => _gateway = gateway;

  protected override async ValueTask<(PaymentMethod?, decimal)> LoadAsync(Contracts.PayOrderRequest request, CancellationToken cancellationToken)
  {
    var method = await _gateway.FindMethodAsync(request.MethodCode, cancellationToken).ConfigureAwait(false);
    var fee = method is null ? 0m : decimal.Round(request.Amount * method.FeePercent / 100m, 2);

    return (method, fee);
  }
}

// ─── фаза 3: контекстные правила ───

public class OrderPayableValidator : EnrichingValidator<Contracts.PayOrderRequest, IPayOrderEnrichment>
{
  public OrderPayableValidator()
  {
    RuleFor(x => x.OrderId).Custom((orderId, context) =>
    {
      var needs = GetEnrichment(context);

      if (needs.Order is null)
        context.AddFailure($"заказ {orderId} не найден");
      else if (needs.RemainingAmount <= 0)
        context.AddFailure($"заказ {orderId} уже оплачен");
    });
  }
}

public class MethodAvailableValidator : EnrichingValidator<Contracts.PayOrderRequest, IPayOrderEnrichment>
{
  public MethodAvailableValidator()
  {
    RuleFor(x => x.MethodCode).Custom((code, context) =>
    {
      if (GetEnrichment(context).Method is null)
        context.AddFailure($"способ оплаты {code} недоступен");
    });
  }
}
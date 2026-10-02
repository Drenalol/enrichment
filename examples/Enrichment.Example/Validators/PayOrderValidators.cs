using System;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Domain;
using Enrichment.Example.Ports;
using FluentValidation;

namespace Enrichment.Example.Validators;

/// <summary>
/// Загружает заказ и считает остаток к оплате — всё уезжает в payload,
/// хендлер возьмёт их из Enrichment, не повторяя запрос в «БД».
/// </summary>
public class ExistingOrderValidator : EnrichingValidator<Contracts.PayOrderRequest, (Order Order, decimal RemainingAmount)>
{
  private readonly IOrderRepository _orders;

  public ExistingOrderValidator(IOrderRepository orders)
  {
    _orders = orders;
    RuleFor(x => x.Amount).GreaterThan(0);
  }

  protected override async ValueTask<(Order Order, decimal RemainingAmount)> EnrichAsync(Contracts.PayOrderRequest request, CancellationToken cancellationToken)
  {
    var order = await _orders.FindOrderAsync(request.OrderId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"заказ {request.OrderId} не найден");

    if (order.RemainingAmount <= 0)
      throw new InvalidOperationException($"заказ {order.Id} уже оплачен");

    return (order, order.RemainingAmount);
  }
}

/// <summary>«API-шный» валидатор: способ оплаты из платёжного шлюза + рассчитанная комиссия.</summary>
public class PaymentFormValidator : EnrichingValidator<Contracts.PayOrderRequest, (PaymentMethod Method, decimal AcquirerFee)>
{
  private readonly IPaymentGateway _gateway;

  public PaymentFormValidator(IPaymentGateway gateway)
  {
    _gateway = gateway;
    RuleFor(x => x.MethodCode).NotEmpty();
  }

  protected override async ValueTask<(PaymentMethod Method, decimal AcquirerFee)> EnrichAsync(Contracts.PayOrderRequest request, CancellationToken cancellationToken)
  {
    var method = await _gateway.FindMethodAsync(request.MethodCode, cancellationToken).ConfigureAwait(false)
                 ?? throw new InvalidOperationException($"способ оплаты {request.MethodCode} недоступен");

    var fee = decimal.Round(request.Amount * method.FeePercent / 100m, 2);

    return (method, fee);
  }
}
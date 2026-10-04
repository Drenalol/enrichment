using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Contracts;

namespace Enrichment.Example.Handlers;

public partial class PayOrderHandler : EnrichedHandler<PayOrderRequest, PayOrderResponse>
{
  public PayOrderHandler(
    IEnumerable<IDataEnricher<PayOrderRequest>> requestEnrichers,
    IEnumerable<IEnrichingValidator<PayOrderRequest>> requestValidators,
    IEnumerable<IDataEnricher<PayOrderResponse>> responseEnrichers,
    IEnumerable<IEnrichingValidator<PayOrderResponse>> responseValidators)
    : base(requestEnrichers, requestValidators, responseEnrichers, responseValidators)
  {
  }

  protected override Task<PayOrderResponse> HandleAsync(PayOrderRequest request, CancellationToken cancellationToken)
  {
    var order = Enrichment.Order!;
    var method = Enrichment.Method!;

    var charged = request.Amount + Enrichment.AcquirerFee;

    if (charged > Enrichment.RemainingAmount)
      throw new InvalidOperationException($"к списанию {charged:0.##} больше остатка по заказу {Enrichment.RemainingAmount:0.##}");

    Console.WriteLine($"      [handler] заказ {order.Id}: способ «{method.Name}», к списанию {charged:0.##} (остаток был {Enrichment.RemainingAmount:0.##})");

    var response = new PayOrderResponse
    {
      PaymentId = $"PAY-{method.Code}-{order.Id}",
      ChargedAmount = charged,
    };

    return Task.FromResult(response);
  }
}
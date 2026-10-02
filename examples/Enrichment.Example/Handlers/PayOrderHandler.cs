using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Contracts;

namespace Enrichment.Example.Handlers;

public partial class PayOrderHandler : EnrichedHandler<PayOrderRequest, PayOrderResponse>
{
  public PayOrderHandler(IEnumerable<IEnrichingValidator<PayOrderRequest>> requestValidators, IEnumerable<IEnrichingValidator<PayOrderResponse>> responseValidators) : base(requestValidators, responseValidators)
  {
  }

  protected override Task<PayOrderResponse> HandleAsync(PayOrderRequest request, CancellationToken cancellationToken)
  {
    var charged = request.Amount + Enrichment.AcquirerFee;

    if (charged > Enrichment.RemainingAmount)
      throw new InvalidOperationException($"к списанию {charged:0.##} больше остатка по заказу {Enrichment.RemainingAmount:0.##}");

    Console.WriteLine($"      [handler] заказ {Enrichment.Order.Id}: способ «{Enrichment.Method.Name}», к списанию {charged:0.##} (остаток был {Enrichment.RemainingAmount:0.##})");

    var response = new PayOrderResponse
    {
      PaymentId = $"PAY-{Enrichment.Method.Code}-{Enrichment.Order.Id}",
      ChargedAmount = charged,
    };

    return Task.FromResult(response);
  }
}
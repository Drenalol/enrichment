using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Contracts;

namespace Enrichment.Example.Handlers;

public partial class CancelOrderHandler : EnrichedHandler<CancelOrderRequest, CancelOrderResponse>
{
  public CancelOrderHandler(IEnumerable<IEnrichingValidator<CancelOrderRequest>> requestValidators, IEnumerable<IEnrichingValidator<CancelOrderResponse>> responseValidators) : base(requestValidators, responseValidators)
  {
  }

  protected override Task<CancelOrderResponse> HandleAsync(CancelOrderRequest request, CancellationToken cancellationToken)
  {
    var refund = Enrichment.Order.PaidAmount * (100m - Enrichment.Policy.FeePercent) / 100m;

    Console.WriteLine($"      [handler] отмена {Enrichment.Order.Id} (статус {Enrichment.Order.Status}): возврат {refund:0.##} минус комиссия политики {Enrichment.Policy.FeePercent:0.##}%");

    var response = new CancelOrderResponse
    {
      OrderId = Enrichment.Order.Id,
      RefundAmount = refund,
    };

    return Task.FromResult(response);
  }
}
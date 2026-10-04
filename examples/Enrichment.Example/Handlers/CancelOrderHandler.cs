using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Contracts;

namespace Enrichment.Example.Handlers;

[EnrichmentContext(Request = "WriteAnyRequest", Response = "WriteAnyResponse")]
public partial class CancelOrderHandler : EnrichedHandler<CancelOrderRequest, CancelOrderResponse>
{
  public CancelOrderHandler(
    IEnumerable<IDataEnricher<CancelOrderRequest>> requestEnrichers,
    IEnumerable<IEnrichingValidator<CancelOrderRequest>> requestValidators,
    IEnumerable<IDataEnricher<CancelOrderResponse>> responseEnrichers,
    IEnumerable<IEnrichingValidator<CancelOrderResponse>> responseValidators)
    : base(requestEnrichers, requestValidators, responseEnrichers, responseValidators)
  {
  }

  protected override Task<CancelOrderResponse> HandleAsync(CancelOrderRequest request, CancellationToken cancellationToken)
  {
    var order = Enrichment.Order!;
    var policy = Enrichment.Policy!;

    var refund = order.PaidAmount * (100m - policy.FeePercent) / 100m;

    Console.WriteLine($"      [handler] отмена {order.Id} (статус {order.Status}): возврат {refund:0.##} минус комиссия политики {policy.FeePercent:0.##}%");

    var response = new CancelOrderResponse
    {
      OrderId = order.Id,
      RefundAmount = refund,
    };

    return Task.FromResult(response);
  }
}
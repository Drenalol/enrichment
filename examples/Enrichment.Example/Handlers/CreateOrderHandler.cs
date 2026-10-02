using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Contracts;

namespace Enrichment.Example.Handlers;

/// <summary>
/// Хендлер не получает порты в конструктор — весь «внешний мир» остался валидаторам.
/// Данные, загруженные ими при проверке, лежат в генерируемом Enrichment.
/// </summary>
public partial class CreateOrderHandler : EnrichedHandler<CreateOrderRequest, CreateOrderResponse>
{
  public CreateOrderHandler(IEnumerable<IEnrichingValidator<CreateOrderRequest>> requestValidators, IEnumerable<IEnrichingValidator<CreateOrderResponse>> responseValidators) : base(requestValidators, responseValidators)
  {
  }

  protected override Task<CreateOrderResponse> HandleAsync(CreateOrderRequest request, CancellationToken cancellationToken)
  {
    Console.WriteLine($"      [handler] собираю подтверждение из Enrichment: заказ {Enrichment.Order.Id}, клиент {Enrichment.Customer.Email} (VIP: {Enrichment.IsVip}), сумма {Enrichment.ItemsTotal:0.##}");

    var response = new CreateOrderResponse
    {
      OrderId = Enrichment.Order.Id,
      Status = Enrichment.IsVip ? "ConfirmedPriority" : "Confirmed",
      Total = Enrichment.ItemsTotal,
    };

    return Task.FromResult(response);
  }
}
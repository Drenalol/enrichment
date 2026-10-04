using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Contracts;

namespace Enrichment.Example.Handlers;

/// <summary>
/// Хендлер не получает порты в конструктор — весь «внешний мир» остался энричерам.
/// Данные, загруженные ими, лежат в генерируемом Enrichment; контекстные валидаторы
/// уже проверили, что заказ и клиент найдены, поэтому «!» здесь безопасно.
/// </summary>
public partial class CreateOrderHandler : EnrichedHandler<CreateOrderRequest, CreateOrderResponse>
{
  public CreateOrderHandler(
    IEnumerable<IDataEnricher<CreateOrderRequest>> requestEnrichers,
    IEnumerable<IEnrichingValidator<CreateOrderRequest>> requestValidators,
    IEnumerable<IDataEnricher<CreateOrderResponse>> responseEnrichers,
    IEnumerable<IEnrichingValidator<CreateOrderResponse>> responseValidators)
    : base(requestEnrichers, requestValidators, responseEnrichers, responseValidators)
  {
  }

  protected override Task<CreateOrderResponse> HandleAsync(CreateOrderRequest request, CancellationToken cancellationToken)
  {
    var order = Enrichment.Order!;
    var customer = Enrichment.Customer!;

    Console.WriteLine($"      [handler] собираю подтверждение из Enrichment: заказ {order.Id}, клиент {customer.Email} (VIP: {Enrichment.IsVip}), сумма {Enrichment.ItemsTotal:0.##}");

    var response = new CreateOrderResponse
    {
      OrderId = order.Id,
      Status = Enrichment.IsVip ? "ConfirmedPriority" : "Confirmed",
      Total = Enrichment.ItemsTotal,
    };

    return Task.FromResult(response);
  }
}
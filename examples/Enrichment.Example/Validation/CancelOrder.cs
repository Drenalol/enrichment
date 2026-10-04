using System;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Domain;
using Enrichment.Example.Handlers;
using Enrichment.Example.Ports;
using FluentValidation;

namespace Enrichment.Example.Validation;

// ─── фаза 1: чистые правила ───

public class CancelOrderRequestRulesValidator : EnrichingValidator<Contracts.CancelOrderRequest>
{
  public CancelOrderRequestRulesValidator()
  {
    RuleFor(x => x.Reason).NotEmpty();
  }
}

// ─── фаза 2: энричеры ───

public class RefundEnricher : Enricher<Contracts.CancelOrderRequest, (Order? Order, RefundPolicy? Policy)>
{
  private readonly IOrderRepository _orders;
  private readonly IPaymentGateway _gateway;

  public RefundEnricher(IOrderRepository orders, IPaymentGateway gateway)
  {
    _orders = orders;
    _gateway = gateway;
  }

  protected override async ValueTask<(Order?, RefundPolicy?)> LoadAsync(Contracts.CancelOrderRequest request, CancellationToken cancellationToken)
  {
    var order = await _orders.FindOrderAsync(request.OrderId, cancellationToken).ConfigureAwait(false);
    var policy = order is null ? null : await _gateway.GetRefundPolicyAsync(order, cancellationToken).ConfigureAwait(false);

    return (order, policy);
  }
}

// ─── фаза 3: контекстные правила ───

public class RefundEligibilityValidator : EnrichingValidator<Contracts.CancelOrderRequest, IWriteAnyRequest>
{
  public RefundEligibilityValidator()
  {
    RuleFor(x => x.OrderId).Custom((orderId, context) =>
    {
      var needs = GetEnrichment(context);

      if (needs.Order is null)
        context.AddFailure($"заказ {orderId} не найден");
      else if (needs.Policy is null)
        context.AddFailure($"для заказа {orderId} недоступна политика возврата");
    });
  }
}

// ─── response-сторона: энричер после хендлера, ResponseEnrichment ───

public class CancellationNoticeEnricher : Enricher<Contracts.CancelOrderResponse, (string NoticeId, DateTime SentAt)>
{
  private readonly INotificationApi _notifications;

  public CancellationNoticeEnricher(INotificationApi notifications) => _notifications = notifications;

  protected override async ValueTask<(string, DateTime)> LoadAsync(Contracts.CancelOrderResponse response, CancellationToken cancellationToken)
  {
    var noticeId = await _notifications.NotifyCancellationAsync(response.OrderId, cancellationToken).ConfigureAwait(false);

    return (noticeId, DateTime.UtcNow);
  }
}
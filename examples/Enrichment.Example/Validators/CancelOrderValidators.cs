using System;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Domain;
using Enrichment.Example.Ports;
using FluentValidation;

namespace Enrichment.Example.Validators;

/// <summary>
/// Валидатор отмены: заказ из «БД» + политика возврата из «шлюза».
/// Оба результата кладутся в payload — хендлеру останется посчитать сумму возврата.
/// </summary>
public class RefundEligibilityValidator : EnrichingValidator<Contracts.CancelOrderRequest, (Order Order, RefundPolicy Policy)>
{
  private readonly IOrderRepository _orders;
  private readonly IPaymentGateway _gateway;

  public RefundEligibilityValidator(IOrderRepository orders, IPaymentGateway gateway)
  {
    _orders = orders;
    _gateway = gateway;
    RuleFor(x => x.Reason).NotEmpty();
  }

  protected override async ValueTask<(Order Order, RefundPolicy Policy)> EnrichAsync(Contracts.CancelOrderRequest request, CancellationToken cancellationToken)
  {
    var order = await _orders.FindOrderAsync(request.OrderId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"заказ {request.OrderId} не найден");

    var policy = await _gateway.GetRefundPolicyAsync(order, cancellationToken).ConfigureAwait(false);

    return (order, policy);
  }
}

/// <summary>
/// Response-валидатор: прогоняется ПОСЛЕ хендлера и обогащает ResponseEnrichment —
/// уведомление об отмене, отправленное во внешнюю систему.
/// </summary>
public class CancellationNoticeValidator : EnrichingValidator<Contracts.CancelOrderResponse, (string NoticeId, DateTime SentAt)>
{
  private readonly INotificationApi _notifications;

  public CancellationNoticeValidator(INotificationApi notifications)
  {
    _notifications = notifications;
  }

  protected override async ValueTask<(string NoticeId, DateTime SentAt)> EnrichAsync(Contracts.CancelOrderResponse response, CancellationToken cancellationToken)
  {
    var noticeId = await _notifications.NotifyCancellationAsync(response.OrderId, cancellationToken).ConfigureAwait(false);

    return (noticeId, DateTime.UtcNow);
  }
}
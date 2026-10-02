using System.Collections.Generic;
using MediatR;

namespace Enrichment.Example.Contracts;

public sealed record CreateOrderRequest : IRequest<CreateOrderResponse>
{
  public required string OrderId { get; init; }
  public required string CustomerId { get; init; }
  public required string Currency { get; init; }
  public required IReadOnlyList<string> Items { get; init; }
}

public sealed record CreateOrderResponse
{
  public required string OrderId { get; init; }
  public required string Status { get; init; }
  public required decimal Total { get; init; }
}

public sealed record PayOrderRequest : IRequest<PayOrderResponse>
{
  public required string OrderId { get; init; }
  public required decimal Amount { get; init; }
  public required string MethodCode { get; init; }
}

public sealed record PayOrderResponse
{
  public required string PaymentId { get; init; }
  public required decimal ChargedAmount { get; init; }
}

public sealed record CancelOrderRequest : IRequest<CancelOrderResponse>
{
  public required string OrderId { get; init; }
  public required string Reason { get; init; }
}

public sealed record CancelOrderResponse
{
  public required string OrderId { get; init; }
  public required decimal RefundAmount { get; init; }
}

public sealed record IssueTicketRequest : IRequest<IssueTicketResponse>
{
  public required string OrderId { get; init; }
  public required string FlightNumber { get; init; }
  public required string PassengerId { get; init; }
  public required string Seat { get; init; }
}

public sealed record IssueTicketResponse
{
  public required string TicketNumber { get; init; }
  public required string Seat { get; init; }
  public required string LoyaltyTier { get; init; }
}
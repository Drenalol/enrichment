namespace Enrichment.Example.Domain;

// Доменные модели — их «достают из БД/внешних API» валидаторы и переиспользует хендлер.

public sealed class Order
{
  public required string Id { get; init; }
  public required string CustomerId { get; init; }
  public required decimal TotalAmount { get; init; }
  public required decimal PaidAmount { get; init; }
  public required string Status { get; init; }

  public decimal RemainingAmount => TotalAmount - PaidAmount;
}

public sealed class Customer
{
  public required string Id { get; init; }
  public required string Email { get; init; }
  public required int BonusBalance { get; init; }
}

public sealed class PaymentMethod
{
  public required string Code { get; init; }
  public required string Name { get; init; }
  public required decimal FeePercent { get; init; }
}

public sealed class RefundPolicy
{
  public required decimal FeePercent { get; init; }
  public required int DaysWindow { get; init; }
}

public sealed class Inventory
{
  public required string FlightNumber { get; init; }
  public required int SeatsLeft { get; init; }
  public required string AircraftType { get; init; }
}

public sealed class Passenger
{
  public required string Id { get; init; }
  public required string FullName { get; init; }
  public required int BonusMiles { get; init; }
}
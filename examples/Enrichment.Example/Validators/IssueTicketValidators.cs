using System;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Domain;
using Enrichment.Example.Ports;
using FluentValidation;

namespace Enrichment.Example.Validators;

/// <summary>«API-шный» валидатор: наличие мест на рейсе, остаток мест — в payload.</summary>
public class SeatAvailabilityValidator : EnrichingValidator<Contracts.IssueTicketRequest, (Inventory Inventory, int SeatsAfter)>
{
  private readonly IInventoryApi _inventory;

  public SeatAvailabilityValidator(IInventoryApi inventory)
  {
    _inventory = inventory;
    RuleFor(x => x.FlightNumber).NotEmpty().Matches(@"^[A-Z]{2}-\d+$");
  }

  protected override async ValueTask<(Inventory Inventory, int SeatsAfter)> EnrichAsync(Contracts.IssueTicketRequest request, CancellationToken cancellationToken)
  {
    var flight = await _inventory.FindFlightAsync(request.FlightNumber, cancellationToken).ConfigureAwait(false)
                 ?? throw new InvalidOperationException($"рейс {request.FlightNumber} не найден");

    if (flight.SeatsLeft == 0)
      throw new InvalidOperationException($"на рейсе {flight.FlightNumber} нет мест");

    return (flight, flight.SeatsLeft - 1);
  }
}

/// <summary>«API-шный» валидатор: пассажир из внешней системы + уровень программы лояльности.</summary>
public class PassengerValidator : EnrichingValidator<Contracts.IssueTicketRequest, (Passenger Passenger, string LoyaltyTier)>
{
  private readonly IPassengerApi _passengers;

  public PassengerValidator(IPassengerApi passengers)
  {
    _passengers = passengers;
    RuleFor(x => x.PassengerId).NotEmpty().Matches(@"^PAX-\d+$");
    RuleFor(x => x.Seat).NotEmpty();
  }

  protected override async ValueTask<(Passenger Passenger, string LoyaltyTier)> EnrichAsync(Contracts.IssueTicketRequest request, CancellationToken cancellationToken)
  {
    var passenger = await _passengers.FindPassengerAsync(request.PassengerId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"пассажир {request.PassengerId} не найден");

    var tier = passenger.BonusMiles switch
    {
      >= 100_000 => "Platinum",
      >= 25_000 => "Gold",
      _ => "Basic",
    };

    return (passenger, tier);
  }
}
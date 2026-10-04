using System;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Domain;
using Enrichment.Example.Handlers;
using Enrichment.Example.Ports;
using FluentValidation;

namespace Enrichment.Example.Validation;

// ─── фаза 1: чистые правила ───

public class IssueTicketRequestRulesValidator : EnrichingValidator<Contracts.IssueTicketRequest>
{
  public IssueTicketRequestRulesValidator()
  {
    RuleFor(x => x.FlightNumber).NotEmpty().Matches(@"^[A-Z]{2}-\d+$");
    RuleFor(x => x.PassengerId).NotEmpty().Matches(@"^PAX-\d+$");
    RuleFor(x => x.Seat).NotEmpty();
  }
}

// ─── фаза 2: энричеры ───

public class FlightInventoryEnricher : Enricher<Contracts.IssueTicketRequest, (Inventory? Inventory, int SeatsAfter)>
{
  private readonly IInventoryApi _inventory;

  public FlightInventoryEnricher(IInventoryApi inventory) => _inventory = inventory;

  protected override async ValueTask<(Inventory?, int)> LoadAsync(Contracts.IssueTicketRequest request, CancellationToken cancellationToken)
  {
    var flight = await _inventory.FindFlightAsync(request.FlightNumber, cancellationToken).ConfigureAwait(false);

    return (flight, flight is null ? 0 : flight.SeatsLeft - 1);
  }
}

public class TicketPassengerEnricher : Enricher<Contracts.IssueTicketRequest, (Passenger? Passenger, string LoyaltyTier)>
{
  private readonly IPassengerApi _passengers;

  public TicketPassengerEnricher(IPassengerApi passengers) => _passengers = passengers;

  protected override async ValueTask<(Passenger?, string)> LoadAsync(Contracts.IssueTicketRequest request, CancellationToken cancellationToken)
  {
    var passenger = await _passengers.FindPassengerAsync(request.PassengerId, cancellationToken).ConfigureAwait(false);
    var tier = passenger?.BonusMiles switch
    {
      >= 100_000 => "Platinum",
      >= 25_000 => "Gold",
      _ => "Basic",
    };

    return (passenger, tier);
  }
}

// ─── фаза 3: контекстные правила ───

public class SeatAvailabilityValidator : EnrichingValidator<Contracts.IssueTicketRequest, IIssueTicketEnrichment>
{
  public SeatAvailabilityValidator()
  {
    RuleFor(x => x.FlightNumber).Custom((flightNumber, context) =>
    {
      var inventory = GetEnrichment(context).Inventory;

      if (inventory is null)
        context.AddFailure($"рейс {flightNumber} не найден");
      else if (inventory.SeatsLeft == 0)
        context.AddFailure($"на рейсе {flightNumber} нет мест");
    });
  }
}

public class PassengerValidator : EnrichingValidator<Contracts.IssueTicketRequest, IIssueTicketEnrichment>
{
  public PassengerValidator()
  {
    RuleFor(x => x.PassengerId).Custom((passengerId, context) =>
    {
      if (GetEnrichment(context).Passenger is null)
        context.AddFailure($"пассажир {passengerId} не найден");
    });
  }
}
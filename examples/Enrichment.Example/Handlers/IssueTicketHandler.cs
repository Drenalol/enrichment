using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Contracts;

namespace Enrichment.Example.Handlers;

public partial class IssueTicketHandler : EnrichedHandler<IssueTicketRequest, IssueTicketResponse>
{
  public IssueTicketHandler(
    IEnumerable<IDataEnricher<IssueTicketRequest>> requestEnrichers,
    IEnumerable<IEnrichingValidator<IssueTicketRequest>> requestValidators,
    IEnumerable<IDataEnricher<IssueTicketResponse>> responseEnrichers,
    IEnumerable<IEnrichingValidator<IssueTicketResponse>> responseValidators)
    : base(requestEnrichers, requestValidators, responseEnrichers, responseValidators)
  {
  }

  protected override Task<IssueTicketResponse> HandleAsync(IssueTicketRequest request, CancellationToken cancellationToken)
  {
    var inventory = Enrichment.Inventory!;
    var passenger = Enrichment.Passenger!;

    Console.WriteLine($"      [handler] билет: рейс {inventory.FlightNumber} ({inventory.AircraftType}), {passenger.FullName}, место {request.Seat}, осталось мест {Enrichment.SeatsAfter}, статус {Enrichment.LoyaltyTier}");

    var response = new IssueTicketResponse
    {
      TicketNumber = $"{inventory.FlightNumber}-{passenger.Id}",
      Seat = request.Seat,
      LoyaltyTier = Enrichment.LoyaltyTier,
    };

    return Task.FromResult(response);
  }
}
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enrichment.Example.Contracts;

namespace Enrichment.Example.Handlers;

public partial class IssueTicketHandler : EnrichedHandler<IssueTicketRequest, IssueTicketResponse>
{
  public IssueTicketHandler(IEnumerable<IEnrichingValidator<IssueTicketRequest>> requestValidators, IEnumerable<IEnrichingValidator<IssueTicketResponse>> responseValidators) : base(requestValidators, responseValidators)
  {
  }

  protected override Task<IssueTicketResponse> HandleAsync(IssueTicketRequest request, CancellationToken cancellationToken)
  {
    Console.WriteLine($"      [handler] билет: рейс {Enrichment.Inventory.FlightNumber} ({Enrichment.Inventory.AircraftType}), {Enrichment.Passenger.FullName}, место {request.Seat}, осталось мест {Enrichment.SeatsAfter}, статус {Enrichment.LoyaltyTier}");

    var response = new IssueTicketResponse
    {
      TicketNumber = $"{Enrichment.Inventory.FlightNumber}-{Enrichment.Passenger.Id}",
      Seat = request.Seat,
      LoyaltyTier = Enrichment.LoyaltyTier,
    };

    return Task.FromResult(response);
  }
}
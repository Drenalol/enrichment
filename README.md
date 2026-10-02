# Enrichment

**The data your validators already loaded, your handlers stop loading twice.**

A FluentValidation validator that checks an order usually fetches that order from the database. The handler then fetches the *same* order again, because the validator's findings were thrown away after `IsValid`. Enrichment turns the validator's loaded data into a compile-time contract: each validator declares the payload it produces, a Roslyn source generator composes those payloads into a strongly-typed `Enrichment` class per handler, and the handler consumes the very same objects the validators returned — no second query, no reflection, no service-locator.

```
validator (rules + load) ──► payload ──┐
validator (rules + load) ──► payload ──┼──► source generator ──► Enrichment (generated, typed)
validator (rules + load) ──► payload ──┘                            ▲
                                                                    │
handler ──── runs validators, collects payloads ────────────────────┘
```

## Getting started

```
dotnet add package Enrichment
```

The package ships the runtime contracts **and** the source generator; referencing one package is enough.

### 1. Write a validator

Rules in the constructor (FluentValidation), data loading in `EnrichAsync`. The payload type — a named tuple, a record, or any type with public properties — is what the generator will flatten into the handler's context.

```csharp
public class OrderExistsValidator : EnrichingValidator<CreateOrderRequest, (Order Order, decimal ItemsTotal)>
{
  private readonly IOrderRepository _orders; // your DbContext/HTTP client

  public OrderExistsValidator(IOrderRepository orders)
  {
    _orders = orders;
    RuleFor(x => x.OrderId).NotEmpty();
  }

  protected override async ValueTask<(Order Order, decimal ItemsTotal)> EnrichAsync(
      CreateOrderRequest request, CancellationToken cancellationToken)
  {
    var order = await _orders.FindOrderAsync(request.OrderId, cancellationToken)
                ?? throw new InvalidOperationException($"order {request.OrderId} not found");

    return (order, order.TotalAmount);
  }
}
```

Validators that only check and load nothing simply derive from `EnrichingValidator<TRequest>` — they keep their rules but never appear in the context.

### 2. Write a handler

Derive from `EnrichedHandler<TRequest, TResponse>` and mark it `partial`. The handler's constructor takes only validators — the "outside world" (repositories, APIs) stays entirely in the validators.

```csharp
public partial class CreateOrderHandler : EnrichedHandler<CreateOrderRequest, CreateOrderResponse>
{
  public CreateOrderHandler(
      IEnumerable<IEnrichingValidator<CreateOrderRequest>> requestValidators,
      IEnumerable<IEnrichingValidator<CreateOrderResponse>> responseValidators)
    : base(requestValidators, responseValidators)
  {
  }

  protected override Task<CreateOrderResponse> HandleAsync(CreateOrderRequest request, CancellationToken cancellationToken)
  {
    // generated, compile-checked, zero extra queries:
    return Task.FromResult(new CreateOrderResponse
    {
      OrderId = Enrichment.Order.Id,
      Status = Enrichment.IsVip ? "ConfirmedPriority" : "Confirmed",
      Total = Enrichment.ItemsTotal,
    });
  }
}
```

### 3. Register

```csharp
services.AddEnrichmentValidators(typeof(Program).Assembly); // Scrutor scan
services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));
```

`AddEnrichmentValidators` finds every `IEnrichingValidator<TRequest>` implementation in the given assemblies and registers it as itself. Plain `AddTransient<...>` lines (or Scrutor `AddClasses().AsImplementedInterfaces()`) work just as well — the generator does not care how DI assembles the injection, only which validator types are visible at compile time.

### What gets generated

For every handler the generator emits one class per side and wires the handler to it:

```csharp
// generated from OrderExistsValidator + CustomerValidator
public sealed class CreateOrderEnrichment
{
  public Order Order { get; }
  public decimal ItemsTotal { get; }
  public Customer Customer { get; }
  public bool IsVip { get; }
  ...
}

public partial class CreateOrderHandler
{
  protected CreateOrderEnrichment Enrichment { get; }
}
```

Response validators (`IEnrichingValidator<TResponse, TData>`) run *after* the handler and fill a separate `ResponseEnrichment`, e.g. `ResponseEnrichment.NoticeId`.

## Behavior you can rely on

- **Rules before data.** The base bridge runs FluentValidation rules first; `EnrichAsync` is never reached for an invalid request, so a rejected request costs zero I/O. Rule failure throws `ValidationException` with the usual `FluentValidation.Results`.
- **One payload per request type per validator.** Declaring two `IEnrichingValidator<TRequest, TData>` on one class is `ENR004`.
- **Name collisions are errors, not surprises.** Two validators producing the same member name report `ENR002` telling you exactly which pair collides.
- **Fail-fast on DI drift.** If DI resolves a validator the generated context has never seen (plugin DLL, open generics), the first handler call throws instead of silently ignoring the validator.
- **Typed access with a good message.** Reading a member whose validator did not run throws `InvalidOperationException` naming the validator and why it may not have run.
- **AOT-friendly.** The generated context is plain C#: a `Dictionary<Type, object>` lookup behind typed properties. No runtime emit, no reflection.

## Payload shapes

| `TData` | context members |
|---|---|
| `(Order Order, decimal Total)` | `Order`, `Total` — element names |
| `(Order, decimal)` | `Item1`, `Item2` |
| nested tuples | flattened with `Item2.Rest.Item1`-style accessors |
| `record OrderBundle(Order Order, bool IsVip)` | `Order`, `IsVip` — public properties |
| `string`, `int`, … | `ENR001` — wrap it in a tuple or record |

## Diagnostics

| ID | Severity | Meaning |
|---|---|---|
| ENR001 | Error | Payload expands to no members (atomic type) |
| ENR002 | Error | Two validators produce the same Enrichment member |
| ENR003 | Warning | Validator not accessible from the handler; its members were skipped |
| ENR004 | Error | One validator declares conflicting payloads for the same request type |

## Requirements

- .NET 8+ (the runtime package targets `net8.0`; transitive: FluentValidation 12, MediatR 12.4, Scrutor 6)
- Any SDK/VS with Roslyn 4.8+ (.NET 8 SDK / Visual Studio 2022 17.8 or newer) runs the generator

## Example

A runnable console demo lives in [`examples/Enrichment.Example`](examples/Enrichment.Example): four handlers, six fake "external resources" with call counters, and a walk-through of validation-before-I/O, handler-side business rules on enriched data, and response-side enrichment.

```
── 1. CreateOrder — request is valid ────────────────────────────────
      [db] SELECT * FROM orders WHERE id = 'ORD-100'
      [api] GET /customers/CUS-7
      [handler] building confirmation from Enrichment: order ORD-100, customer ivanov@example.com (VIP: True), total 12500
      [counters] orders: 1, customers: 1          ← one DB hit per validator, zero in the handler
```

## Repository layout

```
src/Enrichment             runtime contracts + AddEnrichmentValidators (packaged as Enrichment)
src/Enrichment.Generator   the Roslyn source generator (packed into the Enrichment package)
tests/Enrichment.Tests     generator tests driven through CSharpGeneratorDriver
examples/Enrichment.Example console demo
```

## License

MIT — see [LICENSE](LICENSE).

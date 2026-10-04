# Enrichment

**The data your validators need, your enrichers load once.**

A FluentValidation rule that checks an order usually needs that order fetched from the database — but FluentValidation rules run before any data loading, so "order not found" ends up as an exception thrown by the loader instead of a proper validation failure, and the handler fetches the same order a second time anyway. Enrichment splits the pipeline into layers: **enrichers** load data into a payload, a Roslyn source generator composes those payloads into a strongly-typed `Enrichment` class per handler, **validators** check business rules against that context through the generated `I{Context}` interface, and the **handler** consumes the very same objects — no second query, no reflection, no service-locator.

```
 request
   │
   ▼ 1. rules without data     EnrichingValidator<TRequest> — FluentValidation, zero I/O
   │
   ▼ 2. data                   IDataEnricher<TRequest, TData> loads from repositories/APIs
   │       └─► payload ──► source generator ──► I{Context} + Enrichment (generated, typed)
   │
   ▼ 3. rules on data          EnrichingValidator<TRequest, ICreateOrderEnrichment> reads Enrichment
   │       └─► "order not found" is a ValidationFailure, not a loader exception
   ▼
   4. handler                  the same typed Enrichment, zero extra queries
   │
   ▼ response ──► response enrichers ──► response validators ──► response
```

## Getting started

```
dotnet add package Drenalol.Enrichment
```

The package ships the runtime contracts **and** the source generator; referencing one package is enough.

### 1. Write an enricher

Data loading only — no rules. The payload type — a named tuple, a record, or any type with public properties — is what the generator will flatten into the handler's context.

```csharp
public class OrderEnricher : Enricher<CreateOrderRequest, (Order? Order, decimal ItemsTotal)>
{
  private readonly IOrderRepository _orders; // your DbContext/HTTP client

  public OrderEnricher(IOrderRepository orders) => _orders = orders;

  protected override async ValueTask<(Order?, decimal)> LoadAsync(
      CreateOrderRequest request, CancellationToken cancellationToken)
  {
    var order = await _orders.FindOrderAsync(request.OrderId, cancellationToken);

    return (order, order?.TotalAmount ?? 0m);
  }
}
```

### 2. Write validators

Syntax rules go into pure validators (`EnrichingValidator<TRequest>`) — they run before any I/O. Data-dependent rules go into contextual validators: they name the **generated context interface** `I{Context}` (emitted by the generator from the enrichers' payloads — nothing to write by hand) as their second type parameter, and read data through `GetEnrichment`:

```csharp
public class OrderExistsValidator : EnrichingValidator<CreateOrderRequest, ICreateOrderEnrichment>
{
  public OrderExistsValidator()
  {
    RuleFor(x => x.OrderId).NotEmpty();

    RuleFor(x => x.OrderId).Custom((orderId, context) =>
    {
      if (GetEnrichment(context).Order is null)
        context.AddFailure($"order {orderId} not found");
    });
  }
}
```

`ICreateOrderEnrichment` is generated next to the context class and exposes exactly what the side's enrichers produce — a rule that reads a member no enricher loads is a compile error at the rule, not a runtime surprise. `GetEnrichment` reads the typed context from `ValidationContext.RootContextData` — the documented FluentValidation way to feed data into rules, wired up by the base handler. A hand-written second type parameter (any type that isn't the generated interface) is `ENR006`.

### 3. Write a handler

Derive from `EnrichedHandler<TRequest, TResponse>` and mark it `partial`. The handler's constructor takes only enrichers and validators — the "outside world" (repositories, APIs) stays entirely in the enrichers.

```csharp
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
    var order = Enrichment.Order!; // contextual validators already ensured it

    // generated, compile-checked, zero extra queries:
    return Task.FromResult(new CreateOrderResponse
    {
      OrderId = order.Id,
      Status = Enrichment.IsVip ? "ConfirmedPriority" : "Confirmed",
      Total = Enrichment.ItemsTotal,
    });
  }
}
```

### 4. Register

```csharp
services.AddEnrichment(typeof(Program).Assembly); // Scrutor scan
services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));
```

`AddEnrichment` finds every `IDataEnricher<TRequest>` and `IEnrichingValidator<TRequest>` implementation in the given assemblies and registers each under its closed contracts. Plain `AddTransient<...>` lines (or Scrutor `AddClasses().AsImplementedInterfaces()`) work just as well — the generator does not care how DI assembles the injection, only which enricher types are visible at compile time.

### What gets generated

For every handler the generator emits one context class per side plus its interface-view `I{Context}` built from the enrichers' payloads, and wires the handler to it:

```csharp
// generated from OrderEnricher + CustomerEnricher
public interface ICreateOrderEnrichment
{
  Order? Order { get; }
  decimal ItemsTotal { get; }
  Customer? Customer { get; }
  bool IsVip { get; }
}

public sealed class CreateOrderEnrichment : ICreateOrderEnrichment
{
  public Order? Order => ...;   // bag lookup by enricher type
  public decimal ItemsTotal => ...;
  public Customer? Customer => ...;
  public bool IsVip => ...;
}

public partial class CreateOrderHandler
{
  protected CreateOrderEnrichment Enrichment { get; }
}
```

Response enrichers (`IDataEnricher<TResponse, TData>`) run *after* the handler and fill a separate `ResponseEnrichment`, e.g. `ResponseEnrichment.NoticeId`.

### Renaming the context (optional)

By default a context is named after its handler: `CreateOrderHandler` → `CreateOrderEnrichment` / `ICreateOrderEnrichment`. The optional `[EnrichmentContext]` attribute overrides either side independently (the interface always gets the `I` prefix); unspecified sides keep the convention. Two handlers — or the two sides of one handler — claiming the same name in a namespace is `ENR007`.

```csharp
[EnrichmentContext(Request = "SalesContext", Response = "SalesReceiptContext")]
public partial class CreateOrderHandler : EnrichedHandler<CreateOrderRequest, CreateOrderResponse>
{
  // the context property stays Enrichment/ResponseEnrichment; contextual validators
  // reference it by the new interface name:
  //   EnrichingValidator<CreateOrderRequest, ISalesContext>
}
```

## Behavior you can rely on

- **Rules before I/O, data before context rules.** Pure validators run before any enricher, so a syntactically rejected request costs zero I/O. Contextual validators run after loading and reject with `ValidationException` carrying ordinary `ValidationFailure`s — "not found" is a rule failure, not an exception from a loader.
- **The context is a generated interface.** Contextual validators name the emitted `I{Context}` view of the side's enricher payloads — nothing to declare by hand. A rule reading a member no enricher produces is a compile error at the rule; a hand-written needs type is `ENR006`.
- **One payload per request type per enricher.** Declaring two `IDataEnricher<TRequest, TData>` on one class is `ENR004`.
- **Name collisions are errors, not surprises.** Two enrichers producing the same member name report `ENR002` telling you exactly which pair collides.
- **Fail-fast on DI drift.** If DI resolves an enricher or validator the generated context has never seen (plugin DLL, open generics), the first handler call throws instead of silently ignoring it.
- **Typed access with a good message.** Reading a member whose enricher did not run throws `InvalidOperationException` naming the enricher and why it may not have run.
- **AOT-friendly.** The generated context is plain C#: a `Dictionary<Type, object>` lookup behind typed properties. No runtime emit, no reflection.

## Payload shapes

| enricher `TData` | context members |
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
| ENR002 | Error | Two enrichers produce the same Enrichment member |
| ENR003 | Warning | Enricher or validator not accessible from the handler; its members were skipped |
| ENR004 | Error | One enricher declares conflicting payloads for the same request type |
| ENR006 | Error | A contextual validator's needs type is not the generated `I{Context}` interface |
| ENR007 | Error | Two context sides/handlers claim the same `[EnrichmentContext]` name in one namespace |

## Requirements

- .NET 8+ (the runtime package targets `net8.0`; transitive: FluentValidation 12, MediatR 12.4, Scrutor 6)
- Any SDK/VS with Roslyn 4.8+ (.NET 8 SDK / Visual Studio 2022 17.8 or newer) runs the generator

## Example

A runnable console demo lives in [`examples/Enrichment.Example`](examples/Enrichment.Example): four handlers, six fake "external resources" with call counters, and a walk-through of rules-before-I/O, data-checks-as-ValidationFailure, handler-side business rules on enriched data, and response-side enrichment.

```
── 1. CreateOrder — request is valid ────────────────────────────────
      [db] SELECT * FROM orders WHERE id = 'ORD-100'
      [api] GET /customers/CUS-7
      [handler] building confirmation from Enrichment: order ORD-100, customer ivanov@example.com (VIP: True), total 12500
      [counters] orders: 1, customers: 1          ← one DB hit per enricher, zero in the handler
```

## Repository layout

```
src/Enrichment             runtime contracts + AddEnrichment (packaged as Drenalol.Enrichment)
src/Enrichment.Generator   the Roslyn source generator (packed into the Enrichment package)
tests/Enrichment.Tests     generator tests driven through CSharpGeneratorDriver
examples/Enrichment.Example console demo
```

## License

MIT — see [LICENSE](LICENSE).

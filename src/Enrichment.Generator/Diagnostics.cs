using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Enrichment.Generator;

internal static class Diagnostics
{
  private const string Category = "Enrichment";

  public static readonly DiagnosticDescriptor PayloadNotExpandable = new(
    "ENR001",
    "Payload does not expand to members",
    "Payload '{0}' of validator {1} expands to no accessible members; wrap it in a named tuple or a type with public properties",
    Category,
    DiagnosticSeverity.Error,
    isEnabledByDefault: true
  );

  public static readonly DiagnosticDescriptor DuplicateEnrichmentMember = new(
    "ENR002",
    "Duplicate Enrichment member",
    "Enrichment member '{0}' is produced by both {1}; rename the tuple element or property in one of them",
    Category,
    DiagnosticSeverity.Error,
    isEnabledByDefault: true
  );

  public static readonly DiagnosticDescriptor ValidatorNotAccessible = new(
    "ENR003",
    "Validator not accessible from handler",
    "Validator {0} is not accessible from handler {1}; its {0} members were skipped",
    Category,
    DiagnosticSeverity.Warning,
    isEnabledByDefault: true
  );

  public static readonly DiagnosticDescriptor ConflictingPayloads = new(
    "ENR004",
    "Conflicting payload interfaces",
    "Validator {0} declares more than one payload for request type {1}; implement a single IEnrichingValidator<TRequest, TData>",
    Category,
    DiagnosticSeverity.Error,
    isEnabledByDefault: true
  );
}
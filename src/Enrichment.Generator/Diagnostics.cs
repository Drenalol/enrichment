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
    "Payload '{0}' expands to no accessible members; wrap it in a named tuple or a type with public properties",
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
    "Validator or enricher not accessible from handler",
    "Enrichment type {0} is not accessible from handler {1}; its members were skipped",
    Category,
    DiagnosticSeverity.Warning,
    isEnabledByDefault: true
  );

  public static readonly DiagnosticDescriptor ConflictingPayloads = new(
    "ENR004",
    "Conflicting payload interfaces",
    "Enricher {0} declares more than one payload for request type {1}; implement a single IDataEnricher<TRequest, TData>",
    Category,
    DiagnosticSeverity.Error,
    isEnabledByDefault: true
  );

  public static readonly DiagnosticDescriptor NeedsTypeMustBeGenerated = new(
    "ENR006",
    "Contextual validator needs type is not the generated context interface",
    "Contextual validator {0} declares needs {1}, which resolves at scan time; contextual validators must name the generated I{{Context}} interface for request type {2}, so do not write needs interfaces by hand",
    Category,
    DiagnosticSeverity.Error,
    isEnabledByDefault: true
  );

  public static readonly DiagnosticDescriptor DuplicateContextName = new(
    "ENR007",
    "Duplicate generated context name",
    "Context name '{0}' is claimed by both handler {1} and handler {2} in the same namespace; give each side a unique name via [EnrichmentContext] or drop the override",
    Category,
    DiagnosticSeverity.Error,
    isEnabledByDefault: true
  );
}
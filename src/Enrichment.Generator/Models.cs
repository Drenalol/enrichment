using System.Collections.Immutable;

namespace Enrichment.Generator;

// Модели — иммутабельные записи со строковыми ключами: не держат Compilation/Symbol,
// поэтому value-based equality корректно инвалидирует инкрементальный кэш.

internal sealed record OuterType(string Name, string Accessibility, ImmutableArray<string> TypeParameters);

internal sealed record HandlerInfo(
  string? Namespace,
  string Name,
  string Accessibility,
  ImmutableArray<OuterType> OuterTypes,
  string RequestKey,
  string ResponseKey
);

// Одна запись = одна пара (валидатор, TReq). None-валидатор: PayloadDisplay == null, Members пуст.
internal sealed record ValidatorInfo(
  string ValidatorTypeFq,
  string RequestKey,
  bool IsAccessible,
  string? PayloadDisplay,
  ImmutableArray<FlatMember> Members,
  ImmutableArray<ValidatorDiagnostic> Diagnostics
);

internal sealed record FlatMember(string Name, string TypeDisplay, string Accessor);

internal sealed record ValidatorDiagnostic(string Id, string[] Args);
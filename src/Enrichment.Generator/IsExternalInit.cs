// Polyfill: record-ы с init-аксессорами на netstandard2.0 (стандартная практика для analyzer-проектов).
// Тип реферится самим компилятором при эмите init-аксессоров (CS0518 без него), поэтому
// "never used" — ложное срабатывание: явных ссылок в коде у него и не может быть.
// ReSharper disable UnusedMember.Global
#pragma warning disable IDE0051
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit;
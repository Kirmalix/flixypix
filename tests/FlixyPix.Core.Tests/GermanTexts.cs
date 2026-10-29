using System.Runtime.CompilerServices;
using FlixyPix.Core;

namespace FlixyPix.Core.Tests;

/// <summary>The tests were written against the German texts, the former source language. Since English texts are the keys,
/// the test assembly loads German once before any test runs; <see cref="LocalizationTests"/> uses its own translators.</summary>
internal static class GermanTexts
{
    [ModuleInitializer]
    internal static void Use() => Texts.Use(Translator.Load("de"));
}

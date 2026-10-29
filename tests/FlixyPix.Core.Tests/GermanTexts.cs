using System.Globalization;
using System.Runtime.CompilerServices;
using FlixyPix.Core;

namespace FlixyPix.Core.Tests;

/// <summary>The tests were written against the German texts, the former source language. Since English texts are the keys,
/// the test assembly loads German once before any test runs; <see cref="LocalizationTests"/> uses its own translators.
/// Numbers and dates follow German formatting too, so results do not depend on the regional format of the machine
/// (hosted CI runners use en-US).</summary>
internal static class GermanTexts
{
    [ModuleInitializer]
    internal static void Use()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = german;
        CultureInfo.CurrentCulture = german;
        Texts.Use(Translator.Load("de", formatting: german));
    }
}

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Chooses the language once at start, before the first window exists: the setting of this computer or the
/// display language of Windows. Static texts are read when windows are built, so a change needs a restart.</summary>
internal static class LanguageSetup
{
 /// <summary>Display language of Windows, captured before FlixyPix sets its own.</summary>
 internal static CultureInfo DisplayCulture { get; private set; } = CultureInfo.CurrentUICulture;
 /// <summary>The setting the running program started with ("system" or a language code).</summary>
 internal static string StartedWith { get; private set; } = Texts.Automatic;
 private static bool applied;

 internal static void Apply(string? choice, bool useCustomTranslations = true)
 {
  if (applied) throw new InvalidOperationException("The language is chosen once per start.");
  applied = true;
  DisplayCulture = CultureInfo.CurrentUICulture;
  StartedWith = string.IsNullOrWhiteSpace(choice) ? Texts.Automatic : choice;
  var translator = Translator.Load(Texts.Resolve(choice, DisplayCulture), useCustomTranslations ? Texts.CustomDirectory : null);
  Texts.Use(translator);
  foreach (var problem in translator.Problems) ErrorLog.Write("Sprache", new InvalidDataException(problem));
  if (Texts.Find(translator.Language) is not { } language) return;
  var culture = CultureInfo.GetCultureInfo(language.CultureName);
  CultureInfo.DefaultThreadCurrentUICulture = culture; CultureInfo.CurrentUICulture = culture;
  // xml:lang picks fitting fonts (Japanese glyph forms) and hyphenation; numbers in code follow the regional format.
  FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
 }

 /// <summary>Choices for the settings window: automatic (naming the language it gives now), then every language in its own name.</summary>
 internal static IEnumerable<(string Value, string Label)> Choices()
 {
  var automatic = Texts.Find(Texts.Resolve(Texts.Automatic, DisplayCulture))!;
  yield return (Texts.Automatic, T("Follow Windows ({0})", automatic.NativeName));
  foreach (var language in Texts.Languages) yield return (language.Code, language.NativeName);
 }
}

/// <summary>XAML access to the texts: <c>Text="{local:T 'Settings'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string text) : MarkupExtension
{
 public string Text { get; } = text;
 public override object ProvideValue(IServiceProvider serviceProvider) => T(Text);
}

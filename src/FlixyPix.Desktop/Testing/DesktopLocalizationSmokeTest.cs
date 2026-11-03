using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 internal async Task RunLocalizationSmokeTestAsync(string directory)
 {
  static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
  static void RequireFooterFits(Window window)
  {
   if (window.Content is not WindowFrame frame) return;
   window.UpdateLayout();
   var contentBounds = new Rect(frame.RenderSize);
   contentBounds.Inflate(1, 1);
   foreach (var button in frame.Buttons.Children.OfType<Button>())
   {
    var bounds = button.TransformToAncestor(frame).TransformBounds(new Rect(button.RenderSize));
    Require(contentBounds.Contains(bounds), $"Footer action '{button.Content}' is outside the window: {bounds} / {contentBounds}.");
   }
  }
  var loaded = DesktopSettings.LoadFrom(Path.Combine(directory, "appsettings.json"));
  var expected = LanguageSetup.StartedWith == Translator.PseudoCode ? Translator.PseudoCode : Texts.Resolve(loaded.Language, LanguageSetup.DisplayCulture);
  Require(Texts.Current.Language == expected, "Saved language was not used at startup.");
  Require(CultureInfo.CurrentCulture.Name == Texts.Current.Formatting.Name, "UI language changed regional formatting.");
  await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Capture(RootContent, Path.Combine(directory, "main.png"));
  var adultPack = new AdultContentPackWindow(this, _repository);
  adultPack.Show();
  try
  {
   await adultPack.PreviewForTest(); Require(adultPack.PreviewCount >= 62, "Adult content pack is incomplete.");
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture((FrameworkElement)adultPack.Content, Path.Combine(directory, "adult-content-pack.png"));
   await adultPack.ApplyAllForTest(); Require(adultPack.Changed, "Content-pack dialog did not apply the import.");
   adultPack.UndoForTest(); Require(!adultPack.Changed, "Content-pack dialog did not undo the import.");
  }
  finally { adultPack.Close(); }
  var packagePath = Path.Combine(AppContext.BaseDirectory, "tag-vocabularies", (expected == Translator.PseudoCode ? "en" : expected) + TagPackages.Extension);
  var package = TagPackages.Read(packagePath);
  var plan = _repository.PlanTagPackage(package);
  var preview = new TagPackageWindow(this, Path.GetFileName(packagePath), plan, package.Warnings);
  preview.Show();
  try
  {
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   RequireFooterFits(preview);
   Capture((FrameworkElement)preview.Content, Path.Combine(directory, "tag-package.png"));
  }
  finally { preview.Close(); }
  _repository.ApplyTagPackage(plan, plan.Items.Select(i => i.Row.Line).ToArray());
  var tagDirectory = new DirectoryWindow(this, _repository);
  tagDirectory.Show();
  try
  {
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture((FrameworkElement)tagDirectory.Content, Path.Combine(directory, "directory.png"));
  }
  finally { tagDirectory.Close(); }
  var roleViewBefore = CaptureViewState(); var roleWidthBefore = Width; var roleThemeBefore = Theme.Mode;
  _repository.UpsertVocabularyTag(new("Synthetic profile", "Role layout")); _repository.SetTagRole("Role layout", TagRole.Performer);
  ApplyViewState(new ViewState { View = "performers", SearchText = "Synthetic profile" }); await _lastRefresh;
  try
  {
   foreach (var scheme in new[] { Theme.Mode })
   foreach (var width in new[] { 1000d, 1600d })
   {
    Theme.Apply(scheme); Width = width; await Task.Delay(100); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
    if (_roleBrowser == null || _roleBrowser.CompactLayout != (width == 1000)) throw new InvalidOperationException("Role layout did not adapt to the window width.");
    var frame = (WindowFrame)_roleBrowser.Content; var bounds = new Rect(_roleBrowser.RenderSize); bounds.Inflate(1, 1);
    foreach (var button in frame.Buttons.Children.OfType<Button>())
     Require(bounds.Contains(button.TransformToAncestor(_roleBrowser).TransformBounds(new Rect(button.RenderSize))), "Role footer action is clipped: " + button.Content);
    Require(((System.Windows.Media.SolidColorBrush)RootContent.Background).Color == Theme.Color("WindowBackground", scheme == ColorScheme.Dark), "Role theme background did not update.");
    Require(Theme.ReplacedKeys.Count == 0, "Role theme froze a shared brush.");
    File.AppendAllText(Path.Combine(directory, "role-theme-state.jsonl"), System.Text.Json.JsonSerializer.Serialize(new { Scheme = scheme.ToString(), Width = width, Actual = Theme.Mode.ToString(), Theme.IsDark, Replaced = Theme.ReplacedKeys.ToArray(), RootColor = ((System.Windows.Media.SolidColorBrush)RootContent.Background).Color.ToString(), SameRootBrush = ReferenceEquals(RootContent.Background, Theme.Brush("WindowBackground")), Colors = new[] { "Text", "WindowBackground", "ChromeBackground" }.Select(k => new { Key = k, Color = ((System.Windows.Media.SolidColorBrush)Theme.Brush(k)).Color.ToString(), Frozen = Theme.Brush(k).IsFrozen }) }) + Environment.NewLine);
    foreach (var visual in Descendants<UIElement>(RootContent)) visual.InvalidateVisual();
    RootContent.InvalidateVisual(); RootContent.UpdateLayout();
    Capture(this, Path.Combine(directory, "role-" + scheme + "-" + width + ".png"));
   }
   var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
   File.WriteAllText(Path.Combine(directory, "role-observed-dpi.json"), System.Text.Json.JsonSerializer.Serialize(new { dpi.DpiScaleX, dpi.DpiScaleY, Note = "Observed runtime DPI only; width/theme tests do not establish other physical DPI settings." }));
  }
  finally { Width = roleWidthBefore; Theme.Apply(roleThemeBefore); ApplyViewState(roleViewBefore); await _lastRefresh; }
  // Group-mode labels must be complete messages: sentence fragments cannot
  // express all/any/none naturally in languages with different word order.
  var expression = SearchExpression.All(
   SearchExpression.Where(new() { Field = SearchField.Title, Comparison = SearchComparison.Contains, Text = "alpha" }),
   SearchExpression.Where(new() { Field = SearchField.Title, Comparison = SearchComparison.Contains, Text = "beta" }));
  var search = new AdvancedSearchWindow(this, expression);
  search.Show();
  try
  {
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   RequireFooterFits(search);
   var mode = Descendants<ComboBox>(search).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == T("Group operator"));
   var modeLabels = new[] { T("All conditions are met"), T("At least one condition is met"), T("No conditions are met") };
   Require(mode.Items.Cast<object>().Select(item => item.ToString()).SequenceEqual(modeLabels), "Search modes do not use complete localized messages.");
   foreach (var (index, op) in new[] { (0, SearchOperator.All), (1, SearchOperator.Any), (2, SearchOperator.Not) })
   {
    mode.SelectedIndex = index;
    var actual = search.RootGroup.Read();
    Require(actual?.Operator == op, "Changing the localized mode changed the logical search operator.");
    Require(index != 2 || actual!.Children.Single().Operator == SearchOperator.Any, "No conditions must negate the union of conditions.");
   }
   mode.SelectedIndex = 0;
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture((FrameworkElement)search.Content, Path.Combine(directory, "advanced-search.png"));
  }
  finally { search.Close(); }
  var rule = new TagRuleEditorWindow(this, new() { Name = "Localization", Groups = [new()
   { Conditions = [new(TagRuleField.Filename, TagRuleComparison.Contains, "alpha")], Tags = ["example"] }] });
  rule.Show();
  try
  {
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   RequireFooterFits(rule);
   var match = Descendants<ComboBox>(rule).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == T("Combination"));
   Require(match.Items.Cast<object>().Select(item => item.ToString()).SequenceEqual(new[] { T("All conditions are met"), T("At least one condition is met") }), "Rule modes do not use complete localized messages.");
   Capture((FrameworkElement)rule.Content, Path.Combine(directory, "tag-rule.png"));
  }
  finally { rule.Close(); }
  var settings = new SettingsWindow(this, _repository, _settings, _settings.DataDirectory, () => { }, () => { });
  settings.Show();
  try
  {
   Require(settings.Title == T("Settings · FlixyPix"), "Window title was not translated.");
   Require(expected is "de" or "qps" || settings.Title != "Einstellungen · FlixyPix", "German title leaked into another language.");
   var pages = new[] { "Appearance", "General", "Home page", "Video player", "Thumbnails", "Security", "Database" };
   foreach (var page in pages)
   {
    settings.SelectPage(page);
    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    Capture((FrameworkElement)settings.Content, Path.Combine(directory, "settings-" + Array.IndexOf(pages, page) + ".png"));
   }
   settings.LanguageChoice.SelectedItem = settings.LanguageChoice.Items.OfType<ComboBoxItem>().Single(i => (string)i.Tag == "en");
   Require(settings.TrySave(), "Language choice could not be saved.");
   var changed = loaded with { Language = settings.Result!.Language };
   var roundtrip = Path.Combine(directory, "saved-settings.json");
   changed.SaveTo(roundtrip);
   Require(DesktopSettings.LoadFrom(roundtrip).Language == "en", "Saved language did not survive a settings reload.");
   File.WriteAllText(Path.Combine(directory, "language.txt"), expected + "\n" + CultureInfo.CurrentCulture.Name + "\n" + settings.Title);
  }
  finally { settings.Close(); }
 }
}

using System.Windows;
namespace FlixyPix.Desktop;
public static class SidebarCard
{
 public static readonly DependencyProperty ActionsProperty=DependencyProperty.RegisterAttached("Actions",typeof(object),typeof(SidebarCard));
 public static void SetActions(DependencyObject obj,object value)=>obj.SetValue(ActionsProperty,value);
 public static object GetActions(DependencyObject obj)=>obj.GetValue(ActionsProperty);
 public static readonly DependencyProperty GlyphProperty=DependencyProperty.RegisterAttached("Glyph",typeof(string),typeof(SidebarCard));
 public static void SetGlyph(DependencyObject obj,string value)=>obj.SetValue(GlyphProperty,value);
 public static string GetGlyph(DependencyObject obj)=>(string)obj.GetValue(GlyphProperty);
}

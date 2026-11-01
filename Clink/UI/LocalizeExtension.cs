using System.Windows;
using System.Windows.Markup;
using Clink.Services.Localization;

[assembly: XmlnsDefinition("http://schemas.euphoricsoftware.com/clink", "Clink.UI")]
[assembly: XmlnsPrefix("http://schemas.euphoricsoftware.com/clink", "l")]

namespace Clink.UI;

/// <summary>
/// <c>Text="{l:T 'Save clip'}"</c>: the text of the active UI language for an English source string (see <see cref="L"/>). Gives a plain
/// string once, when the XAML is loaded, so it works wherever a string does: Text, Content, Header, ToolTip, Title,
/// AutomationProperties.Name, and in Style setters and templates. An apostrophe inside the key is written <c>\'</c>. The language
/// does not change while the app runs, so nothing needs to watch it. In compiled XAML (every window and control of Clink) declare
/// <c>xmlns:l="clr-namespace:Clink.UI"</c>: the markup compiler does not see this assembly's own XmlnsDefinition URI (error MC3074; proved by
/// Tests/L10nCompiledProbe.xaml). The URI <c>http://schemas.euphoricsoftware.com/clink</c> works only for XAML loaded at run time (XamlReader).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public TExtension() { }
    public TExtension(string key) { Key = key; }

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
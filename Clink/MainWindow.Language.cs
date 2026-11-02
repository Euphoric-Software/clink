using System.Windows;
using System.Windows.Controls;
using Clink.Services;
using Clink.Services.Localization;

namespace Clink;

/// <summary>
/// The App tab's LANGUAGE card: "Automatic (Windows display language)" and the languages that can be picked, by their own names (never
/// translated, each in its own direction). A pick is saved at once as Settings.Language ("auto" or a code) and takes effect at the next
/// start; when it differs from the language Clink is running in, "Restart Clink to use the new language." (written in the language just
/// chosen, so the user can tell what they picked) and "Restart now" appear.
/// </summary>
public partial class MainWindow
{
    /// <summary>The Windows display-language list Automatic walks. The unit tests replace it.</summary>
    internal Func<IReadOnlyList<string>> WindowsLanguages { get; set; } = LanguageNegotiation.WindowsPreferred;

    /// <summary>The language this process runs in, as far as the picker is concerned: a pseudo-locale (a look at the layout, not a language) counts as English.</summary>
    private static string RunningLanguage => L.Code is L.PseudoCode or L.PseudoRtlCode ? "en" : L.Code;

    /// <summary>The language a Settings.Language value stands for right now (Automatic: the first Windows display language Clink has).</summary>
    internal string ChosenLanguage(string? setting) => LanguageNegotiation.Resolve(setting, WindowsLanguages(), L.Available);

    /// <summary>Fills the card from the settings: Automatic, then every available language. Filling is not a choice: nothing is written.</summary>
    internal void FillLanguageCard(Settings s)
    {
        bool was = _filling;
        _filling = true;
        try
        {
            LanguageCombo.Items.Clear();
            LanguageCombo.Items.Add(new ComboBoxItem { Content = L.T("Automatic (Windows display language)"), Tag = LanguageNegotiation.Auto });
            foreach (var k in L.Known)
                if (L.Available.Contains(k.Code))
                    LanguageCombo.Items.Add(new ComboBoxItem { Content = k.NativeName, Tag = k.Code, Language = System.Windows.Markup.XmlLanguage.GetLanguage(k.Code), FlowDirection = k.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight });
            string current = L.CanonicalCode(s.Language) ?? LanguageNegotiation.Auto;
            var items = LanguageCombo.Items.OfType<ComboBoxItem>().ToList();
            LanguageCombo.SelectedItem = items.FirstOrDefault(i => (string?)i.Tag == current) ?? items[0];
        }
        finally { _filling = was; }
        UpdateLanguageHint();
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _filling || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        var s = _app.Settings;
        if (!string.Equals(s.Language, value, StringComparison.Ordinal))
        {
            s.Language = value;
            s.Save();
        }
        UpdateLanguageHint();
    }

    private static string RestartHintIn(string locale) => L.TIn(locale, "Restart Clink to use the new language.");

    /// <summary>Shows the restart hint and button when the saved choice is not the language this process runs in; the hint is in the chosen language.</summary>
    internal void UpdateLanguageHint()
    {
        string chosen = ChosenLanguage(_app.Settings.Language);
        bool differs = !chosen.Equals(RunningLanguage, StringComparison.OrdinalIgnoreCase);
        LanguageRestartPanel.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
        if (!differs) { SetLive(LanguageRestartHint, ""); return; }
        LanguageRestartHint.Language = System.Windows.Markup.XmlLanguage.GetLanguage(chosen);   // the glyphs of the language it is written in (CJK)
        SetLive(LanguageRestartHint, RestartHintIn(chosen));   // screen readers announce it
        LanguageRestartHint.FlowDirection = L.Known.Any(k => k.Code == chosen && k.Rtl) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    private void LanguageRestart_Click(object sender, RoutedEventArgs e) => _app.RestartForLanguage(this);

    // ------------------------------------------------------------------ the archive rule

    /// <summary>The English archive rule: one sentence with the two boxes' places in it. The translated sentence may put {days} and {size} in any order.</summary>
    internal const string ArchiveRuleEnglish = "Move clips older than {days} days, or when the local folder exceeds {size} GB · whichever comes first";

    /// <summary>The rule's text around the two boxes: before the first box, between them, after the second; and which box comes first. Null when {days} or {size} is missing or repeated.</summary>
    internal readonly record struct ArchiveRuleParts(string Before, string Between, string After, bool DaysFirst);

    internal static ArchiveRuleParts? SplitArchiveRule(string template)
    {
        const string days = "{days}", size = "{size}";
        int d = template.IndexOf(days, StringComparison.Ordinal), s = template.IndexOf(size, StringComparison.Ordinal);
        if (d < 0 || s < 0 || template.LastIndexOf(days, StringComparison.Ordinal) != d || template.LastIndexOf(size, StringComparison.Ordinal) != s) return null;
        bool daysFirst = d < s;
        int a = daysFirst ? d : s, aLen = daysFirst ? days.Length : size.Length, b = daysFirst ? s : d;
        return new ArchiveRuleParts(template[..a].Trim(), template[(a + aLen)..b].Trim(), template[(b + (daysFirst ? size.Length : days.Length))..].Trim(), daysFirst);
    }

    /// <summary>Lays the archive rule out from the translated sentence: text, box, text, box, text, in that language's order. A sentence without both places uses the English one.</summary>
    internal void BuildArchiveRulesRow() =>
        BuildArchiveRulesRow(L.T("Move clips older than {days} days, or when the local folder exceeds {size} GB · whichever comes first", ("days", "{days}"), ("size", "{size}")));

    internal void BuildArchiveRulesRow(string template)
    {
        var parts = SplitArchiveRule(template) ?? SplitArchiveRule(ArchiveRuleEnglish)!.Value;
        var panel = ArchiveRulesPanel;
        foreach (var box in new[] { ArchiveDaysBox, ArchiveGbBox }) (box.Parent as Panel)?.Children.Remove(box);   // out of the unit that holds it (or the panel, the first time)
        panel.Children.Clear();   // (and put back where the sentence has them)
        var (first, second) = parts.DaysFirst ? (ArchiveDaysBox, ArchiveGbBox) : (ArchiveGbBox, ArchiveDaysBox);
        if (parts.Before.Length > 0) panel.Children.Add(RuleText(parts.Before, new Thickness(0, 0, 6, 4)));
        panel.Children.Add(RuleUnit(first, parts.Between, new Thickness(0, 0, 6, 4)));
        panel.Children.Add(RuleUnit(second, parts.After, new Thickness(0, 0, 0, 4)));
    }

    private TextBlock RuleText(string text, Thickness margin)
    {
        var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = margin };
        tb.SetResourceReference(FrameworkElement.StyleProperty, "MutedText");
        return tb;
    }

    /// <summary>A box with the text that follows it, kept on one line (the rule wraps between the units only).</summary>
    private UIElement RuleUnit(TextBox box, string textAfter, Thickness margin)
    {
        var unit = new StackPanel { Orientation = Orientation.Horizontal };
        unit.Children.Add(box);
        if (textAfter.Length > 0) unit.Children.Add(RuleText(textAfter, margin));
        return unit;
    }
}
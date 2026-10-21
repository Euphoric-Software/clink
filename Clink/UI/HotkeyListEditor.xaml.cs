using System.Windows;
using System.Windows.Controls;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// The hotkey list (docs/SPEC-v2.md §6): one row per <see cref="HotkeyAction"/> with the key combo (click, then press the
/// keys: <see cref="Hotkeys.CaptureNext"/>), the action (clip / pause / share last) and, for clips, the replay length on the
/// slider's 5 s – 1 h scale (0 = whatever the replay-length slider says). <see cref="Changed"/> fires after every edit with the
/// list already updated in <see cref="Actions"/>; the window saves it and calls <c>AppController.ReloadHotkeys()</c>.
/// </summary>
public partial class HotkeyListEditor : UserControl
{
    private static readonly (string label, string value)[] s_actions =
    {
        ("Clip", "clip"), ("Pause / resume", "pause"), ("Share last clip", "share-last"),
    };
    private static readonly int[] s_lengths = { 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 300, 600, 900, 1200, 1800, 2700, 3600 };

    private readonly List<HotkeyAction> _actions = new();
    private int _defaultSeconds = 120;
    private bool _building;
    private Button? _capturing;
    private string? _capturingLabel;

    private sealed record Choice(string Label, object Value) { public override string ToString() => Label; }

    private Window? _window;

    public HotkeyListEditor()
    {
        InitializeComponent();
        // An armed capture takes the next key pressed anywhere on the system: it must not outlive the window being the
        // active one (Alt+Tab to the game, a click on Discord) or the editor being on screen.
        Loaded += (_, _) =>
        {
            if (_window != null) _window.Deactivated -= OnWindowDeactivated;
            _window = Window.GetWindow(this);
            if (_window != null) _window.Deactivated += OnWindowDeactivated;
        };
        Unloaded += (_, _) =>
        {
            CancelCapture();
            if (_window != null) { _window.Deactivated -= OnWindowDeactivated; _window = null; }
        };
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) CancelCapture(); };
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => CancelCapture();

    /// <summary>Fires after the user changed something; <see cref="Actions"/> already holds the new list.</summary>
    public event Action? Changed;

    /// <summary>The bindings as edited (same objects as passed to <see cref="Load"/> where unchanged).</summary>
    internal IReadOnlyList<HotkeyAction> Actions => _actions;

    /// <summary>The replay-length slider's value, shown in the "Replay length (1:30)" default entry of every clip row.</summary>
    public int DefaultSeconds
    {
        get => _defaultSeconds;
        set
        {
            if (value == _defaultSeconds) return;
            _defaultSeconds = value;
            Rebuild();
        }
    }

    internal void Load(IEnumerable<HotkeyAction> actions, int defaultSeconds)
    {
        _defaultSeconds = defaultSeconds;
        _actions.Clear();
        _actions.AddRange(actions.Where(a => a != null));
        Rebuild();
    }

    // ------------------------------------------------------------------ rows

    private void Rebuild()
    {
        _building = true;
        try
        {
            CancelCapture();
            Rows.Children.Clear();
            foreach (var a in _actions) Rows.Children.Add(BuildRow(a));
            EmptyText.Visibility = _actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _building = false; }
    }

    private UIElement BuildRow(HotkeyAction a)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 84 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var keys = new Button
        {
            Content = KeysLabel(a),
            Padding = new Thickness(8, 6, 8, 6),   // "Press keys" stays on one line down to the column's MinWidth: the row does not jump when a capture starts
            HorizontalContentAlignment = HorizontalAlignment.Left,
            FontWeight = FontWeights.SemiBold,
            ToolTip = "Click, then press the key combo you want (Esc cancels)",
            Tag = a,
        };
        keys.Click += (_, _) => BeginCapture(keys, a);
        keys.LostKeyboardFocus += (_, _) => { if (ReferenceEquals(_capturing, keys)) CancelCapture(); };   // clicked elsewhere: the next key is not a hotkey
        grid.Children.Add(keys);

        var oneLine = TryFindResource("OneLine") as DataTemplate;
        var action = new ComboBox { Margin = new Thickness(8, 0, 0, 0), Width = 128, ToolTip = "What the keys do", ItemTemplate = oneLine };
        action.ItemsSource = s_actions.Select(x => new Choice(x.label, x.value)).ToArray();
        action.SelectedItem = ((Choice[])action.ItemsSource).FirstOrDefault(c => string.Equals((string)c.Value, Normalize(a.Action), StringComparison.OrdinalIgnoreCase)) ?? ((Choice[])action.ItemsSource)[0];
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);

        var length = new ComboBox { Margin = new Thickness(8, 0, 0, 0), Width = 124, ToolTip = "How much this hotkey saves: the replay-length slider's value (Default), or a fixed length up to it (the buffer holds no more)", ItemTemplate = oneLine };
        FillLengths(length, a.Seconds);
        length.Visibility = Hotkeys.IsClip(a.Action) ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(length, 2);
        grid.Children.Add(length);

        var remove = new Button
        {
            Style = TryFindResource("GlyphButton") as Style,
            Content = "",
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Remove this hotkey",
        };
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        action.SelectionChanged += (_, _) =>
        {
            if (_building || action.SelectedItem is not Choice c) return;
            a.Action = (string)c.Value;
            length.Visibility = Hotkeys.IsClip(a.Action) ? Visibility.Visible : Visibility.Collapsed;
            Raise();
        };
        length.SelectionChanged += (_, _) =>
        {
            if (_building || length.SelectedItem is not Choice c) return;
            a.Seconds = (int)c.Value;
            Raise();
        };
        remove.Click += (_, _) =>
        {
            if (ReferenceEquals(_capturing, keys)) CancelCapture();
            _actions.Remove(a);
            Rebuild();
            Raise();
        };
        return grid;
    }

    private void FillLengths(ComboBox combo, int current)
    {
        // Only lengths the replay buffer can hold: the audio timelines keep the slider's value, so a longer pick would start
        // silent (the engine clamps the save to the buffer anyway). A saved value above it stays selectable, marked.
        var items = new List<Choice> { new($"Default · {Capabilities.FormatDuration(_defaultSeconds)}", 0) };
        items.AddRange(s_lengths.Where(l => l <= _defaultSeconds).Select(l => new Choice(Capabilities.FormatDuration(l), l)));
        if (current > 0 && !items.Any(c => (int)c.Value == current))
            items.Add(new Choice(current > _defaultSeconds ? $"{Capabilities.FormatDuration(current)} (capped to {Capabilities.FormatDuration(_defaultSeconds)})" : Capabilities.FormatDuration(current), current));
        bool was = _building;
        _building = true;
        try
        {
            combo.ItemsSource = items;
            combo.SelectedItem = items.FirstOrDefault(c => (int)c.Value == Math.Max(0, current)) ?? items[0];
        }
        finally { _building = was; }
    }

    private static string Normalize(string? action)
    {
        string a = (action ?? "clip").Trim().ToLowerInvariant();
        return a == "share" ? "share-last" : a.Length == 0 ? "clip" : a;
    }

    private static string KeysLabel(HotkeyAction a) => a.Keys == null || a.Keys.Vk <= 0 ? "Not set" : a.Keys.ToString();

    // ------------------------------------------------------------------ capture

    private void BeginCapture(Button button, HotkeyAction a)
    {
        CancelCapture();
        _capturing = button;
        _capturingLabel = button.Content as string;
        button.Content = "Press keys";
        button.Focus();   // so clicking anywhere else (LostKeyboardFocus) disarms the capture
        Arm(button, a);
    }

    /// <summary>Waits for the next combo for row <paramref name="a"/>, again after one that could not be taken. Esc cancels.</summary>
    private void Arm(Button button, HotkeyAction a) => Hotkeys.CaptureNext(
        (vk, ctrl, alt, shift, win) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_capturing, button)) return;   // a newer capture replaced this one, or it was cancelled
            if (vk == 0x1B) { _capturing = null; button.Content = _capturingLabel; Hint(null); return; }   // Esc cancels
            var keys = new HotkeyBinding { Vk = vk, Ctrl = ctrl, Alt = alt, Shift = shift, Win = win };
            // Only the first row with a combo ever fires: a second one would look set up and do nothing. Still listening.
            if (_actions.Any(o => !ReferenceEquals(o, a) && o.Keys is { } k && k.Vk == vk && k.Ctrl == ctrl && k.Alt == alt && k.Shift == shift && k.Win == win))
            {
                Hint($"{keys} is already used by another hotkey. Press other keys, or Esc.");
                Arm(button, a);
                return;
            }
            _capturing = null;
            Hint(null);
            a.Keys = keys;
            button.Content = keys.ToString();
            Raise();
        }),
        // A bare letter, digit or editing key would be eaten in every program from then on: not taken, still listening.
        () => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_capturing, button)) Hint("Letters, digits and editing keys need Ctrl or Alt; F-keys, ScrollLock and Pause work alone.");
        }));

    /// <summary>Shows why the last keys were not taken under the rows, or hides it (null).</summary>
    private void Hint(string? text)
    {
        CaptureHint.Text = text ?? "";
        CaptureHint.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Stops waiting for keys (the previous "Press keys" button gets its label back).</summary>
    public void CancelCapture()
    {
        if (_capturing == null) return;
        Hotkeys.CaptureNext(null);
        _capturing.Content = _capturingLabel;
        _capturing = null;
        Hint(null);
    }

    // ------------------------------------------------------------------ add

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        // A fresh row has no keys yet: the hook skips it (Vk == 0) until the user presses some.
        var a = new HotkeyAction { Keys = new HotkeyBinding { Vk = 0 }, Action = "clip", Seconds = 0 };
        _actions.Add(a);
        Rebuild();
        if (Rows.Children.Count > 0 && Rows.Children[^1] is Grid g && g.Children.Count > 0 && g.Children[0] is Button b) BeginCapture(b, a);
        Raise();
    }

    private void Raise() { if (!_building) Changed?.Invoke(); }
}

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace Clink.UI;

/// <summary>
/// A text that acts like a link for the mouse (the owner handles the click) and also for assistive technology and UI Automation: it offers the
/// Invoke pattern, which raises <see cref="Invoked"/>. Used by the footer version number, whose click opens the version popup.
/// </summary>
public class InvokableTextBlock : TextBlock
{
    /// <summary>An assistive technology (or a UI Automation client) invoked the text.</summary>
    public event EventHandler? Invoked;

    internal void RaiseInvoked() => Invoked?.Invoke(this, EventArgs.Empty);

    protected override AutomationPeer OnCreateAutomationPeer() => new InvokablePeer(this);

    private sealed class InvokablePeer : TextBlockAutomationPeer, IInvokeProvider
    {
        private readonly InvokableTextBlock _owner;
        public InvokablePeer(InvokableTextBlock owner) : base(owner) => _owner = owner;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        public void Invoke() => _owner.Dispatcher.BeginInvoke(new Action(_owner.RaiseInvoked));
    }
}

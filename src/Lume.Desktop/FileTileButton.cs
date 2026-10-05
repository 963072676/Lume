using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Lume.Desktop;

internal sealed class FileTileButton(Action open) : Button
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FileTilePeer(this);

    private sealed class FileTilePeer(FileTileButton button) : ButtonAutomationPeer(button), IInvokeProvider
    {
        void IInvokeProvider.Invoke()
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            if (!button.IsVisible) throw new ElementNotAvailableException();
            // Assistive invocation opens the file; ordinary click still only selects it.
            button.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (button.IsVisible && button.IsEnabled) button.OpenFromAutomation();
            });
        }
    }

    private void OpenFromAutomation() => open();
}

using System.Diagnostics;
using Kasper.Application.Commerce;

namespace Kasper.Infrastructure.Commerce;

/// <summary>Opens PayPal's approval page in the default browser. Refuses anything that isn't https on paypal.com.</summary>
public sealed class BrowserLauncher : IBrowserLauncher
{
    public static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && (u.Host == "paypal.com" || u.Host.EndsWith(".paypal.com", StringComparison.OrdinalIgnoreCase));

    public void Open(string url)
    {
        if (!IsAllowed(url)) throw new InvalidOperationException("Refusing to open a non-PayPal link.");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

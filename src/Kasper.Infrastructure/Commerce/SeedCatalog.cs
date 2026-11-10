using Kasper.Application.Commerce;

namespace Kasper.Infrastructure.Commerce;

/// <summary>Seeded demo catalog (all USD). The only source of prices.</summary>
public sealed class SeedCatalog : ICatalog
{
    private const string Merchant = "KASPER Demo Store";

    public IReadOnlyList<Product> All { get; } =
    [
        new("kb-mech-01", "Mechanical Keyboard (Tenkeyless)", "Hot-swappable tenkeyless keyboard with tactile switches.", 79.99m, Merchant),
        new("kb-mech-02", "Mechanical Keyboard (Compact 60%)", "Compact 60% wireless keyboard with linear switches.", 64.50m, Merchant),
        new("ms-wl-01", "Wireless Mouse", "Ergonomic wireless mouse, 2.4 GHz.", 24.99m, Merchant),
        new("hs-usb-01", "USB Headset", "Over-ear USB headset with noise-cancelling microphone.", 49.00m, Merchant),
        new("cam-hd-01", "HD Webcam", "1080p webcam with privacy shutter.", 39.95m, Merchant),
        new("hub-usbc-01", "USB-C Hub (7-in-1)", "HDMI, SD card reader and USB 3.0 ports.", 34.99m, Merchant),
        new("mon-27-01", "27\" 4K Monitor", "27-inch 4K IPS monitor.", 399.00m, Merchant),
        new("desk-lamp-01", "LED Desk Lamp", "Dimmable LED lamp with USB charging port.", 29.90m, Merchant),
    ];

    public Product? Find(string productId) =>
        All.FirstOrDefault(p => string.Equals(p.Id, productId?.Trim(), StringComparison.OrdinalIgnoreCase));
}

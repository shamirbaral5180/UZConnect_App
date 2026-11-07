using System.Management;
using UZConnect.Core.Models;

namespace UZConnect.Windows.Serial;

/// <summary>
/// Finds the Windows modem port (for example COM11) that belongs to the active
/// USB modem. This is the AT command channel used for USSD; it is separate from
/// the Qualcomm diagnostics port and from the RNDIS network adapter.
/// </summary>
public static class ModemAtPortLocator
{
    public static string? Find(ushort? vendorId, ushort? productId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AttachedTo, PNPDeviceID FROM Win32_POTSModem");
            using var results = searcher.Get();

            string? fallback = null;
            foreach (ManagementObject modem in results)
            {
                using (modem)
                {
                    var port = (modem["AttachedTo"] as string)?.Trim();
                    if (string.IsNullOrWhiteSpace(port) ||
                        !port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var pnp = modem["PNPDeviceID"] as string;
                    var name = modem["Name"] as string ?? string.Empty;
                    if (fallback is null && name.Contains("Modem", StringComparison.OrdinalIgnoreCase))
                    {
                        fallback = port;
                    }

                    if (vendorId is null || productId is null)
                    {
                        continue;
                    }

                    var (vid, pid) = UsbIdParser.Parse(pnp);
                    if (vid == vendorId && pid == productId)
                    {
                        return port;
                    }
                }
            }

            return fallback;
        }
        catch
        {
            return null;
        }
    }
}

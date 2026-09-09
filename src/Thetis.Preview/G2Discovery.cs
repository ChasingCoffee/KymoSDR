using System.Net.NetworkInformation;

namespace Thetis.Preview;

public sealed record DiscoveredG2(G2RadioTarget Target,string InterfaceName,bool Busy)
{
    public override string ToString() => $"G2 · {Target.RadioAddress} · {(Busy ? "BUSY" : "idle")} · {InterfaceName} ({Target.LocalAddress}) · {Target.MacAddress}";
}
public sealed record G2DiscoveryResult(IReadOnlyList<DiscoveredG2> Radios,string Status);

/// <summary>Explicit discovery only: bounded P2 subnet broadcasts on Ethernet.
/// No receive start, TX, Wi-Fi, general broadcast, DNS or saved-address fallback.</summary>
public static class G2Discovery
{
    internal static RadioDiscoveryOptions ScanOptions() => new()
    {
        ProtocolMode = RadioDiscoveryProtocolMode.P2Only, IncludeEthernet = true,
        IncludeWireless = false, IncludeOtherInterfaceTypes = false, AllowLoopback = false,
        AllowAPIPA = true, IncludeGeneralBroadcast = false, MaxScanMilliseconds = 4000
    };
    public static G2DiscoveryResult Scan(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var result = new RadioDiscoveryService().DiscoverUsingAllNics(ScanOptions(),token);
        token.ThrowIfCancellationRequested(); return Describe(result);
    }
    internal static G2DiscoveryResult Describe(IReadOnlyList<NicRadioScanResult> interfaces)
    {
        List<DiscoveredG2> radios = [];
        int eligible = 0,unsupported = 0; bool partial = false;
        foreach (var nic in interfaces)
        {
            if (nic.NicInterfaceType != NetworkInterfaceType.Ethernet || nic.IsLoopbackLocal ||
                nic.LocalIPv4 is null || nic.LocalMaskIPv4 is null) continue;
            ++eligible;
            if (nic.Diagnostics is null || nic.Diagnostics.SocketError || nic.Diagnostics.DeadlineReached) partial = true;
            foreach (var radio in nic.Radios)
            {
                if (radios.Count == 128) { partial = true; break; }
                if (!G2Preflight.IsReviewedProfile(radio)) { ++unsupported; continue; }
                try
                {
                    var target = new G2RadioTarget(nic.LocalIPv4.ToString(),radio.IpAddress?.ToString() ?? "",
                        G2Preflight.NormalizeMac(radio.MacAddress));
                    target.Validate();
                    new Thetis.Engine.G2ReceiveOptions(target.RadioAddress,target.LocalAddress,nic.LocalMaskIPv4.ToString()).Validate();
                    if (!radios.Any(r => r.Target == target)) radios.Add(new(target,nic.NicName ?? "Ethernet",radio.IsBusy));
                }
                catch (ArgumentException) { ++unsupported; }
            }
        }
        string status = eligible == 0 ? "No usable Ethernet interface found. Connect Ethernet and retry; Wi-Fi is not scanned." :
            radios.Count == 0 ? "No compatible G2 found on Ethernet. Check power/cable, retry, or enter addresses manually." :
            $"Found {radios.Count} compatible G2 connection(s). Choose one below; discovery does not connect.";
        if (unsupported > 0) status += $" {unsupported} unsupported/invalid reply(s) omitted.";
        if (partial) status += " Scan incomplete or limited; retry. Connect always repeats an identity/idle check.";
        return new(radios.AsReadOnly(),status);
    }
}

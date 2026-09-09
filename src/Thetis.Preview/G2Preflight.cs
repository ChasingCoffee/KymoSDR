using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Thetis.Engine;

namespace Thetis.Preview;

public sealed record G2RadioTarget(string LocalAddress,string RadioAddress,string MacAddress)
{
    public void Validate()
    {
        foreach (string text in new[] { LocalAddress,RadioAddress })
        {
            if (!IPAddress.TryParse(text,out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
                ip.ToString() != text || ip.GetAddressBytes()[0] is 0 or 127 or >= 224)
                throw new ArgumentException("Explicit non-loopback unicast IPv4 addresses are required.");
        }
        if (LocalAddress == RadioAddress) throw new ArgumentException("The Ethernet and radio addresses must differ.");
        _ = G2Preflight.NormalizeMac(MacAddress);
    }
}

public sealed record G2HardwareRequest(G2RadioTarget Target,int FrequencyHz = 14_074_000,int DurationSeconds = 60,
    bool ConfirmAnt1ReceiveOnly = false, bool ConfirmExtendedReceive = false)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Target); Target.Validate();
        if (!ConfirmAnt1ReceiveOnly) throw new ArgumentException("Confirm ANT1 receive-only operation before each hardware connection.");
        int maximum = ConfirmExtendedReceive ? 3600 : 60;
        if (FrequencyHz is < 14_000_000 or > 14_350_000 || DurationSeconds < 5 || DurationSeconds > maximum)
            throw new ArgumentException($"G2 receive is limited to 14.000–14.350 MHz and 5–{maximum} seconds per connection.");
    }
}

/// <summary>Shared CLI/desktop preflight. Enumeration and discovery stay on the
/// explicit Ethernet address, require the selected idle MAC, and never start RX/TX.</summary>
public static class G2Preflight
{
    public static bool IsReviewedProfile(RadioInfo radio) => radio.Protocol == RadioDiscoveryRadioProtocol.P2 &&
        (int)radio.DeviceType == 10 && radio.DiscoveryPortBase == 1024 && radio.CodeVersion == 27 &&
        radio.Protocol2Supported == 43 && radio.NumRxs == 10;
    public static string NormalizeMac(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string value = text.Replace(':','-').ToUpperInvariant(); var parts = value.Split('-');
        if (parts.Length != 6 || parts.Any(s => s.Length != 2 || s.Any(c => !Uri.IsHexDigit(c))) ||
            !byte.TryParse(parts[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out byte first) || (first & 1) != 0 ||
            value == "00-00-00-00-00-00") throw new ArgumentException("A unicast six-byte MAC address is required.");
        return value;
    }
    public static NicRadioScanResult SelectInterface(List<NicRadioScanResult> interfaces,G2HardwareRequest request)
    {
        request.Validate();
        var matches = interfaces.Where(n => n.LocalIPv4?.ToString() == request.Target.LocalAddress).ToArray();
        if (matches.Length != 1 || matches[0].NicInterfaceType != NetworkInterfaceType.Ethernet ||
            matches[0].LocalMaskIPv4 is null || matches[0].IsLoopbackLocal)
            throw new InvalidOperationException("Exactly one usable Ethernet interface must match the local address; no fallback is permitted.");
        Options(request,matches[0]).Validate(); return matches[0];
    }
    public static G2ReceiveOptions Options(G2HardwareRequest request,NicRadioScanResult nic) => new(
        request.Target.RadioAddress,request.Target.LocalAddress,nic.LocalMaskIPv4.ToString(),request.FrequencyHz,request.DurationSeconds,
        request.ConfirmExtendedReceive);
    public static RadioInfo SelectRadio(List<NicRadioScanResult> scan,G2HardwareRequest request,bool requireIdle = true)
    {
        var nic = SelectInterface(scan,request);
        if (scan.Count != 1 || nic.Diagnostics?.SocketError != false || nic.Diagnostics.DeadlineReached || nic.Radios.Count != 1)
            throw new InvalidOperationException("G2 discovery must finish cleanly with exactly one selected radio.");
        var radio = nic.Radios[0];
        if (radio.IpAddress?.ToString() != request.Target.RadioAddress || NormalizeMac(radio.MacAddress) != NormalizeMac(request.Target.MacAddress) ||
            !IsReviewedProfile(radio))
            throw new InvalidOperationException("Radio identity/capabilities do not match the reviewed Saturn profile (raw code27/protocol43/10 DDCs).");
        if (requireIdle && radio.IsBusy) throw new InvalidOperationException("G2 is busy. Close its other client; this application will not take over.");
        return radio;
    }
    public static G2ReceiveOptions Verify(G2HardwareRequest request,CancellationToken token = default)
    {
        request.Validate(); token.ThrowIfCancellationRequested();
        var service = new RadioDiscoveryService();
        var options = new RadioDiscoveryOptions { FixedLocalIp = IPAddress.Parse(request.Target.LocalAddress),
            FixedTargetIp = IPAddress.Parse(request.Target.RadioAddress), ProtocolMode = RadioDiscoveryProtocolMode.P2Only,
            MaxScanMilliseconds = 2000,IncludeGeneralBroadcast = false };
        var nic = SelectInterface(service.ListUsableNics(options),request);
        _ = SelectRadio(service.DiscoverUsingAllNics(options,token),request);
        return Options(request,nic);
    }
}

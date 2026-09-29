using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Tickshift.Core.Abstractions;

namespace Tickshift.Diagnostics.Network;

public enum NetworkMedium { Ethernet, WiFi, Cellular, Vpn, Other }

public sealed record AdapterInfo(string Name, string Description, NetworkMedium Medium, bool IsUp, long SpeedBitsPerSecond, bool HasGateway);

public sealed record LatencyResult
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public required string Target { get; init; }
    public required string Method { get; init; }
    public int Sent { get; init; }
    public int Received { get; init; }
    public double? MinMs { get; init; }
    public double? AverageMs { get; init; }
    public double? MaxMs { get; init; }
    public double? JitterMs { get; init; }
    public string? Error { get; init; }

    public double LossPercent => Sent == 0 ? 0 : Math.Round((Sent - Received) * 100.0 / Sent, 1);

    public string Quality => Received == 0 ? "Unreachable"
        : LossPercent >= 5 || AverageMs >= 200 || JitterMs >= 50 ? "Poor"
        : LossPercent > 0 || AverageMs >= 80 || JitterMs >= 20 ? "Fair"
        : "Good";
}

public sealed record ConnectivityStatus(bool NetworkAvailable, bool? GatewayReachable, bool? DnsWorking, string Summary, AdapterInfo? PrimaryAdapter, WifiInfo? Wifi);

/// <summary>
/// Read-only network diagnostics: adapter inspection, ICMP echo and TCP-handshake timing to hosts the
/// user chose. It never changes DNS, IPv6, TCP parameters, firewall rules or adapters, and never sends
/// application data. ICMP/TCP handshake latency is not broker execution latency — the UI says so.
/// </summary>
public sealed class NetworkDiagnosticsService
{
    /// <summary>Windows' own connectivity-check host (NCSI); Windows already contacts it routinely.</summary>
    public const string DefaultDnsProbeHost = "www.msftconnecttest.com";

    public const string LatencyDisclaimer =
        "These figures measure the network path from this laptop to the chosen host. They are not your broker's order execution time, " +
        "which also depends on the broker's servers, their internal processing and the market.";

    private readonly IWifiInfoProvider _wifi;

    public NetworkDiagnosticsService(IWifiInfoProvider wifi) => _wifi = wifi;

    public IReadOnlyList<AdapterInfo> GetAdapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            bool hasGateway;
            try { hasGateway = nic.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)); }
            catch (NetworkInformationException) { hasGateway = false; }

            list.Add(new AdapterInfo(nic.Name, nic.Description, Classify(nic), nic.OperationalStatus == OperationalStatus.Up,
                SafeSpeed(nic), hasGateway));
        }

        return list.OrderByDescending(a => a.IsUp && a.HasGateway).ThenBy(a => a.Medium).ToList();
    }

    public static NetworkMedium Classify(NetworkInterface nic)
    {
        var d = (nic.Description + " " + nic.Name).ToLowerInvariant();
        if (d.Contains("vpn") || d.Contains("wireguard") || d.Contains("tap-") || d.Contains("openvpn") || d.Contains("tunnel") || nic.NetworkInterfaceType == NetworkInterfaceType.Ppp)
            return NetworkMedium.Vpn;
        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => NetworkMedium.WiFi,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit
                => d.Contains("wi-fi") || d.Contains("wireless") || d.Contains("802.11") ? NetworkMedium.WiFi : NetworkMedium.Ethernet,
            NetworkInterfaceType.Wman or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => NetworkMedium.Cellular,
            _ => NetworkMedium.Other,
        };
    }

    public async Task<ConnectivityStatus> CheckConnectivityAsync(CancellationToken ct)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
            return new ConnectivityStatus(false, null, null, "No network connection. Check Wi-Fi or the network cable.", null, null);

        var primary = GetAdapters().FirstOrDefault(a => a.IsUp && a.HasGateway);
        var gateway = GetDefaultGateway();
        bool? gatewayOk = null;
        if (gateway is not null)
        {
            var g = await PingAsync(gateway.ToString(), 3, ct).ConfigureAwait(false);
            // Some routers ignore ping; treat "no reply" as unknown rather than failure.
            gatewayOk = g.Received > 0 ? true : null;
        }

        bool? dnsOk;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var addresses = await Dns.GetHostAddressesAsync(DefaultDnsProbeHost, timeout.Token).ConfigureAwait(false);
            dnsOk = addresses.Length > 0;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            dnsOk = false;
        }

        var wifi = primary?.Medium == NetworkMedium.WiFi ? _wifi.GetCurrent() : null;
        var summary = dnsOk == false
            ? "Connected to a network, but names cannot be resolved. The internet may be unavailable."
            : primary?.Medium == NetworkMedium.WiFi
                ? $"Connected over Wi-Fi{(wifi is not null ? $" (signal {wifi.SignalQualityPercent}%)" : string.Empty)}."
                : primary?.Medium == NetworkMedium.Ethernet ? "Connected over Ethernet." : "Connected.";
        return new ConnectivityStatus(true, gatewayOk, dnsOk, summary, primary, wifi);
    }

    public static IPAddress? GetDefaultGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public async Task<LatencyResult> PingAsync(string host, int count, CancellationToken ct)
    {
        if (!EndpointValidator.TryNormalise(host, out var target, out var error))
            return new LatencyResult { Target = host, Method = "ICMP echo", Error = error };

        count = Math.Clamp(count, 1, 20);
        var times = new List<double>();
        using var ping = new Ping();
        string? lastError = null;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(1500), cancellationToken: ct).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
                else lastError = reply.Status.ToString();
            }
            catch (PingException ex)
            {
                lastError = ex.InnerException?.Message ?? ex.Message;
            }

            if (i < count - 1) await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return Summarise(target, "ICMP echo", count, times, times.Count == 0 ? lastError ?? "No replies (the host may block ping)." : null);
    }

    /// <summary>Times TCP handshakes only; the connection is closed immediately and no data is sent.</summary>
    public async Task<LatencyResult> TcpConnectAsync(string host, int port, int count, CancellationToken ct)
    {
        if (!EndpointValidator.TryNormalise(host, out var target, out var error) || port is <= 0 or > 65535)
            return new LatencyResult { Target = host, Method = "TCP connect", Error = error ?? "Invalid port." };

        count = Math.Clamp(count, 1, 10);
        var times = new List<double>();
        string? lastError = null;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var sw = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(target, port, timeout.Token).ConfigureAwait(false);
                sw.Stop();
                times.Add(Math.Round(sw.Elapsed.TotalMilliseconds, 1));
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = "Timed out.";
            }
            catch (SocketException ex)
            {
                lastError = ex.SocketErrorCode.ToString();
            }

            if (i < count - 1) await Task.Delay(300, ct).ConfigureAwait(false);
        }

        return Summarise($"{target}:{port}", "TCP connect", count, times, times.Count == 0 ? lastError : null);
    }

    internal static LatencyResult Summarise(string target, string method, int sent, IReadOnlyList<double> times, string? error)
    {
        double? jitter = null;
        if (times.Count >= 2)
        {
            double sum = 0;
            for (var i = 1; i < times.Count; i++) sum += Math.Abs(times[i] - times[i - 1]);
            jitter = Math.Round(sum / (times.Count - 1), 1);
        }

        return new LatencyResult
        {
            Target = target,
            Method = method,
            Sent = sent,
            Received = times.Count,
            MinMs = times.Count > 0 ? times.Min() : null,
            AverageMs = times.Count > 0 ? Math.Round(times.Average(), 1) : null,
            MaxMs = times.Count > 0 ? times.Max() : null,
            JitterMs = jitter,
            Error = error,
        };
    }

    private static long SafeSpeed(NetworkInterface nic)
    {
        try { return nic.Speed; }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NetworkInformationException) { return 0; }
    }
}

/// <summary>Accepts only a bare hostname or IP address. URLs are reduced to their host; credentials are rejected.</summary>
public static partial class EndpointValidator
{
    public static bool TryNormalise(string? input, out string host, out string? error)
    {
        host = string.Empty;
        error = null;
        var value = (input ?? string.Empty).Trim();
        if (value.Length == 0) { error = "Enter a host name or IP address."; return false; }
        if (value.Length > 253 + 16) { error = "Host name is too long."; return false; }

        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) { error = "Not a valid address."; return false; }
            if (!string.IsNullOrEmpty(uri.UserInfo)) { error = "Addresses containing user names or passwords are not accepted."; return false; }
            value = uri.Host;
        }

        if (value.Contains('@')) { error = "Addresses containing user names are not accepted."; return false; }

        if (IPAddress.TryParse(value.Trim('[', ']'), out var ip))
        {
            host = ip.ToString();
            return true;
        }

        value = value.TrimEnd('.').ToLowerInvariant();
        if (value.Length > 253 || !HostnameRegex().IsMatch(value)) { error = "Not a valid host name."; return false; }
        host = value;
        return true;
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex HostnameRegex();
}

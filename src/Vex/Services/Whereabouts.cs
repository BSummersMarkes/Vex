using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Vex.Services;

/// <summary>Where Ben is working today, as far as Vex can tell.</summary>
public enum Place { Unknown, Home, Office }

/// <summary>
/// Home, office or not sure, worked out from the network this PC is on, compared with the networks saved in Setup →
/// Home days ("This network is home" / "…is the office"). Everything is read from this PC: the Wi-Fi name, the
/// router's hardware (MAC) address from Windows' own ARP cache, each adapter's DNS suffix (and whether it's the joined
/// domain's) and whether a VPN adapter is up. Nothing is sent or probed, so detection never touches the network itself.
/// Checked at startup, when the network changes and when you first sit down each day; "I'm working from home today" /
/// "I'm at the office today" (right-click menu or chat) overrides it until midnight. Home days bring the clock-in and
/// clock-out nudges (<see cref="Nudges"/>); office and unknown days carry on as before.
/// </summary>
public class Whereabouts
{
    /// <summary>What the network says, whatever the override.</summary>
    public Place Detected { get; private set; } = Place.Unknown;
    /// <summary>Why: which saved network matched, or what's here that didn't. Logged and shown in the chat context.</summary>
    public string Reason { get; private set; } = "not checked yet";

    /// <summary>Today's place set by hand (menu or chat), if any. Cleared at midnight (<see cref="Store.RollDay"/>).</summary>
    public Place? Override => Store.State.WhereOverride switch { "home" => Place.Home, "office" => Place.Office, _ => null };

    /// <summary>Where today counts as: the override if set, else what was detected.</summary>
    public Place Today => Override ?? Detected;
    public bool HomeToday => Today == Place.Home;

    // Windows raises several network events as an adapter comes up; wait for them to settle (and for DHCP and the
    // router's ARP entry to appear) before looking, so one change is one check and one log line.
    static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
    volatile bool networkChanged;
    DateTime? recheckAt;
    bool checking;
    string seenDay = "";

    public void Start()
    {
        // Raised on a thread-pool thread: only flag it here, Tick (UI thread) does the rest.
        NetworkChange.NetworkAddressChanged += (_, _) => networkChanged = true;
        NetworkChange.NetworkAvailabilityChanged += (_, _) => networkChanged = true;
        seenDay = Store.Today;
        _ = Check("startup");
    }

    /// <summary>Every few seconds from the Hub: a settled network change, or the first sight of you today, means a fresh look.</summary>
    public void Tick()
    {
        var now = DateTime.Now;
        if (networkChanged) { networkChanged = false; recheckAt = now + Settle; }
        if (recheckAt is { } at && now >= at) { recheckAt = null; _ = Check("network changed"); }

        // A new day with Vex left running: the PC may have moved overnight, so look again when you sit down.
        var first = Hub.Presence.FirstSeenToday;
        if (seenDay != Store.Today && first != DateTime.MaxValue && first.Date == DateTime.Today)
        {
            seenDay = Store.Today;
            _ = Check("first at the desk today");
        }
    }

    /// <summary>Look again now (e.g. after saved networks changed in Setup).</summary>
    public void Recheck(string why) => _ = Check(why);

    async Task Check(string why)
    {
        if (checking) { recheckAt = DateTime.Now; return; }   // one look at a time; go again once this one's done
        checking = true;
        try
        {
            var here = await Task.Run(Observe);   // the Wi-Fi and ARP calls can take a moment; keep them off the UI thread
            (Detected, Reason) = Decide(here);
            Store.Log($"Where: {Detected} ({why}). {Reason}" + (Override is { } o ? $" Set by hand to {o} for today." : ""));
        }
        catch (Exception ex) { Store.Log($"Where: couldn't read the network ({ex.Message})"); }
        finally { checking = false; }
    }

    /// <summary>"I'm working from home today" / "I'm at the office today", or null to go back to detecting. Returns a line to say.</summary>
    public string SetOverride(Place? place, string by)
    {
        Store.State.WhereOverride = place switch { Place.Home => "home", Place.Office => "office", _ => "" };
        Store.SaveState();
        Store.Log(place is { } p ? $"Where: {p} for today, set by hand ({by})." : $"Where: back to detecting ({by}): {Detected}. {Reason}");
        return place switch
        {
            Place.Home => Store.Config.ClockNudges ? "Home today. I'll nag you about clocking in and out." : "Home today. Noted.",
            Place.Office => "Office today. Noted.",
            _ => $"I'll work it out myself. Right now: {Describe(Detected)}.",
        };
    }

    /// <summary>
    /// One line for the chat context (<paramref name="details"/> false) or the local status bubble (true). The chat
    /// context goes to Claude, so it never carries the network details (Wi-Fi name, router address, suffix), only
    /// the outcome: those stay on this PC, in Setup, the status bubble and vex.log.
    /// </summary>
    public string Summary(bool details = false)
    {
        var how = Override != null ? "set by hand for today"
                : details ? Reason.TrimEnd('.')
                : Detected == Place.Unknown ? "no saved network matches" : "detected from the network";
        return $"Where today: {Describe(Today)} ({how}).";
    }

    public static string Describe(Place p) => p switch { Place.Home => "working from home", Place.Office => "at the office", _ => "not sure" };

    /// <summary>When to clock out on a home day: ClockOutAt, or today's leave time (per-day) if that's empty. Null if neither is set.</summary>
    public static DateTime? ClockOutTime() =>
        Store.Config.ClockOutAt.Length > 0
            ? Commute.ParseTime(Store.Config.ClockOutAt) is { } t ? DateTime.Today + t : null
            : Commute.LeaveTime();

    // ───────────── matching ─────────────

    /// <summary>
    /// Scores each saved network against what's here and takes the best. The router's MAC is the strongest sign
    /// (+3), then the Wi-Fi name (+2); either one present on both sides but different counts against (−2), so a
    /// shared DNS suffix can't make home look like the office. A matching DNS suffix, or both being on the joined
    /// domain's network, adds 1; the VPN being up or down as when it was saved is only a tie-breaker. A tie between
    /// home and office, or nothing scoring above zero, is Unknown.
    /// </summary>
    static (Place, string) Decide(NetPrint here)
    {
        var c = Store.Config;
        var best = c.HomeNetworks.Select(p => (Place.Home, Score: Score(p, here)))
            .Concat(c.OfficeNetworks.Select(p => (Place.Office, Score: Score(p, here))))
            .Where(m => m.Score.Points > 0)
            .GroupBy(m => m.Item1)
            .Select(g => g.OrderByDescending(m => m.Score.Points).First())
            .OrderByDescending(m => m.Score.Points)
            .ToList();
        if (best.Count == 0)
            return (Place.Unknown, c.HomeNetworks.Count + c.OfficeNetworks.Count == 0
                ? $"No networks saved yet (here: {Describe(here)})."
                : $"No saved network matches (here: {Describe(here)}).");
        if (best.Count > 1 && best[0].Score.Points == best[1].Score.Points)
            return (Place.Unknown, $"Matches a saved home and a saved office network equally (here: {Describe(here)}).");
        var (place, (_, why)) = best[0];
        return (place, $"Matches a saved {(place == Place.Home ? "home" : "office")} network: {why}.");
    }

    static (double Points, string Why) Score(NetPrint saved, NetPrint here)
    {
        double points = 0;
        var why = new List<string>();
        static string? Common(List<string> a, List<string> b) => a.FirstOrDefault(x => b.Contains(x, StringComparer.OrdinalIgnoreCase));

        if (saved.Gateways.Count > 0 && here.Gateways.Count > 0)
        {
            if (Common(saved.Gateways, here.Gateways) is { } mac) { points += 3; why.Add($"router {mac}"); }
            else points -= 2;
        }
        if (saved.WiFi.Count > 0 && here.WiFi.Count > 0)
        {
            if (Common(saved.WiFi, here.WiFi) is { } ssid) { points += 2; why.Add($"Wi-Fi \"{ssid}\""); }
            else points -= 2;
        }
        if (Common(saved.DnsSuffixes, here.DnsSuffixes) is { } suffix) { points += 1; why.Add($"DNS suffix {suffix}"); }
        else if (saved.DomainNetwork && here.DomainNetwork) { points += 1; why.Add("on the work domain's network"); }
        if (points > 0 && saved.Vpn == here.Vpn) { points += 0.5; why.Add(here.Vpn ? "VPN up, as saved" : "no VPN, as saved"); }
        return (points, string.Join(", ", why));
    }

    /// <summary>A saved or current network in a few words, for Setup's list and the log.</summary>
    public static string Describe(NetPrint p)
    {
        var parts = new List<string>();
        if (p.WiFi.Count > 0) parts.Add("Wi-Fi " + string.Join(" / ", p.WiFi.Select(s => $"\"{s}\"")));
        else parts.Add("wired");
        if (p.Gateways.Count > 0) parts.Add("router " + string.Join(" / ", p.Gateways));
        if (p.DnsSuffixes.Count > 0) parts.Add("suffix " + string.Join(" / ", p.DnsSuffixes));
        if (p.DomainNetwork) parts.Add("work domain");
        if (p.Vpn) parts.Add("VPN up");
        return p.WiFi.Count == 0 && p.Gateways.Count == 0 && p.DnsSuffixes.Count == 0 ? "no network" + (p.Vpn ? ", VPN up" : "") : string.Join(", ", parts);
    }

    // ───────────── reading the network (this PC only) ─────────────

    /// <summary>
    /// The network this PC is on now. A VPN adapter only sets <see cref="NetPrint.Vpn"/>: its DNS suffix and gateway
    /// belong to the office at the far end, not to where the PC physically is. Adapters without a gateway (Hyper-V
    /// switches, Bluetooth) say nothing about the place and are skipped.
    /// </summary>
    public static NetPrint Observe()
    {
        var p = new NetPrint { Saved = DateTime.Now };
        var arp = ArpTable();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); } catch { nics = Array.Empty<NetworkInterface>(); }
        foreach (var n in nics.Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            if (IsVpn(n)) { p.Vpn = true; continue; }
            IPInterfaceProperties ip;
            try { ip = n.GetIPProperties(); } catch { continue; }
            var gateways = ip.GatewayAddresses.Select(g => g.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any)).ToList();
            if (gateways.Count == 0) continue;
            foreach (var g in gateways)
                if (arp.TryGetValue(g, out var mac) && !p.Gateways.Contains(mac)) p.Gateways.Add(mac);
            if (ip.DnsSuffix is { Length: > 0 } s && !p.DnsSuffixes.Contains(s.ToLowerInvariant())) p.DnsSuffixes.Add(s.ToLowerInvariant());
        }
        p.WiFi = Ssids();
        // Domain membership, judged locally: the joined domain's suffix handed out on a (non-VPN) adapter here.
        string domain = "";
        try { domain = IPGlobalProperties.GetIPGlobalProperties().DomainName.ToLowerInvariant(); } catch { }
        p.DomainNetwork = domain.Length > 0 && p.DnsSuffixes.Any(s => s == domain || s.EndsWith("." + domain));
        return p;
    }

    static bool IsVpn(NetworkInterface n) =>
        n.NetworkInterfaceType == NetworkInterfaceType.Ppp
        || Store.Config.VpnAdapterWords.Any(w => n.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                                              || n.Description.Contains(w, StringComparison.OrdinalIgnoreCase));

    [DllImport("iphlpapi.dll")] static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

    /// <summary>
    /// Windows' IPv4 ARP cache (IP → MAC), as already learned by the PC. Only read, never refreshed: no packets are sent.
    /// Right after connecting the router may not be in it yet; the network-change settle delay covers that.
    /// </summary>
    static Dictionary<IPAddress, string> ArpTable()
    {
        var map = new Dictionary<IPAddress, string>();
        int size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0) return map;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buf, ref size, false) != 0) return map;
            int count = Marshal.ReadInt32(buf);
            for (int i = 0; i < count; i++)
            {
                var row = buf + 4 + i * 24;   // MIB_IPNETROW: index, MAC length, MAC[8], IPv4 address, type
                int len = Math.Min(8, Marshal.ReadInt32(row, 4));
                int type = Marshal.ReadInt32(row, 20);
                if (len != 6 || type == 2 /* invalid */) continue;
                var mac = new byte[len];
                Marshal.Copy(row + 8, mac, 0, len);
                if (mac.All(b => b == 0) || mac.All(b => b == 0xFF)) continue;
                map[new IPAddress((uint)Marshal.ReadInt32(row, 16))] = BitConverter.ToString(mac).ToLowerInvariant();
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return map;
    }

    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr valueType);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr memory);

    static bool wifiDeniedLogged;

    /// <summary>
    /// Names of the Wi-Fi networks connected now, from the local WLAN service. Recent Windows 11 only lets apps read
    /// them with location access allowed for desktop apps; without it this is empty and the other signals carry on.
    /// </summary>
    static List<string> Ssids()
    {
        var names = new List<string>();
        IntPtr handle = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return names;   // no Wi-Fi on this PC
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0) return names;
            int count = Marshal.ReadInt32(list);
            for (int i = 0; i < count; i++)
            {
                var info = list + 8 + i * 532;   // WLAN_INTERFACE_INFO: GUID, description[256], state
                if (Marshal.ReadInt32(info, 528) != 1 /* connected */) continue;
                var guid = Marshal.PtrToStructure<Guid>(info);
                int err = WlanQueryInterface(handle, ref guid, 7 /* current_connection */, IntPtr.Zero, out _, out var data, IntPtr.Zero);
                if (err == 5 /* access denied */ && !wifiDeniedLogged)
                {
                    wifiDeniedLogged = true;
                    Store.Log("Where: Wi-Fi name not readable (Windows location access for desktop apps is off); using the router and DNS suffix only.");
                }
                if (err != 0) continue;
                try
                {
                    int len = Math.Clamp(Marshal.ReadInt32(data, 520), 0, 32);   // WLAN_CONNECTION_ATTRIBUTES → DOT11_SSID
                    var ssid = new byte[len];
                    Marshal.Copy(data + 524, ssid, 0, len);
                    if (len > 0) names.Add(Encoding.UTF8.GetString(ssid));
                }
                finally { WlanFreeMemory(data); }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
        return names;
    }
}

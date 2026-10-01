using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MajorGym.Data;

/// <summary>A resolved MajorGym peer found via mDNS/DNS-SD: what Android's
/// <c>NsdManager.ResolveListener.onServiceResolved</c> hands its SyncManager.</summary>
public sealed record MdnsPeer(
    string InstanceName,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<IPAddress> Addresses,
    int Port);

/// <summary>
/// Windows stand-in for Android's <c>NsdManager</c> (registerService + discoverServices +
/// resolveService): a minimal, dependency-free mDNS / DNS-SD implementation (RFC 6762 / 6763)
/// for the single service type <c>_majorgym._tcp.local</c>. It is deliberately scoped to one
/// bounded sync attempt, exactly like the Android version: constructed and started when
/// "Sync Now" is pressed, disposed (sockets closed, goodbye sent) when the attempt ends.
///
/// Wire behaviour, matching what an Android NSD peer publishes/expects:
///  - instance name  "majorgym-&lt;first 8 chars of deviceId&gt;"
///  - service type   "_majorgym._tcp.local"
///  - TXT attributes id, code (SHA-256 hex of the Sync Code), name
///  - SRV (port + target host) and A (IPv4) records so a peer can dial without a second lookup
///
/// Only IPv4 is used (Android's NSD resolves to an IPv4 address on Wi-Fi/hotspot); if a peer
/// publishes no A record the datagram's source address is used instead.
///
/// NOT verified against real hardware in this delivery — see the report's "Unresolved issues".
/// </summary>
public sealed class MdnsSession : IDisposable
{
    private const string ServiceName = "_majorgym._tcp.local";
    private const int MdnsPort = 5353;
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MulticastEndpoint = new(MulticastGroup, MdnsPort);

    private const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33, TypeAny = 255;
    private const ushort ClassIn = 1, CacheFlush = 0x8000;
    private const uint Ttl = 120;

    private readonly string _instanceLabel;
    private readonly string _instanceFqdn;
    private readonly string _hostFqdn;
    private readonly int _port;
    private readonly IReadOnlyDictionary<string, string> _txt;

    private readonly object _sendLock = new();
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private List<IPAddress> _localAddresses = new();

    // Browse state (only touched from the receive loop thread).
    private readonly Dictionary<string, (int Port, string Target)> _srv = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _txtByInstance = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<IPAddress>> _aByHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _sourceByInstance = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastQuery = new();
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (on a background thread) once a peer's SRV + TXT (+ address) are known.
    /// May be raised again if more addresses become known; consumers must be idempotent.</summary>
    public event Action<MdnsPeer>? PeerResolved;

    public MdnsSession(string instanceLabel, int port, IReadOnlyDictionary<string, string> txtAttributes)
    {
        _instanceLabel = instanceLabel;
        _instanceFqdn = $"{instanceLabel}.{ServiceName}";
        _hostFqdn = $"{instanceLabel}.local";
        _port = port;
        _txt = txtAttributes;
    }

    /// <summary>Opens the multicast socket, announces this device, and starts browsing.
    /// Throws if mDNS cannot be used at all (no usable network interface / port refused).</summary>
    public void Start()
    {
        _localAddresses = GetLocalIPv4Addresses();
        if (_localAddresses.Count == 0)
            throw new InvalidOperationException("No active network connection found. Connect to Wi-Fi or a hotspot and try again.");

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
        var joined = 0;
        foreach (var addr in _localAddresses)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastGroup, addr));
                joined++;
            }
            catch (SocketException) { /* interface can't do multicast — skip it */ }
        }
        if (joined == 0)
        {
            socket.Dispose();
            throw new InvalidOperationException("No network interface supports multicast discovery.");
        }
        _socket = socket;
        _cts = new CancellationTokenSource();

        var ct = _cts.Token;
        _ = Task.Run(() => ReceiveLoop(socket, ct));

        // Announce (RFC 6762 §8.3 — twice, a second apart, on a background task) and browse.
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
                {
                    SendAnnouncement(ttl: Ttl);
                    SendQuery(ServiceName, TypePtr);
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                // Keep re-browsing every 2 s for the life of the session so a peer that starts
                // later (or missed our first query) is still found within the sync timeout.
                while (!ct.IsCancellationRequested)
                {
                    SendQuery(ServiceName, TypePtr);
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* best effort */ }
        }, ct);
    }

    public void Dispose()
    {
        var socket = _socket;
        if (socket is null) return;
        _socket = null;
        try { _cts?.Cancel(); } catch { }
        try { SendAnnouncement(ttl: 0, socketOverride: socket); } catch { } // goodbye packet (TTL 0)
        try { socket.Close(); } catch { }
        try { socket.Dispose(); } catch { }
        _cts?.Dispose();
    }

    // ---------------------------------------------------------------- receive

    private void ReceiveLoop(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[9000];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                var n = socket.ReceiveFrom(buffer, ref remote);
                if (n <= 12) continue;
                HandleMessage(buffer.AsSpan(0, n).ToArray(), ((IPEndPoint)remote).Address);
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (ct.IsCancellationRequested || _socket is null) return; }
            catch (Exception) { /* malformed packet — ignore, keep listening */ }
        }
    }

    private void HandleMessage(byte[] msg, IPAddress source)
    {
        var flags = (ushort)((msg[2] << 8) | msg[3]);
        var isResponse = (flags & 0x8000) != 0;
        var qd = (msg[4] << 8) | msg[5];
        var an = (msg[6] << 8) | msg[7];
        var ns = (msg[8] << 8) | msg[9];
        var ar = (msg[10] << 8) | msg[11];
        var pos = 12;

        var questions = new List<(string Name, ushort Type)>();
        for (var i = 0; i < qd; i++)
        {
            var name = ReadName(msg, ref pos);
            var type = ReadU16(msg, ref pos);
            ReadU16(msg, ref pos); // class (+ unicast-response bit) — we always answer via multicast
            questions.Add((name, type));
        }

        if (!isResponse)
        {
            foreach (var (name, type) in questions) AnswerQuestion(name, type);
            return;
        }

        var total = an + ns + ar;
        for (var i = 0; i < total; i++)
        {
            var name = ReadName(msg, ref pos);
            var type = ReadU16(msg, ref pos);
            ReadU16(msg, ref pos); // class
            var ttl = ReadU32(msg, ref pos);
            int rdLen = ReadU16(msg, ref pos);
            var rdStart = pos;
            if (rdStart + rdLen > msg.Length) return;
            var goodbye = ttl == 0;

            switch (type)
            {
                case TypePtr when name.Equals(ServiceName, StringComparison.OrdinalIgnoreCase):
                {
                    var p = rdStart;
                    var instance = ReadName(msg, ref p);
                    if (goodbye) { _knownInstances.Remove(instance); }
                    else if (!instance.Equals(_instanceFqdn, StringComparison.OrdinalIgnoreCase))
                    {
                        _knownInstances.Add(instance);
                        _sourceByInstance[instance] = source;
                    }
                    break;
                }
                case TypeSrv when !goodbye:
                {
                    var p = rdStart + 4; // skip priority + weight
                    var port = (msg[p] << 8) | msg[p + 1];
                    p += 2;
                    var target = ReadName(msg, ref p);
                    _srv[name] = (port, target);
                    _sourceByInstance.TryAdd(name, source);
                    break;
                }
                case TypeTxt when !goodbye:
                {
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    var p = rdStart;
                    while (p < rdStart + rdLen)
                    {
                        int len = msg[p++];
                        if (len == 0 || p + len > rdStart + rdLen) break;
                        var s = Encoding.UTF8.GetString(msg, p, len);
                        p += len;
                        var eq = s.IndexOf('=');
                        if (eq > 0) map[s[..eq]] = s[(eq + 1)..];
                    }
                    _txtByInstance[name] = map;
                    break;
                }
                case TypeA when rdLen == 4 && !goodbye:
                {
                    var addr = new IPAddress(new ReadOnlySpan<byte>(msg, rdStart, 4));
                    if (!_aByHost.TryGetValue(name, out var set)) _aByHost[name] = set = new HashSet<IPAddress>();
                    set.Add(addr);
                    break;
                }
            }
            pos = rdStart + rdLen;
        }

        EvaluatePeers();
    }

    private void EvaluatePeers()
    {
        foreach (var instance in _knownInstances.ToList())
        {
            if (instance.Equals(_instanceFqdn, StringComparison.OrdinalIgnoreCase)) continue;

            if (!_srv.TryGetValue(instance, out var srv) || !_txtByInstance.TryGetValue(instance, out var txt))
            {
                ThrottledQuery(instance, TypeAny); // ask for SRV + TXT of just this instance
                continue;
            }

            var addresses = new List<IPAddress>();
            if (_aByHost.TryGetValue(srv.Target, out var set)) addresses.AddRange(set);
            else ThrottledQuery(srv.Target, TypeA);
            if (_sourceByInstance.TryGetValue(instance, out var src) && !addresses.Contains(src)) addresses.Add(src);
            if (addresses.Count == 0) continue;

            var key = $"{instance}|{srv.Port}|{string.Join(",", addresses.Select(a => a.ToString()).OrderBy(x => x))}";
            if (!_reported.Add(key)) continue;

            var label = instance.EndsWith("." + ServiceName, StringComparison.OrdinalIgnoreCase)
                ? instance[..^(ServiceName.Length + 1)] : instance;
            try { PeerResolved?.Invoke(new MdnsPeer(label, txt, addresses, srv.Port)); }
            catch { /* consumer bug must not kill discovery */ }
        }
    }

    private void ThrottledQuery(string name, ushort type)
    {
        var key = $"{name}/{type}";
        if (_lastQuery.TryGetValue(key, out var last) && (DateTime.UtcNow - last).TotalMilliseconds < 1000) return;
        _lastQuery[key] = DateTime.UtcNow;
        SendQuery(name, type);
    }

    // ---------------------------------------------------------------- answer

    private void AnswerQuestion(string name, ushort type)
    {
        var matchesService = name.Equals(ServiceName, StringComparison.OrdinalIgnoreCase) && (type == TypePtr || type == TypeAny);
        var matchesInstance = name.Equals(_instanceFqdn, StringComparison.OrdinalIgnoreCase) && (type == TypeSrv || type == TypeTxt || type == TypeAny);
        var matchesHost = name.Equals(_hostFqdn, StringComparison.OrdinalIgnoreCase) && (type == TypeA || type == TypeAny);
        if (matchesService || matchesInstance || matchesHost) SendAnnouncement(Ttl);
    }

    // ---------------------------------------------------------------- send

    private void SendQuery(string name, ushort type)
    {
        var w = new DnsWriter();
        w.U16(0); w.U16(0); w.U16(1); w.U16(0); w.U16(0); w.U16(0);
        w.Name(name); w.U16(type); w.U16(ClassIn);
        Send(w.ToArray());
    }

    /// <summary>One response carrying PTR + SRV + TXT + A records (the full set a resolver needs).</summary>
    private void SendAnnouncement(uint ttl, Socket? socketOverride = null)
    {
        var w = new DnsWriter();
        var records = 3 + _localAddresses.Count;
        w.U16(0); w.U16(0x8400); w.U16(0); w.U16((ushort)records); w.U16(0); w.U16(0);

        // PTR  _majorgym._tcp.local -> <instance>._majorgym._tcp.local
        w.Name(ServiceName); w.U16(TypePtr); w.U16(ClassIn); w.U32(ttl);
        w.RData(() => { var t = new DnsWriter(); t.Name(_instanceFqdn); return t.ToArray(); });

        // SRV  <instance> -> port, <instance>.local
        w.Name(_instanceFqdn); w.U16(TypeSrv); w.U16((ushort)(ClassIn | CacheFlush)); w.U32(ttl);
        w.RData(() => { var t = new DnsWriter(); t.U16(0); t.U16(0); t.U16((ushort)_port); t.Name(_hostFqdn); return t.ToArray(); });

        // TXT  id / code / name
        w.Name(_instanceFqdn); w.U16(TypeTxt); w.U16((ushort)(ClassIn | CacheFlush)); w.U32(ttl);
        w.RData(() =>
        {
            var t = new DnsWriter();
            foreach (var kv in _txt)
            {
                var bytes = Encoding.UTF8.GetBytes($"{kv.Key}={kv.Value}");
                if (bytes.Length > 255) bytes = bytes[..255]; // TXT strings are length-prefixed by one byte
                t.Byte((byte)bytes.Length); t.Bytes(bytes);
            }
            return t.ToArray();
        });

        // A  <instance>.local -> each local IPv4
        foreach (var addr in _localAddresses)
        {
            w.Name(_hostFqdn); w.U16(TypeA); w.U16((ushort)(ClassIn | CacheFlush)); w.U32(ttl);
            w.RData(() => addr.GetAddressBytes());
        }
        Send(w.ToArray(), socketOverride);
    }

    private void Send(byte[] packet, Socket? socketOverride = null)
    {
        var socket = socketOverride ?? _socket;
        if (socket is null) return;
        lock (_sendLock)
        {
            foreach (var addr in _localAddresses)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                    socket.SendTo(packet, MulticastEndpoint);
                }
                catch (Exception) { /* one interface failing must not stop the others */ }
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static List<IPAddress> GetLocalIPv4Addresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (!nic.SupportsMulticast) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue; // link-local self-assigned = no real network
                result.Add(ua.Address);
            }
        }
        return result;
    }

    private static ushort ReadU16(byte[] m, ref int pos) { var v = (ushort)((m[pos] << 8) | m[pos + 1]); pos += 2; return v; }
    private static uint ReadU32(byte[] m, ref int pos)
    {
        var v = ((uint)m[pos] << 24) | ((uint)m[pos + 1] << 16) | ((uint)m[pos + 2] << 8) | m[pos + 3];
        pos += 4; return v;
    }

    /// <summary>Reads a possibly-compressed DNS name (RFC 1035 §4.1.4) starting at <paramref name="pos"/>;
    /// leaves <paramref name="pos"/> just past the name as it appears in-line.</summary>
    private static string ReadName(byte[] m, ref int pos)
    {
        var labels = new List<string>();
        var p = pos;
        var jumped = false;
        var guard = 0;
        while (true)
        {
            if (p >= m.Length || guard++ > 128) throw new FormatException("bad DNS name");
            int len = m[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                var ptr = ((len & 0x3F) << 8) | m[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = ptr;
                continue;
            }
            p++;
            if (p + len > m.Length) throw new FormatException("bad DNS label");
            labels.Add(Encoding.UTF8.GetString(m, p, len));
            p += len;
        }
        if (!jumped) pos = p;
        return string.Join(".", labels);
    }

    private sealed class DnsWriter
    {
        private readonly MemoryStream _ms = new();
        public void Byte(byte b) => _ms.WriteByte(b);
        public void Bytes(byte[] b) => _ms.Write(b, 0, b.Length);
        public void U16(ushort v) { _ms.WriteByte((byte)(v >> 8)); _ms.WriteByte((byte)v); }
        public void U32(uint v) { U16((ushort)(v >> 16)); U16((ushort)v); }
        public void Name(string name)
        {
            foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var b = Encoding.UTF8.GetBytes(label);
                if (b.Length > 63) b = b[..63];
                Byte((byte)b.Length); Bytes(b);
            }
            Byte(0);
        }
        /// <summary>Writes RDLENGTH + RDATA. RDATA is built uncompressed, so no offsets can go stale.</summary>
        public void RData(Func<byte[]> build) { var d = build(); U16((ushort)d.Length); Bytes(d); }
        public byte[] ToArray() => _ms.ToArray();
    }
}

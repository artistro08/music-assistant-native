using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MusicAssistant.Sendspin;

/// <summary>
/// Announces this PC's Sendspin listener on the local network as a <c>_sendspin._tcp</c> service, so Music Assistant
/// finds it and connects.
/// </summary>
/// <remarks>
/// Uses the DNS-SD API built into Windows 10 1809 and later (dnsapi.dll), which answers mDNS queries through the
/// system's DNS Client service, so the app needs no mDNS library of its own. The instance name is the client_id and
/// the TXT record carries the WebSocket path and the friendly name, as aiosendspin's client listener advertises them.
///
/// @author Devin Green (Artistro08)
/// @link https://github.com/Sendspin/spec/blob/main/connection.md#server-initiated-connections
/// @link https://learn.microsoft.com/en-us/windows/win32/api/dnssd/nf-dnssd-dnsserviceregister
/// </remarks>
public sealed class MdnsAdvertiser : IDisposable
{
    /// <summary>DnsServiceRegister's return value when the registration was queued, the success case.</summary>
    private const int DnsRequestPending = 9506;

    /// <summary>DNS_QUERY_REQUEST_VERSION1, the only request version the API accepts.</summary>
    private const uint RequestVersion1 = 1;

    private readonly object gate = new();

    /// <summary>
    /// The completion callback, static so it stays referenced for the life of the process: the API calls it back from
    /// its own threads, including after this object was disposed and collected.
    /// </summary>
    private static readonly RegisterCompleteCallback OnComplete = OnRegisterComplete;

    private IntPtr request;
    private IntPtr instance;
    private IntPtr address;

    /// <summary>
    /// Announces the service. Registration completes in the background; a failure there is logged, since the listener
    /// works either way and the speaker falls back to connecting through the server when nothing arrives.
    /// </summary>
    /// <param name="clientId">This PC's Sendspin client_id, used as the service instance name.</param>
    /// <param name="name">The friendly name for the TXT record, the same name the client/hello carries.</param>
    /// <param name="port">The listener's port.</param>
    /// <param name="address">The IPv4 address to announce: the one this PC uses to reach the Music Assistant server.</param>
    /// <exception cref="InvalidOperationException">Windows refused the registration request.</exception>
    public void Register(string clientId, string name, int port, IPAddress address)
    {
        lock (gate)
        {
            if (request != IntPtr.Zero)
            {
                return;
            }

            string host = $"{Dns.GetHostName()}.local";
            this.address = Marshal.AllocHGlobal(sizeof(uint));

            // IP4_ADDRESS is a DWORD in network byte order: the address bytes as they are, read as one integer.
            Marshal.WriteInt32(this.address, BitConverter.ToInt32(address.GetAddressBytes()));

            instance = DnsServiceConstructInstance(
                $"{clientId}._sendspin._tcp.local",
                host,
                this.address,
                IntPtr.Zero,
                (ushort)port,
                0,
                0,
                2,
                ["path", "name"],
                [SendspinListener.EndpointPath, name]);
            if (instance == IntPtr.Zero)
            {
                ReleaseMemory();
                throw new InvalidOperationException("Windows couldn't build the mDNS service record");
            }

            request = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceRegisterRequest>());
            Marshal.StructureToPtr(new ServiceRegisterRequest
            {
                Version                    = RequestVersion1,
                InterfaceIndex             = 0,
                ServiceInstance            = instance,
                RegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(OnComplete),
                QueryContext               = IntPtr.Zero,
                Credentials                = IntPtr.Zero,
                UnicastEnabled             = false,
            }, request, false);

            int status = DnsServiceRegister(request, IntPtr.Zero);
            if (status != DnsRequestPending)
            {
                ReleaseMemory();
                throw new InvalidOperationException($"Windows refused the mDNS registration (error {status})");
            }
        }
    }

    /// <summary>The IPv4 address this PC uses to reach a host, found by routing a UDP socket (nothing is sent).</summary>
    /// <param name="host">The Music Assistant server's host name or address.</param>
    /// <returns>The local address, or <see langword="null"/> when the host can't be routed to over IPv4.</returns>
    public static IPAddress? LocalAddressFor(string host)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(host, 9);
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static void OnRegisterComplete(int status, IntPtr context, IntPtr registered)
    {
        if (registered != IntPtr.Zero)
        {
            DnsServiceFreeInstance(registered);
        }

        if (status != 0)
        {
            App.Log($"Speaker: mDNS announcement failed (error {status})");
        }
    }

    /// <summary>Withdraws the announcement (Windows sends the mDNS goodbye) and frees the registration.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (request == IntPtr.Zero)
            {
                return;
            }

            // The withdrawal completes in the background and reads the request, so its memory is left for the
            // process to reclaim rather than freed under it.
            // ponytail: a few hundred bytes per speaker restart; free them from the completion callback if restarts become frequent.
            DnsServiceDeRegister(request, IntPtr.Zero);
            request  = IntPtr.Zero;
            instance = IntPtr.Zero;
            address  = IntPtr.Zero;
        }
    }

    private void ReleaseMemory()
    {
        if (instance != IntPtr.Zero)
        {
            DnsServiceFreeInstance(instance);
        }

        if (address != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(address);
        }

        if (request != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(request);
        }

        request  = IntPtr.Zero;
        instance = IntPtr.Zero;
        address  = IntPtr.Zero;
    }

    // =========================================================================
    // INTEROP
    // =========================================================================

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterCompleteCallback(int status, IntPtr context, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceRegisterRequest
    {
        public uint   Version;
        public uint   InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr RegisterCompletionCallback;
        public IntPtr QueryContext;
        public IntPtr Credentials;

        [MarshalAs(UnmanagedType.Bool)]
        public bool   UnicastEnabled;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DnsServiceConstructInstance(
        string serviceName,
        string hostName,
        IntPtr ip4,
        IntPtr ip6,
        ushort port,
        ushort priority,
        ushort weight,
        uint propertiesCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values);

    [DllImport("dnsapi.dll")]
    private static extern void DnsServiceFreeInstance(IntPtr instance);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceDeRegister(IntPtr request, IntPtr cancel);
}

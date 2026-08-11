using System.Runtime.InteropServices;

namespace QingToolbox.Modules.QingTransfer;

internal static class QingTransferNative
{
    internal const uint DnsQueryRequestVersion1 = 1;
    // DNS_REQUEST_PENDING from windns.h (0x2522 / 9506), not Win32 ERROR_IO_PENDING.
    internal const uint DnsRequestPending = 9506;
    internal const ushort DnsRecordTypePtr = 12;
    internal const uint DnsFreeRecordList = 1;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void ServiceComplete(uint status, IntPtr queryContext, IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void BrowseComplete(uint status, IntPtr queryContext, IntPtr records);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceCancel
    {
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceInstance
    {
        public IntPtr InstanceName;
        public IntPtr HostName;
        public IntPtr Ip4Address;
        public IntPtr Ip6Address;
        public ushort Port;
        public ushort Priority;
        public ushort Weight;
        public uint PropertyCount;
        public IntPtr Keys;
        public IntPtr Values;
        public uint InterfaceIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr RegisterCompletionCallback;
        public IntPtr QueryContext;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BrowseRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr BrowseCompletionCallback;
        public IntPtr QueryContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ResolveRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr ResolveCompletionCallback;
        public IntPtr QueryContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DnsRecord
    {
        public IntPtr Next;
        public IntPtr Name;
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
        public IntPtr Data;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr DnsServiceConstructInstance(
        string serviceName,
        string hostName,
        IntPtr ip4,
        IntPtr ip6,
        ushort port,
        ushort priority,
        ushort weight,
        uint propertyCount,
        IntPtr keys,
        IntPtr values);

    [DllImport("dnsapi.dll", CallingConvention = CallingConvention.StdCall)]
    internal static extern uint DnsServiceRegister(
        ref RegisterRequest request,
        out ServiceCancel cancel);

    [DllImport("dnsapi.dll", CallingConvention = CallingConvention.StdCall)]
    internal static extern uint DnsServiceDeRegister(
        ref RegisterRequest request,
        IntPtr cancel);

    [DllImport("dnsapi.dll", CallingConvention = CallingConvention.StdCall)]
    internal static extern uint DnsServiceRegisterCancel(ref ServiceCancel cancel);

    [DllImport("dnsapi.dll", CallingConvention = CallingConvention.StdCall)]
    internal static extern uint DnsServiceBrowse(
        ref BrowseRequest request,
        out ServiceCancel cancel);

    [DllImport("dnsapi.dll", CallingConvention = CallingConvention.StdCall)]
    internal static extern uint DnsServiceBrowseCancel(ref ServiceCancel cancel);

    [DllImport("dnsapi.dll", CallingConvention = CallingConvention.StdCall)]
    internal static extern uint DnsServiceResolve(
        ref ResolveRequest request,
        out ServiceCancel cancel);

    [DllImport("dnsapi.dll", CallingConvention = CallingConvention.StdCall)]
    internal static extern uint DnsServiceResolveCancel(ref ServiceCancel cancel);

    [DllImport("dnsapi.dll", CallingConvention = CallingConvention.StdCall)]
    internal static extern void DnsServiceFreeInstance(IntPtr instance);

    [DllImport("dnsapi.dll", CallingConvention = CallingConvention.StdCall)]
    internal static extern void DnsRecordListFree(IntPtr records, uint freeType);
}

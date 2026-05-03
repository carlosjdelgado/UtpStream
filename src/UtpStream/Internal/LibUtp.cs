using System.Runtime.InteropServices;

namespace UtpStream.Internal;

/// <summary>
/// P/Invoke layer over libutp's C ABI (see native/libutp/utp.h).
/// All entry points are <c>internal</c>; consumers of the package never see them.
/// </summary>
internal static partial class LibUtp
{
    public const string Library = "utp";

    public const int UtpVersion = 2;

    // --- callback ids (utp.h enum) ---
    public const int UTP_ON_FIREWALL = 0;
    public const int UTP_ON_ACCEPT = 1;
    public const int UTP_ON_CONNECT = 2;
    public const int UTP_ON_ERROR = 3;
    public const int UTP_ON_READ = 4;
    public const int UTP_ON_OVERHEAD_STATISTICS = 5;
    public const int UTP_ON_STATE_CHANGE = 6;
    public const int UTP_GET_READ_BUFFER_SIZE = 7;
    public const int UTP_ON_DELAY_SAMPLE = 8;
    public const int UTP_GET_UDP_MTU = 9;
    public const int UTP_GET_UDP_OVERHEAD = 10;
    public const int UTP_GET_MILLISECONDS = 11;
    public const int UTP_GET_MICROSECONDS = 12;
    public const int UTP_GET_RANDOM = 13;
    public const int UTP_LOG = 14;
    public const int UTP_SENDTO = 15;

    // --- options ---
    public const int UTP_LOG_NORMAL = 16;
    public const int UTP_LOG_MTU = 17;
    public const int UTP_LOG_DEBUG = 18;
    public const int UTP_SNDBUF = 19;
    public const int UTP_RCVBUF = 20;
    public const int UTP_TARGET_DELAY = 21;

    // --- socket states (UTP_ON_STATE_CHANGE) ---
    public const int UTP_STATE_CONNECT = 1;
    public const int UTP_STATE_WRITABLE = 2;
    public const int UTP_STATE_EOF = 3;
    public const int UTP_STATE_DESTROYING = 4;

    // --- error codes (UTP_ON_ERROR) ---
    public const int UTP_ECONNREFUSED = 0;
    public const int UTP_ECONNRESET = 1;
    public const int UTP_ETIMEDOUT = 2;

    [LibraryImport(Library)]
    public static partial nint utp_init(int version);

    [LibraryImport(Library)]
    public static partial void utp_destroy(nint ctx);

    [LibraryImport(Library)]
    public static partial void utp_set_callback(nint ctx, int callback_name, nint proc);

    [LibraryImport(Library)]
    public static partial nint utp_context_set_userdata(nint ctx, nint userdata);

    [LibraryImport(Library)]
    public static partial nint utp_context_get_userdata(nint ctx);

    [LibraryImport(Library)]
    public static partial int utp_context_set_option(nint ctx, int opt, int val);

    [LibraryImport(Library)]
    public static partial int utp_process_udp(nint ctx, nint buf, nuint len, nint addr, int addrlen);

    [LibraryImport(Library)]
    public static partial void utp_check_timeouts(nint ctx);

    [LibraryImport(Library)]
    public static partial void utp_issue_deferred_acks(nint ctx);

    [LibraryImport(Library)]
    public static partial nint utp_create_socket(nint ctx);

    [LibraryImport(Library)]
    public static partial nint utp_set_userdata(nint sock, nint userdata);

    [LibraryImport(Library)]
    public static partial nint utp_get_userdata(nint sock);

    [LibraryImport(Library)]
    public static partial int utp_connect(nint sock, nint addr, int addrlen);

    [LibraryImport(Library)]
    public static partial nint utp_write(nint sock, nint buf, nuint count);

    [LibraryImport(Library)]
    public static partial void utp_read_drained(nint sock);

    [LibraryImport(Library)]
    public static partial int utp_getpeername(nint sock, nint addr, ref int addrlen);

    [LibraryImport(Library)]
    public static partial void utp_close(nint sock);
}

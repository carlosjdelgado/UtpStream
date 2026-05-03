using System.Runtime.InteropServices;

namespace UtpStream.Internal;

/// <summary>
/// Mirrors <c>utp_callback_arguments</c> from <c>utp.h</c>.
///
/// Layout (LP64): pointers/size_t are 8 bytes, ints are 4. The two unions
/// follow <c>buf</c>; the first union holds either a sockaddr*, or one of
/// several 4-byte ints; the second union holds either a socklen_t (4 bytes)
/// or an int (4 bytes). Total size on LP64: 56 bytes.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 56)]
internal struct UtpCallbackArguments
{
    [FieldOffset(0)] public nint context;
    [FieldOffset(8)] public nint socket;
    [FieldOffset(16)] public nuint len;
    [FieldOffset(24)] public uint flags;
    [FieldOffset(28)] public int callback_type;
    [FieldOffset(32)] public nint buf;

    // First union @ offset 40
    [FieldOffset(40)] public nint address;     // const struct sockaddr*
    [FieldOffset(40)] public int send;
    [FieldOffset(40)] public int sample_ms;
    [FieldOffset(40)] public int error_code;
    [FieldOffset(40)] public int state;

    // Second union @ offset 48 (after the 8-byte pointer of the first union)
    [FieldOffset(48)] public int address_len;  // socklen_t
    [FieldOffset(48)] public int type;
}

/// <summary>
/// Native callback signature: <c>uint64 utp_callback_t(utp_callback_arguments*)</c>.
/// libutp invokes this synchronously from the thread that owns the context loop.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate ulong UtpCallback(UtpCallbackArguments* args);

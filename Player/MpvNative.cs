using System;
using System.Runtime.InteropServices;

namespace Mio.Player;

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9
}

internal static class MpvNative
{
    private const string LibraryName = "libmpv-2.dll";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_create")]
    private static extern IntPtr NativeCreate();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_initialize")]
    private static extern int NativeInitialize(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_terminate_destroy")]
    private static extern void NativeTerminateDestroy(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_option_string")]
    private static extern int NativeSetOptionString(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_property_string")]
    private static extern int NativeSetPropertyString(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    private static extern int NativeGetProperty(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        IntPtr data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property_string")]
    private static extern IntPtr NativeGetPropertyString(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_command")]
    private static extern int NativeCommand(IntPtr handle, IntPtr args);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_error_string")]
    private static extern IntPtr NativeErrorString(int error);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_free")]
    private static extern void NativeFree(IntPtr data);

    public static IntPtr Create()
    {
        try
        {
            return NativeCreate();
        }
        catch (DllNotFoundException ex)
        {
            throw new MpvException("libmpv-2.dll or one of its native dependencies was not found. Place libmpv-2.dll and required runtime DLLs next to Mio.exe; the project copies Native/libmpv-2.dll automatically.", ex);
        }
        catch (BadImageFormatException ex)
        {
            throw new MpvException("libmpv-2.dll architecture mismatch. Mio is x64 only, so use an x64 libmpv build.", ex);
        }
    }

    public static int Initialize(IntPtr handle)
    {
        return NativeInitialize(handle);
    }

    public static void TerminateDestroy(IntPtr handle)
    {
        NativeTerminateDestroy(handle);
    }

    public static int SetOptionString(IntPtr handle, string name, string value)
    {
        return NativeSetOptionString(handle, name, value);
    }

    public static int SetPropertyString(IntPtr handle, string name, string value)
    {
        return NativeSetPropertyString(handle, name, value);
    }

    public static string? GetPropertyString(IntPtr handle, string name)
    {
        var value = NativeGetPropertyString(handle, name);
        if (value == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            NativeFree(value);
        }
    }

    public static bool TryGetString(IntPtr handle, string name, out string? value)
    {
        value = GetPropertyString(handle, name);
        return value is not null;
    }

    public static bool TrySetPropertyString(IntPtr handle, string name, string value)
    {
        return SetPropertyString(handle, name, value) >= 0;
    }

    public static bool TryGetFlag(IntPtr handle, string name, out bool value)
    {
        value = false;
        var result = TryGetFlagWithResult(handle, name, out value);
        return result >= 0;
    }

    public static bool TryGetDouble(IntPtr handle, string name, out double value)
    {
        value = 0;
        var result = TryGetDoubleWithResult(handle, name, out value);
        return result >= 0;
    }

    public static bool TryGetInt64(IntPtr handle, string name, out long value)
    {
        value = 0;
        var result = TryGetInt64WithResult(handle, name, out value);
        return result >= 0;
    }

    public static int TryGetFlagWithResult(IntPtr handle, string name, out bool value)
    {
        value = false;
        var data = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(data, 0);
            var result = NativeGetProperty(handle, name, MpvFormat.Flag, data);
            if (result >= 0)
            {
                value = Marshal.ReadInt32(data) != 0;
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    public static int TryGetDoubleWithResult(IntPtr handle, string name, out double value)
    {
        value = 0;
        var data = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(data, 0);
            var result = NativeGetProperty(handle, name, MpvFormat.Double, data);
            if (result >= 0)
            {
                value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data));
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    public static int TryGetInt64WithResult(IntPtr handle, string name, out long value)
    {
        value = 0;
        var data = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(data, 0);
            var result = NativeGetProperty(handle, name, MpvFormat.Int64, data);
            if (result >= 0)
            {
                value = Marshal.ReadInt64(data);
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    public static int Command(IntPtr handle, string[] args)
    {
        var nativeStrings = new IntPtr[args.Length + 1];
        var argv = IntPtr.Zero;

        try
        {
            for (var index = 0; index < args.Length; index++)
            {
                nativeStrings[index] = Marshal.StringToCoTaskMemUTF8(args[index]);
            }

            argv = Marshal.AllocHGlobal(IntPtr.Size * nativeStrings.Length);
            for (var index = 0; index < nativeStrings.Length; index++)
            {
                Marshal.WriteIntPtr(argv, index * IntPtr.Size, nativeStrings[index]);
            }

            return NativeCommand(handle, argv);
        }
        finally
        {
            if (argv != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(argv);
            }

            foreach (var nativeString in nativeStrings)
            {
                if (nativeString != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(nativeString);
                }
            }
        }
    }

    public static string ErrorString(int error)
    {
        var value = NativeErrorString(error);
        return Marshal.PtrToStringUTF8(value) ?? $"mpv error {error}";
    }

    public static void ThrowIfError(int result, string operation)
    {
        if (result < 0)
        {
            throw new MpvException($"{operation} failed: {ErrorString(result)} ({result})");
        }
    }
}

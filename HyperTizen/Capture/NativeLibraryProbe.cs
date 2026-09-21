using System;
using System.Runtime.InteropServices;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Small, shared loader for Samsung OS libraries. A library is only kept open
    /// after at least one expected symbol has been found.
    /// </summary>
    internal static class NativeLibraryProbe
    {
        private const int RTLD_LAZY = 1;

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlopen(string filename, int flags);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dlclose(IntPtr handle);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlerror();

        public static IntPtr Open(string path)
        {
            try
            {
                return dlopen(path, RTLD_LAZY);
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"NativeLibraryProbe: dlopen exception for {path}: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        public static IntPtr Symbol(IntPtr handle, string symbol)
        {
            if (handle == IntPtr.Zero || string.IsNullOrEmpty(symbol))
            {
                return IntPtr.Zero;
            }

            try
            {
                return dlsym(handle, symbol);
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"NativeLibraryProbe: dlsym exception for {symbol}: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        public static void Close(ref IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                dlclose(handle);
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"NativeLibraryProbe: dlclose exception: {ex.Message}");
            }
            finally
            {
                handle = IntPtr.Zero;
            }
        }

        public static string LastError()
        {
            try
            {
                IntPtr error = dlerror();
                return error == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(error);
            }
            catch
            {
                return null;
            }
        }
    }
}

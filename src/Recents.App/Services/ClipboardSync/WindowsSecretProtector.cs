using System.Runtime.InteropServices;
using System.Text;

namespace Recents.App.Services.ClipboardSync;

internal static class WindowsSecretProtector
{
    public static string ProtectToBase64(string secret)
    {
        if (string.IsNullOrEmpty(secret))
            return string.Empty;

        var protectedBytes = RunCryptProtect(Encoding.UTF8.GetBytes(secret), protect: true);
        return protectedBytes.Length == 0 ? string.Empty : Convert.ToBase64String(protectedBytes);
    }

    public static string UnprotectFromBase64(string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
            return string.Empty;

        try
        {
            var bytes = Convert.FromBase64String(protectedSecret);
            var clear = RunCryptProtect(bytes, protect: false);
            return clear.Length == 0 ? string.Empty : Encoding.UTF8.GetString(clear);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static byte[] RunCryptProtect(byte[] bytes, bool protect)
    {
        var input = new DataBlob(bytes);
        var output = default(DATA_BLOB);
        try
        {
            var ok = protect
                ? CryptProtectData(ref input.Blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output)
                : CryptUnprotectData(ref input.Blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output);

            if (!ok)
                return Array.Empty<byte>();

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            input.Dispose();
            if (output.pbData != IntPtr.Zero)
                LocalFree(output.pbData);
        }
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, string? ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private sealed class DataBlob : IDisposable
    {
        public DATA_BLOB Blob;

        public DataBlob(byte[] bytes)
        {
            Blob.cbData = bytes.Length;
            Blob.pbData = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, Blob.pbData, bytes.Length);
        }

        public void Dispose()
        {
            if (Blob.pbData == IntPtr.Zero)
                return;

            Marshal.FreeHGlobal(Blob.pbData);
            Blob.pbData = IntPtr.Zero;
            Blob.cbData = 0;
        }
    }
}

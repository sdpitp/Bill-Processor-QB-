using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BillProcessor.Infrastructure.Security;

internal static class DpapiProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] clearBytes)
    {
        ArgumentNullException.ThrowIfNull(clearBytes);
        return Transform(clearBytes, protect: true);
    }

    public static byte[] Unprotect(byte[] encryptedBytes)
    {
        ArgumentNullException.ThrowIfNull(encryptedBytes);
        return Transform(encryptedBytes, protect: false);
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (input.Length == 0)
        {
            return [];
        }

        var inputBlob = new DataBlob();
        var outputBlob = new DataBlob();

        try
        {
            inputBlob.pbData = Marshal.AllocHGlobal(input.Length);
            inputBlob.cbData = input.Length;
            Marshal.Copy(input, 0, inputBlob.pbData, input.Length);

            var success = protect
                ? CryptProtectData(
                    ref inputBlob,
                    string.Empty,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref outputBlob);

            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI encryption/decryption failed.");
            }

            var output = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, output, 0, outputBlob.cbData);
            return output;
        }
        finally
        {
            if (inputBlob.pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputBlob.pbData);
            }

            if (outputBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outputBlob.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

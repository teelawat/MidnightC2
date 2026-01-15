using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MidnightAgent.Utils
{
    public static class CryptoUtils
    {
        // BCrypt P/Invoke for AES-GCM
        // IMPORTANT: BCrypt uses Unicode strings, so we must specify CharSet.Unicode
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptOpenAlgorithmProvider(out IntPtr phAlgorithm, string pszAlgId, string pszImplementation, uint dwFlags);

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptSetProperty(IntPtr hObject, string pszProperty, byte[] pbInput, int cbInput, int dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptGenerateSymmetricKey(IntPtr hAlgorithm, out IntPtr phKey, IntPtr pbKeyObject, int cbKeyObject, byte[] pbSecret, int cbSecret, int dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptDecrypt(IntPtr hKey, byte[] pbInput, int cbInput, ref BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo, byte[] pbIV, int cbIV, byte[] pbOutput, int cbOutput, out int pcbResult, int dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptDestroyKey(IntPtr hKey);

        [StructLayout(LayoutKind.Sequential)]
        private struct BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO
        {
            public int cbSize;
            public int dwInfoVersion;
            public IntPtr pbNonce;
            public int cbNonce;
            public IntPtr pbAuthData;
            public int cbAuthData;
            public IntPtr pbTag;
            public int cbTag;
            public IntPtr pbMacContext;
            public int cbMacContext;
            public int cbAAD;
            public long cbData;
            public int dwFlags;

            public static void Init(ref BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO info)
            {
                info.cbSize = Marshal.SizeOf(typeof(BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO));
                info.dwInfoVersion = 1; // BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO_VERSION
            }
        }

        private const string BCRYPT_AES_ALGORITHM = "AES";
        private const string BCRYPT_CHAINING_MODE = "ChainingMode";
        private const string BCRYPT_CHAIN_MODE_GCM = "ChainingModeGCM";

        public static byte[] DecryptAesGcm(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag, out int errorCode)
        {
            IntPtr hAlg = IntPtr.Zero;
            IntPtr hKey = IntPtr.Zero;
            int status = 0;
            errorCode = 0;
            
            try
            {
                // Open AES provider (null = default implementation)
                status = BCryptOpenAlgorithmProvider(out hAlg, BCRYPT_AES_ALGORITHM, null, 0);
                if (status != 0) { errorCode = 0x10000000 | status; return null; }

                // Set GCM mode
                byte[] modeBytes = Encoding.Unicode.GetBytes(BCRYPT_CHAIN_MODE_GCM);
                status = BCryptSetProperty(hAlg, BCRYPT_CHAINING_MODE, modeBytes, modeBytes.Length, 0);
                if (status != 0) { errorCode = 0x20000000 | status; return null; }

                // Generate key - MUST pin the key object buffer
                byte[] keyObj = new byte[1024];
                GCHandle hKeyObj = GCHandle.Alloc(keyObj, GCHandleType.Pinned);
                
                try
                {
                    status = BCryptGenerateSymmetricKey(hAlg, out hKey, hKeyObj.AddrOfPinnedObject(), keyObj.Length, key, key.Length, 0);
                    if (status != 0) { errorCode = 0x30000000 | status; return null; }

                    // Setup auth info for GCM
                    var authInfo = new BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO();
                    BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO.Init(ref authInfo);
                    
                    GCHandle hNonce = GCHandle.Alloc(nonce, GCHandleType.Pinned);
                    GCHandle hTag = GCHandle.Alloc(tag, GCHandleType.Pinned);
                    
                    try
                    {
                        authInfo.pbNonce = hNonce.AddrOfPinnedObject();
                        authInfo.cbNonce = nonce.Length;
                        authInfo.pbTag = hTag.AddrOfPinnedObject();
                        authInfo.cbTag = tag.Length;

                        // Calculate output size
                        int outputSize = 0;
                        status = BCryptDecrypt(hKey, ciphertext, ciphertext.Length, ref authInfo, null, 0, null, 0, out outputSize, 0);
                        
                        if (status != 0) { errorCode = status; return null; }

                        byte[] plaintext = new byte[outputSize];
                        status = BCryptDecrypt(hKey, ciphertext, ciphertext.Length, ref authInfo, null, 0, plaintext, outputSize, out outputSize, 0);

                        if (status != 0) { errorCode = status; return null; }

                        return plaintext;
                    }
                    finally
                    {
                        if (hNonce.IsAllocated) hNonce.Free();
                        if (hTag.IsAllocated) hTag.Free();
                    }
                }
                finally
                {
                    if (hKey != IntPtr.Zero) BCryptDestroyKey(hKey);
                    if (hKeyObj.IsAllocated) hKeyObj.Free();
                }
            }
            finally
            {
                if (hAlg != IntPtr.Zero) BCryptCloseAlgorithmProvider(hAlg, 0);
            }
        }

        public static byte[] DecryptApi(byte[] data)
        {
            try
            {
                return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            }
            catch
            {
                return null;
            }
        }
    }
}

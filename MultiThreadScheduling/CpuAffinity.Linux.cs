using System;
using System.Runtime.InteropServices;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Manipulates CPU affinity for the current process and thread.
    /// </summary>
    internal static unsafe class CpuAffinity
    {
        [DllImport("libc", EntryPoint = "sched_getaffinity", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, SetLastError = true)]
        private extern static int SchedGetAffinity(Int32 pid, System.UIntPtr cpuSetSize, ulong* mask);

        [DllImport("libc", EntryPoint = "sched_setaffinity", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, SetLastError = true)]
        private extern static int SchedSetAffinity(Int32 pid, System.UIntPtr cpuSetSize, ulong* mask);

        [DllImport("libc", EntryPoint = "getpid", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private extern static Int32 GetPid();

        /// <summary>
        /// Get the CPU affinity mask for the current process.
        /// </summary>
        /// <param name="cpuMask">On success, the mask will be stored in this buffer. </param>
        public static void GetForCurrentProcess(in BitMask cpuMask)
        {
            var pid = GetPid();

            fixed (ulong* cpuMaskPtr = &cpuMask.GetPinnableReference())
            {
                if (SchedGetAffinity(pid, cpuMask.NumNativeWords, cpuMaskPtr) == 0)
                    return;
            }
        }

        /// <summary>
        /// Set the CPU affinity mask for the current thread.
        /// </summary>
        /// <param name="cpuMask">On success, the mask will be stored in this buffer. </param>
        public static void SetForCurrentThread(in BitMask cpuMask)
        {
            fixed (ulong* cpuMaskPtr = &cpuMask.GetPinnableReference())
            {
                if (SchedSetAffinity(0, cpuMask.NumNativeWords, cpuMaskPtr) == 0)
                    return;
            }
        }
    }
}

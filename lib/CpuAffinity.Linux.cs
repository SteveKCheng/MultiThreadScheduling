using System;
using System.Runtime.InteropServices;
using System.Threading;

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

        [DllImport("libc", EntryPoint = "syscall", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, SetLastError = true)]
        private extern static IntPtr SysCall0(IntPtr number);

        [DllImport("libc", EntryPoint = "setpriority", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, SetLastError = true)]
        private extern static int SetPriority(int which, Int32 who, int value);
        
        public static void SetThreadNiceValue(Int32 pid, int niceValue)
        {
            if (SetPriority(0 /* PRIO_PROCESS */, pid, niceValue) < 0)
                throw new ThreadStateException("Error setting thread's nice value (priority)");
        }

        public static Int32 GetCurrentThreadId()
        {
            int number = Environment.Is64BitProcess ? 186 : 224;
            return (int)SysCall0((IntPtr)number);
        }

        public static void SetCurrentThreadPriority(ThreadPriority priority)
        {
            int niceValue = priority switch
            {
                ThreadPriority.Lowest => 19,
                ThreadPriority.BelowNormal => 10,
                ThreadPriority.Normal => 0,
                ThreadPriority.AboveNormal => -10,
                ThreadPriority.Highest => -20,
                _ => throw new NotImplementedException()
            };

            int tid = GetCurrentThreadId();
            SetThreadNiceValue(tid, niceValue);
        }
    }
}

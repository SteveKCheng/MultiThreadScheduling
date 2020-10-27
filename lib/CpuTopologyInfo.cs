using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Information on the topology of a logical CPU, relevant for scheduling threads.
    /// </summary>
    internal partial struct CpuTopologyInfo
    {
        /// <summary>
        /// ID for the logical CPU.  
        /// </summary>
        /// <remarks>
        /// This member is guaranteed to be an increasing integer when 
        /// an array of <see cref="CpuTopologyInfo"/> is reported by
        /// <see cref="GetList"/>.
        /// </remarks>
        public short LogicalId;

        /// <summary>
        /// ID for the CPU core this logical CPU is part of.
        /// </summary>
        public short CoreId;

        /// <summary>
        /// ID for the CPU physical package this logical CPU is part of.
        /// </summary>
        public short PackageId;

        /// <summary>
        /// Get the list of all logical CPUs in the host system.
        /// </summary>
        public static CpuTopologyInfo[] GetList()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return Linux_GetList();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Windows_GetList();
            else
                throw new PlatformNotSupportedException();
        }

        /// <summary>
        /// Count how many physical cores in total are present.
        /// </summary>
        /// <param name="infoArray">CPU topology info from <see cref="GetList"/>. </param>
        public static int CountNumberOfCores(CpuTopologyInfo[] infoArray)
        {
            // Prevent stack overflow
            if (infoArray.Length > short.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(infoArray));

            var cores = new BitMask(stackalloc ulong[BitMask.GetNumberOfNativeWords(infoArray.Length)]);

            foreach (var item in infoArray)
                cores[item.CoreId] = true;

            return cores.CountOnBits();
        }
    }
}

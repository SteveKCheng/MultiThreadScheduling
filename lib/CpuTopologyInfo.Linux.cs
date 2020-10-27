using System;
using System.Buffers.Text;
using System.Globalization;
using System.IO;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Information on the topology of a logical CPU, relevant for scheduling threads.
    /// </summary>
    internal struct CpuTopologyInfo
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
        /// Check if the last segment of the path is of the form "cpuN" 
        /// which is the name of the entry for a CPU under Linux's sysfs tree.
        /// </summary>
        /// <param name="path">Absolute path to the directory. </param>
        /// <param name="id">If successful, set to the number assigned
        /// to the CPU, decoded from the path. </param>
        /// <returns>Whether the path string matches the expected string
        /// for a CPU entry.
        /// </returns>
        private static bool IsSysFsCpuDirectory(string path, out short id)
        {
            id = 0;
            var span = path.AsSpan();

            var index = span.LastIndexOf('/');
            if (index < 0)
                return false;
                
            span = span.Slice(index + 1);
            if (!span.StartsWith("cpu"))
                return false;
            span = span.Slice(3);

            if (int.TryParse(span, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var idInt)
                && idInt >= 0 && idInt <= short.MaxValue)
            {
                id = (short)idInt;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Read one of Linux's sysfs files which is assumed to contain a small
        /// non-negative integer, written in decimal ASCII digits.
        /// </summary>
        /// <param name="filePath">Full path to the sysfs file. </param>
        /// <returns>The integer read. </returns>
        private static short ParseShortIntegerFromFile(string filePath)
        {
            Span<byte> buffer = stackalloc byte[32];

            using (var file = File.OpenRead(filePath))
            {
                int bytesRead = file.Read(buffer);

                // Roundabout way of checking if we are not at end of file yet.
                // We cannot use file.Length because the kernel reports an incorrect
                // size, 4096, for the sysfs file.  This bug is even visible from
                // the output of the "ls" command!
                if (bytesRead > 16)
                    throw new Exception($"Unexpected data in {filePath}");

                if (bytesRead <= 0 || buffer[bytesRead - 1] != 0x0A)
                    throw new Exception($"Unexpected data in {filePath}");

                buffer = buffer.Slice(0, bytesRead - 1);
                if (!Utf8Parser.TryParse(buffer, out short value, out bytesRead) || value < 0)
                    throw new Exception($"Unexpected data in {filePath}");

                return value;
            }
        }

        /// <summary>
        /// Read the topological information for a logical CPU from its
        /// Linux sysfs file entry.
        /// </summary>
        /// <param name="cpuId">Integer ID assigned to the logical CPU. </param>
        /// <returns>Basic topological information about the logical CPU. </returns>
        private static CpuTopologyInfo ReadFromSysFsFile(int cpuId)
        {
            var dirPath = $"/sys/devices/system/cpu/cpu{cpuId}/topology/";

            return new CpuTopologyInfo
            {
                LogicalId = (short)cpuId,
                CoreId = ParseShortIntegerFromFile(dirPath + "core_id"),
                PackageId = ParseShortIntegerFromFile(dirPath + "physical_package_id")
            };
        }

        /// <summary>
        /// Get the list of all logical CPUs in the host system.
        /// </summary>
        public static CpuTopologyInfo[] GetList()
        {
            var cpuMask = new BitMask(stackalloc ulong[512]);
            int cpuCount = 0;

            foreach (var fullDirName in Directory.EnumerateDirectories("/sys/devices/system/cpu/"))
            {
                if (IsSysFsCpuDirectory(fullDirName, out var cpuIdShort))
                {
                    cpuMask[cpuIdShort] = true;
                    cpuCount++;
                }
            }

            var infoArray = new CpuTopologyInfo[cpuCount];
            int cpuId = -1;
            for (int i = 0; (cpuId = cpuMask.GetIndexOfNextOnBit(++cpuId)) >= 0; ++i)
                infoArray[i] = ReadFromSysFsFile(cpuId);

            return infoArray;
        }
    }
}

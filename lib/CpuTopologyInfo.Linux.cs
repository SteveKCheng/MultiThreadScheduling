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
        private static bool IsSysFsCpuDirectory(string path, out int id)
        {
            var span = path.AsSpan();

            var index = span.LastIndexOf('/');
            if (index < 0)
            {
                id = 0;
                return false;
            }
                
            span = span.Slice(index + 1);
            if (!span.StartsWith("cpu"))
                return false;
            span = span.Slice(3);

            return int.TryParse(span, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out id) && id >= 0;
        }

        /// <summary>
        /// Read one of Linux's sysfs files which is assumed to contain a small
        /// non-negative integer, written in decimal ASCII digits.
        /// </summary>
        /// <param name="filePath">Full path to the sysfs file. </param>
        /// <param name="buffer">Pre-allocated buffer for reading the file. </param>
        /// <returns>The integer read. </returns>
        private static short ParseShortIntegerFromFile(string filePath, Span<byte> buffer)
        {
            using (var file = File.OpenRead(filePath))
            {
                int bytesRead = file.Read(buffer);
                if (file.Position != file.Length)
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
            Span<byte> buffer = stackalloc byte[64];

            var dirPath = $"/sys/devices/system/cpu/cpu{cpuId}/topology/";

            return new CpuTopologyInfo
            {
                CoreId = ParseShortIntegerFromFile(dirPath + "core_id", buffer),
                PackageId = ParseShortIntegerFromFile(dirPath + "physical_package_id", buffer)
            };
        }

        /// <summary>
        /// Get the list of all logical CPUs in the host system.
        /// </summary>
        public static CpuTopologyInfo?[] GetList()
        {
            int maxCpuId = -1;

            Span<bool> mask = stackalloc bool[short.MaxValue];

            foreach (var fullDirName in Directory.EnumerateDirectories("/sys/devices/system/cpu/"))
            {
                if (IsSysFsCpuDirectory(fullDirName, out var cpuId) && cpuId <= short.MaxValue)
                {
                    maxCpuId = Math.Max(maxCpuId, cpuId);
                    mask[cpuId] = true;
                }
            }

            if (maxCpuId < 0)
                return new CpuTopologyInfo?[0];

            var infoArray = new CpuTopologyInfo?[maxCpuId + 1];
            for (int cpuId = 0; cpuId <= maxCpuId; ++cpuId)
            {
                if (mask[cpuId])
                    infoArray[cpuId] = ReadFromSysFsFile(cpuId);
            }

            return infoArray;
        }
    }
}

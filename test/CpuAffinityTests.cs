using System;
using System.Linq;
using Xunit;
using System.Runtime.InteropServices;

namespace MultiThreadScheduling.Tests
{
    public class CpuAffinityTests
    {
        [Fact]
        public void Test1()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return;

            var cpuMask = new BitMask(stackalloc ulong[512]);
            CpuAffinity.GetForCurrentProcess(cpuMask);

            int countTrue = 0;
            for (int i = 0; i < cpuMask.Count; ++i)
            {
                if (cpuMask[i]) countTrue++;
            }

            Assert.NotEqual(0, countTrue);
        }
    }
}

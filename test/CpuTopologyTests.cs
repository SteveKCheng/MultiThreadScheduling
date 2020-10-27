using System;
using System.Linq;
using Xunit;
using System.Runtime.InteropServices;

namespace MultiThreadScheduling.Tests
{
    public class CpuTopologyTests
    {
        [Fact]
        public void GetList()
        {
            var infoArray = CpuTopologyInfo.GetList();

            for (int i = 0; i < infoArray.Length; ++i)
                Assert.Equal(infoArray[i].LogicalId, i);

            // Environment.ProcessorCount may be less than the number
            // of logical CPUs because of CPU quota.
            Assert.InRange(infoArray.Length, Environment.ProcessorCount, int.MaxValue);

            var numCores = CpuTopologyInfo.CountNumberOfCores(infoArray);
            Assert.InRange(numCores, 1, infoArray.Length);
        }
    }
}

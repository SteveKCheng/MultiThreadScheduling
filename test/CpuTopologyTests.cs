using System;
using System.Linq;
using Xunit;
using System.Runtime.InteropServices;

namespace MultiThreadScheduling.Tests
{
    public class CpuTopologyTests
    {
        [Fact]
        public void Test1()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return;

            var infoArray = CpuTopologyInfo.GetList();
            Assert.NotNull(infoArray);
        }
    }
}

using System;
using Xunit;

namespace MultiThreadScheduling.Tests
{
    public class BitMaskTests
    {
        [Fact]
        public void GetIndexOfNextOnBit()
        {
            var mask = new BitMask(stackalloc ulong[4]);
            mask[2] = true;

            Assert.Equal(2, mask.GetIndexOfNextOnBit(0));
            Assert.Equal(2, mask.GetIndexOfNextOnBit(1));
            Assert.Equal(2, mask.GetIndexOfNextOnBit(2));
            Assert.Equal(-1, mask.GetIndexOfNextOnBit(3));
            Assert.Equal(-1, mask.GetIndexOfNextOnBit(64));
        }

        [Fact]
        public void CountOnBits()
        {
            var mask = new BitMask(stackalloc ulong[4]);

            for (int i = 0; i < (4 * 64) / 8; ++i)
                mask[i * 8 + 3] = true;

            Assert.Equal((4 * 64) / 8, mask.CountOnBits());
        }
    }
}

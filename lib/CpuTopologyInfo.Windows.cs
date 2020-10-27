using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiThreadScheduling
{
    internal partial struct CpuTopologyInfo
    {
        [DllImport("kernel32", CallingConvention = CallingConvention.Winapi, ExactSpelling = true, SetLastError = true)]
        private extern static bool GetLogicalProcessorInformationEx(
                                    LogicalProcessorRelationship relationshipType,
                                    IntPtr buffer,
                                    ref uint returnedLength);

        private enum LogicalProcessorRelationship : int
        {
            ProcessorCore = 0,
            NumaNode = 1,
            Cache = 2,
            ProcessorPackage = 3,
            Group = 4,
            All = 0xFFFF
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct SystemLogicalProcessorInformationEx
        {
            [FieldOffset(0)]
            public LogicalProcessorRelationship Relationship;

            [FieldOffset(4)]
            public uint Size;

            [FieldOffset(8)]
            public ProcessorRelationship Processor;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct ProcessorRelationship
        {
            [FieldOffset(0)]
            public byte Flags;

            [FieldOffset(1)]
            public byte EfficiencyClass;

            [FieldOffset(22)]
            public ushort GroupCount;

            public static unsafe GroupAffinity* GetGroup(ProcessorRelationship* p, int index)
            {
                if (index < 0 || index >= p->GroupCount)
                    throw new IndexOutOfRangeException();

                return (GroupAffinity*)((byte*)p + 24) + index;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GroupAffinity
        {
            public System.UIntPtr Mask;
            public ushort Group;
            private ushort Reserved0;
            private ushort Reserved1;
            private ushort Reserved2;
        }

        private ref struct LogicalProcessorInformationIterator
        {
            private Span<byte> _buffer;
            private int _next;
            private int _start;

            public unsafe LogicalProcessorInformationIterator(LogicalProcessorRelationship relationship,
                                                              Span<byte> buffer)
            {
                uint length = (uint)buffer.Length;
                bool success;

                fixed (byte* p = buffer)
                {
                    success = GetLogicalProcessorInformationEx(relationship,
                                                               (IntPtr)p,
                                                               ref length);
                }

                if (success)
                {
                    _buffer = buffer.Slice(0, (int)length);
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 122 /* ERROR_INSUFFICIENT_BUFFER */)
                        throw new Win32Exception(error);

                    _buffer = new Span<byte>();
                }

                _start = 0;
                _next = 0;
            }

            public unsafe bool MoveNext()
            {
                _start += _next;
                if (_start == _buffer.Length)
                    return false;

                fixed (byte* p = _buffer)
                {
                    var item = (SystemLogicalProcessorInformationEx*)(p + _start);
                    _next = (int)item->Size;
                }

                return true;
            }

            public unsafe ref SystemLogicalProcessorInformationEx Current
            {
                get
                {
                    if (_start == _buffer.Length)
                        throw new InvalidOperationException();

                    return ref Unsafe.AsRef<SystemLogicalProcessorInformationEx>(
                            Unsafe.AsPointer(ref _buffer.Slice(_start).GetPinnableReference()));
                }
            }

            public void Reset()
            {
                _start = 0;
                _next = 0;
            }
        }

        private static unsafe void LoadCpuMaskFromGroupAffinities(ProcessorRelationship* p, 
                                                                  ref BitMask cpuMask)
        {
            for (int i = 0; i < p->GroupCount; ++i)
            {
                GroupAffinity* g = ProcessorRelationship.GetGroup(p, i);
                int groupIndex = g->Group;

                if (Environment.Is64BitProcess)
                    cpuMask.SetWord64(groupIndex, (ulong)g->Mask);
                else
                    cpuMask.SetWord32(groupIndex, (uint)g->Mask);
            }
        }

        private unsafe static CpuTopologyInfo[] Windows_GetList()
        {
            Span<byte> buffer = stackalloc byte[8192];

            var coresIterator = new LogicalProcessorInformationIterator(
                                        LogicalProcessorRelationship.ProcessorCore, 
                                        buffer);

            // First, count the total of logical processors, to allocate the result array.
            int cpuCount = 0;
            while (coresIterator.MoveNext())
            {
                fixed (SystemLogicalProcessorInformationEx* p = &coresIterator.Current)
                {
                    if (p->Relationship != LogicalProcessorRelationship.ProcessorCore)
                        continue;

                    for (int i = 0; i < p->Processor.GroupCount; ++i)
                    {
                        GroupAffinity* g = ProcessorRelationship.GetGroup(&p->Processor, i);
                        cpuCount += Environment.Is64BitProcess ? BitOperations.PopCount((ulong)g->Mask)
                                                               : BitOperations.PopCount((uint)g->Mask);
                    }
                }
            }

            var infoArray = new CpuTopologyInfo[cpuCount];
            for (int cpuId = 0; cpuId < cpuCount; ++cpuId)
                infoArray[cpuId].LogicalId = (short)cpuId;

            var cpuMask = new BitMask(stackalloc ulong[BitMask.GetNumberOfNativeWords(cpuCount)]);

            // Now assign IDs to the physical cores, and then set these IDs into infoArray
            coresIterator.Reset();
            short coreId = 0;
            while (coresIterator.MoveNext())
            {
                fixed (SystemLogicalProcessorInformationEx* p = &coresIterator.Current)
                {
                    if (p->Relationship != LogicalProcessorRelationship.ProcessorCore)
                        continue;

                    LoadCpuMaskFromGroupAffinities(&p->Processor, ref cpuMask);

                    for (int cpuId = 0; (cpuId = cpuMask.GetIndexOfNextOnBit(cpuId)) >= 0; ++cpuId)
                        infoArray[cpuId].CoreId = coreId;

                    ++coreId;
                }
            }

            // Do the same for package IDs
            var packagesIterator = new LogicalProcessorInformationIterator(
                                        LogicalProcessorRelationship.ProcessorPackage,
                                        buffer);
            short packageId = 0;
            while (packagesIterator.MoveNext())
            {
                fixed (SystemLogicalProcessorInformationEx* p = &packagesIterator.Current)
                {
                    if (p->Relationship != LogicalProcessorRelationship.ProcessorPackage)
                        continue;

                    LoadCpuMaskFromGroupAffinities(&p->Processor, ref cpuMask);

                    for (int cpuId = 0; (cpuId = cpuMask.GetIndexOfNextOnBit(cpuId)) >= 0; ++cpuId)
                        infoArray[cpuId].PackageId = packageId;

                    ++packageId;
                }
            }

            return infoArray;
        }
    }
}

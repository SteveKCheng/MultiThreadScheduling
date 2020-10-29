using System;
using System.Threading;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Specifies how worker threads 
    /// in <see cref="MultiThreadScheduler{TWorkItem, TExecutor}"/>
    /// are to be created.
    /// </summary>
    [Flags]
    public enum MultiThreadCreationMode
    {
        /// <summary>
        /// The caller is setting the number of threads manually.
        /// </summary>
        CustomNumberOfThreads = 0,

        /// <summary>
        /// Set the number of threads to be equal to the number of logical CPUs.
        /// </summary>
        /// <remarks>
        /// Only the logical CPUs accessible to this process are considered.
        /// </remarks>
        OneThreadPerLogicalCpu = 1,

        /// <summary>
        /// Set the number of threads to be equal to the number of CPU cores.
        /// </summary>
        /// <remarks>
        /// Only the CPU cores accessible to this process are considered.
        /// </remarks>
        OneThreadPerCpuCore = 3,

        /// <summary>
        /// Cap the number of threads to the CPU quota in rounded up to the
        /// next unit of CPU.
        /// </summary>
        CapThreadsAtCpuQuota = 4,
    }

    /// <summary>
    /// User-serviceable options for <see cref="MultiThreadScheduler{TWorkItem, TExecutor}"/>
    /// and <see cref="MultiThreadTaskScheduler"/>.
    /// </summary>
    public struct MultiThreadSchedulingSettings
    {
        /// <summary>
        /// Controls how worker threads to be created.
        /// </summary>
        public MultiThreadCreationMode Mode { get; set; }

        /// <summary>
        /// Specifies the priority of the worker threads for the operating system's
        /// scheduler.
        /// </summary>
        public ThreadPriority ThreadPriority { get; set; }

        /// <summary>
        /// Sets the number of worker threads explicitly.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This member is consulted when <see cref="Mode"/> is specified
        /// with <see cref="MultiThreadCreationMode.CustomNumberOfThreads"/>,
        /// in which case it must be a positive number, and may not exceed 
        /// some implementation-defined multiple of the total number of CPUs.
        /// </para>
        /// <para>
        /// When <see cref="Mode"/> is specified to automatically determine
        /// the number of threads, this member is ignored on input.  But
        /// this member does get set on output to the determined number of
        /// threads in <see cref="MultiThreadScheduler{TWorkItem, TExecutor}.SchedulingOptions"/>.
        /// </para>
        /// </remarks>
        public int NumberOfThreads { get; set; }
    }
}

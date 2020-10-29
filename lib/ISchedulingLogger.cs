using System;
using System.Collections.Generic;
using System.Text;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Indicates where a task item has been de-queued from, for logging.
    /// </summary>
    public enum WorkSourceQueue
    {
        /// <summary>
        /// The local queue to a worker thread.
        /// </summary>
        Local,

        /// <summary>
        /// The global queue shared by all worker threads.
        /// </summary>
        Global,

        /// <summary>
        /// The local queue from another worker thread that is
        /// not the current worker thread.
        /// </summary>
        Stolen
    }

    /// <summary>
    /// Receives notification when <see cref="MultiThreadTaskScheduler"/>
    /// does something significant, for diagnostic logging.
    /// </summary>
    public interface ISchedulingLogger
    {
        /// <summary>
        /// Called when a worker thread starts up.
        /// </summary>
        /// <param name="workerId">ID of the worker thread calling this method. </param>
        /// <remarks>
        /// This method should not throw an exception at all, or it will become
        /// an unhandled exception.
        /// </remarks>
        void WorkerStarts(uint workerId);

        /// <summary>
        /// Called when a worker thread is about to stop.
        /// </summary>
        /// <param name="workerId">ID of the worker thread calling this method. </param>
        /// <remarks>
        /// This method should not throw an exception at all, or it will become
        /// an unhandled exception.
        /// </remarks>
        void WorkerStops(uint workerId);

        /// <summary>
        /// Called when the scheduler is about to run a task item.
        /// </summary>
        /// <param name="workerId">ID of the worker thread calling this method. </param>
        /// <param name="sourceQueue">Where the task item came from. </param>
        /// <param name="workInfo">Some basic information about the work item. </param>
        void BeginTask(uint workerId, WorkSourceQueue sourceQueue, in WorkItemInfo workInfo);

        /// <summary>
        /// Called when the scheduler has finished running a task,
        /// even if it fails.
        /// </summary>
        /// <param name="workerId">ID of the worker thread calling this method. </param>
        /// <param name="sourceQueue">Where the task item came from. </param>
        /// <param name="workInfo">The same information about the work item
        /// passed in to <see cref="BeginTask"/>. </param>
        /// <param name="workStatus">Status after having run the work item. </param>
        void EndTask(uint workerId, 
                     WorkSourceQueue sourceQueue, 
                     in WorkItemInfo workInfo, 
                     WorkExecutionStatus workStatus);

        /// <summary>
        /// A worker thread is about to become idle because it (momentarily)
        /// found no work to execute.
        /// </summary>
        /// <param name="workerId">ID of the worker thread calling this method. </param>
        /// <remarks>
        /// The worker thread implicitly stops idling (sleeping) when 
        /// it next invokes another method in this interface.
        /// </remarks>
        void Idle(uint workerId);

        /// <summary>
        /// Called when an exception occurs in the worker's queue
        /// processing or when a task propagates out an exception.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Normally such exceptions should not occur.  Tasks hold any
        /// exceptions from executing the user's code and do not leak
        /// that out to the executor.
        /// </para>
        /// <para>
        /// This method should not raise exceptions itself.  If it does,
        /// the process may shut down because the exception may be unhandled.
        /// </para>
        /// </remarks>
        /// <param name="exception">The exception being thrown. </param>
        void RaiseCriticalError(Exception exception);
    }
}

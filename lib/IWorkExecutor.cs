using System;
using System.Collections.Generic;
using System.Text;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Represents an object that can execute abstract work items in <see cref="MultiThreadScheduler{TWorkItem, TExecutor}"/>,
    /// or otherwise any state that is needed to interpret or process the work item.
    /// </summary>
    /// <typeparam name="TWorkItem">
    /// Type of the abstract message that may be queued into <see cref="MultiThreadScheduler{TWorkItem}"/>
    /// and gets processed by one of its worker threads.
    /// </typeparam>
    public interface IWorkExecutor<in TWorkItem>
    {
        /// <summary>
        /// Called by a worker thread to execute the work item, synchronously, in that thread.
        /// </summary>
        /// <param name="workItem">The work item to execute. </param>
        /// <remarks>
        /// This method normally should not propagate exceptions out.  Exceptions
        /// will cause the worker thread to terminate.
        /// </remarks>
        WorkExecutionStatus Execute(TWorkItem workItem);

        /// <summary>
        /// Get a summary of the work item about to be executed, 
        /// to pass to <see cref="ISchedulingLogger"/>.
        /// </summary>
        /// <param name="workItem">The work item about to be executed. </param>
        /// <returns>Applicable information about the work item. </returns>
        /// <remarks>
        /// Any exception thrown from this method becomes a critical error for the 
        /// worker thread.
        /// </remarks>
        WorkItemInfo GetWorkItemInfo(TWorkItem workItem);
    }

    /// <summary>
    /// Summarizes the status of running a work item, for logging.
    /// </summary>
    public enum WorkExecutionStatus
    {
        /// <summary>
        /// The work has been run successfully, but may be re-queued again
        /// because it is currently awaiting other work.
        /// </summary>
        Succeeded = 0,

        /// <summary>
        /// The work has been completed successfully.
        /// </summary>
        Completed = 1,

        /// <summary>
        /// The work was cancelled.
        /// </summary>
        Cancelled = 2,

        /// <summary>
        /// The work has failed.
        /// </summary>
        Faulted = 3,
    }

    /// <summary>
    /// A summary of a work item, for logging.
    /// </summary>
    /// <remarks>
    /// The work item type in <see cref="MultiThreadScheduler{TWorkItem, TExecutor}"/> is generic,
    /// but the logging interface <see cref="ISchedulingLogger"/> is not generic. 
    /// This structure contains a subset of information that has been deemed useful for logging
    /// and monitoring.
    /// </remarks>
    public readonly struct WorkItemInfo
    {
        /// <summary>
        /// Object or state that represents the work item.
        /// </summary>
        /// <remarks>
        /// This member is completely optional.  If the work item is intrinsically
        /// a structure, then it is recommended not to box it to report it
        /// to this member.  This object may be some kind of task object or
        /// delegate.
        /// </remarks>
        public readonly object? Object1 { get; }

        /// <summary>
        /// Auxiliary object or state that represents the work item.
        /// </summary>
        /// <remarks>
        /// This member is completely optional like <see cref="Object1"/>. 
        /// This member may be the type-erased state object if 
        /// <see cref="Object1"/> is an action delegate.
        /// </remarks>
        public readonly object? Object2 { get; }

        /// <summary>
        /// The number of milliseconds the work item has spent in the queue.
        /// </summary>
        /// <remarks>
        /// The call to <see cref="IWorkExecutor{TWorkItem}.GetWorkItemInfo(TWorkItem)"/>
        /// is implicitly the time when the work item gets de-queued.
        /// </remarks>
        public readonly int MillisecondsInQueue { get; }

        /// <summary>
        /// A numerical ID assigned to the work item.
        /// </summary>
        public readonly int Id { get; }

        /// <summary>
        /// Report a work item with an ID and waiting time.
        /// </summary>
        public WorkItemInfo(int id, int millisecondsInQueue, object? object1 = null, object? object2 = null)
        {
            Id = id;
            MillisecondsInQueue = millisecondsInQueue;
            Object1 = object1;
            Object2 = object2;
        }

        /// <summary>
        /// Report a work item with an ID.
        /// </summary>
        public WorkItemInfo(int id, object? object1 = null, object? object2 = null)
        {
            Id = id;
            MillisecondsInQueue = -1;
            Object1 = object1;
            Object2 = object2;
        }
    }
}

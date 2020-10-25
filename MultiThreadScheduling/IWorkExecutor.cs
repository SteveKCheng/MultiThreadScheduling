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
        void Execute(TWorkItem workItem);
    }
}

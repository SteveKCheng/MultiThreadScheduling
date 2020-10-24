using System;
using System.Collections.Generic;
using System.Text;

namespace WorkStealingScheduler
{
    /// <summary>
    /// Receives notification when <see cref="WorkStealingTaskScheduler"/>
    /// does something significant, for diagnostic logging.
    /// </summary>
    public interface ITaskSchedulerLogger
    {
        /// <summary>
        /// Where a task item has been de-queued from.
        /// </summary>
        public enum SourceQueue
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
        /// Called when the scheduler is about to run a task item.
        /// </summary>
        /// <param name="sourceQueue">Where the task item came from. </param>
        void BeginTask(SourceQueue sourceQueue);

        /// <summary>
        /// Called when the scheduler has finished running a task,
        /// even if it fails.
        /// </summary>
        /// <param name="sourceQueue">Where the task item came from. </param>
        void EndTask(SourceQueue sourceQueue);

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

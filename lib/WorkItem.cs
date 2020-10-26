using System;
using System.Threading;
using System.Threading.Tasks;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Describes one unit of work to be done for <see cref="MultiThreadTaskScheduler"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="MultiThreadTaskScheduler"/> doubles up as a synchronization context
    /// so this structure allows <see cref="System.Threading.Tasks.Task"/> objects or
    /// <see cref="SendOrPostCallback"/> delegates.
    /// </remarks>
    internal readonly struct WorkItem
    {
        /// <summary>
        /// The task to execute as the work.
        /// </summary>
        public readonly Task? Task;

        /// <summary>
        /// Delegate passed to <see cref="SynchronizationContext"/> to run.
        /// </summary>
        public readonly SendOrPostCallback? SyncContextAction;

        /// <summary>
        /// State object to pass to <see cref="SyncContextAction"/>.
        /// </summary>
        public readonly object? SyncContextActionState;

        public WorkItem(Task task)
        {
            Task = task;
            SyncContextAction = null;
            SyncContextActionState = null;
        }

        public WorkItem(SendOrPostCallback action, object? state)
        {
            Task = null;
            SyncContextAction = action;
            SyncContextActionState = state;
        }
    }
}

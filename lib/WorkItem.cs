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
        /// Stores either <see cref="Task"/> or <see cref="SendOrPostCallback"/>.
        /// </summary>
        internal readonly object Action;

        /// <summary>
        /// State object if <see cref="Action"/> is <see cref="SendOrPostCallback"/>.
        /// </summary>
        internal readonly object? State;

        public WorkItem(Task task)
        {
            Action = task;
            State = null;
        }

        public WorkItem(SendOrPostCallback action, object? state)
        {
            Action = action;
            State = state;
        }
    }
}

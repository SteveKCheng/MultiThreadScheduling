using System;
using System.Threading.Tasks;

namespace WorkStealingScheduler
{
    /// <summary>
    /// Describes one unit of work to be done.
    /// </summary>
    internal struct WorkItem
    {
        /// <summary>
        /// The task to execute as the work.
        /// </summary>
        /// <remarks>
        /// For now this is the only member.  We still have this struct
        /// in case we want to augment the task with diagnostics information,
        /// or take a delegate directly to execute.
        /// </remarks>
        public Task TaskToRun;
    }
}

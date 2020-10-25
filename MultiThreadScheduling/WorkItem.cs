using System;
using System.Threading.Tasks;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Describes one unit of work to be done.
    /// </summary>
    internal struct WorkItem : IWorkItem
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

        public void Execute(object? executor)
        {
            var taskScheduler = (MultiThreadTaskScheduler)executor!;
            taskScheduler.ExecuteTaskFromWorker(this);
        }
    }
}

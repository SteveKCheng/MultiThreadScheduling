using System;
using System.Collections.Generic;
using System.Text;

namespace WorkStealingScheduler
{
    /// <summary>
    /// Dummy implementation of logging for <see cref="WorkStealingTaskScheduler"/>
    /// that does nothing.
    /// </summary>
    public class NullTaskSchedulerLogger : ITaskSchedulerLogger
    {
        public void BeginTask(ITaskSchedulerLogger.SourceQueue sourceQueue)
        {
        }

        public void EndTask(ITaskSchedulerLogger.SourceQueue sourceQueue)
        {
        }
    }
}

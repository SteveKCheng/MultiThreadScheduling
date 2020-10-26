using System;
using System.Collections.Generic;
using System.Text;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Dummy implementation of logging for <see cref="MultiThreadTaskScheduler"/>
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

        public void RaiseCriticalError(Exception exception)
        {
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Dummy implementation of logging for <see cref="MultiThreadTaskScheduler"/>
    /// that does nothing.
    /// </summary>
    public class NullSchedulingLogger : ISchedulingLogger
    {
        public void BeginTask(uint workerId, ISchedulingLogger.SourceQueue sourceQueue)
        {
        }

        public void EndTask(uint workerId, ISchedulingLogger.SourceQueue sourceQueue)
        {
        }

        public void Idle(uint workerId)
        {
        }

        public void RaiseCriticalError(Exception exception)
        {
        }
    }
}

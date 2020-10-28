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
        public void BeginTask(uint workerId, ISchedulingLogger.SourceQueue sourceQueue, in WorkItemInfo workInfo)
        {
        }

        public void EndTask(uint workerId, ISchedulingLogger.SourceQueue sourceQueue, in WorkItemInfo workInfo)
        {
        }

        public void Idle(uint workerId)
        {
        }

        public void RaiseCriticalError(Exception exception)
        {
        }

        public void WorkerStarts(uint workerId)
        {
        }

        public void WorkerStops(uint workerId)
        {
        }
    }
}

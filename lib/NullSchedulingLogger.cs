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
        /// <inheritdoc/>
        public void BeginTask(uint workerId, 
                              WorkSourceQueue sourceQueue, 
                              in WorkItemInfo workInfo)
        {
        }

        /// <inheritdoc/>
        public void EndTask(uint workerId, 
                            WorkSourceQueue sourceQueue, 
                            in WorkItemInfo workInfo,
                            WorkExecutionStatus workStatus)
        {
        }

        /// <inheritdoc/>
        public void EnqueueWork(uint? workerId, in WorkItemInfo workInfo)
        {
        }

        /// <inheritdoc/>
        public void Idle(uint workerId)
        {
        }

        /// <inheritdoc/>
        public void RaiseCriticalError(uint? workerId, Exception exception)
        {
        }

        /// <inheritdoc/>
        public void WorkerStarts(uint workerId)
        {
        }

        /// <inheritdoc/>
        public void WorkerStops(uint workerId)
        {
        }
    }
}

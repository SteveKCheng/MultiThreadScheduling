using System;
using System.Threading;
using System.Threading.Tasks;

namespace WorkStealingScheduler
{
    public sealed partial class WorkStealingTaskScheduler
    {
        /// <summary>
        /// Adapt <see cref="WorkStealingTaskScheduler"/>
        /// into a <see cref="SynchronizationContext"/>.
        /// </summary>
        private class SyncContextAdaptor : SynchronizationContext
        {
            private readonly WorkStealingTaskScheduler _master;

            public SyncContextAdaptor(WorkStealingTaskScheduler master)
            {
                _master = master;
            }

            public override void Post(SendOrPostCallback d, object state)
            {
                throw new NotImplementedException();
            }
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace MultiThreadScheduling
{
    public sealed partial class MultiThreadTaskScheduler
    {
        /// <summary>
        /// Adapt <see cref="MultiThreadTaskScheduler"/>
        /// into a <see cref="SynchronizationContext"/>.
        /// </summary>
        private class SyncContextAdaptor : SynchronizationContext
        {
            private readonly MultiThreadTaskScheduler _master;

            public SyncContextAdaptor(MultiThreadTaskScheduler master)
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

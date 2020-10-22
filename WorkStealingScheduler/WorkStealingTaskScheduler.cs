using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WorkStealingScheduler
{
    public sealed class WorkStealingTaskScheduler : TaskScheduler, IDisposable
    {
        /// <summary>
        /// Describes one unit of work to be done.
        /// </summary>
        private struct WorkItem
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

        /// <summary>
        /// The worker threads managed by this scheduler, accessible as a list
        /// or as a thread-local variable.
        /// </summary>
        private ThreadLocal<Worker> _currentWorker = new ThreadLocal<Worker>(trackAllValues: true);

        /// <summary>
        /// The queue that work items are put in when they do not come from a
        /// worker thread.
        /// </summary>
        private ConcurrentQueue<WorkItem> _globalQueue = new ConcurrentQueue<WorkItem>();

        private Worker[] _allWorkers;

        /// <summary>
        /// Signals to worker threads that there may be items to run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// We admit false positives, i.e. spurious wake-ups of worker threads.
        /// What must NOT happen are false negatives, i.e. failing to raise
        /// this semaphore when there are tasks available to run.
        /// </para>
        /// <para>
        /// Currently, each item put into the global queue raises this semaphore
        /// by one.  Each successful dequeuing decreases this semaphore by one.
        /// Putting an item in a local queue can also raise this semaphore
        /// so tell other workers that they may steal work.
        /// </para>
        /// </remarks>
        private SemaphoreSlim _semaphore = new SemaphoreSlim(0);

        private class SyncContextAdaptor : SynchronizationContext
        {
            private readonly WorkStealingTaskScheduler _master;

            public SyncContextAdaptor(WorkStealingTaskScheduler master)
            {
                _master = master;
            }

            public override void Post(SendOrPostCallback d, object state)
            {
                
            }
        }

        public WorkStealingTaskScheduler(int numThreads)
        {
            if (numThreads <= 0 || numThreads > Environment.ProcessorCount * 32)
                throw new ArgumentOutOfRangeException(nameof(numThreads));

            for (int i = 0; i < numThreads; ++i)
            {
                var worker = new Worker(this, initialDequeCapacity: 256);
                worker.StartThread($"{nameof(WorkStealingTaskScheduler)} thread #{i+1}");
            }
        }

        #region Implementation of TaskScheduler

        /// <summary>
        /// Takes a best-effort snapshot of the tasks that have been scheduled so far.
        /// </summary>
        /// <returns></returns>
        protected override IEnumerable<Task> GetScheduledTasks()
        {
            var tasks = new List<Task>();

            foreach (var worker in _currentWorker.Values)
            {
                var localItems = worker.UnsafeGetItems();
                if (localItems == null)
                    continue;

                foreach (var item in localItems)
                {
                    var task = item.TaskToRun;
                    if (task != null)
                        tasks.Add(task);
                }
            }

            tasks.AddRange(this._globalQueue.ToArray().Select(item => item.TaskToRun));

            return tasks;
        }

        /// <summary>
        /// Push a task into this task scheduler.
        /// </summary>
        /// <param name="task">The desired task to execute later under this scheduler. 
        /// </param>
        protected override void QueueTask(Task task)
        {
            var workItem = new WorkItem { TaskToRun = task };
            var taskOptions = task.CreationOptions;

            if ((taskOptions & TaskCreationOptions.LongRunning) != 0)
            {
                // FIXME log exceptions
                var thread = new Thread(task => this.TryExecuteTask((Task)task!));
                thread.Priority = ThreadPriority.BelowNormal;
                thread.IsBackground = true;
                thread.Name = nameof(WorkStealingTaskScheduler) + " thread for long-running task";
                thread.Start(task);
                return;
            }

            if ((taskOptions & TaskCreationOptions.PreferFairness) == 0)
            {
                var worker = this._currentWorker.Value;
                if (worker != null && worker.TryLocalPush(workItem))
                    return;
            }
            
            this._globalQueue.Enqueue(workItem);
            this._semaphore.Release();
        }

        /// <summary>
        /// Tries to execute the task inline when the current thread is a worker thread.
        /// </summary>
        /// <param name="task">The task that the .NET tasks infrastructure wants to
        /// execute. </param>
        /// <param name="taskWasPreviouslyQueued">If true, this method will unconditionally
        /// fail to inline the task.  It is not natural for this implementation to scan
        /// the local queue for a task that was queued earlier.  Looking through .NET Core's
        /// source code, it seems this parameter is only set to true when performing
        /// synchronous waits on an incomplete task <see cref="Task"/>.  One cannot expect 
        /// good performance for such code anyway so we refuse to complicate this implementation
        /// for it.
        /// </param>
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            var worker = this._currentWorker.Value;
            if (worker != null && !taskWasPreviouslyQueued)
                return TryExecuteTask(task);

            return false;
        }

        #endregion

        /// <summary>
        /// Get the floor of the logarithm to the base of two for an integer.
        /// </summary>
        /// <remarks>
        /// This implementation is not one of the fast ones using "bit twiddling",
        /// but we only call it once during construction of this class.
        /// </remarks>
        private static uint Log2(ulong v)
        {
            uint r = 0;
            while ((v >>= 1) != 0)
                r++;
            return r;
        }

        private static readonly int Log2Frequency;

        private bool TryStealWorkItem(out WorkItem workItem)
        {
            var workers = this._currentWorker.Values;

            var numWorkers = workers.Count;
            var startIndex = (int)(((uint)(Stopwatch.GetTimestamp() >> Log2Frequency)) % (uint)numWorkers);

            int i = startIndex;
            do
            {
                if (workers[i].TrySteal(out workItem))
                    return true;

                if (++i == numWorkers)
                    i = 0;
            } while (i != startIndex);

            return false;
        }

        public void Dispose()
        {
            this._currentWorker.Dispose();
            this._semaphore.Dispose();
        }

        private void RaiseSemaphoreForLocalItem()
        {
            this._semaphore.Release();
        }

        private class Worker
        {
            /// <summary>
            /// Work items queued locally by this worker.
            /// </summary>
            private ChaseLevQueue<WorkItem> localQueue;

            /// <summary>
            /// The task scheduler that owns this worker.
            /// </summary>
            private readonly WorkStealingTaskScheduler master;


            private readonly CancellationTokenSource cancellationTokenSource;
            private readonly CancellationToken cancellationToken;

            /// <summary>
            /// The number of items in the local deque. 
            /// </summary>
            /// <remarks>
            /// <para>
            /// This number is tracked primarily so we can raise the scheduler's
            /// semaphore, to unblock other workers, whenever this worker has
            /// items that can potentially be stolen.  The semaphore is only
            /// raised when the local deque becomes non-empty, i.e. this count
            /// gets decremented from zero to one; so with large
            /// batches of work, the workers do not contend on the semaphore
            /// constantly. (The semaphore necessarily has to be on a shared cached
            /// line.)  Think of this semaphore raising as a ticket for other
            /// workers to steal work from this worker.
            /// </para>
            /// <para>
            /// When another worker steals an item, but the decremented count
            /// remains above zero, then the ticket for work-stealing is replaced,
            /// i.e. the semaphore is re-raised.  Thus if there is more work
            /// to be stolen other workers can wake up too.
            /// </para>
            /// <para>
            /// For efficiency, a worker popping its own work does not
            /// remove the ticket as with a worker dequeuing other workers'
            /// work.  This can lead to another worker spuriously waking up,
            /// but afterwards it will find that there is no work to steal
            /// and effectively cancel the ticket.  It is harmless behavior.
            /// </para>
            /// </remarks>
            private int numLocalItems;

            public bool TryLocalPush(in WorkItem taskItem)
            {
                if (localQueue.TryPush(taskItem, false))
                {
                    if (Interlocked.Increment(ref numLocalItems) == 1)
                        master.RaiseSemaphoreForLocalItem();

                    return true;
                }

                return false;
            }
               
            /// <summary>
            /// Called by another worker to try to steal work from this worker.
            /// </summary>
            /// <param name="workItem">Set to the stolen work item, if there is one.
            /// Otherwise set to the default value.
            /// </param>
            /// <returns>Whether an item was successfully stolen. </returns>
            public bool TrySteal(out WorkItem workItem)
            {
                bool success = localQueue.TrySteal(out workItem);
                if (success && Interlocked.Decrement(ref numLocalItems) > 0)
                    master.RaiseSemaphoreForLocalItem();
                return success;
            }

            public WorkItem[]? UnsafeGetItems() => localQueue.UnsafeGetItems();

            public Worker(WorkStealingTaskScheduler master, int initialDequeCapacity)
            {
                this.master = master;
                this.localQueue = new ChaseLevQueue<WorkItem>(initialDequeCapacity);
                this.cancellationTokenSource = new CancellationTokenSource();
            }

            private static readonly ParameterizedThreadStart RunInThreadDelegate = 
                self => ((Worker)self!).RunInThread();

            public void StartThread(string? label)
            {
                var thread = new Thread(RunInThreadDelegate);
                thread.Priority = ThreadPriority.BelowNormal;
                thread.IsBackground = true;
                thread.Name = label;
                thread.Start(this);
            }

#if false
            public bool TryPopTaskAtFront(Task task)
            {
                if (localQueue.TryPeek(out var workItem) && workItem.TaskToRun == task)
                {
                    if (localQueue.TryPop(out workItem))
                    {
                        if (workItem.TaskToRun == task)
                            return true;

                        localQueue.TryPush(workItem, false);
                    }
                }

                return false;
            }
#endif

            /// <summary>
            /// The thread procedure for the worker thread.
            /// </summary>
            private void RunInThread()
            {
                try
                {
                    this.master._currentWorker.Value = this;

                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        WorkItem workItem;

                        if (localQueue.TryPop(out workItem))
                        {
                            Interlocked.Decrement(ref numLocalItems);

                            // Log message that localQueue has been processed
                        }
                        else if (master._globalQueue.TryDequeue(out workItem))
                        {
                            // Log message that globalQueue has been processed
                        }
                        else if (master.TryStealWorkItem(out workItem))
                        {
                            // Log message that item has been stolen from another worker
                        }
                        else
                        {
                            master._semaphore.Wait(this.cancellationToken);
                            continue;
                        }

                        if (workItem.TaskToRun != null)
                            master.TryExecuteTask(workItem.TaskToRun);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                }
            }
        }
    }
}

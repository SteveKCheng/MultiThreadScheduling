using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WorkStealingScheduler
{
    public sealed partial class WorkStealingTaskScheduler : TaskScheduler, IDisposable
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
        /// to tell other workers that they may steal work.
        /// </para>
        /// </remarks>
        private SemaphoreSlim _semaphore = new SemaphoreSlim(0);

        public WorkStealingTaskScheduler(int numThreads, ITaskSchedulerLogger? logger)
        {
            _logger = logger ?? new NullTaskSchedulerLogger();

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

        private readonly ITaskSchedulerLogger _logger;

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

        public void Dispose()
        {
            this._currentWorker.Dispose();
            this._semaphore.Dispose();
        }

        private void RaiseSemaphoreForLocalItem()
        {
            this._semaphore.Release();
        }

    }
}

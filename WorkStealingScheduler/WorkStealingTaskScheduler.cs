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
        /// The worker object that is running in the current thread, if any.
        /// </summary>
        private ThreadLocal<Worker> _currentWorker = new ThreadLocal<Worker>();

        /// <summary>
        /// The queue that work items are put in when they do not come from a
        /// worker thread.
        /// </summary>
        private ConcurrentQueue<WorkItem> _globalQueue = new ConcurrentQueue<WorkItem>();

        /// <summary>
        /// Tracks all worker threads that have been instantiated.
        /// </summary>
        /// <remarks>
        /// The "track all values" functionality of <see cref="ThreadLocal{Worker}"/>
        /// is not used because it is not efficient.
        /// </remarks>
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

        #region Dynamically adjusting the number of workers

        /// <summary>
        /// .NET object to lock when adjusting the number of workers.
        /// </summary>
        /// <remarks>
        /// Adjusting the number of workers is not performance-critical.
        /// For simplicity, operations can be serialized.
        /// </remarks>
        private object LockObject => _globalQueue;

        /// <summary>
        /// The number of workers that are in excess versus the desired
        /// number of workers.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Since workers may be executing task items that are not immediately
        /// interruptible, we do not change the number of threads/workers
        /// synchronously.  A change in the number of workers is treated
        /// as a request that a worker handles when it is free.
        /// </para>
        /// <para>
        /// This variable is always at least zero.
        /// </para>
        /// </remarks>
        private int _excessNumThreads = 0;

        /// <summary>
        /// The number of workers that are supposed to be active currently.
        /// </summary>
        /// <remarks>
        /// The following invariant is kept when <see cref="LockObject"/>
        /// is not locked: <see cref="_totalNumThreads"/> is equal
        /// to the length of <see cref="_allWorkers"/> minus the number
        /// of workers whose property <see cref="Worker.HasQuit"/>
        /// is true.
        /// </remarks>
        private int _totalNumThreads = 0;

        /// <summary>
        /// Adjust the number of threads up or down.
        /// </summary>
        /// <param name="desiredNumThreads">The desired number of threads. </param>
        private void SetNumberOfThreads(int desiredNumThreads)
        {
            if (desiredNumThreads <= 0 || desiredNumThreads > Environment.ProcessorCount * 32)
                throw new ArgumentOutOfRangeException(nameof(desiredNumThreads));

            lock (LockObject)
            {
                int excessNumThreads = _totalNumThreads - desiredNumThreads;
                if (excessNumThreads > 0)
                {
                    _excessNumThreads = excessNumThreads;
                    _semaphore.Release(excessNumThreads);
                    return;
                }

                _excessNumThreads = 0;
                if (excessNumThreads == 0)
                    return;

                var newWorkers = new Worker[desiredNumThreads];
                var oldWorkers = _allWorkers;
                int workersCount = 0;

                for (int i = 0; i < oldWorkers.Length; ++i)
                {
                    var worker = oldWorkers[i];
                    if (!worker.HasQuit)
                        newWorkers[workersCount++] = oldWorkers[i];
                }
                    
                for (int i = workersCount; i < desiredNumThreads; ++i)
                    newWorkers[i] = new Worker(this, initialDequeCapacity: 256);

                // Publish the workers array even if starting the threads fail below
                _allWorkers = newWorkers;
                _totalNumThreads = desiredNumThreads;

                for (int i = workersCount; i < desiredNumThreads; ++i)
                {
                    try
                    {
                        newWorkers[i].StartThread($"{nameof(WorkStealingTaskScheduler)} thread #{i + 1}");
                    }
                    catch (Exception e)
                    {
                        _logger.RaiseCriticalError(e);

                        // If one thread fails to start do not try to start any more.
                        // The workers act the same way so starting more threads will likely
                        // just fail again.
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Check if a worker should quit because the desired number of workers
        /// has been adjusted downwards.
        /// </summary>
        /// <param name="hasQuit">Backing field for the worker's
        /// <see cref="Worker.HasQuit"/> property.  The property 
        /// must be modified by this method because it does so under a lock.
        /// </param>
        /// <returns>True if the worker should continue running; false
        /// if it should quit. </returns>
        private bool ShouldWorkerContinueRunning(ref bool hasQuit)
        {
            if (_excessNumThreads > 0)
            {
                lock (LockObject)
                {
                    if (_excessNumThreads > 0)
                    {
                        bool reachedZero = (--_excessNumThreads == 0);

                        --_totalNumThreads;

                        // Change this property while holding the lock so that 
                        // this worker is guaranteed to be removed from the array
                        // of all workers when excessNumThreads reaches zero.
                        hasQuit = true;

                        if (reachedZero)
                            ConsolidateWorkersAfterTrimmingExcess();

                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Re-create the <see cref="_allWorkers"/> array after trimming
        /// excess workers.
        /// </summary>
        private void ConsolidateWorkersAfterTrimmingExcess()
        {
            var oldWorkers = _allWorkers;
            int workersCount = 0;

            // Copy over the references to workers that are still active.
            var newWorkers = new Worker[_totalNumThreads];
            for (int i = 0; i < oldWorkers.Length; ++i)
            {
                var worker = oldWorkers[i];
                if (!worker.HasQuit)
                    newWorkers[workersCount++] = worker;
            }

            _allWorkers = newWorkers;
        }

        #endregion

        public WorkStealingTaskScheduler(int numThreads, ITaskSchedulerLogger? logger)
        {
            _logger = logger ?? new NullTaskSchedulerLogger();

            if (numThreads <= 0 || numThreads > Environment.ProcessorCount * 32)
                throw new ArgumentOutOfRangeException(nameof(numThreads));

            var allWorkers = new Worker[numThreads];

            for (int i = 0; i < numThreads; ++i)
            {
                var worker = new Worker(this, initialDequeCapacity: 256);
                _allWorkers[i] = worker;
            }

            _allWorkers = allWorkers;

            for (int i = 0; i < numThreads; ++i)
            {
                var worker = allWorkers[i];
                worker.StartThread($"{nameof(WorkStealingTaskScheduler)} thread #{i + 1}");
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

            foreach (var worker in _allWorkers)
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
        /// 
        /// </summary>
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

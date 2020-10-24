using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WorkStealingScheduler
{
    public sealed partial class WorkStealingTaskScheduler : TaskScheduler, IDisposable, IAsyncDisposable
    {
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
        internal Worker[]? AllWorkers { get; private set; }

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
        /// to the length of <see cref="AllWorkers"/> minus the number
        /// of workers whose property <see cref="Worker.HasQuit"/>
        /// is true.
        /// </remarks>
        private int _totalNumThreads = 0;

        /// <summary>
        /// Adjust the number of threads up or down.
        /// </summary>
        /// <param name="desiredNumThreads">The desired number of threads. Must 
        /// not be negative. </param>
        private void SetNumberOfThreadsInternal(int desiredNumThreads)
        {
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
                {
                    FinishDisposing();
                    return;
                }

                var newWorkers = new Worker[desiredNumThreads];
                var oldWorkers = AllWorkers!;
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
                AllWorkers = newWorkers;
                _totalNumThreads = desiredNumThreads;

                for (int i = workersCount; i < desiredNumThreads; ++i)
                {
                    try
                    {
                        newWorkers[i].StartThread($"{nameof(WorkStealingTaskScheduler)} thread #{i + 1}");
                    }
                    catch (Exception e)
                    {
                        Logger.RaiseCriticalError(e);

                        // If one thread fails to start do not try to start any more.
                        // The workers act the same way so starting more threads will likely
                        // just fail again.
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Adjust the number of threads up or down.
        /// </summary>
        /// <param name="desiredNumThreads">The desired number of threads. Must 
        /// not be negative. </param>
        public void SetNumberOfThreads(int desiredNumThreads)
        {
            if (_disposalComplete != null)
                throw new ObjectDisposedException("Cannot set the number of threads for a disposed scheduler. ");

            if (desiredNumThreads <= 0 || desiredNumThreads > Environment.ProcessorCount * 32)
                throw new ArgumentOutOfRangeException(nameof(desiredNumThreads));

            SetNumberOfThreadsInternal(desiredNumThreads);
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
        internal bool ShouldWorkerContinueRunning(ref bool hasQuit)
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
                        {
                            FinishDisposing();
                            ConsolidateWorkersAfterTrimmingExcess();
                        }

                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Re-create the <see cref="AllWorkers"/> array after trimming
        /// excess workers.
        /// </summary>
        /// <remarks>
        /// This method must be called while holding the lock on <see cref="LockObject"/>.
        /// </remarks>
        private void ConsolidateWorkersAfterTrimmingExcess()
        {
            if (_totalNumThreads == 0)
            {
                AllWorkers = null;
                return;
            }

            var oldWorkers = AllWorkers!;
            int workersCount = 0;

            // Copy over the references to workers that are still active.
            var newWorkers = new Worker[_totalNumThreads];
            for (int i = 0; i < oldWorkers.Length; ++i)
            {
                var worker = oldWorkers[i];
                if (!worker.HasQuit)
                    newWorkers[workersCount++] = worker;
            }

            AllWorkers = newWorkers;
        }

        #endregion

        public WorkStealingTaskScheduler(int numThreads, ITaskSchedulerLogger? logger)
        {
            Logger = logger ?? new NullTaskSchedulerLogger();

            if (numThreads <= 0 || numThreads > Environment.ProcessorCount * 32)
                throw new ArgumentOutOfRangeException(nameof(numThreads));

            var allWorkers = new Worker[numThreads];

            for (int i = 0; i < numThreads; ++i)
            {
                var worker = new Worker(this, initialDequeCapacity: 256);
                allWorkers[i] = worker;
            }

            AllWorkers = allWorkers;

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

            var allWorkers = AllWorkers;
            if (allWorkers != null)
            {
                foreach (var worker in allWorkers)
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
                var worker = Worker.OfCurrentThread;
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
            var worker = Worker.OfCurrentThread;
            if (worker != null && !taskWasPreviouslyQueued)
                return TryExecuteTask(task);

            return false;
        }

        #endregion

        /// <summary>
        /// 
        /// </summary>
        public ITaskSchedulerLogger Logger { get; }

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


        #region Disposal

        /// <summary>
        /// Task source used to signal all workers have completely stopped.
        /// </summary>
        private volatile TaskCompletionSource<bool>? _disposalComplete;

        /// <summary>
        /// Requests and waits for all workers to stop, and 
        /// cleans up resources allocated by this scheduler.
        /// </summary>
        /// <remarks>
        /// If a worker is in the middle of executing some work item, it can only stop
        /// after the work item has run.
        /// </remarks>
        public void Dispose() => DisposeAsync().Wait();

        ValueTask IAsyncDisposable.DisposeAsync() => new ValueTask(DisposeAsync());

        /// <summary>
        /// Requests all workers to stop and cleans up resources allocated by this scheduler.
        /// </summary>
        /// <remarks>
        /// If a worker is in the middle of executing some work item, it can only stop
        /// after the work item has run.
        /// </remarks>
        public Task DisposeAsync()
        {
            if (Worker.IsCurrentWorkerOwnedBy(this))
                throw new InvalidOperationException($"{nameof(WorkStealingScheduler)}.{nameof(Dispose)} may not be called from within one of its worker threads. ");

            // Somebody is already disposing or has disposed
            var disposalComplete = _disposalComplete;
            if (disposalComplete != null)
                return Task.CompletedTask;

            // Raced to dispose
            disposalComplete = new TaskCompletionSource<bool>();
            if (Interlocked.Exchange(ref _disposalComplete, disposalComplete) != null)
                return Task.CompletedTask;

            SetNumberOfThreadsInternal(0);
            return disposalComplete.Task;
        }

        /// <summary>
        /// Clean up resources in this instance after all concurrent workers are known to have stopped.
        /// </summary>
        private void FinishDisposing()
        {
            if (_disposalComplete != null)
            {
                _semaphore.Dispose();
                _disposalComplete.TrySetResult(true);
            }
        }

        #endregion

        #region Methods for workers to call

        /// <summary>
        /// Increment semaphore after a worker has enqueued a work item locally.
        /// </summary>
        internal void RaiseSemaphoreForLocalItem() => _semaphore.Release();

        /// <summary>
        /// Make the current worker thread synchronously wait for new work.
        /// </summary>
        internal void WaitOnSemaphore() => _semaphore.Wait();

        /// <summary>
        /// Push a work item onto the global queue.
        /// </summary>
        internal void EnqueueGlobalTaskItem(WorkItem workItem) => _globalQueue.Enqueue(workItem);

        /// <summary>
        /// Take a work item off the global queue if one exists.
        /// </summary>
        internal bool TryDequeueGlobalTaskItem(out WorkItem workItem) => _globalQueue.TryDequeue(out workItem);

        /// <summary>
        /// Have the current worker thread run a work item synchronously.
        /// </summary>
        internal void ExecuteTaskItemFromWorker(WorkItem workItem)
        {
            if (workItem.TaskToRun != null)
            {
                TryExecuteTask(workItem.TaskToRun);
            }
        }

        #endregion
    }
}

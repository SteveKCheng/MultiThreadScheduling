using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Distributes abstract work items for multiple threads to process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class implements the concept of dispatching abstract work items,
    /// or messages, to multiple, dedicated worker threads.  Each worker 
    /// thread may create and push more work items as part of processing work.
    /// These work items preferentially go into a local queue that the same
    /// worker thread can process later.  Worker threads that are free
    /// can steal work from other worker threads.
    /// </para>
    /// <para>
    /// This class was created to implement <see cref="MultiThreadTaskScheduler"/>,
    /// specializing for work items that are <see cref="Task"/> objects,
    /// but (server) applications may find use for a scheduler that can work
    /// on application-specific messages.
    /// </para>
    /// </remarks>
    /// <typeparam name="TWorkItem">
    /// Type of the abstract message that may be queued into <see cref="MultiThreadScheduler{TWorkItem}"/>
    /// and gets processed by one of its worker threads.
    /// </typeparam>
    /// <typeparam name="TExecutor">
    /// Type of the object or state that can process an instance of <typeparamref name="TWorkItem"/>.
    /// It is recommended that this type not be a mutable struct.
    /// </typeparam>
    public class MultiThreadScheduler<TWorkItem, TExecutor> : IDisposable, IAsyncDisposable 
        where TExecutor: IWorkExecutor<TWorkItem>
    {
        #region Global work queue

        /// <summary>
        /// The queue that work items are put in when they do not come from a
        /// worker thread.
        /// </summary>
        private ConcurrentQueue<TWorkItem> _globalQueue = new ConcurrentQueue<TWorkItem>();

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

        #endregion

        #region Dynamically adjusting the number of workers

        /// <summary>
        /// Tracks all worker threads that have been instantiated.
        /// </summary>
        /// <remarks>
        /// The "track all values" functionality of <see cref="ThreadLocal{Worker}"/>
        /// is not used because it is not efficient.
        /// </remarks>
        internal Worker<TWorkItem, TExecutor>[]? AllWorkers { get; private set; }

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
        /// <para>
        /// This variable may only be changed after locking <see cref="LockObject"/>.
        /// Unfortunately, atomic increment/decrement is not sufficient because
        /// threads would otherwise race to set this variable to less than zero.
        /// </para>
        /// </remarks>
        private int _excessNumThreads = 0;

        /// <summary>
        /// The number of workers that are supposed to be running currently.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The following invariant is kept when <see cref="LockObject"/>
        /// is not locked: <see cref="_totalNumThreads"/> is equal
        /// to the length of <see cref="AllWorkers"/> minus the number
        /// of workers whose property <see cref="Worker.HasQuit"/>
        /// is true.
        /// </para>
        /// <para>
        /// This variable may only be changed after locking <see cref="LockObject"/>.
        /// </para>
        /// <para>
        /// If a worker thread exits abnormally, this variable is not decremented.
        /// </para>
        /// </remarks>
        private int _totalNumThreads = 0;

        /// <summary>
        /// Integer ID to be assigned to the next thread that gets started.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The ID is used for naming the worker thread to aid debugging.
        /// Thus this counter is shared across all instances of this class.
        /// The ID is treated as unsigned and may overflow.
        /// </para>
        /// <para>
        /// This variable is incremented atomically without needing any locks.
        /// </para>
        /// </remarks>
        private static volatile int _nextWorkerId = 0;

        /// <summary>
        /// Priority of the worker threads.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This variable may only be changed after locking <see cref="LockObject"/>.
        /// </para>
        /// </remarks>
        private ThreadPriority _threadPriority = ThreadPriority.Normal;

        /// <summary>
        /// Adjust the number of threads up or down.
        /// </summary>
        /// <param name="desiredNumThreads">The desired number of threads. Must 
        /// not be negative. </param>
        /// <param name="threadPriority">The desired priority of the worker threads.
        /// Existing threads' priority will be changed if different from the setting,
        /// unless all threads are to be brought down, i.e. 
        /// <paramref name="desiredNumThreads"/> is zero.
        /// </param>
        private void SetNumberOfThreads(int desiredNumThreads, ThreadPriority threadPriority)
        {
            Worker<TWorkItem, TExecutor>[] newWorkers;
            int workersCount;

            lock (LockObject)
            {
                // If the scheduler is already disposed, ignore any attempt to set a 
                // positive number of threads.  This check is in addition to
                // the error-throwing checks inside the public methods, because
                // the latter are performed outside of the lock and so may race.
                if (_disposalComplete != null && desiredNumThreads > 0)
                    return;

                var oldWorkers = AllWorkers;

                // Re-set priority of threads if different
                if (threadPriority != _threadPriority && desiredNumThreads > 0)
                {
                    if (oldWorkers != null)
                    {
                        for (int i = 0; i < oldWorkers.Length; ++i)
                            oldWorkers[i].ChangeThreadPriority(threadPriority);
                    }

                    _threadPriority = threadPriority;
                }

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

                newWorkers = new Worker<TWorkItem, TExecutor>[desiredNumThreads];
                workersCount = 0;

                if (oldWorkers != null)
                {
                    for (int i = 0; i < oldWorkers.Length; ++i)
                    {
                        var worker = oldWorkers[i];
                        if (!worker.HasQuit)
                            newWorkers[workersCount++] = oldWorkers[i];
                    }
                }

                // Generate seeds pseudo-randomly so workers will get different seeds
                // even if we can only get low-resolution timestamps.
                var random = new QuickRandomGenerator(QuickRandomGenerator.GetSeedFromTime());

                for (int i = workersCount; i < desiredNumThreads; ++i)
                {
                    var workerId = (uint)Interlocked.Increment(ref _nextWorkerId);
                    newWorkers[i] = new Worker<TWorkItem, TExecutor>(this,
                                        threadPriority,
                                        initialDequeCapacity: 256,
                                        seed: random.Next(),
                                        id: workerId,
                                        name: $"{nameof(MultiThreadTaskScheduler)} thread #{workerId}");
                }

                // Publish the workers array even if starting the threads fail below
                AllWorkers = newWorkers;
                _totalNumThreads = desiredNumThreads;
            }

            // Can start threads outside the lock once all instance data has been published
            for (int i = workersCount; i < desiredNumThreads; ++i)
            {
                var worker = newWorkers[i];

                try
                {
                    worker.StartThread();
                }
                catch (Exception e)
                {
                    Logger.RaiseCriticalError(worker.Id, e);

                    // If one thread fails to start do not try to start any more.
                    // The workers act the same way so starting more threads will likely
                    // just fail again.
                    throw;
                }
            }
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
            var newWorkers = new Worker<TWorkItem, TExecutor>[_totalNumThreads];
            for (int i = 0; i < oldWorkers.Length; ++i)
            {
                var worker = oldWorkers[i];
                if (!worker.HasQuit)
                    newWorkers[workersCount++] = worker;
            }

            AllWorkers = newWorkers;
        }

        #endregion

        #region User control of scheduling

        /// <summary>
        /// Prepare to accept tasks.  No worker threads are started initially.
        /// </summary>
        /// <param name="executor">Object or state for processing work items.
        /// </param>
        /// <param name="syncContext">The synchronization context to set on all
        /// worker threads. </param>
        /// <param name="logger">Logger to observe important events during this
        /// scheduler's lifetime. </param>
        public MultiThreadScheduler(TExecutor executor, 
                                    SynchronizationContext? syncContext,
                                    ISchedulingLogger? logger)
        {
            Logger = logger ?? new NullSchedulingLogger();
            Executor = executor;
            WorkerSyncContext = syncContext;

            // Initial settings
            SchedulingOptions = new MultiThreadSchedulingSettings
            {
                Mode = MultiThreadCreationMode.CustomNumberOfThreads,
                ThreadPriority = ThreadPriority.Normal,
                NumberOfThreads = 0
            };
        }

        /// <summary>
        /// Adjust scheduling options.
        /// </summary>
        /// <param name="settings">The new scheduling options. </param>
        public void SetSchedulingOptions(MultiThreadSchedulingSettings settings)
        {
            if (_disposalComplete != null)
                throw new ObjectDisposedException("Cannot set the number of threads for a disposed scheduler. ");

            int numThreads;

            if ((settings.Mode & MultiThreadCreationMode.OneThreadPerLogicalCpu) != 0)
            {
                // FIXME filter this list based on process CPU affinity
                var cpuInfo = CpuTopologyInfo.GetList();

                if ((settings.Mode & MultiThreadCreationMode.OneThreadPerCpuCore) == MultiThreadCreationMode.OneThreadPerCpuCore)
                    numThreads = CpuTopologyInfo.CountNumberOfCores(cpuInfo);
                else
                    numThreads = cpuInfo.Length;
            }
            else
            {
                numThreads = settings.NumberOfThreads;
                if (numThreads <= 0)
                    throw new ArgumentOutOfRangeException($"{nameof(MultiThreadSchedulingSettings)}.{nameof(MultiThreadSchedulingSettings.NumberOfThreads)} is out of range: {numThreads}");
            }

            // Cap at CPU quota, if requested.
            // FIXME For dynamic updates to CPU quota we should read the quota ourselves
            if ((settings.Mode & MultiThreadCreationMode.CapThreadsAtCpuQuota) != 0)
                numThreads = Math.Max(numThreads, Environment.ProcessorCount);

            if (numThreads == 0)
                numThreads = 1;

            SetNumberOfThreads(numThreads, settings.ThreadPriority);

            // Publish new options, if SetNumberOfThreads does not fail
            SchedulingOptions = settings;
        }

        /// <summary>
        /// The scheduling options that should currently be in effect.
        /// </summary>
        /// <remarks>
        /// This data is not protected by a lock, and in case <see cref="SetSchedulingOptions"/>
        /// fails, it may not accurately reflect the current state of this scheduler.
        /// The data is advisory so that the user of this class can re-use
        /// the existing settings when adjusting this scheduler.  It is recommended
        /// that the user not adjust the scheduler concurrently from multiple threads.
        /// </remarks>
        public MultiThreadSchedulingSettings SchedulingOptions { get; private set; }

        /// <summary>
        /// Get the number of work threads currently active, for monitoring.
        /// </summary>
        /// <remarks>
        /// This number is not guaranteed to be consistent with the other
        /// operations that may be performed on this instance, because
        /// access to this variable is not mediated by a lock.  However, this 
        /// "eventually consistent" view suffices for monitoring.
        /// </remarks>
        public int NumberOfActiveThreads
        {
            get
            {
                var bias = (_disposalComplete != null) ? 1 : 0;
                return _activeNumThreads + bias;
            }
        }

        #endregion

        #region User-customizable state objects

        /// <summary>
        /// The synchronization context to establish on all worker threads. 
        /// </summary>
        /// <remarks>
        /// This synchronization context may be constructed such that all actions
        /// are always re-directed back to run under the same scheduler.
        /// </remarks>
        internal SynchronizationContext? WorkerSyncContext { get; }

        /// <summary>
        /// Executes any abstract work item that gets queued.
        /// </summary>
        /// <remarks>
        /// This member is not a property in case it is a mutable struct.
        /// </remarks>
        internal TExecutor Executor;

        /// <summary>
        /// Called to log significant events as this scheduler and its
        /// worker threads run.
        /// </summary>
        public ISchedulingLogger Logger { get; }

        #endregion

        #region Disposal

        /// <summary>
        /// The number of workers that are actively accepting work.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The difference between this variable and <see cref="_totalNumThreads"/>
        /// is that worker threads that are exiting, normally or abnormally,
        /// will decrement this variable, atomically.  This variable is used
        /// for knowing when disposal of this scheduler is finished, even when
        /// there are some worker threads behaving abnormally.  
        /// </para>
        /// <para>
        /// To increase reliability, this variable is designated to used in such
        /// a restricted manner that locks are not required to access it.
        /// More precisely, it is consulted only to implement disposal, i.e. terminating
        /// all worker threads permanently, and not for arbitrary adjustments of the
        /// number of threads.
        /// </para>
        /// <para>
        /// This variable takes the special value of -1 to distinguish the 
        /// "final clean-up" state.
        /// </para>
        /// </remarks>
        private volatile int _activeNumThreads = 0;

        /// <summary>
        /// Task source used to signal all workers have completely stopped.
        /// </summary>
        private volatile TaskCompletionSource<bool>? _disposalComplete;

        /// <summary>
        /// Requests and waits for all workers to stop, and 
        /// cleans up resources allocated by this scheduler.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If a worker is in the middle of executing some work item, it can only stop
        /// after the work item has run.
        /// </para>
        /// <para>
        /// If some worker is running an item that is taking forever, or is otherwise
        /// stuck, this call may wait forever and never return.  We recommend,
        /// for robustness, that <see cref="DisposeAsync"/> be used instead, 
        /// with a timeout.
        /// </para>
        /// </remarks>
        public void Dispose()
        {
            if (Worker.IsRunningInWorkerFor(this))
                throw new InvalidOperationException($"{nameof(MultiThreadScheduling)}.{nameof(Dispose)} may not be called from within one of its worker threads, because it would deadlock. ");

            DisposeAsync().Wait();
        }

        ValueTask IAsyncDisposable.DisposeAsync() => new ValueTask(DisposeAsync());

        /// <summary>
        /// Requests all workers to stop and cleans up resources allocated by this scheduler.
        /// </summary>
        /// <remarks>
        /// If a worker is in the middle of executing some work item, it can only stop
        /// after the work item has run.
        /// </remarks>
        /// <returns>Task that completes only when disposal is complete.
        /// If this method gets called (concurrently) multiple times, their returned
        /// Task objects all complete only when the disposal completes.  The returned
        /// Task should not be synchronously waited, via <see cref="Task.Wait"/>,
        /// from a worker thread, or disposal will deadlock.
        /// </returns>
        public Task DisposeAsync()
        {
            // Somebody is already disposing or has disposed
            var disposalComplete = _disposalComplete;
            if (disposalComplete != null)
                return disposalComplete.Task;

            // Disallow inline execution of continuations because usually a worker thread
            // that is about to die completes the task.
            var newDisposalComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Raced to dispose
            disposalComplete = Interlocked.Exchange(ref _disposalComplete, newDisposalComplete);
            if (disposalComplete != null)
                return disposalComplete.Task;

            // Request all worker threads to stop
            SetNumberOfThreads(0, ThreadPriority.Normal /* ignored */);

            // Allow _activeNumThreads to be decreased all the way to -1.
            // When all worker threads have already stopped, this will clean up synchronously.
            DecrementActiveThreadCount(null);

            return newDisposalComplete.Task;
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
        internal void EnqueueGlobalTaskItem(TWorkItem workItem) => _globalQueue.Enqueue(workItem);

        /// <summary>
        /// Take a work item off the global queue if one exists.
        /// </summary>
        internal bool TryDequeueGlobalTaskItem(out TWorkItem workItem) => _globalQueue.TryDequeue(out workItem);

        /// <summary>
        /// Increment the count of active worker threads.
        /// </summary>
        internal void IncrementActiveThreadCount()
        {
            Interlocked.Increment(ref _activeNumThreads);
        }

        /// <summary>
        /// Decrement the count of active worker threads, and finish any pending disposal when
        /// there are no more threads.
        /// </summary>
        internal void DecrementActiveThreadCount(uint? workerId)
        {
            if (Interlocked.Decrement(ref _activeNumThreads) == -1)
            {
                try
                {
                    _semaphore.Dispose();
                }
                catch (Exception e)
                {
                    Logger.RaiseCriticalError(workerId, e);
                }

                // When _activeNumThreads == -1, _disposalComplete should be non-null
                _disposalComplete!.SetResult(true);
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
                            ConsolidateWorkersAfterTrimmingExcess();

                        return false;
                    }
                }
            }

            return true;
        }

        #endregion

        #region Viewing and manipulating the queue

        /// <summary>
        /// Takes a best-effort snapshot of the items that are in the queues.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<TWorkItem> GetScheduledItems()
        {
            var items = new List<TWorkItem>();

            var allWorkers = AllWorkers;
            if (allWorkers != null)
            {
                foreach (var worker in allWorkers)
                {
                    var localItems = worker.UnsafeGetItems();
                    if (localItems == null)
                        continue;

                    items.AddRange(localItems);
                }
            }

            items.AddRange(this._globalQueue.ToArray());
            return items;
        }

        public void EnqueueItem(TWorkItem workItem, bool preferLocal)
        {
            if (preferLocal)
            {
                var worker = Worker<TWorkItem, TExecutor>.TryGetCurrentWorkerFor(this);
                if (worker != null && worker.TryLocalPush(workItem))
                    return;
            }

            this._globalQueue.Enqueue(workItem);
            this._semaphore.Release();
        }

        #endregion
    }
}

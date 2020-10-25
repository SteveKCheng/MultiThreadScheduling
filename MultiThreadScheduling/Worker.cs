using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Holds member data in <see cref="Worker{TWorkItem, TExecutor}"/> that is independent of
    /// the type parameters.
    /// </summary>
    /// <remarks>
    /// For the thread-local field <see cref="_ofCurrentThread"/> in particular, we only want
    /// one instantiation regardless of the parameterized type.
    /// </remarks>
    internal class Worker
    {
        /// <summary>
        /// Points to the Worker object that manages the current thread originated from, when applicable.
        /// </summary>
        [ThreadStatic]
        private static Worker? _ofCurrentThread;

        /// <summary>
        /// Type-erased version of <see cref="Worker{TWorkItem, TExecutor}._master"/>
        /// used for object comparison only.
        /// </summary>
        private readonly object _masterObject;

        protected Worker(object masterObject)
        {
            _masterObject = masterObject;
        }

        /// <summary>
        /// Register this instance as the worker for the current thread.
        /// </summary>
        protected void SetThisWorkerForCurrentThread() => _ofCurrentThread = this;

        /// <summary>
        /// Unregister this instance as the worker for the current thread.
        /// </summary>
        protected void UnsetThisWorkerForCurrentThread() => _ofCurrentThread = null;
        
        /// <summary>
        /// If the current thread is a worker thread from the given scheduler, return
        /// the worker object; otherwise null.
        public static Worker<TWorkItem, TExecutor>? TryGetCurrentWorkerFor<TWorkItem, TExecutor>(MultiThreadScheduler<TWorkItem, TExecutor> master)
            where TExecutor : IWorkExecutor<TWorkItem>
        {
            var worker = _ofCurrentThread;
            return IsWorkerOwnedBy(worker, master) ? (Worker<TWorkItem, TExecutor>)worker! : null;
        }

        /// <summary>
        /// Whether the current thread is run by a worker for the given scheduler.
        /// </summary>
        public static bool IsRunningInWorkerFor<TWorkItem, TExecutor>(MultiThreadScheduler<TWorkItem, TExecutor> master)
            where TExecutor : IWorkExecutor<TWorkItem>
            => IsWorkerOwnedBy(_ofCurrentThread, master);

        /// <summary>
        /// Test if a given worker is owned by a given scheduler.
        /// </summary>
        /// <remarks>
        /// Common code for <see cref="TryGetCurrentWorkerFor{TWorkItem, TExecutor}(MultiThreadScheduler{TWorkItem, TExecutor})"/>
        /// and <see cref="IsRunningInWorkerFor{TWorkItem, TExecutor}(MultiThreadScheduler{TWorkItem, TExecutor})"/>.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsWorkerOwnedBy(Worker? worker, object master)
            => worker != null && object.ReferenceEquals(worker._masterObject, master);
    }

    /// <summary>
    /// One of the workers in <see cref="MultiThreadScheduler"/>,
    /// managing the thread and the local set of tasks.
    /// </summary>
    internal sealed class Worker<TWorkItem, TExecutor> : Worker where TExecutor: IWorkExecutor<TWorkItem>
    {
        /// <summary>
        /// Work items queued locally by this worker.
        /// </summary>
        private ChaseLevQueue<TWorkItem> _localQueue;

        /// <summary>
        /// The task scheduler that owns this worker.
        /// </summary>
        private readonly MultiThreadScheduler<TWorkItem, TExecutor> _master;

        /// <summary>
        /// Name attached to this worker to aid debugging.
        /// </summary>
        public string Name => _thread.Name;

        /// <summary>
        /// Flag to signal to the master that this worker has voluntarily stopped
        /// processing work forever, and should be removed from the
        /// array of workers.
        /// </summary>
        /// <remarks>
        /// To simplify the implementation, this flag is not set if the worker 
        /// stops because of a critical error.  Thus a failed worker will hang
        /// around.  This behavior can actually be helpful for debugging.  
        /// To recover, the application will typically have to be restarted anyway
        /// at the process level, so we do not attempt autonomous recovery 
        /// of the worker thread.
        /// </remarks>
        public bool HasQuit => _hasQuit;

        /// <summary>
        /// Backing field for <see cref="HasQuit"/> property.
        /// </summary>
        private bool _hasQuit = false;

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
        private int _numLocalItems;

        /// <summary>
        /// Try to push an item to the local queue for this worker.
        /// </summary>
        public bool TryLocalPush(in TWorkItem taskItem)
        {
            if (_localQueue.TryPush(taskItem, false))
            {
                if (Interlocked.Increment(ref _numLocalItems) == 1)
                    _master.RaiseSemaphoreForLocalItem();

                return true;
            }

            return false;
        }

        /// <summary>
        /// Pseudo-random number generator to select another worker when
        /// stealing work items.
        /// </summary>
        private QuickRandomGenerator _random;

        /// <summary>
        /// Try to steal an item off the end of the queue of a randomly-
        /// selected worker.
        /// </summary>
        /// <param name="workItem">Set to the item stolen off some queue. </param>
        /// <returns>Whether an item has successfully been stolen. </returns>
        private bool TryStealWorkItem(out TWorkItem workItem)
        {
            var workers = this._master.AllWorkers!;

            // Randomly pick a worker
            var numWorkers = workers.Length;
            var startIndex = (int)(_random.Next() % (uint)numWorkers);

            // Scan each worker starting from the one picked above until
            // we can steal an item
            int i = startIndex;
            do
            {
                var worker = workers[i];

                // Do not steal from self
                if (worker != this && worker.TrySteal(out workItem))
                    return true;

                // Try next worker, wrapping around the end of the array
                if (++i == numWorkers)
                    i = 0;
            } while (i != startIndex);

            workItem = default!;
            return false;
        }

        /// <summary>
        /// Called by another worker to try to steal work from this worker.
        /// </summary>
        /// <param name="workItem">Set to the stolen work item, if there is one.
        /// Otherwise set to the default value.
        /// </param>
        /// <returns>Whether an item was successfully stolen. </returns>
        public bool TrySteal(out TWorkItem workItem)
        {
            bool success = _localQueue.TrySteal(out workItem);
            if (success && Interlocked.Decrement(ref _numLocalItems) > 0)
                _master.RaiseSemaphoreForLocalItem();
            return success;
        }

        /// <summary>
        /// Observe the work items in the local queue.
        /// </summary>
        /// <remarks>
        /// The results should not be taken as the true state of the queue.
        /// They are subject to tearing because no locks are taken.
        /// This method is for diagnostics only.
        /// </remarks>
        public TWorkItem[]? UnsafeGetItems() => _localQueue.UnsafeGetItems();

        /// <summary>
        /// Prepare a worker for <see cref="MultiThreadTaskScheduler"/>
        /// but do not start it yet.
        /// </summary>
        /// <param name="master">The owner of this worker. </param>
        /// <param name="initialDequeCapacity">Initial capacity for the Chase-Lev queue.
        /// Must be a power of two.
        /// </param>
        /// <param name="seed">Seed for the pseudo-random generator
        /// to select other workers to steal from. </param>
        /// <param name="name">The name assigned to this worker for debugging.
        /// This name will become the (managed) name of the thread.
        /// </param>
        public Worker(MultiThreadScheduler<TWorkItem, TExecutor> master, int initialDequeCapacity, uint seed, string name)
            : base(master)
        {
            _master = master;
            _localQueue = new ChaseLevQueue<TWorkItem>(initialDequeCapacity);
            _random = new QuickRandomGenerator(seed);

            _thread = new Thread(RunInThreadDelegate)
            {
                Priority = ThreadPriority.BelowNormal,
                IsBackground = true,
                Name = name
            };
        }

        /// <summary>
        /// Cached delegate for the worker thread.
        /// </summary>
        private static readonly ParameterizedThreadStart RunInThreadDelegate =
            self => ((Worker<TWorkItem, TExecutor>)self!).RunInThread();

        /// <summary>
        /// The background thread that dispatches work items for this worker.
        /// </summary>
        private readonly Thread _thread;

        /// <summary>
        /// Start the thread for this worker.  This method may only be called once.
        /// </summary>
        public void StartThread()
        {
            _thread.Start(this);
        }

        /// <summary>
        /// Drain the local queue of items and put them into the global queue.
        /// </summary>
        private void DrainLocalQueue()
        {
            var master = this._master;

            while (_localQueue.TryPop(out var workItem))
                master.EnqueueGlobalTaskItem(workItem);
        }

        /// <summary>
        /// The thread procedure for the worker thread.
        /// </summary>
        private void RunInThread()
        {
            var master = this._master;
            var logger = master.Logger;

            // Should not fail
            master.IncrementActiveThreadCount();

            try
            {
                SetThisWorkerForCurrentThread();

                ITaskSchedulerLogger.SourceQueue whichQueue;

                while (master.ShouldWorkerContinueRunning(ref _hasQuit))
                {
                    TWorkItem workItem;

                    if (_localQueue.TryPop(out workItem))
                    {
                        Interlocked.Decrement(ref _numLocalItems);
                        whichQueue = ITaskSchedulerLogger.SourceQueue.Local;
                    }
                    else if (master.TryDequeueGlobalTaskItem(out workItem))
                    {
                        whichQueue = ITaskSchedulerLogger.SourceQueue.Global;
                    }
                    else if (TryStealWorkItem(out workItem))
                    {
                        whichQueue = ITaskSchedulerLogger.SourceQueue.Stolen;
                    }
                    else
                    {
                        master.WaitOnSemaphore();
                        continue;
                    }

                    try
                    {
                        logger.BeginTask(whichQueue);
                        master.Executor.Execute(workItem);
                    }
                    finally
                    {
                        logger.EndTask(whichQueue);
                    }
                }
            }
            catch (Exception e)
            {
                logger.RaiseCriticalError(e);
            }

            try
            {
                UnsetThisWorkerForCurrentThread();
                DrainLocalQueue();
            }
            catch (Exception e)
            {
                logger.RaiseCriticalError(e);
            }

            try
            {
                master.DecrementActiveThreadCount();
            }
            catch (Exception e)
            {
                logger.RaiseCriticalError(e);
            }
        }
    }
}

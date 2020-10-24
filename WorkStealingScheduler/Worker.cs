﻿using System;
using System.Diagnostics;
using System.Threading;

namespace WorkStealingScheduler
{
    internal class Worker
    {
        /// <summary>
        /// Thread-local backing field for the <see cref="OfCurrentThread"/> property.
        /// </summary>
        [ThreadStatic]
        private static Worker? _ofCurrentThread;

        /// <summary>
        /// The worker object that runs the current thread, if any.
        /// </summary>
        public static Worker? OfCurrentThread => _ofCurrentThread;

        /// <summary>
        /// Work items queued locally by this worker.
        /// </summary>
        private ChaseLevQueue<WorkItem> _localQueue;

        /// <summary>
        /// The task scheduler that owns this worker.
        /// </summary>
        private readonly WorkStealingTaskScheduler _master;

        /// <summary>
        /// Whether the current thread is run by a worker for the given scheduler.
        /// </summary>
        public static bool IsCurrentWorkerOwnedBy(WorkStealingTaskScheduler master)
        {
            var worker = OfCurrentThread;
            return worker != null && object.ReferenceEquals(worker._master, master);
        }

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

        public bool TryLocalPush(in WorkItem taskItem)
        {
            if (_localQueue.TryPush(taskItem, false))
            {
                if (Interlocked.Increment(ref _numLocalItems) == 1)
                    _master.RaiseSemaphoreForLocalItem();

                return true;
            }

            return false;
        }

        private static readonly int Log2Frequency;

        /// <summary>
        /// Try to steal an item off the end of the queue of a randomly-
        /// selected worker.
        /// </summary>
        /// <param name="workItem">Set to the item stolen off some queue. </param>
        /// <returns>Whether an item has successfully been stolen. </returns>
        private bool TryStealWorkItem(out WorkItem workItem)
        {
            var workers = this._master.AllWorkers!;

            // Randomly pick a worker
            var numWorkers = workers.Length;
            var startIndex = (int)(((uint)(Stopwatch.GetTimestamp() >> Log2Frequency)) % (uint)numWorkers);

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

            workItem = default;
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
            bool success = _localQueue.TrySteal(out workItem);
            if (success && Interlocked.Decrement(ref _numLocalItems) > 0)
                _master.RaiseSemaphoreForLocalItem();
            return success;
        }

        public WorkItem[]? UnsafeGetItems() => _localQueue.UnsafeGetItems();

        public Worker(WorkStealingTaskScheduler master, int initialDequeCapacity)
        {
            this._master = master;
            this._localQueue = new ChaseLevQueue<WorkItem>(initialDequeCapacity);
        }

        /// <summary>
        /// Cached delegate for the worker thread.
        /// </summary>
        private static readonly ParameterizedThreadStart RunInThreadDelegate =
            self => ((Worker)self!).RunInThread();

        /// <summary>
        /// Start the thread for this worker.  This method may only be called once.
        /// </summary>
        /// <param name="label"></param>
        public void StartThread(string? label)
        {
            var thread = new Thread(RunInThreadDelegate)
            {
                Priority = ThreadPriority.BelowNormal,
                IsBackground = true,
                Name = label
            };
            thread.Start(this);
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

            try
            {
                _ofCurrentThread = this;

                ITaskSchedulerLogger.SourceQueue whichQueue;

                while (master.ShouldWorkerContinueRunning(ref _hasQuit))
                {
                    WorkItem workItem;

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
                        master.ExecuteTaskItemFromWorker(workItem);
                    }
                    finally
                    {
                        logger.EndTask(whichQueue);
                    }
                }

                DrainLocalQueue();
                _ofCurrentThread = null;
                return;
            }
            catch (Exception e)
            {
                logger.RaiseCriticalError(e);
            }

            try
            {
                _ofCurrentThread = null;
                DrainLocalQueue();
            }
            catch (Exception e)
            {
                logger.RaiseCriticalError(e);
            }
        }
    }
}
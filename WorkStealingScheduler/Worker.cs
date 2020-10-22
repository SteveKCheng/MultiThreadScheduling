using System;
using System.Threading;

namespace WorkStealingScheduler
{
    public sealed partial class WorkStealingTaskScheduler
    {
        /// <summary>
        /// A worker controlled by <see cref="WorkStealingTaskScheduler"/>
        /// which runs on its own dedicated thread.
        /// </summary>
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

                    var logger = this.master._logger;
                    ITaskSchedulerLogger.SourceQueue whichQueue;

                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        WorkItem workItem;

                        if (localQueue.TryPop(out workItem))
                        {
                            Interlocked.Decrement(ref numLocalItems);
                            whichQueue = ITaskSchedulerLogger.SourceQueue.Local;
                        }
                        else if (master._globalQueue.TryDequeue(out workItem))
                        {
                            whichQueue = ITaskSchedulerLogger.SourceQueue.Global;
                        }
                        else if (master.TryStealWorkItem(out workItem))
                        {
                            whichQueue = ITaskSchedulerLogger.SourceQueue.Stolen;
                        }
                        else
                        {
                            master._semaphore.Wait(this.cancellationToken);
                            continue;
                        }

                        if (workItem.TaskToRun != null)
                        {
                            try
                            {
                                logger.BeginTask(whichQueue);
                                master.TryExecuteTask(workItem.TaskToRun);
                            }
                            finally
                            {
                                logger.EndTask(whichQueue);
                            }
                        }
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MultiThreadScheduling
{
    /// <summary>
    /// A work-stealing, multi-thread scheduler for CPU-bound workloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This scheduler can be used instead of the default thread pool scheduler.
    /// It is optimized for CPU-bound tasks that do not wait synchronously in any way.
    /// It starts up some number of threads and does not adjust them except by explicit
    /// control.  
    /// </para>
    /// <para>
    /// In particular, the fancy "hill-climbing" algorithm from .NET's thread
    /// pool is not used, which behaves badly when all threads become busy doing CPU-intensive
    /// tasks: it tries to start more when the system is fully loaded, only to stop them
    /// again a short while later.  The oscillation in the number of threads significantly
    /// hurts performance.
    /// </para>
    /// <para>
    /// This scheduler also allows the user to monitor key events, to log them, which
    /// is useful for server applications.  
    /// </para>
    /// <para>
    /// Each worker thread prioritizes work from a local set of tasks, i.e. those tasks
    /// which have been pushed from other tasks executing in the same worker thread.
    /// For CPU-bound tasks, respecting thread affinity of work increases performance,
    /// especially if the work depends on thread-local caches.
    /// </para>
    /// <para>
    /// Tasks not localized to a worker thread go in a global queue.  When a worker 
    /// has no more local tasks and no more global tasks, it can steal work from other
    /// workers.  Then a common "tail problem" is avoided whereby a few threads are 
    /// occupied with a long set of local work while other threads just sit idle,
    /// when nearing completion of a big horde of tasks.
    /// </para>
    /// <para>
    /// This scheduler should still work well with tasks doing asynchronous I/O although it 
    /// has no special optimizations for them.  But running tasks with synchronous waiting is 
    /// not recommended.
    /// </para>
    /// </remarks>
    public sealed partial class MultiThreadTaskScheduler : TaskScheduler, IDisposable, IAsyncDisposable
    {
        private readonly MultiThreadScheduler<WorkItem, Executor> _scheduler;

        private readonly struct Executor : IWorkExecutor<WorkItem>
        {
            private readonly MultiThreadTaskScheduler _taskScheduler;

            public Executor(MultiThreadTaskScheduler taskScheduler) => _taskScheduler = taskScheduler;

            public void Execute(WorkItem workItem) => _taskScheduler.ExecuteTaskFromWorker(workItem);
        }

        public MultiThreadTaskScheduler(ITaskSchedulerLogger? logger)
        {
            _scheduler = new MultiThreadScheduler<WorkItem, Executor>(new Executor(this), logger);
        }

        internal void ExecuteTaskFromWorker(WorkItem workItem)
        {
            TryExecuteTask(workItem.TaskToRun);
        }

        #region Implementation of TaskScheduler

        /// <summary>
        /// Takes a best-effort snapshot of the tasks that have been scheduled so far.
        /// </summary>
        /// <returns></returns>
        protected override IEnumerable<Task> GetScheduledTasks()
        {
            return _scheduler.GetScheduledItems().Select(item => item.TaskToRun);
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
                thread.Name = nameof(MultiThreadTaskScheduler) + " thread for long-running task";
                thread.Start(task);
                return;
            }

            bool preferLocal = ((taskOptions & TaskCreationOptions.PreferFairness) == 0);
            _scheduler.EnqueueItem(workItem, preferLocal);
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
            if (!taskWasPreviouslyQueued && Worker.IsRunningInWorkerFor(_scheduler))
                return TryExecuteTask(task);

            return false;
        }

        #endregion

        #region Disposal

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
        public void Dispose() => _scheduler.Dispose();

        ValueTask IAsyncDisposable.DisposeAsync() => new ValueTask(_scheduler.DisposeAsync());

        /// <summary>
        /// Requests all workers to stop and cleans up resources allocated by this scheduler.
        /// </summary>
        /// <remarks>
        /// If a worker is in the middle of executing some work item, it can only stop
        /// after the work item has run.
        /// </remarks>
        /// <returns>Task that completes only when disposal is complete.
        /// If this method gets called (concurrently) multiple times, their returned
        /// Task objects all complete only when the disposal completes.
        /// </returns>
        public Task DisposeAsync() => _scheduler.DisposeAsync();

        #endregion
    }
}

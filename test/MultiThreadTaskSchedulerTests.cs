using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MultiThreadScheduling.Tests
{
    public class MultiThreadTaskSchedulerTests
    {
        [Fact]
        public async Task GloballyQueuedTasks()
        {
            var logger = new CountTasksLogger();
            var taskScheduler = new MultiThreadTaskScheduler(logger);
            taskScheduler.SetSchedulingOptions(new MultiThreadSchedulingSettings
            {
                NumberOfThreads = 4,
                ThreadPriority = System.Threading.ThreadPriority.BelowNormal
            });

            var random = new Random();
            var subtasks = new Task<int>[300];

            for (int i = 0; i < subtasks.Length; ++i)
            {
                var delayMs = random.Next(10, 80);
                int iCopy = i;  // freeze for lambda capture
                subtasks[i] = Task.Factory.StartNew(() =>
                {
                    // Pretend to be doing work.
                    Thread.Sleep(delayMs);
                    return iCopy;
                }, CancellationToken.None, TaskCreationOptions.None, taskScheduler);
            }

            await Task.WhenAll(subtasks);

            for (int i = 0; i < subtasks.Length; ++i)
                Assert.Equal(i, subtasks[i].Result);

            Assert.Equal(subtasks.Length, logger.GlobalTasksCount);

            await taskScheduler.DisposeAsync();

            Assert.InRange(logger.WorkersCount, 1, taskScheduler.SchedulingOptions.NumberOfThreads);
            Assert.Equal(logger.WorkersStopped, logger.WorkersCount);
        }

        [Fact]
        public async Task LocallyQueuedTasks()
        {
            var logger = new CountTasksLogger();
            var taskScheduler = new MultiThreadTaskScheduler(logger);
            taskScheduler.SetSchedulingOptions(new MultiThreadSchedulingSettings
            {
                NumberOfThreads = 4,
                ThreadPriority = System.Threading.ThreadPriority.BelowNormal
            });

            var tasks = new Task[6];
            int stolenTasksCount = 0;
            const int numChildTasks = 50;

            for (int i = 0; i < tasks.Length; ++i)
            {
                var iCopy = i;

                tasks[i] = TaskExtensions.Unwrap(Task.Factory.StartNew(async () =>
                {
                    var random = new Random();
                    var subtasks = new Task[numChildTasks];
                    var threadId = Environment.CurrentManagedThreadId;

                    for (int j = 0; j < subtasks.Length; ++j)
                    {
                        var jCopy = j;
                        var delayMs = random.Next(10, 80);

                        // This child task goes to the local queue
                        subtasks[j] = Task.Factory.StartNew(() =>
                        {
                            if (Environment.CurrentManagedThreadId != threadId)
                                Interlocked.Increment(ref stolenTasksCount);

                            // Pretend to be doing work.
                            Thread.Sleep(delayMs);
                            return (iCopy, jCopy);
                        });
                    }

                    await Task.WhenAll(subtasks);
                }, CancellationToken.None, TaskCreationOptions.None, taskScheduler));
            }

            await Task.WhenAll(tasks);

            Assert.Equal(stolenTasksCount, logger.StolenTasksCount);
            Assert.Equal(tasks.Length, logger.GlobalTasksCount);
            Assert.Equal(tasks.Length * numChildTasks - stolenTasksCount, logger.LocalTasksCount);

            await taskScheduler.DisposeAsync();
        }

        [Fact]
        public async Task StressTaskScheduling()
        {
            var logger = new CountTasksLogger();
            var taskScheduler = new MultiThreadTaskScheduler(logger);
            taskScheduler.SetSchedulingOptions(new MultiThreadSchedulingSettings
            {
                Mode = MultiThreadCreationMode.OneThreadPerCpuCore,
                ThreadPriority = System.Threading.ThreadPriority.BelowNormal
            });

            var numThreads = taskScheduler.SchedulingOptions.NumberOfThreads;

            var tasks = new Task[numThreads * 50];
            int overflowCount = 0;

            // Deliberately make the work unbalanced
            int GetNumChildTasks(int i) => 1000 + (i * 800) / numThreads;

            int totalChildTasks = Enumerable.Range(0, tasks.Length).Select(GetNumChildTasks).Sum();

            for (int i = 0; i < tasks.Length; ++i)
            {
                var iCopy = i;

                // This task goes to the global queue
                tasks[i] = TaskExtensions.Unwrap(Task.Factory.StartNew(async () =>
                {
                    var subtasks = new Task<(int, int)>[GetNumChildTasks(iCopy)];
                    var threadId = Environment.CurrentManagedThreadId;

                    for (int j = 0; j < subtasks.Length; ++j)
                    {
                        var jCopy = j;

                        // This child task goes to the local queue unless the local
                        // queue overflows
                        subtasks[j] = Task.Factory.StartNew(() =>
                        {
                            if (Environment.CurrentManagedThreadId != threadId)
                                Interlocked.Increment(ref overflowCount);

                            return (iCopy, jCopy);
                        });
                    }

                    await Task.WhenAll(subtasks);

                    for (int j = 0; j < subtasks.Length; ++j)
                        Assert.Equal((iCopy, j), subtasks[j].Result);

                }, CancellationToken.None, TaskCreationOptions.None, taskScheduler));
            }

            await Task.WhenAll(tasks);

            Assert.InRange(overflowCount, logger.StolenTasksCount, int.MaxValue);
            Assert.Equal(tasks.Length + totalChildTasks, logger.LocalEnqueuesCount + logger.GlobalEnqueuesCount);
            Assert.Equal(tasks.Length + totalChildTasks, logger.LocalTasksCount + logger.GlobalTasksCount +
                                                         logger.StolenTasksCount);

            await taskScheduler.DisposeAsync();
        }

        private class CountTasksLogger : ISchedulingLogger
        {
            private volatile int _localTasksCount = 0;
            private volatile int _globalTasksCount = 0;
            private volatile int _stolenTasksCount = 0;
            private volatile int _workersCount = 0;
            private volatile int _workersStopped = 0;
            private volatile int _localEnqueuesCount = 0;
            private volatile int _globalEnqueuesCount = 0;

            public int LocalTasksCount => _localTasksCount;
            public int GlobalTasksCount => _globalTasksCount;
            public int StolenTasksCount => _stolenTasksCount;
            public int WorkersCount => _workersCount;
            public int WorkersStopped => _workersStopped;
            public int LocalEnqueuesCount => _localEnqueuesCount;
            public int GlobalEnqueuesCount => _globalEnqueuesCount;

            public void BeginTask(uint workerId, WorkSourceQueue sourceQueue, in WorkItemInfo workInfo)
            {
                switch (sourceQueue)
                {
                    case WorkSourceQueue.Local:
                        Interlocked.Increment(ref _localTasksCount);
                        break;
                    case WorkSourceQueue.Global:
                        Interlocked.Increment(ref _globalTasksCount);
                        break;
                    case WorkSourceQueue.Stolen:
                        Interlocked.Increment(ref _stolenTasksCount);
                        break;
                }
            }

            public void EndTask(uint workerId, 
                                WorkSourceQueue sourceQueue, 
                                in WorkItemInfo workInfo,
                                WorkExecutionStatus workStatus)
            {
            }

            public void EnqueueWork(uint? workerId, in WorkItemInfo workInfo)
            {
                if (workerId != null)
                    Interlocked.Increment(ref _localEnqueuesCount);
                else
                    Interlocked.Increment(ref _globalEnqueuesCount);
            }

            public void Idle(uint workerId)
            {
            }

            public void RaiseCriticalError(uint? workerId, Exception exception)
            {
            }

            public void WorkerStarts(uint workerId)
            {
                Interlocked.Increment(ref _workersCount);
            }

            public void WorkerStops(uint workerId)
            {
                Interlocked.Increment(ref _workersStopped);
            }
        }
    }
}

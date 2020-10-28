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
                    var threadId = Thread.CurrentThread.ManagedThreadId;

                    for (int j = 0; j < subtasks.Length; ++j)
                    {
                        var jCopy = j;
                        var delayMs = random.Next(10, 80);

                        // This child task goes to the local queue
                        subtasks[j] = Task.Factory.StartNew(() =>
                        {
                            if (Thread.CurrentThread.ManagedThreadId != threadId)
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

        private class CountTasksLogger : ISchedulingLogger
        {
            private volatile int _localTasksCount = 0;
            private volatile int _globalTasksCount = 0;
            private volatile int _stolenTasksCount = 0;

            public int LocalTasksCount => _localTasksCount;
            public int GlobalTasksCount => _globalTasksCount;
            public int StolenTasksCount => _stolenTasksCount;

            public void BeginTask(uint workerId, ISchedulingLogger.SourceQueue sourceQueue)
            {
                switch (sourceQueue)
                {
                    case ISchedulingLogger.SourceQueue.Local:
                        Interlocked.Increment(ref _localTasksCount);
                        break;
                    case ISchedulingLogger.SourceQueue.Global:
                        Interlocked.Increment(ref _globalTasksCount);
                        break;
                    case ISchedulingLogger.SourceQueue.Stolen:
                        Interlocked.Increment(ref _stolenTasksCount);
                        break;
                }
            }

            public void EndTask(uint workerId, ISchedulingLogger.SourceQueue sourceQueue)
            {
            }

            public void RaiseCriticalError(Exception exception)
            {
            }
        }
    }
}

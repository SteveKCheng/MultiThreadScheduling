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
            var taskScheduler = new MultiThreadTaskScheduler();
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
        }

        [Fact]
        public async Task LocallyQueuedTasks()
        {
            var logger = new CountStolenTasksLogger();
            var taskScheduler = new MultiThreadTaskScheduler(logger);
            taskScheduler.SetSchedulingOptions(new MultiThreadSchedulingSettings
            {
                NumberOfThreads = 4,
                ThreadPriority = System.Threading.ThreadPriority.BelowNormal
            });

            var tasks = new Task[6];
            int stolenTasksCount = 0;

            for (int i = 0; i < tasks.Length; ++i)
            {
                var iCopy = i;

                tasks[i] = TaskExtensions.Unwrap(Task.Factory.StartNew(async () =>
                {
                    var random = new Random();
                    var subtasks = new Task[50];
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
        }

        private class CountStolenTasksLogger : ISchedulingLogger
        {
            private volatile int _stolenTasksCount = 0;

            public int StolenTasksCount => _stolenTasksCount;

            public void BeginTask(ISchedulingLogger.SourceQueue sourceQueue)
            {
                if (sourceQueue == ISchedulingLogger.SourceQueue.Stolen)
                    Interlocked.Increment(ref _stolenTasksCount);
            }

            public void EndTask(ISchedulingLogger.SourceQueue sourceQueue)
            {
            }

            public void RaiseCriticalError(Exception exception)
            {
            }
        }
    }
}

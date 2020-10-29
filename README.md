Multi-threaded task scheduler for .NET Core
===========================================

This library implements a .NET ``TaskScheduler`` that can be used in place
of the default thread-pool scheduler.

It is optimized for CPU-bound tasks that do not wait synchronously in any way.
It starts up some number of threads and does not adjust them except by explicit
control.  

In particular, the fancy "hill-climbing" algorithm from .NET's thread
pool is not used, which behaves badly when all threads become busy doing CPU-intensive
tasks: it tries to start more when the system is fully loaded, only to stop them
again a short while later.  The oscillation in the number of threads significantly
hurts performance.

This scheduler also allows the user to monitor key events, to log them, which
is useful for server applications.  

Each worker thread prioritizes work from a local set of tasks, i.e. those tasks
which have been pushed from other tasks executing in the same worker thread.
For CPU-bound tasks, respecting thread affinity of work increases performance,
especially if the work depends on thread-local caches.

Tasks not localized to a worker thread go in a global queue.  When a worker 
has no more local tasks and no more global tasks, it can steal work from other
workers.  Then a common "tail problem" is avoided whereby a few threads are 
occupied with a long set of local work while other threads just sit idle,
when nearing completion of a big horde of tasks.

This scheduler should still work well with tasks doing asynchronous I/O although it 
has no special optimizations for them.  But running tasks with synchronous waiting is 
not recommended.

The author is looking to use this scheduler on servers with large numbers of cores
(32 to 128), for which optimizations for CPU topology may become important.

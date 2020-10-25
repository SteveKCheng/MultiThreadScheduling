using System;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Abstract message that may be queued into <see cref="MultiThreadScheduler{TWorkItem}"/>
    /// and gets processed by one of its worker threads.
    /// </summary>
    public interface IWorkItem
    {
        /// <summary>
        /// Called by a worker thread to execute the work item, synchronously, in that thread.
        /// </summary>
        /// <param name="executor">
        /// User-defined object or state that may be needed to interpret or execute the work. 
        /// </param>
        void Execute(object? executor);
    }
}

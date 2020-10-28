using System;
using System.Collections.Generic;
using System.Threading;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Implementation of the Chase-Lev lock-free queue for work-stealing task scheduling.
    /// </summary>
    /// </summary>
    /// <typeparam name="TItem">The element type of the items. </typeparam>
    /// <remarks>
    /// <para>
    /// This data structure is a struct for efficiency; it is meant to be embedded
    /// in a larger class for a work-stealing queue.  The struct
    /// should not be default initialized or copied.
    /// </para>
    /// <para>
    /// This data structure is well-known and is described in the following papers:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>David Chase, Yossi Lev. 
    /// “Dynamic circular work-stealing deque”.  Proceedings of the 17th 
    /// annual ACM symposium on Parallelism in algorithms and architectures, July 2005.
    /// Pages 21–28. Link: https://www.dre.vanderbilt.edu/~schmidt/PDF/work-stealing-dequeue.pdf
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// Nhat Minh Lê, Antoniu Pop, Albert Cohen, Francesco Zappa Nardelli.
    /// “Correct and Efficient Work-Stealing for Weak Memory Models”. 
    /// Proceedings of the 18th ACM SIGPLAN symposium on Principles and practice of parallel programming,
    /// February 2013. Pages 69–80.  Link: https://fzn.fr/readings/ppopp13.pdf
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// There are many open-source implementations of this data structure 
    /// in various programming languages.  The second paper gives a translation
    /// (and proof!) of the algorithm for the C11 atomics memory model.
    /// </para>
    /// <para>
    /// We have translated that C11 code to C# with the following adaptations:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// Items are cleared after they are retrieved, to avoid unnecessary references
    /// to GC objects, as well as to aid observing the contents of the queue.
    /// In C11, this could lead to an illegal data race, i.e. undefined behavior,
    /// but it is not a problem for .NET.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// The code does not fail when the (32-bit) array indices wrap around.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// In .NET, <see cref="Interlocked.CompareExchange"/> is already sequentially
    /// consistent.  That means it is inefficient on ARM but there is nothing
    /// we can do about that.  The second paper in fact points out that full sequential
    /// consistency is too strong but also that there is no way to express
    /// the required “cumulative release” barrier in the C11 memory model.
    /// </description>
    /// </item>
    /// <item>
    /// The sequentially-consistent fence in C11 is replaced by 
    /// <see cref="Interlocked.MemoryBarrier"/>.  This is faithful translation
    /// though, again, as the second paper points out, at certain points
    /// in the code such a barrier is stronger than necessary.  On x86,
    /// such a fence compiles to a dummy interlocked bitwise-or on the stack pointer.
    /// </item>
    /// <item>
    /// There is no equivalent of the C11 release fence in .NET. 
    /// Fortunately, it is good enough to use <see cref="Volatile.Write"/> since
    /// the underlying memory barrier operations in .NET are not tied
    /// to specific variables like in C11.  (Hardware generally does not have
    /// fences tied to specific memory locations; presumably the very weak memory model
    /// of C11 facilitate re-ordering of atomic operations by optimizing compilers.)
    /// </item>
    /// </remarks>
    public struct ChaseLevQueue<TItem>
    {
        /// <summary>
        /// Index to the "bottom" end of the deque in which the "main processor"
        /// can push to or pop items from.
        /// </summary>
        /// <remarks>
        /// This index to <see cref="_storage"/> must be interpreted modulo
        /// the array size.  Negative indices can occur on integer overflow;
        /// they should interpreted modulo the same way.
        /// </remarks>
        private int _bottom;

        /// <summary>
        /// Index to the "top" end of the dequeue in which other processors
        /// can steal items from.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This index to <see cref="_storage"/> must be interpreted modulo
        /// the array size.  Negative indices can occur on integer overflow;
        /// they should interpreted modulo the same way.
        /// </para>
        /// <para>
        /// At some points of this code, we compare for <see cref="_top"/>
        /// being less or less than equal to <see cref="_bottom"/>.  We 
        /// must be careful to express such comparisons as <c>b - t &gt; 0</c>
        /// or <c>b - t &gt;= 0</c>, so that it works even when 
        /// <see cref="_bottom"/> has wrapped around zero
        /// but <see cref="_top"/> has not.  The comparison must be in signed 
        /// arithmetic, i.e. two's complement.
        /// </para>
        /// </remarks>
        private int _top;

        /// <summary>
        /// Circular buffer for storing the items.  
        /// </summary>
        /// <remarks>
        /// Successive items are put at increasing indices.
        /// This array may resize.  For efficiency its capacity is always a power of two.
        /// </remarks>
        private TItem[] _storage;

        /// <summary>
        /// Round up an integer to the next higher power of two,
        /// or leave it unchanged if it is already a power of two, or zero.
        /// </summary>
        private static uint RoundUpToNextPowerOfTwo(uint v)
        {
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            v++;
            return v;
        }

        /// <summary>
        /// The maximum number of elements this instance is allowed to hold at once.
        /// </summary>
        public readonly int AllowedCapacity;

        /// <summary>
        /// Initialize as empty.
        /// </summary>
        /// <param name="initialCapacity">Suggested number of elements that
        /// this instance may hold before having to re-allocate arrays.
        /// It must not exceed <see cref="MaxCapacity"/>
        /// The actual capacity may be rounded up.
        /// </param>
        /// <param name="allowedCapacity">How much this instance is allowed
        /// to grow. </param>
        public ChaseLevQueue(int initialCapacity, int allowedCapacity)
        {
            if (initialCapacity < 0 || initialCapacity > allowedCapacity)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity));

            if (allowedCapacity > MaxCapacity)
                throw new ArgumentOutOfRangeException(nameof(allowedCapacity));

            initialCapacity = initialCapacity <= MinCapacity
                                ? MinCapacity
                                : (int)RoundUpToNextPowerOfTwo((uint)initialCapacity);

            allowedCapacity = allowedCapacity <= initialCapacity
                                ? initialCapacity
                                : (int)RoundUpToNextPowerOfTwo((uint)allowedCapacity);

            this.AllowedCapacity = allowedCapacity;
            this._storage = new TItem[initialCapacity];
            this._bottom = 0;
            this._top = 0;
        }

        /// <summary>
        /// Initialize as empty with the default limit on the allowed capacity.
        /// </summary>
        /// <param name="initialCapacity">Suggested number of elements that
        /// this instance may hold before having to re-allocate arrays.
        /// It must not exceed <see cref="MaxCapacity"/>
        /// The actual capacity may be rounded up.
        /// </param>
        public ChaseLevQueue(int initialCapacity)
            : this(initialCapacity, MaxCapacity)
        {
        }

        /// <summary>
        /// The minimum allowed number of slots allocated by an instance
        /// of this structure.
        /// </summary>
        /// <remarks>
        /// It has to be non-zero to avoid unnecessary boundary conditions.
        /// It is a power of two.
        /// </remarks>
        public static readonly int MinCapacity = 16;

        /// <summary>
        /// The maximum allowed number of slots allocated by an instance
        /// of this structure.
        /// </summary>
        /// <remarks>
        /// This is to avoid runaway
        /// It is a power of two.
        /// </remarks>
        public static readonly int MaxCapacity = 1048576;

        /// <summary>
        /// Copy out the items in the array, to observe them for diagnostic purposes.
        /// </summary>
        /// <remarks>
        /// The results should not be taken as the true state of the queue.
        /// They are subject to tearing because no locks are taken.
        /// </remarks>
        public TItem[]? UnsafeGetItems()
        {
            int t = Volatile.Read(ref this._top);
            Interlocked.MemoryBarrier();
            int b = Volatile.Read(ref this._bottom);
            TItem[] storage = this._storage;
            int capacity = storage.Length;

            int numItems = b - t;
            if (numItems <= 0)
                return null;

            TItem[] items = new TItem[numItems];
            for (int i = 0; i < numItems; ++i)
                items[i] = storage[(i + t) & (capacity - 1)];

            return items;
        }

        /// <summary>
        /// Take an item from the "bottom" end.  This method
        /// must only be called by one designated flow of execution.
        /// </summary>
        /// <param name="item">Set to the item that has been popped, if it exists.
        /// If it does not exist, this parameter gets the default value.
        /// </param>
        /// <returns>Whether an item has just been taken from the queue. </returns>
        public bool TryPop(out TItem item)
        {
            unchecked // allow indices to wrap around zero
            {
                int b = this._bottom - 1;
                TItem[] storage = this._storage;
                this._bottom = b;

                Interlocked.MemoryBarrier();

                int t = this._top;

                // Non-empty queue.
                if (b - t >= 0)
                {
                    ref TItem y = ref storage[b & (storage.Length - 1)];
                    item = y;

                    // Single last element in queue.
                    if (b == t)
                    {
                        if (Interlocked.CompareExchange(ref this._top, t + 1, t) != t)
                        {
                            item = default!;
                            return false;
                        }

                        this._bottom = b + 1;
                    }

                    // Erase the existing entry to avoid dangling references for GC.
                    // Since the Chase-Lev queue only allows pushing from one
                    // designated flow of execution, namely this one, no harmful
                    // data race will be caused here.
                    y = default!;

                    return true;
                }

                // Queue is empty.
                else
                {
                    item = default!;
                    this._bottom = b + 1;
                    return false;
                }
            }
        }

#if false
        /// <summary>
        /// Peek at the next item from the "bottom" end.  This method
        /// must only be called by one designated flow of execution.
        /// </summary>
        /// <param name="item">Set to the item at "bottom" end.  
        /// If it does not exist, this parameter gets the default value.
        /// </param>
        /// <remarks>
        /// The caller must not consume the item.  This method shall
        /// only be used for optimization where the caller wants to know
        /// what item is there before trying to consume it.  The item
        /// may be concurrently consumed by <see cref="TrySteal(out TItem)"/>
        /// called by another thread.
        /// </remarks>
        public bool TryPeek(out TItem item)
        {
            unchecked // allow indices to wrap around zero
            {
                int b = this._bottom - 1;
                TItem[] storage = this._storage;
                int t = this._top;

                if (b - t >= 0)
                {
                    item = storage[b & (storage.Length - 1)];
                    return true;
                }
                else
                {
                    item = default!;
                    return false;
                }
            }
        }
#endif

        /// <summary>
        /// Insert an item ahead of all the existing ones at the "bottom" end.
        /// This method must only be called by one designated
        /// flow of execution.
        /// </summary>
        /// <param name="item">The new item to put in. </param>
        /// <param name="allowGrow">Allow the capacity of the array to grow,
        /// up to the limit specified at construction.
        /// </param>
        /// <returns>
        /// Whether the item can be successfully put, resizing the array
        /// buffer as necessary.
        /// </returns>
        public bool TryPush(in TItem item, bool allowGrow)
        {
            unchecked // allow indices to wrap around zero
            {
                int b = this._bottom;
                int t = Volatile.Read(ref this._top);

                TItem[] storage = this._storage;
                int capacity = storage.Length;

                // Queue is full.
                if (b - t >= capacity)
                {
                    if (!allowGrow)
                        return false;

                    if (capacity >= this.AllowedCapacity)
                        throw new InvalidOperationException("Cannot push any more items into the queue because it is at the maximum allowed capacity. ");

                    int newCapacity = capacity * 2;

                    // Double capacity of the array and then copy the old elements over.
                    TItem[] newStorage = new TItem[newCapacity];
                    for (int i = t; i != b + 1; ++i)
                        newStorage[i & (newCapacity - 1)] = storage[i & (capacity-1)];

                    Volatile.Write(ref this._storage, newStorage);

                    storage = newStorage;
                    capacity = newCapacity;
                }

                storage[b & (capacity - 1)] = item;
                Volatile.Write(ref this._bottom, b + 1);
                return true;
            }
        }

        /// <summary>
        /// Attempt to take an item from the "top" end of the dequeue.
        /// </summary>
        /// <remarks>
        /// This method may be called from any concurrent thread.
        /// </remarks>
        /// <param name="item">Set to the item that has been "stolen", if it exists.
        /// If it does not exist, this parameter gets the default value.
        /// </param>
        /// <returns>Whether an item has just been taken from the queue. </returns>
        public bool TrySteal(out TItem item)
        {
            unchecked // allow indices to wrap around zero
            {
                int t = Volatile.Read(ref this._top);
                Interlocked.MemoryBarrier();
                int b = Volatile.Read(ref this._bottom);

                if (b - t > 0)
                {
                    TItem[] storage = this._storage;
                    ref TItem y = ref storage[t & (storage.Length - 1)];
                    item = y;

                    if (Interlocked.CompareExchange(ref this._top, t + 1, t) == t)
                    {
                        y = default!;
                        return true;
                    }
                }

                item = default!;
                return false;
            }
        }
    }
}

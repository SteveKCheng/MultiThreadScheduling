using System;

namespace MultiThreadScheduling
{
    /// <summary>
    /// Interprets a caller-supplied buffer as a bit mask and allows basic
    /// bit-mask operations on it.
    /// </summary>
    /// <remarks>
    /// Used to represent sets of CPUs.  <see cref="System.Collections.BitArray"/>
    /// cannot be used because it allocates on GC, while we need a value type
    /// for efficiency and for interoperability with C APIs.  The bit masks
    /// are not persisted in this application so we may use stack allocation
    /// for the buffers.
    /// </remarks>
    internal readonly ref struct BitMask
    {
        private readonly Span<ulong> _buffer;

        public BitMask(Span<ulong> buffer)
        {
            _buffer = buffer;
        }

        public bool this[int index]
        {
            get
            {
                if (index < 0 || index >= _buffer.Length * 64)
                    throw new IndexOutOfRangeException();

                return (_buffer[index >> 6] & ((ulong)1 << (index & 63))) != 0;
            }
            set
            {
                if (index < 0 || index >= _buffer.Length * 64)
                    throw new IndexOutOfRangeException();

                if (value)
                    _buffer[index >> 6] |= ((ulong)1 << (index & 63));
                else
                    _buffer[index >> 6] &= ~((ulong)1 << (index & 63));
            }
        }

        public void Clear()
        {
            for (int i = 0; i < _buffer.Length; ++i)
                _buffer[i] = 0;
        }

        public void SetAll()
        {
            for (int i = 0; i < _buffer.Length; ++i)
                _buffer[i] = unchecked((ulong)-1);
        }

        public int Count => _buffer.Length * 64;

        public System.UIntPtr NumNativeWords => (System.UIntPtr)_buffer.Length;

        public ref ulong GetPinnableReference() => ref _buffer.GetPinnableReference();
    }
}

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MultiThreadScheduling.Tests")]

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

        /// <summary>
        /// Get or set a bit at the specified location.
        /// </summary>
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

        /// <summary>
        /// Turn all bits off.
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < _buffer.Length; ++i)
                _buffer[i] = 0;
        }

        /// <summary>
        /// Turn all bits on.
        /// </summary>
        public void SetOnAll()
        {
            for (int i = 0; i < _buffer.Length; ++i)
                _buffer[i] = unchecked((ulong)-1);
        }

        public void SetWord64(int wordIndex, ulong word)
        {
            _buffer[wordIndex] = word;
        }

        public void SetWord32(int wordIndex, uint word)
        {
            ref ulong c = ref _buffer[wordIndex >> 1];
            if ((wordIndex & 1) == 0)
                c = (c & ((ulong)0xFFFFFFFF00000000)) | ((ulong)word);
            else
                c = (c & ((ulong)0xFFFFFFFF)) | (((ulong)word) << 32);
        }

        /// <summary>
        /// The number of elements stored by this bit mask.  It is always
        /// a multiple of the word size in bits, which is 64.
        /// </summary>
        public int Count => _buffer.Length * 64;

        public System.UIntPtr NumNativeWords => (System.UIntPtr)_buffer.Length;
        
        /// <summary>
        /// Get the number of machine words for a bit mask
        /// needed to hold some number of bits.
        /// </summary>
        /// <param name="count">Desired number of elements to store
        /// in the bit mask.
        /// </param>
        public static int GetNumberOfNativeWords(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            return (count + 63) / 64;
        }

        public ref ulong GetPinnableReference() => ref _buffer.GetPinnableReference();

        /// <summary>
        /// Find the position of the next bit, on or after <paramref name="startIndex"/>,
        /// that is set on.
        /// </summary>
        /// <param name="startIndex">Index of the bit to start scanning from. </param>
        /// <returns>Position of the next bit that is set, or -1 if all the bits
        /// from <paramref name="startIndex"/> onwards are off.</returns>
        public int GetIndexOfNextOnBit(int startIndex)
        {
            if (startIndex < 0)
                throw new IndexOutOfRangeException();

            if (startIndex >= 64 * _buffer.Length)
                return -1;

            // Scan one 64-bit word at a time
            int wordIndex = (startIndex >> 6);
            ulong currentWord = _buffer[wordIndex] & (unchecked((ulong)-1) << (startIndex & 63));
            while (currentWord == 0)
            {
                if (++wordIndex == _buffer.Length) return -1;
                currentWord = _buffer[wordIndex];
            }

            return (wordIndex << 6) + BitOperations.TrailingZeroCount(currentWord);
        }

        /// <summary>
        /// Count the number of bits that are set on.
        /// </summary>
        public int CountOnBits()
        {
            int sum = 0;
            for (int i = 0; i < _buffer.Length; ++i)
                sum += BitOperations.PopCount(_buffer[i]);
            return sum;
        }
    }
}

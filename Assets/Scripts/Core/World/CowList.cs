using System;
using System.Collections;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// A growable list stored in fixed pages that copies share until one of them writes (copy on write, per page).
    /// The battle royale map needs a copy per user (the server's, the client's, the one drawn), and almost all of it
    /// never changes in a match: <see cref="ShareCopy"/> costs one small page table, and a page is copied only when
    /// a tile on it is first damaged. Each copy writes only its own page table, so copies on different threads (the
    /// server on its thread, the client on the main thread) never touch each other's pages.
    /// </summary>
    public sealed class CowList<T> : IReadOnlyList<T>
    {
        private const int Shift = 8; // 256 items: damage in one spot copies little
        public const int PageSize = 1 << Shift;
        private const int Mask = PageSize - 1;

        private T[][] _pages = new T[0][];
        private bool[] _own = new bool[0];
        private int _count;

        public int Count => _count;

        public T this[int i]
        {
            get
            {
                if ((uint)i >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(i));
                return _pages[i >> Shift][i & Mask];
            }
            set
            {
                if ((uint)i >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(i));
                Own(i >> Shift)[i & Mask] = value;
            }
        }

        public void Add(T v)
        {
            int p = _count >> Shift;
            if (p == _pages.Length)
            {
                Array.Resize(ref _pages, Math.Max(4, _pages.Length * 2));
                Array.Resize(ref _own, _pages.Length);
            }
            if (_pages[p] == null) { _pages[p] = new T[PageSize]; _own[p] = true; }
            Own(p)[_count & Mask] = v;
            _count++;
        }

        /// <summary>Grows the list to <paramref name="n"/> items, new ones set to <paramref name="fill"/>.</summary>
        public void GrowTo(int n, T fill)
        {
            while (_count < n) Add(fill);
        }

        /// <summary>A copy that shares every page with this list until either of them writes to it.</summary>
        public CowList<T> ShareCopy()
        {
            var c = new CowList<T> { _pages = (T[][])_pages.Clone(), _own = new bool[_own.Length], _count = _count };
            for (int i = 0; i < _own.Length; i++) _own[i] = false; // this list no longer owns them alone either
            return c;
        }

        /// <summary>Copies <paramref name="n"/> items from <paramref name="start"/> (they must lie on one page).</summary>
        public void CopyTo(int start, T[] dst, int dstStart, int n)
        {
            if (n == 0) return;
            if ((start >> Shift) != ((start + n - 1) >> Shift) || start < 0 || start + n > _count) throw new ArgumentOutOfRangeException(nameof(start));
            Array.Copy(_pages[start >> Shift], start & Mask, dst, dstStart, n);
        }

        /// <summary>Writes <paramref name="n"/> items at <paramref name="start"/> (they must lie on one page).</summary>
        public void CopyFrom(T[] src, int srcStart, int start, int n)
        {
            if (n == 0) return;
            if ((start >> Shift) != ((start + n - 1) >> Shift) || start < 0 || start + n > _count) throw new ArgumentOutOfRangeException(nameof(start));
            Array.Copy(src, srcStart, Own(start >> Shift), start & Mask, n);
        }

        /// <summary>Pages this list holds alone (written since it was shared, or never shared).</summary>
        public int OwnPages
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _own.Length; i++) if (_own[i] && _pages[i] != null) n++;
                return n;
            }
        }

        private T[] Own(int p)
        {
            if (!_own[p])
            {
                _pages[p] = (T[])_pages[p].Clone();
                _own[p] = true;
            }
            return _pages[p];
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < _count; i++) yield return _pages[i >> Shift][i & Mask];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

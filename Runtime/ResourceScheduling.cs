using System;

namespace ZRAsset
{
    // Main-thread call scope. Async builders explicitly restore it for continuations;
    // a scope is never held across an await or propagated into worker threads.
    internal static class ResourceScheduling
    {
        [ThreadStatic] internal static ResourcePackage Current;
        internal static Scope Enter(ResourcePackage package)
        {
            return new(package);
        }

        internal readonly struct Scope: IDisposable
        {
            private readonly ResourcePackage m_previous;
            internal Scope(ResourcePackage package) { m_previous = Current; Current = package; }
            public void Dispose() { Current = m_previous; }
        }
    }
}

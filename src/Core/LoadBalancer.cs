using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RampageTracker.Core
{
    public static class LoadBalancer
    {
        public static async Task ForEachAsync<T>(
            IEnumerable<T> items,
            int parallelism,
            Func<T, Task> body,
            CancellationToken ct = default)
        {
            var sem = new SemaphoreSlim(Math.Max(1, parallelism));
            var tasks = new List<Task>();
            foreach (var item in items)
            {
                await sem.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try { await body(item); } finally { sem.Release(); }
                }, ct));
            }
            await Task.WhenAll(tasks);
        }
    }
}

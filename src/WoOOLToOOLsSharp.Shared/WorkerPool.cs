using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WoOOLToOOLsSharp.Shared;

public sealed class WorkerPool : IDisposable, IAsyncDisposable
{
    private readonly Channel<Action> _queue;
    private readonly Task[] _workers;
    private bool _disposed;

    public int WorkerCount => _workers.Length;

    public WorkerPool(int workerCount = 0)
    {
        if (workerCount <= 0)
        {
            workerCount = Environment.ProcessorCount;
        }

        workerCount = Math.Clamp(workerCount, 2, 16);

        _queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });

        _workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            _workers[i] = Task.Factory.StartNew(
                WorkerLoop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public void Submit(Action task)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WorkerPool));
        if (task is null) throw new ArgumentNullException(nameof(task));

        if (!_queue.Writer.TryWrite(task))
        {
            throw new InvalidOperationException("任务队列已关闭，无法提交任务。");
        }
    }

    public void Submit(Func<Task> task)
    {
        if (task is null) throw new ArgumentNullException(nameof(task));
        Submit(() => task().GetAwaiter().GetResult());
    }

    public void ParallelForEach(IReadOnlyList<Action> tasks, Action? onComplete = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WorkerPool));
        if (tasks is null) throw new ArgumentNullException(nameof(tasks));
        if (tasks.Count == 0) return;

        using var countdown = new CountdownEvent(tasks.Count);

        for (int i = 0; i < tasks.Count; i++)
        {
            Action task = tasks[i] ?? throw new ArgumentException("任务列表包含 null。", nameof(tasks));
            Submit(() =>
            {
                try
                {
                    task();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WorkerPool] task exception: {ex}");
                }

                try
                {
                    onComplete?.Invoke();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WorkerPool] onComplete exception: {ex}");
                }

                countdown.Signal();
            });
        }

        countdown.Wait();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.Writer.TryComplete();

        try
        {
            Task.WaitAll(_workers);
        }
        catch (AggregateException ex)
        {
            Console.Error.WriteLine($"[WorkerPool] worker exception: {ex}");
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void WorkerLoop()
    {
        var reader = _queue.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out Action? work))
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WorkerPool] unhandled worker exception: {ex}");
                }
            }
        }
    }
}



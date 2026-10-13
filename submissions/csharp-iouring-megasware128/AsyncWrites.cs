using System.Buffers;
using System.Collections.Concurrent;

namespace FeedApi;

// A slow WAL sync must not stop the socket loop. Each SQLite connection has one owner.
internal sealed unsafe class AsyncWrites : IDisposable
{
    private sealed record Job(object Context, byte[] Buffer, int Method, int Path, int Auth, int Body);
    internal readonly record struct Completion(object Context, Reply Reply, byte[] Buffer);
    private readonly ConcurrentQueue<Job> _jobs = new();
    private readonly ConcurrentQueue<Completion> _completed = new();
    private readonly AutoResetEvent _work = new(false);
    private readonly Thread _thread;
    private readonly Db _db;
    private readonly Api _api;
    private volatile bool _stopping;
    private int _outstanding;
    private int _inputBytes;
    internal int EventFd { get; }

    public AsyncWrites(Db db, byte[] secret)
    {
        _db = db; _api = new Api(db,secret);
        EventFd = Native.eventfd(0,Native.SOCK_NONBLOCK|Native.SOCK_CLOEXEC);
        if (EventFd<0) throw new InvalidOperationException("eventfd: "+Native.Error);
        _thread = new Thread(Run) { IsBackground=true, Name="SQLite writes" };
        _thread.Start();
    }

    public bool Enqueue(object context, ReadOnlySpan<byte> method, ReadOnlySpan<byte> path,
                        ReadOnlySpan<byte> auth, ReadOnlySpan<byte> body)
    {
        int size=method.Length+path.Length+auth.Length+body.Length;
        int count=Interlocked.Increment(ref _outstanding);
        int bytes=Interlocked.Add(ref _inputBytes,size);
        if (count>8192 || bytes>16*1024*1024)
        {
            Interlocked.Decrement(ref _outstanding); Interlocked.Add(ref _inputBytes,-size); return false;
        }
        byte[] buffer=ArrayPool<byte>.Shared.Rent(size);
        int offset=0;
        method.CopyTo(buffer); offset+=method.Length;
        path.CopyTo(buffer.AsSpan(offset)); offset+=path.Length;
        auth.CopyTo(buffer.AsSpan(offset)); offset+=auth.Length;
        body.CopyTo(buffer.AsSpan(offset));
        _jobs.Enqueue(new Job(context,buffer,method.Length,path.Length,auth.Length,body.Length));
        _work.Set(); return true;
    }

    private void Run()
    {
        var batch=new List<Completion>(512);
        while (!_stopping)
        {
            _work.WaitOne();
            while (!_stopping && !_jobs.IsEmpty)
            {
                batch.Clear();
                for (int n=0;n<512 && _jobs.TryDequeue(out var job);n++)
                {
                    var input=job.Buffer.AsSpan();
                    Reply reply=_api.Handle(input[..job.Method],input.Slice(job.Method,job.Path),
                        input.Slice(job.Method+job.Path,job.Auth),
                        input.Slice(job.Method+job.Path+job.Auth,job.Body));
                    byte[] output=ArrayPool<byte>.Shared.Rent(reply.Body.Length);
                    reply.Body.Span.CopyTo(output);
                    batch.Add(new Completion(job.Context,new Reply(reply.Status,output.AsMemory(0,reply.Body.Length)),output));
                    ArrayPool<byte>.Shared.Return(job.Buffer);
                    Interlocked.Add(ref _inputBytes,-(job.Method+job.Path+job.Auth+job.Body));
                }
                bool committed=_db.EndBatch();
                foreach (var completion in batch)
                    _completed.Enqueue(committed ? completion : completion with { Reply=Api.Error(500,"internal server error") });
                // eventfd wakes epoll after COMMIT. No successful reply is observable sooner.
                ulong signal=1;
                Native.write(EventFd,&signal,8);
            }
        }
    }

    public bool Take(out Completion completion)
    {
        if (!_completed.TryDequeue(out completion)) return false;
        Interlocked.Decrement(ref _outstanding); return true;
    }
    public void DrainSignal()
    {
        ulong count;
        while (Native.read(EventFd,&count,8)<0 && Native.Error==Native.EINTR) { }
    }
    public void Dispose()
    {
        _stopping=true; _work.Set(); _thread.Join();
        while (_jobs.TryDequeue(out var job)) ArrayPool<byte>.Shared.Return(job.Buffer);
        while (_completed.TryDequeue(out var completed)) ArrayPool<byte>.Shared.Return(completed.Buffer);
        Native.close(EventFd); _work.Dispose();
    }
}

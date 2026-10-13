using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeedApi;

internal sealed unsafe class UringServer : IDisposable
{
    private sealed class Connection
    {
        public ulong Token;
        public byte[]? Input;
        public int Length;
        public bool Closing,Deferred,ContinueSent,Blocked;
        public long Progress=Stopwatch.GetTimestamp();
    }
    private static UringServer? _current;
    private readonly Api _api;
    private readonly AsyncWrites _writes;
    private readonly Dictionary<ulong,Connection> _connections=new(65536);
    private readonly byte[] _decoded=new byte[HttpParser.MaxBody];
    private readonly List<Connection> _blocked=[],_sweep=[];
    private readonly nint _transport;
    private long _lastSweep=Stopwatch.GetTimestamp();
    private const int HighWater=65536;

    public UringServer(string host,int port,Api api,AsyncWrites writes)
    {
        _api=api;_writes=writes;_current=this;
        _transport=UringNative.td_create(host,port,writes.EventFd,&OnData,&OnClose,&OnTick);
        if(_transport==0)throw new InvalidOperationException("io_uring transport initialization failed");
    }
    public void Run()
    {
        if(UringNative.td_run(_transport)<0)throw new InvalidOperationException("io_uring transport failed");
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static void OnData(ulong token,byte* pointer,int length)
    {
        try { _current!.Receive(token,pointer,length); }
        catch(Exception error)
        {
            Console.Error.WriteLine(error.Message);
            if(_current is { } server)UringNative.td_finish(server._transport,token);
        }
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static void OnClose(ulong token)
    {
        if(_current is { } server)server.Remove(token);
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static void OnTick()
    {
        try { _current!.Tick(); }
        catch(Exception error) { Console.Error.WriteLine(error.Message); }
    }
    private void Remove(ulong token)
    {
        if(!_connections.Remove(token,out var c))return;
        if(c.Input is not null)ArrayPool<byte>.Shared.Return(c.Input);
        c.Input=null;c.Length=0;
    }
    private bool Alive(Connection c)=>_connections.TryGetValue(c.Token,out var live) && ReferenceEquals(c,live);
    private void Receive(ulong token,byte* pointer,int length)
    {
        if(!_connections.TryGetValue(token,out var c))
        {
            if(length<=0) { UringNative.td_finish(_transport,token);return; }
            c=new Connection{Token=token};_connections.Add(token,c);
        }
        if(length<=0)
        {
            c.Closing=true;
            if(!c.Deferred)UringNative.td_finish(_transport,token);
            return;
        }
        c.Progress=Stopwatch.GetTimestamp();
        if(c.Closing)return;
        var bytes=new ReadOnlySpan<byte>(pointer,length);
        if(c.Length==0 && !c.Deferred && UringNative.td_pending(_transport,token)<HighWater)
        {
            int consumed=Process(c,bytes);
            if(consumed<bytes.Length && !c.Closing)Save(c,bytes[consumed..]);
        }
        else
        {
            Save(c,bytes);
            if(!c.Deferred && !c.Closing && UringNative.td_pending(_transport,token)<HighWater)ProcessSaved(c);
        }
        if(c.Length>0 && (c.Deferred || UringNative.td_pending(_transport,token)>=HighWater))Block(c);
    }
    private void Block(Connection c)
    {
        if(c.Blocked)return;c.Blocked=true;_blocked.Add(c);
    }
    private int Process(Connection c,ReadOnlySpan<byte> input)
    {
        int consumed=0;
        while(!input.IsEmpty && !c.Closing && !c.Deferred && UringNative.td_pending(_transport,c.Token)<HighWater)
        {
            int parsed=HttpParser.Parse(input,_decoded,out var request);
            if(parsed<0)
            {
                c.Closing=true;Reply(c,Api.Error(400,"malformed HTTP request"));return consumed+input.Length;
            }
            if(parsed==0)
            {
                if(request.ExpectContinue && !c.ContinueSent)
                {
                    var interim="HTTP/1.1 100 Continue\r\n\r\n"u8;
                    fixed(byte* p=interim)UringNative.td_send(_transport,c.Token,p,interim.Length,null,0,0);
                    c.ContinueSent=true;
                }
                break;
            }
            c.ContinueSent=false;c.Closing=request.Close || UringNative.td_pressure(_transport)!=0;
            if(request.Method.SequenceEqual("POST"u8))
            {
                if(_writes.Enqueue(c,request.Method,request.Path,request.Authorization,request.Body))c.Deferred=true;
                else Reply(c,Api.Error(503,"write queue is full"));
            }
            else Reply(c,_api.Handle(request.Method,request.Path,request.Authorization,request.Body));
            consumed+=request.Consumed;input=input[request.Consumed..];
        }
        return consumed;
    }
    private void Save(Connection c,ReadOnlySpan<byte> input)
    {
        int size=c.Length+input.Length;
        if(size>HttpParser.MaxBody+2*HttpParser.MaxHeader)
        {
            c.Closing=true;
            if(c.Deferred)UringNative.td_finish(_transport,c.Token);
            else Reply(c,Api.Error(413,"request too large"));
            return;
        }
        if(c.Input is null || c.Input.Length<size)
        {
            byte[] next=ArrayPool<byte>.Shared.Rent(Math.Max(size,4096));
            if(c.Input is not null) { c.Input.AsSpan(0,c.Length).CopyTo(next);ArrayPool<byte>.Shared.Return(c.Input); }
            c.Input=next;
        }
        input.CopyTo(c.Input.AsSpan(c.Length));c.Length=size;
    }
    private void ProcessSaved(Connection c)
    {
        if(c.Length==0 || c.Deferred || c.Closing)return;
        int used=Process(c,c.Input.AsSpan(0,c.Length));
        c.Length-=used;c.Input.AsSpan(used,c.Length).CopyTo(c.Input);
        if(c.Length==0 && c.Input is not null) { ArrayPool<byte>.Shared.Return(c.Input);c.Input=null; }
    }
    private void Reply(Connection c,Reply reply)
    {
        Span<byte> header=stackalloc byte[192];
        ReadOnlySpan<byte> status=reply.Status switch {
            200=>"HTTP/1.1 200 OK\r\n"u8,201=>"HTTP/1.1 201 Created\r\n"u8,
            400=>"HTTP/1.1 400 Bad Request\r\n"u8,401=>"HTTP/1.1 401 Unauthorized\r\n"u8,
            404=>"HTTP/1.1 404 Not Found\r\n"u8,413=>"HTTP/1.1 413 Content Too Large\r\n"u8,
            503=>"HTTP/1.1 503 Service Unavailable\r\n"u8,_=>"HTTP/1.1 500 Internal Server Error\r\n"u8};
        status.CopyTo(header);int used=status.Length;
        var fields="Content-Type: application/json\r\nContent-Length: "u8;
        fields.CopyTo(header[used..]);used+=fields.Length;
        Utf8Formatter.TryFormat(reply.Body.Length,header[used..],out int digits);used+=digits;
        var tail=c.Closing ? "\r\nConnection: close\r\n\r\n"u8 : "\r\n\r\n"u8;
        tail.CopyTo(header[used..]);used+=tail.Length;
        int rc;
        fixed(byte* hp=header)
        fixed(byte* bp=reply.Body.Span)rc=UringNative.td_send(_transport,c.Token,hp,used,bp,reply.Body.Length,c.Closing?1:0);
        if(rc<0) { c.Closing=true;UringNative.td_finish(_transport,c.Token); }
        c.Progress=Stopwatch.GetTimestamp();
    }
    private void Tick()
    {
        _writes.DrainSignal();
        while(_writes.Take(out var completion))
        {
            var c=(Connection)completion.Context;
            if(Alive(c))
            {
                c.Deferred=false;Reply(c,completion.Reply);
                ProcessSaved(c);
            }
            ArrayPool<byte>.Shared.Return(completion.Buffer);
        }
        for(int i=_blocked.Count-1;i>=0;i--)
        {
            var c=_blocked[i];
            if(Alive(c) && !c.Deferred && !c.Closing && UringNative.td_pending(_transport,c.Token)<HighWater)ProcessSaved(c);
            if(!Alive(c) || c.Length==0 || c.Closing || (!c.Deferred && UringNative.td_pending(_transport,c.Token)<HighWater))
            {
                c.Blocked=false;_blocked.RemoveAt(i);
            }
        }
        if(Stopwatch.GetElapsedTime(_lastSweep).TotalSeconds<5)return;
        _sweep.Clear();
        foreach(var c in _connections.Values)
            if(Stopwatch.GetElapsedTime(c.Progress).TotalSeconds>((c.Length>0 || UringNative.td_pending(_transport,c.Token)>0)?30:130))_sweep.Add(c);
        foreach(var c in _sweep) { c.Closing=true;UringNative.td_finish(_transport,c.Token); }
        _lastSweep=Stopwatch.GetTimestamp();
    }
    public void Dispose()
    {
        UringNative.td_destroy(_transport);
        foreach(var c in _connections.Values)if(c.Input is not null)ArrayPool<byte>.Shared.Return(c.Input);
        _connections.Clear();_current=null;
    }
}
internal static unsafe partial class UringNative
{
    [LibraryImport("libtwelve_transport",StringMarshalling=StringMarshalling.Utf8)]
    internal static partial nint td_create(string host,int port,int writes,
        delegate* unmanaged[Cdecl]<ulong,byte*,int,void> data,
        delegate* unmanaged[Cdecl]<ulong,void> close,
        delegate* unmanaged[Cdecl]<void> tick);
    [LibraryImport("libtwelve_transport")] internal static partial int td_run(nint transport);
    [LibraryImport("libtwelve_transport")] internal static partial int td_pending(nint transport,ulong token);
    [LibraryImport("libtwelve_transport")] internal static partial int td_pressure(nint transport);
    [LibraryImport("libtwelve_transport")] internal static partial int td_send(nint transport,ulong token,byte* header,int hlen,byte* body,int blen,int close);
    [LibraryImport("libtwelve_transport")] internal static partial void td_finish(nint transport,ulong token);
    [LibraryImport("libtwelve_transport")] internal static partial void td_destroy(nint transport);
}

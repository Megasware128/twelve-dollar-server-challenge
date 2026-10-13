using System.Runtime.InteropServices;

namespace FeedApi;

internal static unsafe partial class Native
{
    internal const int AF_INET=2, SOCK_STREAM=1, SOCK_NONBLOCK=2048, SOCK_CLOEXEC=524288;
    internal const int SOL_SOCKET=1, SO_REUSEADDR=2, IPPROTO_TCP=6, TCP_NODELAY=1;
    internal const int ADD=1, DEL=2, MOD=3;
    internal const uint IN=1, OUT=4, ERR=8, HUP=16, RDHUP=8192;
    internal const int EAGAIN=11, EINTR=4, MSG_NOSIGNAL=16384;
    [StructLayout(LayoutKind.Sequential,Pack=1)]
    internal struct Event { public uint Events; public ulong Data; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Address { public ushort Family,Port; public uint Ip; public ulong Zero; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Iovec { public void* Base; public nuint Length; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public void* Name; public uint NameLength;
        public Iovec* Vectors; public nuint VectorCount;
        public void* Control; public nuint ControlLength; public int Flags;
    }
    [LibraryImport("libc",SetLastError=true)] internal static partial int socket(int domain,int type,int protocol);
    [LibraryImport("libc",SetLastError=true)] internal static partial int bind(int fd,Address* address,uint length);
    [LibraryImport("libc",SetLastError=true)] internal static partial int listen(int fd,int backlog);
    [LibraryImport("libc",SetLastError=true)] internal static partial int accept4(int fd,void* address,void* length,int flags);
    [LibraryImport("libc",SetLastError=true)] internal static partial int setsockopt(int fd,int level,int name,int* value,uint length);
    [LibraryImport("libc",SetLastError=true)] internal static partial nint recv(int fd,byte* buffer,nuint length,int flags);
    [LibraryImport("libc",SetLastError=true)] internal static partial nint send(int fd,byte* buffer,nuint length,int flags);
    [LibraryImport("libc",SetLastError=true)] internal static partial nint sendmsg(int fd,Message* message,int flags);
    [LibraryImport("libc",SetLastError=true)] internal static partial int close(int fd);
    [LibraryImport("libc",SetLastError=true)] internal static partial int eventfd(uint initial,int flags);
    [LibraryImport("libc",SetLastError=true)] internal static partial nint read(int fd,void* buffer,nuint length);
    [LibraryImport("libc",SetLastError=true)] internal static partial nint write(int fd,void* buffer,nuint length);
    [LibraryImport("libc",SetLastError=true)] internal static partial int epoll_create1(int flags);
    [LibraryImport("libc",SetLastError=true)] internal static partial int epoll_ctl(int epoll,int operation,int fd,Event* ev);
    [LibraryImport("libc",SetLastError=true)] internal static partial int epoll_wait(int epoll,Event* events,int count,int timeout);
    internal static int Error=>Marshal.GetLastPInvokeError();
}

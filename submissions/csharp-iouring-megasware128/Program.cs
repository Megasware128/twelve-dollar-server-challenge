using System.Text;
using FeedApi;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux epoll is required");
// Each SQLite connection has one owner; the checkpoint connection runs on its own thread.
// Configure before the first connection opens.
SQLitePCL.Batteries_V2.Init();
if (SQLitePCL.raw.sqlite3_config(SQLitePCL.raw.SQLITE_CONFIG_MULTITHREAD) != SQLitePCL.raw.SQLITE_OK ||
    SQLitePCL.raw.sqlite3_config(SQLitePCL.raw.SQLITE_CONFIG_MEMSTATUS,0) != SQLitePCL.raw.SQLITE_OK)
    throw new InvalidOperationException("SQLite process configuration failed");
string path = Environment.GetEnvironmentVariable("SQLITE_PATH") ??
    throw new InvalidOperationException("SQLITE_PATH is required");
using var db = new Db(path,checkpoint:false);
using var writeDb = new Db(path);
var secret = Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("JWT_SECRET") ??
    throw new InvalidOperationException("JWT_SECRET is required"));
string host = Environment.GetEnvironmentVariable("HOST") ?? "127.0.0.1";
int port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "3000");
using var writes = new AsyncWrites(writeDb,secret);
using var server = new UringServer(host,port,new Api(db,secret),writes);
Console.WriteLine($"C# io_uring listening on {host}:{port}");
server.Run();

using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace FeedApi;

internal enum LikeOutcome { Liked, AlreadyLiked, PostMissing }

// One connection, serialized access. Statements cache plans only; every call steps SQLite.
internal sealed class Db : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    private readonly List<sqlite3_stmt> _statements = [];
    private readonly sqlite3_stmt _feed, _post, _insertPost, _insertLike, _postExists, _selectOne;
    public bool InBatch { get; private set; }
    private readonly ManualResetEventSlim _stopping = new(false);
    private Thread? _checkpointThread;

    public void BeginBatch()
    {
        if (InBatch) return;
        Exec("BEGIN IMMEDIATE");
        InBatch = true;
    }

    public bool EndBatch()
    {
        if (!InBatch) return true;
        try { Exec("COMMIT"); return true; }
        catch
        {
            try { Exec("ROLLBACK"); } catch { }
            return false;
        }
        finally { InBatch = false; }
    }

    public Db(string path, bool checkpoint=true)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        try
        {
            _conn.Open();
            Exec("PRAGMA journal_mode=WAL");
            Exec("PRAGMA synchronous=NORMAL");
            Exec("PRAGMA wal_autocheckpoint=0");
            Exec("PRAGMA journal_size_limit=67108864");
            Check(raw.sqlite3_busy_timeout(_conn.Handle!, 5000));
            Exec("PRAGMA mmap_size=1073741824");
            // mmap serves unchanged pages; keep a bounded cache for WAL/dirty pages.
            Exec("PRAGMA cache_size=-32768");
            Exec("PRAGMA temp_store=MEMORY");
            Exec("PRAGMA foreign_keys=ON");
            _feed = Prepare("""
                SELECT '{"posts":[' ||
                       coalesce(group_concat(object ORDER BY created_at DESC,id DESC), '') || ']}'
                FROM (
                  SELECT p.id, p.created_at,
                         json_object('id',p.id,'body',p.body,'created_at',p.created_at,'author',u.username,
                           'like_count',(SELECT count(*) FROM likes l WHERE l.post_id=p.id)) AS object
                    FROM posts p JOIN users u ON u.id=p.user_id
                   ORDER BY p.created_at DESC,p.id DESC LIMIT 20
                )
                """);
            _post = Prepare("""
                SELECT '{"post":' ||
                       json_object('id',p.id,'body',p.body,'created_at',p.created_at,'author',u.username,
                         'like_count',(SELECT count(*) FROM likes l WHERE l.post_id=p.id)) || '}'
                  FROM posts p JOIN users u ON u.id=p.user_id WHERE p.id=?1
                """);
            _insertPost = Prepare("INSERT INTO posts (user_id,body) VALUES (?1,?2) RETURNING id,created_at");
            _insertLike = Prepare("""
                INSERT INTO likes (user_id,post_id)
                SELECT ?1,?2 WHERE EXISTS (SELECT 1 FROM posts WHERE id=?2)
                ON CONFLICT (user_id,post_id) DO NOTHING
                """);
            _postExists = Prepare("SELECT 1 FROM posts WHERE id=?1");
            _selectOne = Prepare("SELECT 1");
            if (checkpoint) _checkpointThread = new Thread(() => Checkpoint(path))
            {
                IsBackground = true, Name = "SQLite checkpoint",
            };
            _checkpointThread?.Start();
        }
        catch { Dispose(); throw; }
    }

    private void Check(int rc)
    {
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"SQLite ({rc}): {raw.sqlite3_errmsg(_conn.Handle!).utf8_to_string()}");
    }

    private void Checkpoint(string path)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
            }.ToString());
            connection.Open();
            using (var setup = connection.CreateCommand())
            {
                setup.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=0; PRAGMA cache_size=500; PRAGMA wal_autocheckpoint=0";
                setup.ExecuteNonQuery();
            }
            while (!_stopping.Wait(200))
            {
                int rc = raw.sqlite3_wal_checkpoint_v2(connection.Handle!, "main",
                    raw.SQLITE_CHECKPOINT_PASSIVE, out _, out _);
                if (rc != raw.SQLITE_OK && rc != raw.SQLITE_BUSY)
                    Console.Error.WriteLine($"Checkpoint SQLite ({rc})");
            }
        }
        catch (Exception error) { Console.Error.WriteLine("Checkpoint: " + error.Message); }
    }

    private void Exec(string sql) => Check(raw.sqlite3_exec(_conn.Handle!, sql));

    private sqlite3_stmt Prepare(string sql)
    {
        int rc = raw.sqlite3_prepare_v2(_conn.Handle!, sql, out var stmt);
        if (rc != raw.SQLITE_OK) { stmt?.Dispose(); Check(rc); }
        if (stmt is null) throw new InvalidOperationException("SQLite prepared no statement");
        _statements.Add(stmt);
        return stmt;
    }

    private bool Read(sqlite3_stmt stmt)
    {
        int rc = raw.sqlite3_step(stmt);
        if (rc == raw.SQLITE_ROW) return true;
        if (rc != raw.SQLITE_DONE) Check(rc);
        return false;
    }

    // Reset even on errors. Clear bindings so a large previous body isn't retained.
    private static void Reset(sqlite3_stmt stmt)
    {
        raw.sqlite3_reset(stmt);
        raw.sqlite3_clear_bindings(stmt);
    }

    public void WriteFeed(PooledJson json)
    {
        lock (_gate)
        {
            try
            {
                if (!Read(_feed)) throw new InvalidOperationException("Feed returned no row");
                // Copy while SQLite owns the row. Never retain its span past reset.
                json.Buffer.Write(raw.sqlite3_column_blob(_feed, 0));
            }
            finally { Reset(_feed); }
        }
    }

    public bool WritePost(PooledJson json, long id)
    {
        lock (_gate)
        {
            try
            {
                Check(raw.sqlite3_bind_int64(_post, 1, id));
                if (!Read(_post)) return false;
                json.Buffer.Write(raw.sqlite3_column_blob(_post, 0));
                return true;
            }
            finally { Reset(_post); }
        }
    }

    public (long Id, string CreatedAt) InsertPost(long userId, string body)
    {
        lock (_gate)
        {
            try
            {
                Check(raw.sqlite3_bind_int64(_insertPost, 1, userId));
                Check(raw.sqlite3_bind_text(_insertPost, 2, body));
                if (!Read(_insertPost)) throw new InvalidOperationException("INSERT returned no row");
                long id = raw.sqlite3_column_int64(_insertPost, 0);
                string createdAt = Encoding.UTF8.GetString(raw.sqlite3_column_blob(_insertPost, 1));
                // SQLITE_DONE completes autocommit. Never acknowledge merely receiving RETURNING.
                if (Read(_insertPost)) throw new InvalidOperationException("INSERT returned extra row");
                return (id, createdAt);
            }
            finally { Reset(_insertPost); }
        }
    }

    public LikeOutcome Like(long userId, long postId)
    {
        lock (_gate)
        {
            try
            {
                Check(raw.sqlite3_bind_int64(_insertLike, 1, userId));
                Check(raw.sqlite3_bind_int64(_insertLike, 2, postId));
                if (Read(_insertLike)) throw new InvalidOperationException("Unexpected like row");
                if (raw.sqlite3_changes(_conn.Handle!) == 1) return LikeOutcome.Liked;
                Check(raw.sqlite3_bind_int64(_postExists, 1, postId));
                return Read(_postExists) ? LikeOutcome.AlreadyLiked : LikeOutcome.PostMissing;
            }
            finally { Reset(_insertLike); Reset(_postExists); }
        }
    }

    public string? Health()
    {
        try
        {
            lock (_gate)
            {
                try
                {
                    if (!Read(_selectOne)) throw new InvalidOperationException("SELECT 1 returned no row");
                }
                finally { Reset(_selectOne); }
            }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public void Dispose()
    {
        _stopping.Set();
        _checkpointThread?.Join();
        foreach (var stmt in _statements) stmt.Dispose();
        _statements.Clear();
        _conn.Dispose();
        _stopping.Dispose();
    }
}

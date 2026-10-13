using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace FeedApi;

internal readonly record struct Reply(int Status, ReadOnlyMemory<byte> Body);

internal sealed class Api(Db db, byte[] secret)
{
    private readonly PooledJson _json = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private const string Whitespace = "\t\n\v\f\r \u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
    private static readonly ConcurrentDictionary<string,ReadOnlyMemory<byte>> Errors = new(StringComparer.Ordinal);
    public static Reply Error(int status,string message)
    {
        // Only constant application errors; no database result or token is remembered.
        var bytes=Errors.GetOrAdd(message,static text=>Encoding.UTF8.GetBytes("{\"error\":\""+text+"\"}"));
        return new Reply(status,bytes);
    }

    public Reply Handle(ReadOnlySpan<byte> method,ReadOnlySpan<byte> path,
                        ReadOnlySpan<byte> authorization,ReadOnlySpan<byte> body)
    {
        _json.Reset();
        try
        {
            if (method.SequenceEqual("GET"u8))
            {
                if (path.SequenceEqual("/feed"u8)) { db.WriteFeed(_json); return Result(200); }
                if (path.SequenceEqual("/health"u8))
                {
                    string? error=db.Health();
                    var w=_json.Writer;
                    w.WriteStartObject();
                    w.WriteString("status",error is null ? "ok" : "degraded");
                    w.WriteString("db",error is null ? "ok" : "unreachable");
                    if (error is null) w.WriteNumber("uptime_s",(long)Stopwatch.GetElapsedTime(_started).TotalSeconds);
                    else w.WriteString("error",error);
                    w.WriteEndObject();w.Flush();
                    return Result(error is null ? 200 : 503);
                }
                if (path.StartsWith("/posts/"u8) && path.Length>7 && !path[7..].Contains((byte)'/'))
                {
                    if (!Id(path[7..],out long id,out bool overflow)) return Error(400,"invalid post id");
                    if (overflow || !db.WritePost(_json,id)) return Error(404,"post not found");
                    return Result(200);
                }
            }
            else if (method.SequenceEqual("POST"u8))
            {
                bool create=path.SequenceEqual("/posts"u8);
                bool like=path.Length>12 && path.StartsWith("/posts/"u8) && path.EndsWith("/like"u8)
                          && !path[7..^5].Contains((byte)'/');
                if (create || like)
                {
                    var auth=Jwt.Verify(authorization,secret);
                    if (auth.Status!=AuthStatus.Ok)
                        return Error(401,auth.Status switch {
                            AuthStatus.MissingToken=>"missing bearer token",
                            AuthStatus.BadPayload=>"invalid token payload",
                            _=>"invalid or expired token"});
                    if (like)
                    {
                        if (!Id(path[7..^5],out long id,out bool overflow)) return Error(400,"invalid post id");
                        if (overflow) return Error(404,"post not found");
                        db.BeginBatch();
                        var outcome=db.Like(auth.UserId,id);
                        if (outcome==LikeOutcome.PostMissing) return Error(404,"post not found");
                        var w=_json.Writer;
                        w.WriteStartObject();w.WriteBoolean("liked",true);
                        w.WriteBoolean("already_liked",outcome==LikeOutcome.AlreadyLiked);
                        w.WriteNumber("post_id",id);w.WriteEndObject();w.Flush();
                        return Result(outcome==LikeOutcome.Liked ? 201 : 200);
                    }
                    string text;
                    try
                    {
                        using var doc=JsonDocument.Parse(body.ToArray());
                        if (doc.RootElement.ValueKind!=JsonValueKind.Object ||
                            !doc.RootElement.TryGetProperty("body",out var value) || value.ValueKind!=JsonValueKind.String)
                            return Error(400,"body is required");
                        var raw=value.GetString()!;
                        var trimmed=raw.AsSpan().Trim(Whitespace);
                        text=trimmed.Length==raw.Length ? raw : new string(trimmed);
                        if (text.Length==0) return Error(400,"body is required");
                        if (text.Length>500)
                        {
                            int count=0;
                            foreach(var rune in text.EnumerateRunes()) if (++count>500) return Error(400,"body must be at most 500 characters");
                        }
                    }
                    catch(JsonException) { return Error(400,"malformed JSON body"); }
                    db.BeginBatch();
                    var (postId,createdAt)=db.InsertPost(auth.UserId,text);
                    var writer=_json.Writer;
                    writer.WriteStartObject();writer.WriteStartObject("post");
                    writer.WriteNumber("id",postId);writer.WriteString("body",text);
                    writer.WriteString("created_at",createdAt);writer.WriteString("author",auth.Username);
                    writer.WriteNumber("like_count",0);writer.WriteEndObject();writer.WriteEndObject();writer.Flush();
                    return Result(201);
                }
            }
            return Error(404,"not found");
        }
        catch(Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return Error(500,"internal server error");
        }
    }
    private Reply Result(int status)=>new(status,_json.Buffer.WrittenMemory);
    private static bool Id(ReadOnlySpan<byte> text,out long id,out bool overflow)
    {
        id=0;overflow=false;
        if (text.IsEmpty) return false;
        ulong value=0;
        foreach(byte c in text)
        {
            if (c<'0' || c>'9') return false;
            if (!overflow)
            {
                value=value*10+(uint)(c-'0');
                if (value>long.MaxValue) overflow=true;
            }
        }
        if (overflow) return true;
        id=(long)value;
        return id>0;
    }
}

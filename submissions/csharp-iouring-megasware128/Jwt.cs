using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace FeedApi;

internal enum AuthStatus
{
    Ok,
    MissingToken,   // no Authorization header, or it doesn't start with "Bearer "
    InvalidToken,   // bad signature, expired, not HS256, or malformed JWT
    BadPayload,     // valid JWT, but sub isn't a positive integer string or username isn't a string
}

internal readonly struct AuthResult(AuthStatus status, long userId, string username)
{
    public AuthStatus Status { get; } = status;
    public long UserId { get; } = userId;
    public string Username { get; } = username;
}

/// <summary>
/// Hand-rolled HS256 verification, run on every authenticated request (rule 5: no remembered
/// verifications). Stackalloc buffers, a thread-static HMAC, a hand-rolled base64url decoder
/// and a Utf8JsonReader scan of the payload: no per-request allocations except the username.
/// </summary>
internal static class Jwt
{
    private const int MaxSegmentChars = 1024;

    [ThreadStatic] private static HMACSHA256? _hmac;

    public static AuthResult Verify(ReadOnlySpan<byte> authorization, byte[] secret)
    {
        if (!authorization.StartsWith("Bearer "u8))
            return new AuthResult(AuthStatus.MissingToken, 0, "");
        // Tokens are ASCII. Decode into a bounded stack span instead of allocating a string.
        if (authorization.Length - 7 > 2048) return Invalid;
        Span<char> tokenBuffer = stackalloc char[authorization.Length - 7];
        for (int i = 0; i < tokenBuffer.Length; i++)
        {
            if (authorization[i + 7] > 127) return Invalid;
            tokenBuffer[i] = (char)authorization[i + 7];
        }
        ReadOnlySpan<char> token = tokenBuffer;
        int dot1 = token.IndexOf('.');
        if (dot1 < 0) return Invalid;
        int nextDot = token[(dot1 + 1)..].IndexOf('.');
        if (nextDot < 0) return Invalid;
        int dot2 = dot1 + 1 + nextDot;
        if (token[(dot2 + 1)..].IndexOf('.') >= 0) return Invalid;

        // Header: must be a JSON object with alg == HS256 (checked before the signature,
        // exactly like the reference implementation: alg=none is "invalid", never verified).
        Span<byte> head = stackalloc byte[512];
        if (!TryB64UrlDecode(token[..dot1], head, out int headLen)) return Invalid;
        if (!IsHs256Header(head[..headLen])) return Invalid;

        // Signature over the ASCII bytes "header.payload" exactly as they appear in the token.
        if (dot2 > MaxSegmentChars) return Invalid;
        Span<byte> signed = stackalloc byte[dot2];
        for (int i = 0; i < dot2; i++)
        {
            char c = token[i];
            bool ok = c == '.' || c == '-' || c == '_'
                || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (!ok) return Invalid;
            signed[i] = (byte)c;
        }
        Span<byte> sig = stackalloc byte[48];
        if (!TryB64UrlDecode(token[(dot2 + 1)..], sig, out int sigLen) || sigLen != 32) return Invalid;

        var hmac = _hmac ??= new HMACSHA256(secret);
        hmac.Initialize();
        Span<byte> digest = stackalloc byte[32];
        hmac.TryComputeHash(signed, digest, out _);
        if (!CryptographicOperations.FixedTimeEquals(sig[..32], digest)) return Invalid;

        Span<byte> payload = stackalloc byte[768];
        if (!TryB64UrlDecode(token[(dot1 + 1)..dot2], payload, out int payloadLen))
            return Invalid;
        return ParsePayload(payload[..payloadLen]);
    }

    private static AuthResult Invalid => new(AuthStatus.InvalidToken, 0, "");
    private static AuthResult BadPayload => new(AuthStatus.BadPayload, 0, "");

    private static bool IsHs256Header(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            bool hs256 = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) return hs256 && !reader.Read();
                if (reader.TokenType != JsonTokenType.PropertyName) return false;
                if (reader.ValueTextEquals("alg"u8))
                {
                    hs256 = reader.Read()
                        && reader.TokenType == JsonTokenType.String
                        && reader.ValueTextEquals("HS256"u8);
                    reader.Skip();
                    continue;
                }
                if (!reader.Read()) return false;
                reader.Skip();
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AuthResult ParsePayload(ReadOnlySpan<byte> json)
    {
        long userId = 0;
        string username = "";
        bool sawSub = false, sawUsername = false, sawExp = false, ended = false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return Invalid;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    ended = true;
                    if (reader.Read()) return Invalid;
                    break;
                }
                if (reader.TokenType != JsonTokenType.PropertyName) return Invalid;

                bool isExp = reader.ValueTextEquals("exp"u8);
                bool isNbf = !isExp && reader.ValueTextEquals("nbf"u8);
                bool isSub = !isExp && !isNbf && reader.ValueTextEquals("sub"u8);
                bool isUsername = !isExp && !isNbf && !isSub && reader.ValueTextEquals("username"u8);

                if (!reader.Read()) return Invalid;
                var kind = reader.TokenType;

                if (isExp || isNbf)
                {
                    if (kind != JsonTokenType.Number) return Invalid;
                    double moment = reader.GetDouble();
                    if (!double.IsFinite(moment)) return Invalid;
                    if (isExp) sawExp = true;
                    bool outOfWindow = isExp ? now >= moment : now < moment;
                    if (outOfWindow) return Invalid;
                }
                else if (isSub)
                {
                    sawSub = kind == JsonTokenType.String && TryParseSub(reader.GetString(), out userId);
                    if (!sawSub) reader.Skip();
                }
                else if (isUsername)
                {
                    sawUsername = kind == JsonTokenType.String;
                    if (sawUsername) username = reader.GetString()!;
                    else reader.Skip();
                }
                else
                {
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            return Invalid;
        }

        if (!ended || !sawExp) return Invalid;
        if (!sawSub || !sawUsername) return BadPayload;
        return new AuthResult(AuthStatus.Ok, userId, username);
    }

    /// A positive integer in plain decimal digits that fits SQLite's INTEGER range.
    private static bool TryParseSub(string? sub, out long userId)
    {
        userId = 0;
        if (string.IsNullOrEmpty(sub)) return false;
        foreach (var c in sub)
        {
            if (c < '0' || c > '9') return false;
        }
        if (!long.TryParse(sub, NumberStyles.None, CultureInfo.InvariantCulture, out userId)) return false;
        return userId > 0;
    }

    /// Base64url without padding; rejects any other character (like the reference's regex).
    private static bool TryB64UrlDecode(ReadOnlySpan<char> src, Span<byte> dst, out int written)
    {
        written = 0;
        int len = src.Length;
        if (len == 0 || (len & 3) == 1) return false;
        int expected = (len / 4) * 3 + (len % 4 == 2 ? 1 : len % 4 == 3 ? 2 : 0);
        if (expected > dst.Length) return false;

        int acc = 0, bits = 0, o = 0;
        for (int i = 0; i < len; i++)
        {
            int v = src[i] switch
            {
                >= 'A' and <= 'Z' => src[i] - 'A',
                >= 'a' and <= 'z' => src[i] - 'a' + 26,
                >= '0' and <= '9' => src[i] - '0' + 52,
                '-' => 62,
                '_' => 63,
                _ => -1,
            };
            if (v < 0) return false;
            acc = (acc << 6) | v;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                dst[o++] = (byte)(acc >> bits);
            }
        }
        written = o;
        return true;
    }
}

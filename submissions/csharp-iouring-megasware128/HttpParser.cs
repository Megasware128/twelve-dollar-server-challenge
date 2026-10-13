using System.Buffers.Text;

namespace FeedApi;

internal ref struct Request
{
    public ReadOnlySpan<byte> Method,Path,Authorization,Body;
    public int Consumed;
    public bool Close,ExpectContinue;
}

internal static class HttpParser
{
    internal const int MaxHeader=16384, MaxBody=1<<20;
    // 0: incomplete; 1: complete; -1: invalid framing.
    public static int Parse(ReadOnlySpan<byte> input,Span<byte> decoded,out Request request)
    {
        request=default;
        int end=input.IndexOf("\r\n\r\n"u8);
        if (end<0) return input.Length>MaxHeader ? -1 : 0;
        if (end>MaxHeader) return -1;
        int lineEnd=input.IndexOf("\r\n"u8);
        var line=input[..lineEnd];
        int first=line.IndexOf((byte)' '), last=line.LastIndexOf((byte)' ');
        if (first<=0 || last<=first+1) return -1;
        request.Method=line[..first];
        request.Path=line[(first+1)..last];
        var version=line[(last+1)..];
        if (!version.SequenceEqual("HTTP/1.1"u8) && !version.SequenceEqual("HTTP/1.0"u8)) return -1;
        request.Close=version.SequenceEqual("HTTP/1.0"u8);
        int length=-1;
        bool chunked=false,host=false,sawTransfer=false;
        int at=lineEnd+2;
        while (at<end)
        {
            int n=input[at..].IndexOf("\r\n"u8);
            if (n<0) return -1;
            line=input.Slice(at,n);at+=n+2;
            int colon=line.IndexOf((byte)':');
            if (colon<=0 || line[..colon].Contains((byte)' ') || line[0]=='\t') return -1;
            var name=line[..colon];var value=Trim(line[(colon+1)..]);
            if (Equal(name,"content-length"u8))
            {
                if (value.IsEmpty || !Utf8Parser.TryParse(value,out int parsed,out int used) || used!=value.Length || parsed<0 || parsed>MaxBody) return -1;
                // Reject signs and conflicting lengths.
                foreach(byte c in value) if(c<'0' || c>'9') return -1;
                if (length>=0 && length!=parsed) return -1;
                length=parsed;
            }
            else if (Equal(name,"transfer-encoding"u8))
            {
                if (sawTransfer || !Equal(value,"chunked"u8)) return -1;
                sawTransfer=true;chunked=true;
            }
            else if (Equal(name,"authorization"u8)) request.Authorization=value;
            else if (Equal(name,"connection"u8))
            {
                if (HasToken(value,"close"u8)) request.Close=true;
                else if (HasToken(value,"keep-alive"u8)) request.Close=false;
            }
            else if (Equal(name,"expect"u8)) request.ExpectContinue=Equal(value,"100-continue"u8);
            else if (Equal(name,"host"u8)) host=!value.IsEmpty;
        }
        if (!host && version.SequenceEqual("HTTP/1.1"u8)) return -1;
        if (chunked && length>=0) return -1;
        at=end+4;
        if (!chunked)
        {
            if (length<0) length=0;
            if (input.Length-at<length) return 0;
            request.Body=input.Slice(at,length);
            request.Consumed=at+length;
            return 1;
        }
        int written=0;
        while (true)
        {
            int n=input[at..].IndexOf("\r\n"u8);
            if (n<0) return input.Length-at>1024 ? -1 : 0;
            var size=input.Slice(at,n);
            int semicolon=size.IndexOf((byte)';');
            if (semicolon>=0) size=size[..semicolon];
            if (size.IsEmpty || size.Length>8 || !Utf8Parser.TryParse(size,out uint chunk,out int used,'x') || used!=size.Length || chunk>MaxBody) return -1;
            at+=n+2;
            if(chunk==0)
            {
                // Trailer fields finish with an empty line.
                while (true)
                {
                    n=input[at..].IndexOf("\r\n"u8);
                    if(n<0) return input.Length-at>MaxHeader ? -1 : 0;
                    if(n==0)
                    {
                        request.Body=decoded[..written];request.Consumed=at+2;return 1;
                    }
                    var trailer=input.Slice(at,n);
                    if (trailer.IndexOf((byte)':')<=0) return -1;
                    at+=n+2;
                }
            }
            if (written+(int)chunk>MaxBody) return -1;
            if (input.Length-at<(int)chunk+2) return 0;
            if (!input.Slice(at+(int)chunk,2).SequenceEqual("\r\n"u8)) return -1;
            input.Slice(at,(int)chunk).CopyTo(decoded[written..]);
            written+=(int)chunk;at+=(int)chunk+2;
        }
    }
    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
    {
        while(!value.IsEmpty && (value[0]==' ' || value[0]=='\t')) value=value[1..];
        while(!value.IsEmpty && (value[^1]==' ' || value[^1]=='\t')) value=value[..^1];
        return value;
    }
    private static bool HasToken(ReadOnlySpan<byte> value,ReadOnlySpan<byte> token)
    {
        while(!value.IsEmpty)
        {
            int comma=value.IndexOf((byte)',');
            if(comma<0) return Equal(Trim(value),token);
            if(Equal(Trim(value[..comma]),token)) return true;
            value=value[(comma+1)..];
        }
        return false;
    }
    private static bool Equal(ReadOnlySpan<byte> a,ReadOnlySpan<byte> b)
    {
        if(a.Length!=b.Length) return false;
        for(int i=0;i<a.Length;i++)
        {
            byte c=a[i];if(c>='A' && c<='Z') c+=32;
            if(c!=b[i]) return false;
        }
        return true;
    }
}

#!/usr/bin/env python3
"""Transport regressions against a running server; no third-party packages.
python3 verify_http.py HOST PORT JWT_SECRET
"""
import base64,hashlib,hmac,json,socket,sys,time,concurrent.futures
host=sys.argv[1];port=int(sys.argv[2]);secret=sys.argv[3].encode()
def b64(x):return base64.urlsafe_b64encode(x).rstrip(b"=")
unsigned=b64(b'{"alg":"HS256"}')+b"."+b64(json.dumps(dict(sub="1",username="transport",exp=time.time()+3600)).encode())
auth=unsigned+b"."+b64(hmac.new(secret,unsigned,hashlib.sha256).digest())
def connection():
    s=socket.create_connection((host,port),timeout=10);s.settimeout(15);return s
class Reader:
    def __init__(self,s):self.s=s;self.buffer=b""
    def response(self):
        while b"\r\n\r\n" not in self.buffer:
            data=self.s.recv(65536);assert data,"EOF in headers";self.buffer+=data
        h,self.buffer=self.buffer.split(b"\r\n\r\n",1)
        fields=dict(line.split(b":",1) for line in h.split(b"\r\n")[1:])
        length=int(next((v for k,v in fields.items() if k.lower()==b"content-length"),b"0"))
        while len(self.buffer)<length:
            data=self.s.recv(65536);assert data,"EOF in body";self.buffer+=data
        body,self.buffer=self.buffer[:length],self.buffer[length:]
        return int(h.split(b" ")[1]),json.loads(body) if body else None
def post(body,close=False):
    return b"POST /posts HTTP/1.1\r\nHost: test\r\nAuthorization: Bearer "+auth+b"\r\nContent-Length: "+str(len(body)).encode()+b"\r\n"+(b"Connection: close\r\n" if close else b"")+b"\r\n"+body
with connection() as s:
    r=Reader(s)
    payload=json.dumps(dict(body='fragmented 😀 "quote"')).encode()
    for part in post(payload):s.sendall(bytes([part]))
    status,value=r.response();assert status==201 and value["post"]["body"]=='fragmented 😀 "quote"'
with connection() as s:
    r=Reader(s)
    header=b"POST /posts HTTP/1.1\r\nHost: test\r\nAuthorization: Bearer "+auth+b"\r\nTransfer-Encoding: chunked\r\nExpect: 100-continue\r\n\r\n"
    s.sendall(header);assert r.response()==(100,None)
    body=json.dumps(dict(body="chunked 🎉")).encode()
    for offset in range(0,len(body),3):
        chunk=body[offset:offset+3]
        s.sendall(f"{len(chunk):x};test=1\r\n".encode()+chunk+b"\r\n")
    s.sendall(b"0\r\nX-Test: trailer\r\n\r\n")
    status,value=r.response();assert status==201 and value["post"]["body"]=="chunked 🎉"
with connection() as s:
    r=Reader(s)
    s.sendall(b"".join(post(json.dumps(dict(body=f"pipeline {n}")).encode(),close=n==31) for n in range(32)))
    results=[r.response() for _ in range(32)]
    assert all(status==201 for status,_ in results)
    assert [x["post"]["body"] for _,x in results]==[f"pipeline {n}" for n in range(32)]
    assert s.recv(1)==b""
with connection() as s:
    r=Reader(s)
    request=b"GET /feed HTTP/1.1\r\nHost: test\r\n\r\n"
    s.sendall(request*1500)
    time.sleep(.5)
    for _ in range(1500):
        status,feed=r.response();assert status==200 and len(feed["posts"])==20
def abandoned(n):
    with connection() as s:s.sendall(post(b'{"body":"partial"')[:-5])
with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:list(pool.map(abandoned,range(100)))
with connection() as s:
    r=Reader(s);s.sendall(b"GET /health HTTP/1.1\r\nHost: test\r\n\r\n")
    assert r.response()[0]==200
with connection() as s:
    r=Reader(s);s.sendall(b"GET /health HTTP/1.1\r\nHost: test\r\nContent-Length: 1\r\nTransfer-Encoding: chunked\r\n\r\n")
    assert r.response()[0]==400 and s.recv(1)==b""
print("Transport passed: byte fragmentation, chunked UTF-8, trailers, 100 Continue, 32 pipelined writes + close, 1500 backpressured reads, 100 abandoned uploads, conflicting framing")

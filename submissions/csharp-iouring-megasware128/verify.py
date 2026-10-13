#!/usr/bin/env python3
"""Regression checks for SQLite spans, freshness, concurrency, commits and JWT parsing.

Usage: python3 verify.py /absolute/path/to/published/FeedApi /path/to/schema.sql
Also accepts a managed .dll with dotnet on PATH. Uses Python's standard library.
"""
import base64
import concurrent.futures
import hashlib
import hmac
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

SECRET = "verification-secret"

def token(payload=None, header=None):
    header = '{"alg":"HS256"}' if header is None else header
    if payload is None:
        payload = json.dumps(dict(sub="1",username="token-name",exp=time.time()+3600))
    def b64(s): return base64.urlsafe_b64encode(s).rstrip(b"=").decode()
    unsigned = b64(header.encode())+"."+b64(payload.encode())
    return unsigned+"."+b64(hmac.new(SECRET.encode(),unsigned.encode(),hashlib.sha256).digest())

def main():
    with tempfile.TemporaryDirectory() as directory:
        db = Path(directory)/"feed.db"
        with sqlite3.connect(db) as c:
            c.executescript(Path(sys.argv[2]).read_text())
            c.execute("INSERT INTO users (id,username) VALUES (1,'db-name')")
            schema = c.execute("SELECT type,name,sql FROM sqlite_master ORDER BY name").fetchall()
        with socket.socket() as s:
            s.bind(("127.0.0.1",0))
            port = s.getsockname()[1]
        env = os.environ | dict(SQLITE_PATH=str(db),JWT_SECRET=SECRET,HOST="127.0.0.1",PORT=str(port))
        def start():
            command = ["dotnet",sys.argv[1]] if sys.argv[1].endswith(".dll") else [sys.argv[1]]
            log=Path(directory)/"server.log"
            p = subprocess.Popen(command,env=env,stdout=log.open("w"),stderr=subprocess.STDOUT)
            for _ in range(100):
                try:
                    urllib.request.urlopen(f"http://127.0.0.1:{port}/health",timeout=1)
                    return p
                except Exception:
                    if p.poll() is not None: raise AssertionError("Server exited: "+log.read_text())
                    time.sleep(.05)
            p.kill(); p.wait()
            raise AssertionError("Server not ready")
        def request(method,path,body=None,jwt=None):
            headers = {"Content-Type":"application/json"}
            if jwt is not None: headers["Authorization"] = "Bearer "+jwt
            data = None if body is None else json.dumps(body,ensure_ascii=False).encode()
            req = urllib.request.Request(f"http://127.0.0.1:{port}"+path,data=data,headers=headers,method=method)
            try: response = urllib.request.urlopen(req,timeout=15)
            except urllib.error.HTTPError as e: response=e
            with response:
                raw = response.read()
                assert response.headers["Content-Type"].startswith("application/json")
                value = json.loads(raw)
                return response.status,value
        p = start()
        try:
            assert request("GET","/feed") == (200,dict(posts=[]))
            with sqlite3.connect(db) as c:
                c.executemany("INSERT INTO posts (id,user_id,body,created_at) VALUES (?,1,'seed','2025-01-01T00:00:00.000Z')", [(n,) for n in range(1,26)])
            status,feed = request("GET","/feed")
            assert status==200 and [x["id"] for x in feed["posts"]] == list(range(25,5,-1))
            auth = token()
            for text in ['café 🎉 "quoted" \\ slash / <>& \t\n\x00 after', "😀"*500]:
                status,result = request("POST","/posts",{"body":text},auth)
                assert status==201 and result["post"]["body"]==text and result["post"]["author"]=="token-name"
                post = result["post"]["id"]
                status,read = request("GET",f"/posts/{post}")
                assert status==200 and read["post"]["body"]==text and read["post"]["author"]=="db-name"
                assert list(read["post"])==["id","body","created_at","author","like_count"]
                with sqlite3.connect(db) as c:
                    assert c.execute("SELECT body FROM posts WHERE id=?",(post,)).fetchone()[0]==text
            assert request("POST","/posts",{"body":"😀"*501},auth)[0]==400
            with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
                likes = list(pool.map(lambda _:request("POST",f"/posts/{post}/like",jwt=auth),range(64)))
            assert sum(x[0]==201 for x in likes)==1 and sum(x[0]==200 for x in likes)==63
            assert request("GET",f"/posts/{post}")[1]["post"]["like_count"]==1
            with sqlite3.connect(db) as c:
                c.execute("UPDATE posts SET body='external update' WHERE id=?",(post,))
                c.execute("DELETE FROM likes WHERE post_id=?",(post,))
            current = request("GET",f"/posts/{post}")[1]["post"]
            assert current["body"]=="external update" and current["like_count"]==0
            assert request("GET","/feed")[1]["posts"][0]==current
            missing_user = token(json.dumps(dict(sub="99999",username="x",exp=time.time()+3600)))
            assert request("POST","/posts",{"body":"bad FK"},missing_user)[0]==500
            assert request("POST",f"/posts/{post}/like",jwt=missing_user)[0]==500
            # Statements must still work after both constraint failures.
            assert request("POST",f"/posts/{post}/like",jwt=auth)[0]==201
            for payload in [
                '{"sub":"1","username":"x"}',
                '{"sub":"1","username":"x","exp":null}',
                '{"sub":"1","username":"x","exp":9999999999} trailing',
                '{"sub":"1","username":"x","exp":9999999999',
            ]:
                assert request("POST","/posts",{"body":"x"},token(payload))[0]==401
            for header in ['{"alg":"HS256"} trailing','{"alg":"HS256",','{"alg":"HS512"}']:
                assert request("POST","/posts",{"body":"x"},token(header=header))[0]==401
            # A writer waiting on SQLite must not block unrelated reads on the event loop.
            with sqlite3.connect(db) as blocker, concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
                blocker.execute("BEGIN IMMEDIATE")
                waiting=pool.submit(request,"POST","/posts",{"body":"queued write"},auth)
                time.sleep(.1)
                started=time.monotonic()
                assert request("GET",f"/posts/{post}")[0]==200
                assert time.monotonic()-started<.5
                assert not waiting.done()
                blocker.commit()
                status,value=waiting.result(timeout=10)
                assert status==201 and request("GET",f"/posts/{value['post']['id']}")[0]==200
            with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
                creates=list(pool.map(lambda n:request("POST","/posts",{"body":f"durable {n}"},auth),range(64)))
            assert all(s==201 for s,_ in creates)
            ids=[value["post"]["id"] for _,value in creates]
            assert len(set(ids))==64
            p.kill(); p.wait()  # All acknowledged writes survive abrupt process termination.
            p=start()
            for id in ids: assert request("GET",f"/posts/{id}")[0]==200
            with sqlite3.connect(db) as c:
                assert c.execute("SELECT type,name,sql FROM sqlite_master ORDER BY name").fetchall()==schema
                assert c.execute("PRAGMA journal_mode").fetchone()[0]=="wal"
                assert c.execute("PRAGMA integrity_check").fetchone()[0]=="ok"
                assert c.execute("PRAGMA foreign_key_check").fetchall()==[]
            print("Regression checks passed: Unicode/NUL, empty/tied feed, live external updates, 64 duplicate likes, FK recovery, JWT, reads during blocked write, 64 writes + SIGKILL/restart, unchanged schema")
        finally:
            p.terminate(); p.wait()

if __name__=="__main__": main()

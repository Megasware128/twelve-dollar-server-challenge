# C# / NativeAOT / io_uring / SQLite

Direct HTTP/1.1 on `HOST:PORT`; no Nginx. Linux x86-64, .NET SDK
10.0.401, .NET 10.0.12 NativeAOT, Microsoft.Data.Sqlite 10.0.12 and its
locked SQLitePCLRaw dependencies. SQLite 3.53.4 is built from its SHA-256-verified
amalgamation, replacing the bundle's `libe_sqlite3.so` in the published directory.
Ubuntu 24.04 supplies liburing 2.5. A small C bridge wraps its inline interfaces;
HTTP parsing, routing, JWT, JSON handling and SQLite access remain in C#.
Source only; build on the target. Linux 6.0+ with io_uring enabled is required.

```bash
sudo bash install.sh
bash build.sh
SQLITE_PATH=/path/to/feed.db JWT_SECRET=twelve-dollar-challenge \
  HOST=127.0.0.1 PORT=3000 bash start.sh
# From the repository root, with the verified seed available:
bash test/run.sh submissions/csharp-iouring-megasware128
python3 submissions/csharp-iouring-megasware128/verify.py \
  submissions/csharp-iouring-megasware128/bin/publish/FeedApi schema.sql
python3 submissions/csharp-iouring-megasware128/verify_http.py \
  127.0.0.1 3000 twelve-dollar-challenge
```

The service inherits its file descriptor limit and capability to bind port 80.
It does not change kernel settings or CPU affinity. `HOST` is an IPv4 address.

## Implementation

One event loop in the liburing byte transport invokes C# callbacks. Multishot
accepts install sockets directly in six sparse registered-file tables. Each table
has at most the inherited nofile limit, capped at 65,535 entries; these registered
descriptors do not consume ordinary process descriptors. At the challenge limit
the transport has room for almost 393,000 connections without changing RLIMIT.
Multishot receives use shared provided buffers, with a classic provided-buffer
fallback for kernels that refuse buffer-ring registration. HTTP parsing uses
spans and pooled fragment buffers. The native byte transport copies responses
into owned packets until asynchronous sends finish, and handles partial sends.
Partial sends, pipelining, chunked uploads, `100 Continue`, disconnects and bounded
input/output buffering are handled (16 KiB headers, 1 MiB bodies). Idle keep-alive connections live for 130 s.
The global native output queue is bounded at 128 MiB, and per-connection output
backpressure stops processing further pipelined requests at 64 KiB.
The transport checks usable system memory before accepting more sockets and
closes a completed response cleanly under pressure. Reclaimable socket slab and
CMA pages are excluded from the estimate; no host limits are changed.

Every read steps a prepared SQLite statement. SQLite constructs ordered compact
JSON; its borrowed UTF-8 result is copied before resetting the statement. There
is no cross-request response, row or token-verification cache. mmap is managed
by SQLite (1 GiB), with a bounded 32 MiB page cache per application connection.
Foreign keys remain enabled and the fixed schema is untouched.

Writes use WAL and `synchronous=NORMAL`. A dedicated worker owns the write
connection and groups queued requests in immediate transactions. Every POST
response waits until COMMIT succeeds; commit failure produces 500 responses.
The socket loop owns a separate read connection, so it can serve reads while
the writer waits for disk I/O. Reads see committed SQLite snapshots, including
acknowledged writes, and cannot expose the worker's uncommitted changes.
Requests on the same HTTP connection remain ordered. The write queue is bounded
by both 8,192 outstanding requests and 16 MiB of queued input; saturation returns 503.
SQLite's automatic checkpoint is disabled; a separate connection performs a
PASSIVE checkpoint every 200 ms. This lets disk checkpoint I/O proceed without
blocking the HTTP event loop. SQLite still owns all WAL synchronization; the
checkpoint connection has its own thread and small page cache.

SQLite uses `-O3 -march=native` and profile-guided optimization. Build-time training
uses a disposable synthetic database and the SQL/JSON read and write paths;
neither its data nor its profile is committed. Thread safety stays enabled:
the application configures MULTITHREAD mode before initialization and each
connection belongs to its own thread. The build fails if training fails.

JWT HS256 signatures and expiration are checked on every authenticated request.
JSON validation is complete; post limits count Unicode scalar values and trim
the reference implementation's whitespace set.

## Validation and measurements

The official 42 checks pass. Additional regression scripts cover external SQLite
updates, empty/tied feeds, Unicode and NUL text, concurrent duplicate likes,
foreign-key failures, SIGKILL/restart after acknowledged writes, fragmented and
chunked requests, pipelining, backpressure and abandoned uploads.

Azure measurements and their limitations are recorded in the accompanying
benchmark report. They are comparative experiments, not a DigitalOcean score
or a claim to the official first place. A raw-socket workload approximates
`bench/load.js`; only unmodified k6 runs are labeled k6 results. The scored
challenge requires a five-minute hold on the organizer's droplet.

On an Azure F1als_v7 (1 vCPU, 2 GiB, no swap), this version passed a raw-socket
150,000-user run with a 60 s ramp and 300 s hold: hold p95 5.70 ms, p99 38.16 ms,
4,762,545 requests and zero hold failures. Over ramp plus hold there were two
failures in 5,316,586 requests. The maximum was 22 s; percentiles, not maximum,
are the scoring thresholds. **This is diagnostic, not an official score:** the
Azure guest firewall's connection tracking was bypassed for private test traffic.
That exception is in the benchmark fixture, not install/build/start or server code.
The generator approximates the think-time loop and is not k6. The 300,000-user
prototype without memory-aware acceptance was OOM-killed and did not pass.

## Credits and license

Database/JSON/JWT work derives from maks-march's
[C# PR #27](https://github.com/arjaythedev/twelve-dollar-server-challenge/pull/27).
[PR #13](https://github.com/arjaythedev/twelve-dollar-server-challenge/pull/13)
informed SQLite JSON, and [PR #9](https://github.com/arjaythedev/twelve-dollar-server-challenge/pull/9)
informed mmap/cache experiments. The group-commit and connection-pressure
experiments were informed by [raoz's PR #26](https://github.com/arjaythedev/twelve-dollar-server-challenge/pull/26)
and [hendy643's PR #32](https://github.com/arjaythedev/twelve-dollar-server-challenge/pull/32).
The C# application and byte transport bridge are implemented here; the comparison load generator derives
from PR #26 and is not part of the server.

MIT under the repository's [license](../../LICENSE).

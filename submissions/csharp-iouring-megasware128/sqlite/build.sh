#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p deps/sqlite/profile
archive=deps/sqlite/sqlite.zip
curl -fsSL https://www.sqlite.org/2026/sqlite-amalgamation-3530400.zip -o "$archive"
echo "1e71ddf93849c6a6ecf58b827c0692073d2dd7ee40196158068f7b29f422e87d  $archive" | sha256sum -c -
unzip -oq "$archive" -d deps/sqlite
source=deps/sqlite/sqlite-amalgamation-3530400
flags=(-O3 -march=native -DNDEBUG -fPIC -DSQLITE_THREADSAFE=1
       -DSQLITE_DEFAULT_MEMSTATUS=0 -DSQLITE_DQS=0 -DSQLITE_ENABLE_COLUMN_METADATA)
object=deps/sqlite/profile/sqlite3.o
rm -f deps/sqlite/profile/*.gcda
cc "${flags[@]}" -fprofile-generate -fprofile-update=atomic -c "$source/sqlite3.c" -o "$object"
cc -O2 -I"$source" sqlite/train.c "$object" -fprofile-generate -lpthread -lm -o deps/sqlite/train
training=$(mktemp -d)
trap 'rm -rf "$training"' EXIT
deps/sqlite/train "$training/train.db"
cc "${flags[@]}" -fprofile-use -fprofile-correction -Wno-missing-profile -c "$source/sqlite3.c" -o "$object"
cc -shared "$object" -lpthread -lm -o bin/publish/libe_sqlite3.so

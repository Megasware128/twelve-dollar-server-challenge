/* Build-time SQLite profile. Synthetic data only; never shipped to the server. */
#include <sqlite3.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

static void exec(sqlite3 *db, const char *sql) {
    char *error = NULL;
    if (sqlite3_exec(db, sql, NULL, NULL, &error) != SQLITE_OK) {
        fprintf(stderr, "training SQL: %s\n", error);
        sqlite3_free(error); exit(1);
    }
}
static sqlite3_stmt *prepare(sqlite3 *db, const char *sql) {
    sqlite3_stmt *statement = NULL;
    if (sqlite3_prepare_v3(db, sql, -1, SQLITE_PREPARE_PERSISTENT, &statement, NULL) != SQLITE_OK) {
        fprintf(stderr, "training prepare: %s\n", sqlite3_errmsg(db)); exit(1);
    }
    return statement;
}
static uint64_t read(sqlite3 *db, sqlite3_stmt *statement) {
    uint64_t sink = 0; int rc;
    while ((rc = sqlite3_step(statement)) == SQLITE_ROW) {
        for (int col=0; col<sqlite3_column_count(statement); ++col) {
            const unsigned char *text = sqlite3_column_blob(statement,col);
            int size=sqlite3_column_bytes(statement,col);
            sink += size + (size ? text[0] : 0);
        }
    }
    if (rc != SQLITE_DONE) { fprintf(stderr,"training step: %s\n",sqlite3_errmsg(db)); exit(1); }
    sqlite3_reset(statement); return sink;
}
int main(int argc, char **argv) {
    if (argc!=2) return 2;
    sqlite3_config(SQLITE_CONFIG_MULTITHREAD);
    sqlite3_config(SQLITE_CONFIG_MEMSTATUS,0);
    sqlite3 *db=NULL;
    if (sqlite3_open(argv[1],&db)!=SQLITE_OK) return 1;
    exec(db,"PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA mmap_size=1073741824; PRAGMA cache_size=-32768; PRAGMA foreign_keys=ON;");
    exec(db,"CREATE TABLE users(id INTEGER PRIMARY KEY,username TEXT NOT NULL UNIQUE);"
        "CREATE TABLE posts(id INTEGER PRIMARY KEY,user_id INTEGER NOT NULL REFERENCES users(id),body TEXT NOT NULL,created_at TEXT NOT NULL DEFAULT(strftime('%Y-%m-%dT%H:%M:%fZ','now')));"
        "CREATE TABLE likes(user_id INTEGER NOT NULL REFERENCES users(id),post_id INTEGER NOT NULL REFERENCES posts(id),PRIMARY KEY(user_id,post_id));"
        "CREATE INDEX posts_created_at_id_idx ON posts(created_at DESC,id DESC);"
        "CREATE INDEX posts_user_id_idx ON posts(user_id); CREATE INDEX likes_post_id_idx ON likes(post_id);"
        "BEGIN; WITH RECURSIVE n(x) AS(VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000) INSERT INTO users SELECT x,'user_'||x FROM n;"
        "WITH RECURSIVE n(x) AS(VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100000) INSERT INTO posts SELECT x,1+x%1000,'Synthetic text with quotes \" and backslash \\ and café 🎉','2025-12-31T23:59:19.409Z' FROM n;"
        "WITH RECURSIVE n(x) AS(VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<400000) INSERT OR IGNORE INTO likes SELECT 1+(x*7919)%1000,1+x%100000 FROM n; COMMIT;");
    sqlite3_stmt *feed=prepare(db,
        "SELECT '{\"posts\":['||coalesce(group_concat(object ORDER BY created_at DESC,id DESC),'')||']}' FROM ("
        "SELECT p.id,p.created_at,json_object('id',p.id,'body',p.body,'created_at',p.created_at,'author',u.username,'like_count',(SELECT count(*) FROM likes l WHERE l.post_id=p.id)) AS object "
        "FROM posts p JOIN users u ON u.id=p.user_id ORDER BY p.created_at DESC,p.id DESC LIMIT 20)");
    sqlite3_stmt *post=prepare(db,"SELECT '{\"post\":'||json_object('id',p.id,'body',p.body,'created_at',p.created_at,'author',u.username,'like_count',(SELECT count(*) FROM likes l WHERE l.post_id=p.id))||'}' FROM posts p JOIN users u ON u.id=p.user_id WHERE p.id=?1");
    sqlite3_stmt *create=prepare(db,"INSERT INTO posts(user_id,body) VALUES(?1,?2) RETURNING id,created_at");
    sqlite3_stmt *like=prepare(db,"INSERT INTO likes(user_id,post_id) SELECT ?1,?2 WHERE EXISTS(SELECT 1 FROM posts WHERE id=?2) ON CONFLICT(user_id,post_id) DO NOTHING");
    uint32_t random=99; uint64_t sink=0;
    for(int n=0;n<30000;++n) {
        random=random*1664525u+1013904223u;
        sink+=read(db,feed);
        sqlite3_bind_int64(post,1,1+random%100000); sink+=read(db,post);
        if(n%100<15) {
            exec(db,"BEGIN IMMEDIATE");
            sqlite3_bind_int64(like,1,1+random%1000); sqlite3_bind_int64(like,2,1+(random/1000)%100000); sink+=read(db,like);
            if(n%100<2) {
                sqlite3_bind_int64(create,1,1+random%1000);
                sqlite3_bind_text(create,2,"Build-time training only",-1,SQLITE_STATIC); sink+=read(db,create);
            }
            exec(db,"COMMIT");
        }
    }
    sqlite3_finalize(feed);sqlite3_finalize(post);sqlite3_finalize(create);sqlite3_finalize(like);
    sqlite3_close(db); printf("SQLite profile checksum: %llu\n",(unsigned long long)sink);
    return 0;
}

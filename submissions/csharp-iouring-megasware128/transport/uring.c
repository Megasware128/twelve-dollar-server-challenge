/* A byte transport only. HTTP, authentication and SQLite remain in C#.
 * Fixed-file and provided-buffer techniques are informed by PRs #26 and #32. */
#define _GNU_SOURCE
#include <liburing.h>
#include <arpa/inet.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/mman.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <sys/eventfd.h>
#include <unistd.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <errno.h>
#include <fcntl.h>
#include <time.h>

#define NRINGS 6
#define NBUFS 512
#define BUFSIZE 16384
#define GROUP 1
enum { ACCEPT=1, RECV=2, SEND=3, CLOSE=4, IGNORE=5 };
typedef void (*data_cb)(uint64_t,const unsigned char*,int);
typedef void (*close_cb)(uint64_t);
typedef void (*tick_cb)(void);
typedef struct packet { struct packet *next; int size,offset; unsigned char bytes[]; } packet;
typedef struct connection { uint32_t gen; int live,recv_on,send_on,closing,close_on,pending; packet *head,*tail; } connection;
typedef struct ring_state { struct io_uring ring; struct io_uring_buf_ring *buffers; unsigned char *memory; int accept_on,open,ready; uint32_t accept_gen; } ring_state;
typedef struct transport {
    ring_state rings[NRINGS]; connection *connections;
    int slots,listener,notify,writes;
    int pressure;
    double memory_check;
    size_t queued;
    data_cb data; close_cb closed; tick_cb tick;
} transport;

static uint64_t tag(uint64_t token,int op) { return (token&0xffffffff00000000ull)|((token&0xffffffffull)<<3)|(unsigned)op; }
static uint64_t token_for(transport *t,unsigned id) { return ((uint64_t)t->connections[id].gen<<32)|id; }
static connection *lookup(transport *t,uint64_t token) {
    unsigned id=(unsigned)token;
    if(id>=(unsigned)(NRINGS*t->slots))return NULL;
    connection *c=&t->connections[id];
    return c->live && c->gen==(uint32_t)(token>>32) ? c : NULL;
}
static struct io_uring_sqe *sqe(ring_state *r) {
    struct io_uring_sqe *s=io_uring_get_sqe(&r->ring);
    while(!s) { if(io_uring_submit(&r->ring)<0)return NULL; s=io_uring_get_sqe(&r->ring); }
    return s;
}
static void arm_accept(transport *t,int index) {
    ring_state *r=&t->rings[index];
    if(r->accept_on || t->pressure || r->open>=t->slots-64)return;
    struct io_uring_sqe *s=sqe(r); if(!s)return;
    io_uring_prep_multishot_accept_direct(s,t->listener,NULL,NULL,0);
    s->user_data=((uint64_t)++r->accept_gen<<32)|ACCEPT; r->accept_on=1;
}
static void memory_pressure(transport *t) {
    struct timespec stamp;clock_gettime(CLOCK_MONOTONIC,&stamp);
    double now=stamp.tv_sec+stamp.tv_nsec*1e-9;if(now<t->memory_check)return;
    t->memory_check=now+.25;
    char text[4096];int fd=open("/proc/meminfo",O_RDONLY);if(fd<0)return;
    ssize_t n=read(fd,text,sizeof text-1);close(fd);if(n<=0)return;text[n]=0;
    char *a=strstr(text,"MemAvailable:"),*s=strstr(text,"SReclaimable:"),*c=strstr(text,"CmaFree:");
    if(!a)return;
    long kb=strtol(a+13,NULL,10)-(s?strtol(s+13,NULL,10):0)-(c?strtol(c+8,NULL,10):0);
    int low=kb<192*1024 ? 2 : kb<256*1024 ? 1 : kb>320*1024 ? 0 : t->pressure;
    t->pressure=low;
    if(!low)return;
    for(int i=0;i<NRINGS;i++) {
        ring_state *r=&t->rings[i];if(!r->accept_on)continue;
        struct io_uring_sqe *q=sqe(r);if(!q)continue;
        io_uring_prep_cancel64(q,((uint64_t)r->accept_gen<<32)|ACCEPT,0);
        q->user_data=IGNORE;q->flags|=IOSQE_CQE_SKIP_SUCCESS;r->accept_on=0;
    }
}
int td_pressure(transport *t) { return t->pressure!=0; }
static void arm_recv(transport *t,unsigned id) {
    connection *c=&t->connections[id]; ring_state *r=&t->rings[id/t->slots];
    if(c->recv_on || c->closing || !c->live)return;
    struct io_uring_sqe *s=sqe(r); if(!s)return;
    io_uring_prep_recv_multishot(s,id%t->slots,NULL,0,0);
    s->flags|=IOSQE_FIXED_FILE|IOSQE_BUFFER_SELECT; s->buf_group=GROUP;
    s->user_data=tag(token_for(t,id),RECV);c->recv_on=1;
}
static void recycle(ring_state *r,int bid) {
    if(r->buffers) {
        io_uring_buf_ring_add(r->buffers,r->memory+(size_t)bid*BUFSIZE,BUFSIZE,bid,NBUFS-1,0);
        io_uring_buf_ring_advance(r->buffers,1);
    } else {
        struct io_uring_sqe *s=sqe(r);if(!s)return;
        io_uring_prep_provide_buffers(s,r->memory+(size_t)bid*BUFSIZE,BUFSIZE,1,GROUP,bid);
        s->user_data=IGNORE;s->flags|=IOSQE_CQE_SKIP_SUCCESS;
    }
}
static void arm_send(transport *t,unsigned id) {
    connection *c=&t->connections[id];if(c->send_on || !c->head || c->close_on)return;
    struct io_uring_sqe *s=sqe(&t->rings[id/t->slots]);if(!s)return;
    packet *p=c->head;
    io_uring_prep_send(s,id%t->slots,p->bytes+p->offset,p->size-p->offset,MSG_NOSIGNAL);
    s->flags|=IOSQE_FIXED_FILE;s->user_data=tag(token_for(t,id),SEND);c->send_on=1;
}
static void close_ready(transport *t,unsigned id) {
    connection *c=&t->connections[id];
    if(!c->live || c->close_on || !c->closing || c->head || c->send_on)return;
    ring_state *r=&t->rings[id/t->slots];
    if(c->recv_on) {
        struct io_uring_sqe *s=sqe(r);if(!s)return;
        io_uring_prep_cancel64(s,tag(token_for(t,id),RECV),0);s->user_data=IGNORE;
        s->flags|=IOSQE_CQE_SKIP_SUCCESS;
    }
    struct io_uring_sqe *s=sqe(r);if(!s)return;
    io_uring_prep_close_direct(s,id%t->slots);s->user_data=tag(token_for(t,id),CLOSE);c->close_on=1;
}
static void drop_packets(transport *t,connection *c) {
    while(c->head) { packet *p=c->head;c->head=p->next;t->queued-=p->size;free(p); }
    c->tail=NULL;c->pending=0;
}
int td_pending(transport *t,uint64_t token) { connection *c=lookup(t,token);return c ? c->pending : -1; }
void td_finish(transport *t,uint64_t token) {
    connection *c=lookup(t,token);if(!c)return;c->closing=1;close_ready(t,(unsigned)token);
}
int td_send(transport *t,uint64_t token,const unsigned char *header,int hlen,const unsigned char *body,int blen,int closing) {
    connection *c=lookup(t,token);if(!c || c->close_on)return -1;
    int size=hlen+blen;
    if(size<0 || t->queued+(size_t)size>128*1024*1024)return -ENOMEM;
    packet *p=malloc(sizeof *p+(size_t)size);if(!p)return -ENOMEM;
    p->next=NULL;p->size=size;p->offset=0;if(hlen)memcpy(p->bytes,header,hlen);if(blen)memcpy(p->bytes+hlen,body,blen);
    if(c->tail)c->tail->next=p;else c->head=p;c->tail=p;c->pending+=size;t->queued+=size;
    if(closing)c->closing=1;arm_send(t,(unsigned)token);return 0;
}
void td_destroy(transport *t) {
    if(!t)return;
    for(int i=0;i<NRINGS;i++)if(t->rings[i].ready) {
        // queue_exit cancels requests before their buffers are released.
        io_uring_queue_exit(&t->rings[i].ring);
        if(t->rings[i].memory)munmap(t->rings[i].memory,(size_t)NBUFS*BUFSIZE);
    }
    if(t->connections) { for(int i=0;i<NRINGS*t->slots;i++)drop_packets(t,&t->connections[i]);free(t->connections); }
    if(t->listener>=0)close(t->listener);if(t->notify>=0)close(t->notify);free(t);
}
transport *td_create(const char *host,int port,int writes,data_cb data,close_cb closed,tick_cb tick) {
    transport *t=calloc(1,sizeof *t);if(!t)return NULL;
    t->listener=-1;t->notify=-1;t->writes=writes;t->data=data;t->closed=closed;t->tick=tick;
    struct rlimit limit;if(getrlimit(RLIMIT_NOFILE,&limit))goto failed;
    t->slots=limit.rlim_cur>65535 ? 65535 : (int)limit.rlim_cur;
    if(t->slots<256)goto failed;
    t->connections=calloc((size_t)NRINGS*t->slots,sizeof *t->connections);if(!t->connections)goto failed;
    t->listener=socket(AF_INET,SOCK_STREAM|SOCK_CLOEXEC,0);if(t->listener<0)goto failed;
    int one=1;setsockopt(t->listener,SOL_SOCKET,SO_REUSEADDR,&one,sizeof one);
    setsockopt(t->listener,IPPROTO_TCP,TCP_NODELAY,&one,sizeof one);
    struct sockaddr_in address={.sin_family=AF_INET,.sin_port=htons(port)};
    if(inet_pton(AF_INET,host,&address.sin_addr)!=1)goto failed;
    int bound=0;
    // Fixed-file requests can finish cancellation shortly after a killed process exits.
    for(int retry=0;retry<100;retry++) {
        if(bind(t->listener,(void*)&address,sizeof address)==0) { bound=1;break; }
        if(errno!=EADDRINUSE)break;
        usleep(50000);
    }
    if(!bound || listen(t->listener,4096))goto failed;
    t->notify=eventfd(0,EFD_NONBLOCK|EFD_CLOEXEC);if(t->notify<0)goto failed;
    for(int i=0;i<NRINGS;i++) {
        ring_state *r=&t->rings[i];struct io_uring_params parameters={.flags=IORING_SETUP_CQSIZE,.cq_entries=8192};
        int rc=io_uring_queue_init_params(2048,&r->ring,&parameters);
        if(rc<0) { fprintf(stderr,"io_uring init: %s\n",strerror(-rc));goto failed; } r->ready=1;
        rc=io_uring_register_files_sparse(&r->ring,t->slots);
        if(rc<0) { fprintf(stderr,"fixed files: %s\n",strerror(-rc));goto failed; }
        if(io_uring_register_eventfd(&r->ring,t->notify)<0)goto failed;
        r->memory=mmap(NULL,(size_t)NBUFS*BUFSIZE,PROT_READ|PROT_WRITE,MAP_PRIVATE|MAP_ANONYMOUS,-1,0);
        if(r->memory==MAP_FAILED) { r->memory=NULL;goto failed; }
        int error;r->buffers=io_uring_setup_buf_ring(&r->ring,NBUFS,GROUP,0,&error);
        if(r->buffers)for(int bid=0;bid<NBUFS;bid++)recycle(r,bid);
        else {
            struct io_uring_sqe *s=sqe(r);if(!s)goto failed;
            io_uring_prep_provide_buffers(s,r->memory,BUFSIZE,NBUFS,GROUP,0);s->user_data=IGNORE;
            struct io_uring_cqe *cqe;
            if(io_uring_submit_and_wait(&r->ring,1)<0 || io_uring_wait_cqe(&r->ring,&cqe)<0)goto failed;
            rc=cqe->res;io_uring_cqe_seen(&r->ring,cqe);if(rc<0)goto failed;
        }
        arm_accept(t,i);io_uring_submit(&r->ring);
    }
    return t;
failed:
    fprintf(stderr,"io_uring transport startup failed (errno %d)\n",errno);td_destroy(t);return NULL;
}
static void completion(transport *t,int ri,struct io_uring_cqe *q) {
    ring_state *r=&t->rings[ri];int op=q->user_data&7,res=q->res;
    if(op==IGNORE)return;
    if(op==ACCEPT) {
        if(!(q->flags&IORING_CQE_F_MORE) && (uint32_t)(q->user_data>>32)==r->accept_gen)r->accept_on=0;
        if(res<0)return;
        unsigned id=(unsigned)(ri*t->slots+res);connection *c=&t->connections[id];
        c->gen++;c->live=1;c->recv_on=c->send_on=c->closing=c->close_on=c->pending=0;c->head=c->tail=NULL;
        r->open++;arm_recv(t,id);return;
    }
    unsigned id=(unsigned)(q->user_data>>3)&0x1fffffffu;
    uint64_t token=(q->user_data&0xffffffff00000000ull)|id;
    int bid=(q->flags&IORING_CQE_F_BUFFER) ? (int)(q->flags>>IORING_CQE_BUFFER_SHIFT) : -1;
    connection *c=lookup(t,token);
    if(op==CLOSE && res>=0) {
        // The kernel may allocate the released slot before its old close CQE is consumed.
        // Notify the old managed generation even if a new accept already reused the slot.
        r->open--;if(c) { c->live=0;drop_packets(t,c); }
        t->closed(token);arm_accept(t,ri);return;
    }
    if(!c) { if(bid>=0)recycle(r,bid);return; }
    if(op==RECV) {
        if(!(q->flags&IORING_CQE_F_MORE))c->recv_on=0;
        if(res>0 && bid>=0) { t->data(token,r->memory+(size_t)bid*BUFSIZE,res); }
        else if(res==0)t->data(token,NULL,0);
        else if(res!=-ENOBUFS && res!=-ECANCELED) { c->closing=1;t->data(token,NULL,-1); }
        if(bid>=0)recycle(r,bid);
        if(res>0 || res==-ENOBUFS)arm_recv(t,id);
    } else if(op==SEND) {
        c->send_on=0;packet *p=c->head;
        if(res<=0) { drop_packets(t,c);c->closing=1; }
        else if(p) {
            p->offset+=res;c->pending-=res;
            if(p->offset>=p->size) { c->head=p->next;if(!c->head)c->tail=NULL;t->queued-=p->size;free(p); }
        }
        arm_send(t,id);close_ready(t,id);
    } else if(op==CLOSE) {
        if(res<0) { c->close_on=0;close_ready(t,id);return; }
    }
}
int td_run(transport *t) {
    struct pollfd waiters[2]={{.fd=t->notify,.events=POLLIN},{.fd=t->writes,.events=POLLIN}};
    for(;;) {
        memory_pressure(t);
        int handled=0;
        for(int i=0;i<NRINGS;i++) {
            ring_state *r=&t->rings[i];struct io_uring_cqe *q;
            for(int n=0;n<512 && io_uring_peek_cqe(&r->ring,&q)==0;n++) {
                struct io_uring_cqe copy=*q;io_uring_cqe_seen(&r->ring,q);completion(t,i,&copy);handled++;
            }
            arm_accept(t,i);
        }
        t->tick();
        for(int i=0;i<NRINGS;i++)if(io_uring_submit(&t->rings[i].ring)<0)return -1;
        if(handled>=512)continue;
        int rc=poll(waiters,2,1000);if(rc<0 && errno!=EINTR)return -1;
        uint64_t count;while(read(t->notify,&count,8)<0 && errno==EINTR) { }
    }
}

/* Fixed public DNS service probe. Sends only a DNS A question for example.com.+ * No workspace content, credentials or arbitrary target/payload are accepted. */
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <stdio.h>

static int tcp(int family, const char *address) {
    SOCKET sock=socket(family,SOCK_STREAM,IPPROTO_TCP);
    struct sockaddr_storage storage;
    int size,error=0; u_long nonblocking=1;
    fd_set write_set, error_set; struct timeval wait={3,0};
    if(sock==INVALID_SOCKET) return WSAGetLastError();
    ZeroMemory(&storage,sizeof(storage));
    if(family==AF_INET) {
        struct sockaddr_in *a=(struct sockaddr_in*)&storage;
        a->sin_family=AF_INET; a->sin_port=htons(53); inet_pton(AF_INET,address,&a->sin_addr); size=sizeof(*a);
    } else {
        struct sockaddr_in6 *a=(struct sockaddr_in6*)&storage;
        a->sin6_family=AF_INET6; a->sin6_port=htons(53); inet_pton(AF_INET6,address,&a->sin6_addr); size=sizeof(*a);
    }
    ioctlsocket(sock,FIONBIO,&nonblocking);
    if(connect(sock,(struct sockaddr*)&storage,size)!=0) {
        error=WSAGetLastError();
        if(error==WSAEWOULDBLOCK) {
            FD_ZERO(&write_set); FD_ZERO(&error_set); FD_SET(sock,&write_set); FD_SET(sock,&error_set);
            if(select(0,NULL,&write_set,&error_set,&wait)>0) {
                int length=sizeof(error); getsockopt(sock,SOL_SOCKET,SO_ERROR,(char*)&error,&length);
            } else error=WSAETIMEDOUT;
        }
    }
    closesocket(sock); return error;
}

int main(void) {
    WSADATA wsa; SOCKET sock; struct sockaddr_in target;
    int sent,received,error=0; DWORD timeout=3000;
    unsigned char answer[512];
    const unsigned char query[]={0x53,0x48,1,0,0,1,0,0,0,0,0,0,7,'e','x','a','m','p','l','e',3,'c','o','m',0,0,1,0,1};
    if(WSAStartup(MAKEWORD(2,2),&wsa)!=0) return 64;
    sock=socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);
    if(sock==INVALID_SOCKET) return 65;
    ZeroMemory(&target,sizeof(target)); target.sin_family=AF_INET; target.sin_port=htons(53);
    inet_pton(AF_INET,"223.5.5.5",&target.sin_addr);
    setsockopt(sock,SOL_SOCKET,SO_RCVTIMEO,(const char*)&timeout,sizeof(timeout));
    /* Connected UDP checks that a response originates at the selected endpoint. */
    if(connect(sock,(struct sockaddr*)&target,sizeof(target))!=0) error=WSAGetLastError();
    sent=error==0 ? send(sock,(const char*)query,sizeof(query),0) : SOCKET_ERROR;
    if(sent==SOCKET_ERROR && error==0) error=WSAGetLastError();
    received=sent==(int)sizeof(query) ? recv(sock,(char*)answer,sizeof(answer),0) : SOCKET_ERROR;
    if(received==SOCKET_ERROR && error==0) error=WSAGetLastError();
    printf("{\"tcpV4Error\":%d,\"udpError\":%d,\"dnsResponse\":%d}\n",
        tcp(AF_INET,"223.5.5.5"),error,
        received>=12 && answer[0]==0x53 && answer[1]==0x48 && (answer[2]&0x80)!=0);
    closesocket(sock); WSACleanup(); return 0;
}

#ifndef GARLIC_PS5PARAM_H
#define GARLIC_PS5PARAM_H
#include <stdint.h>
#include <stddef.h>
#include <string.h>

static uint32_t pp_u32(const uint8_t *p) { uint32_t v; memcpy(&v,p,4); return v; }
static uint16_t pp_u16(const uint8_t *p) { uint16_t v; memcpy(&v,p,2); return v; }
/* Validate all entries before exposing a field; reject duplicate keys and truncated tables. */
static uint8_t *pp_field(uint8_t *s,size_t size,const char *key,uint32_t *length) {
    if(size<20 || memcmp(s,"\0PSF",4)) return NULL;
    uint32_t keys=pp_u32(s+8),values=pp_u32(s+12),count=pp_u32(s+16);
    if(count>(size-20)/16 || keys<20+count*16 || keys>=values || values>size) return NULL;
    uint8_t *found=NULL;
    for(uint32_t i=0;i<count;i++) {
        uint8_t *e=s+20+i*16; uint32_t ko=pp_u16(e),vo=pp_u32(e+12),n=pp_u32(e+4),cap=pp_u32(e+8);
        if(ko>=values-keys || !memchr(s+keys+ko,0,values-keys-ko) || n>cap || vo>size-values || cap>size-values-vo) return NULL;
        if(!strcmp((char *)s+keys+ko,key)) { if(found)return NULL; found=s+values+vo;*length=n; }
    }
    return found;
}
static uint8_t *pp_sfo(uint8_t *p,size_t cap,uint32_t *size) {
    if(cap<20 || memcmp(p,"PS5PARAM",8) || pp_u32(p+8)!=0) return NULL;
    uint32_t off=pp_u32(p+16);*size=pp_u32(p+12);
    if(off<20 || off>cap || *size>cap-off)return NULL;
    return p+off;
}
static int pp_identity(uint8_t *s,size_t size,uint64_t *aid,uint32_t *uid,uint8_t hmac[32]) {
    uint32_t n=0;uint8_t *a=pp_field(s,size,"ACCOUNT_ID",&n);if(!a || n!=8)return -1;
    uint8_t *p=pp_field(s,size,"PARAMS",&n);if(!p || n<40)return -1;
    memcpy(aid,a,8);memcpy(uid,p+4,4);memcpy(hmac,p+8,32);
    unsigned nonzero=0;for(int i=0;i<32;i++)nonzero|=hmac[i];
    return nonzero?0:-1;
}
/* Preserve slot titles, revision, geometry, game parameters and all payload bytes. */
static int pp_patch(uint8_t *buffer,size_t cap,uint64_t aid,uint32_t uid,const uint8_t hmac[32]) {
    uint32_t size=0,n=0;uint8_t *s=pp_sfo(buffer,cap,&size);if(!s)return -1;
    uint8_t *a=pp_field(s,size,"ACCOUNT_ID",&n);if(!a || n!=8)return -1;
    uint8_t *p=pp_field(s,size,"PARAMS",&n);if(!p || n<40)return -1;
    memcpy(a,&aid,8);memcpy(p+4,&uid,4);memcpy(p+8,hmac,32);return 0;
}
#endif

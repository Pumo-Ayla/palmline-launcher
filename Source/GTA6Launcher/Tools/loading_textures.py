"""Decode the top mip of the BC1, BC3 and BC7 loading-screen atlases.

The small mips share a 4 KiB tail. Larger mips each start on a tile boundary.
"""
import numpy as np
from PIL import Image

def read_atlas(info, data):
    w,h=info['width'],info['height']
    bc={98:7,77:3,71:1}[info['dxgi_format']]
    byte=8 if bc==1 else 16
    bw,bh=4096//(16*byte),16
    ew,eh=(w+3)//4,(h+3)//4
    nm=info['mips']
    levels=[(max(1,ew>>i),max(1,eh>>i)) for i in range(nm)]
    first=next((i for i,(x,y) in enumerate(levels) if nm>1 and x<=bw//2 and y<=bh and nm-i<=8),nm)
    offset=4096 if first<nm else 0
    for x,y in levels[1:first]:
        offset+=((x+bw-1)//bw)*((y+bh-1)//bh)*4096
    cx=cy=0
    if first==0:
        offset=0
        # Tail's largest mip lies at its upper/lower half-block origin.
        mo=16<<7
        cx=sum(((mo>>(9+2*b))&1)<<b for b in range(6))*(8 if byte==8 else 4)
        cy=sum(((mo>>(8+2*b))&1)<<b for b in range(6))*4
    yy,xx=np.indices((eh,ew),dtype=np.int32)
    page=((yy//bh)*((ew+bw-1)//bw)+(xx//bw))*(4096//byte)
    xx=(xx%bw)+cx; yy=(yy%bh)+cy
    order=[(yy,0),(yy,1),(xx,0),(xx,1),(yy,2),(xx,2),(yy,3),(xx,3)] if byte==16 else [(xx,0),(yy,0),(yy,1),(xx,1),(xx,2),(yy,2),(xx,3),(yy,3),(xx,4)]
    address=page+sum(((v>>bit)&1)<<pos for pos,(v,bit) in enumerate(order))
    start=info['data_off']+offset
    blocks=np.frombuffer(data[start:],dtype='V'+str(byte))
    return Image.frombytes('RGBA',(w,h),blocks[address].tobytes(),'bcn',(bc,'BC'+str(bc)))


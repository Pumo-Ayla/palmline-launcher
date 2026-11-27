"""Rebuild the local scene library from data.rpf and its SixNG carving index.

Requires Pillow, NumPy, JDK 21 and the lib directory from JPEXS 26.5.
Run with --help for build paths. The launcher itself uses only the .scene files.
"""
from pathlib import Path
import argparse, json, re, struct, subprocess, zlib
import numpy as np
from PIL import Image
from loading_textures import read_atlas
from pack_scenes import pack

def tags(data, pos):
    while pos + 2 <= len(data):
        code, = struct.unpack_from('<H', data, pos)
        pos += 2
        tag, size = divmod(code, 64)
        if size == 63:
            size, = struct.unpack_from('<I', data, pos)
            pos += 4
        yield tag, data[pos:pos+size]
        pos += size
        if tag == 0:
            break

def convert(raw, textures, gpu, output):
    start = 12 + (5 + 4 * (raw[8] >> 3) + 7) // 8
    body = bytearray(raw[8:start])
    for code, payload in tags(raw, start):
        if code == 1009:
            name = re.search(rb'(loading_screen_[\da-f]+_i[\da-f]+)\.dds', payload, re.I)[1].decode().lower()
            width, height = struct.unpack_from('<HH', payload, 6)
            pic = read_atlas(textures[name], gpu).resize((width,height), Image.Resampling.BILINEAR)
            rgba = np.asarray(pic).astype(np.uint16)
            alpha = rgba[:,:,3:4]
            argb = np.concatenate((alpha, (rgba[:,:,:3] * alpha + 127) // 255), axis=2).astype('uint8')
            code, payload = 36, payload[:2] + struct.pack('<BHH',5,width,height) + zlib.compress(argb.tobytes(),2)
        elif code in (82, 76, 1000):
            continue
        body.extend(struct.pack('<HI', (code<<6) | 63, len(payload)))
        body.extend(payload)
    output.write_bytes(b'FWS' + bytes([19]) + struct.pack('<I', len(body)+8) + body)


def inflate(archive, stream, limit=None):
    offset, compressed, size = stream
    archive.seek(offset)
    if limit:
        return zlib.decompressobj().decompress(archive.read(min(compressed, 16384)), limit)
    result = zlib.decompress(archive.read(compressed))
    if len(result) != size:
        raise ValueError(f'Unexpected size at {offset:x}')
    return result


def texture_dictionary(system):
    atlas = {}
    offset = 0
    while offset + 0x48 <= len(system):
        data, = struct.unpack_from('<Q', system, offset + 0x18)
        if data >> 28 != 6 or system[offset + 14:offset + 16] != b'\x08\x00':
            offset += 8
            continue
        pointer, = struct.unpack_from('<Q', system, offset + 0x30)
        start = (pointer & 0x0FFFFFFF) + 8
        name = system[start:system.index(0, start)].decode('ascii')
        width, height = struct.unpack_from('<HH', system, offset + 0x38)
        atlas[name] = dict(width=width, height=height, data_off=data & 0x0FFFFFFF,
                           dxgi_format=system[offset + 0x3F], mips=system[offset + 0x42])
        offset += 0x70
    return atlas


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('archive', 'index', 'jdk', 'ffdec-lib', 'work', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    parser.add_argument('--scene', help='Rebuild just this two-digit scene')
    args = parser.parse_args()
    args.work.mkdir(parents=True, exist_ok=True)
    index = json.loads(args.index.read_text('utf-8'))
    if args.archive.stat().st_size != index['size']:
        raise ValueError('Archive does not match the supplied index')
    movies, atlases = {}, {}
    with args.archive.open('rb') as archive:
        for entry in index['entries']:
            kind = entry['kind']
            if kind not in ('gfx', 'rageobj'):
                continue
            streams = entry['streams']
            head = inflate(archive, streams[0], 4096)
            pattern = rb'loading_screen_(\d\d)\x00' if kind == 'gfx' else rb'loading_screen_(\d\d)_i[0-9a-f]+\x00'
            match = re.search(pattern,head)
            if match:
                (movies if kind == 'gfx' else atlases)[match[1].decode()] = streams
        ids = [args.scene] if args.scene else sorted(movies)
        if not ids or not set(ids) <= atlases.keys():
            raise ValueError('Missing loading screen or texture dictionary')
        for key in ids:
            movie = b''.join(inflate(archive, s) for s in movies[key])
            system, *gpu = (inflate(archive, s) for s in atlases[key])
            convert(movie, texture_dictionary(system), b''.join(gpu), args.work/(key+'.swf'))

    tools = Path(__file__).resolve().parent
    libraries = str(args.ffdec_lib/'*')
    subprocess.run([args.jdk/'bin/javac.exe','-cp',libraries,'-d',args.work,tools/'ExportLayers.java'],check=True)
    subprocess.run([args.jdk/'bin/java.exe','-Xmx1600m','-cp',str(args.work)+';'+libraries,
                    'ExportLayers',args.work,args.work/'stages',*ids],check=True)
    for key in ids:
        pack(args.work/'stages'/key,args.output)


if __name__ == '__main__':
    main()


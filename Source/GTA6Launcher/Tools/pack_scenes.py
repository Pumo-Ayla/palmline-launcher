"""Pack the rasterized shapes and original movie timeline into a launcher scene."""
import json
import struct
import sys
from io import BytesIO
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED, ZIP_STORED

IDENTITY = (1, 0, 0, 0, 1, 0)

def compose(parent, child):
    a, b, x, c, d, y = parent
    e, f, u, g, h, v = child
    return a*e+b*g, a*f+b*h, a*u+b*v+x, c*e+d*g, c*f+d*h, c*u+d*v+y

def pack(directory, destination):
    data = json.loads((directory/'scene.json').read_text())
    sprites = {int(k): v for k, v in data['sprites'].items()}
    shapes = {int(k): v for k, v in data['shapes'].items()}
    used = {}
    stream = BytesIO()
    def put(fmt, *values):
        stream.write(struct.pack('<'+fmt, *values))
    def image(op, key, matrix):
        index = used.setdefault(key, len(used))
        a,b,x,c,d,y = matrix
        put('BH6f', op, index, a,c,b,d,x,y)
    def display(layers, elapsed, parent):
        i = 0
        while i < len(layers):
            layer = layers[i]
            key, depth, mask_end, blend, alpha, time = layer[:6]
            i += 1
            matrix = compose(parent, layer[6:])
            if mask_end >= 0:
                if key not in shapes:
                    raise ValueError('Unexpected sprite mask')
                end = i
                while end < len(layers) and layers[end][1] <= mask_end:
                    end += 1
                image(2, key, matrix)
                display(layers[i:end], elapsed, parent)
                put('B', 4)
                i = end
                continue
            if not alpha:
                continue
            if blend not in (0,1,2):
                raise ValueError(f'Visible blend mode {blend} requires compositing')
            if alpha != 256:
                put('Bf', 3, alpha/256)
            if key in shapes:
                image(1, key, matrix)
            else:
                frames = sprites[key]
                moment = elapsed+time
                frame = moment % len(frames)
                display(frames[frame], moment-frame, matrix)
            if alpha != 256:
                put('B', 4)
    frames = sprites[data['root']]
    for frame in frames:
        display(frame, 0, IDENTITY)
        put('B', 0)
    header = BytesIO()
    header.write(struct.pack('<4sHHfHH', b'PLS1', 2560, 1440, data['fps'], len(frames), len(used)))
    destination.mkdir(parents=True, exist_ok=True)
    with ZipFile(destination/(directory.name+'.scene'), 'w') as archive:
        for key, index in used.items():
            header.write(struct.pack('<4f', *shapes[key]))
            archive.write(directory/(str(key)+'.png'), str(index)+'.png', compress_type=ZIP_STORED)
        archive.writestr('timeline', header.getvalue()+stream.getvalue(), compress_type=ZIP_DEFLATED)

if __name__ == '__main__':
    for folder in sorted(Path(sys.argv[1]).glob('[0-9][0-9]')):
        pack(folder, Path(sys.argv[2]))


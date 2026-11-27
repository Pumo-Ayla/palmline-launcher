"""Build a private data.rpf with mapped Scaleform button glyphs and inline text prompts."""
import mmap
import math
import re
import shutil
import struct
import zlib
from functools import lru_cache


_ZLIB = re.compile(rb"\x78[\x01\x5e\x9c\xda]")
_TOKEN = re.compile(rb"~(INPUT_[A-Z0-9_]+)(?:\|[^~\x00]{1,128})?~")
_BASE = 0x50000000

# Export numbers in platform_xbsx.input_icons, identified from their vector artwork.
_BUTTON_ACTION = {
    4: "GAME_MENU_UP", 5: "GAME_MENU_DOWN", 6: "GAME_MENU_LEFT", 7: "GAME_MENU_RIGHT",
    16: "SPRINT", 25: "LOOK_BEHIND", 30: "FRONTEND_ACCEPT", 31: "FRONTEND_CANCEL",
    32: "RELOAD", 33: "CONTEXT_Y", 34: "SELECT_WEAPON", 35: "AIM", 36: "COVER",
    37: "ATTACK", 38: "FRONTEND_PAUSE", 39: "MAP",
}
_EXPORT = b"platform_xbsx.input_icons.Main_Button_"
_TEXT_LEGENDS = {b"[Enter]": "[↵]".encode(), b"[Space]": "[␣]".encode()}


def _tags(movie, pos):
    while pos + 2 <= len(movie):
        start = pos
        kind, size = divmod(struct.unpack_from("<H", movie, pos)[0], 64)
        pos += 2
        if size == 63:
            size, = struct.unpack_from("<I", movie, pos)
            pos += 4
        if pos + size > len(movie):
            raise ValueError("Invalid input icon movie tag.")
        yield kind, movie[pos:pos + size], movie[start:pos + size]
        pos += size
        if not kind:
            break


def _tag(kind, body):
    head = struct.pack("<H", kind << 6 | min(63, len(body)))
    return head + (struct.pack("<I", len(body)) if len(body) >= 63 else b"") + body


@lru_cache(maxsize=128)
def _letter_paths(label):
    # Get closed outlines from the Windows font once per distinct keycap.
    import ctypes as ct
    from ctypes import wintypes as wt
    gdi = ct.WinDLL("gdi32", use_last_error=True)
    def api(name, result, *args):
        fn = getattr(gdi, name)
        fn.argtypes, fn.restype = args, result
        return fn
    handle = wt.HANDLE
    dc = api("CreateCompatibleDC", handle, handle)(None)
    font = api("CreateFontW", handle, *([ct.c_int] * 5), *([wt.DWORD] * 8), wt.LPCWSTR)(
        -36, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI")
    select = api("SelectObject", handle, handle, handle)
    prior = select(dc, font)
    try:
        if not (api("SetBkMode", ct.c_int, handle, ct.c_int)(dc, 1)
                and api("BeginPath", wt.BOOL, handle)(dc)
                and api("TextOutW", wt.BOOL, handle, ct.c_int, ct.c_int, wt.LPCWSTR, ct.c_int)(dc, 0, 0, label, len(label))
                and api("EndPath", wt.BOOL, handle)(dc) and api("FlattenPath", wt.BOOL, handle)(dc)):
            raise OSError("Could not draw the keyboard prompt.")
        path = api("GetPath", ct.c_int, handle, ct.POINTER(wt.POINT), ct.POINTER(ct.c_ubyte), ct.c_int)
        count = path(dc, None, None, 0)
        if not 3 <= count <= 8192:
            raise ValueError("Missing keycap font outline: " + label)
        points, types = (wt.POINT * count)(), (ct.c_ubyte * count)()
        if path(dc, points, types, count) != count:
            raise OSError("Could not read the keycap outline.")
        contours = []
        for pt, kind in zip(points, types):
            if kind & 6 == 6:
                contours.append([])
            contours[-1].append((pt.x, pt.y))
        return contours
    finally:
        select(dc, prior)
        api("DeleteObject", wt.BOOL, handle)(font)
        api("DeleteDC", wt.BOOL, handle)(dc)


def _keycap_paths(label):
    # Draw the two wide key names as physical key legends, at HUD scale.
    if label == "Enter":
        return [[(875,375),(985,375),(985,735),(520,735),(654,869),
                 (575,948),(309,682),(575,416),(654,495),(520,629),(875,629)]]
    if label == "Space":
        return [[(263,493),(373,493),(373,680),(907,680),
                 (907,493),(1017,493),(1017,790),(263,790)]]
    short = {"Backspace":"Bksp", "LeftShift":"LShift", "RightShift":"RShift",
             "LeftCtrl":"LCtrl", "RightCtrl":"RCtrl", "Capital":"Caps", "Left":"←", "Up":"↑",
             "Right":"→", "Down":"↓", "Mouse 4":"M4", "Mouse 5":"M5", "Wheel Up":"Wh↑", "Wheel Down":"Wh↓",
                  "Multiply":"Num×", "Divide":"Num÷", "Decimal":"Num.", "Add":"Num+",
                  "Subtract":"Num−", "OemPlus":"+", "OemMinus":"−", "OemComma":",", "OemPeriod":".",
                  "PageUp":"PgUp", "PageDown":"PgDn", "NumLock":"NumLk"}
    name = "N"+label[-1] if re.fullmatch(r"NumPad[0-9]", label) else short.get(label, label)
    paths = _letter_paths(name)
    xs, ys = zip(*(point for polygon in paths for point in polygon))
    width, height = max(xs) - min(xs), max(ys) - min(ys)
    # Fit even the corners of a long label inside the circular face.
    scale = min(630 / max(1, height), 1000 / max(1, math.hypot(width, height)))
    cx, cy = (max(xs) + min(xs)) / 2, (max(ys) + min(ys)) / 2
    return [[(round(640 + (x - cx) * scale), round(640 + (y - cy) * scale)) for x, y in polygon] for polygon in paths]


def _vector_shape(shape_id, color, paths):
    # DefineShape3, with one solid fill. All geometry uses the movie's 64px keycap cell.
    bits = []
    def put(value, width):
        bits.append(format(value & ((1 << width) - 1), f"0{width}b"))
    def finish():
        stream = "".join(bits)
        bits.clear()
        return int(stream.ljust((len(stream)+7)//8*8, "0"), 2).to_bytes((len(stream)+7)//8, "big")
    def signed_width(*values):
        return max((v if v >= 0 else ~v).bit_length() + 1 for v in values)
    put(12, 5)
    for v in (0, 1280, 0, 1280):
        put(v, 12)
    header = struct.pack("<H", shape_id) + finish() + bytes((1, 0, *color, 255, 0, 0x10))
    for points in paths:
        if len(points) < 3:
            continue
        x, y = points[0]
        put(5, 6)  # MoveTo + FillStyle1
        width = signed_width(x, y)
        put(width, 5); put(x, width); put(y, width); put(1, 1)
        for point in points[1:] + points[:1]:
            if len(point) == 4:
                cx, cy, nx, ny = point
                deltas = (cx-x, cy-y, nx-cx, ny-cy)
                width = max(2, signed_width(*deltas))
                put(2, 2)  # CurvedEdgeRecord
                put(width-2, 4)
                for delta in deltas:
                    put(delta, width)
                x, y = nx, ny
                continue
            nx, ny = point
            dx, dy = nx-x, ny-y
            if dx or dy:
                width = max(2, signed_width(dx, dy))
                put(3, 2); put(width-2, 4); put(1, 1); put(dx, width); put(dy, width)
            x, y = nx, ny
    put(0, 6)
    return _tag(32, header + finish())


def _cap_outline(radius):
    # Eight quadratic arcs stay smooth when Scaleform scales the HUD.
    def polar(angle, distance):
        return round(640 + distance*math.cos(angle)), round(640 + distance*math.sin(angle))
    path = [polar(0, radius)]
    for i in range(8):
        mid = (i + .5)*math.pi/4
        control = polar(mid, radius/math.cos(math.pi/8))
        path.append(control + polar((i+1)*math.pi/4, radius))
    return [path]


def replace_button_glyphs(movie, bindings):
    if not movie.startswith(b"GFX") or _EXPORT not in movie:
        return None
    start = 12 + (5 + 4*(movie[8] >> 3) + 7)//8
    tags = list(_tags(movie, start))
    symbols = {}
    for kind, data, _ in tags:
        if kind == 56:
            pos = 2
            for _ in range(struct.unpack_from("<H", data)[0]):
                child, = struct.unpack_from("<H", data, pos)
                end = data.index(0, pos+2)
                name = data[pos+2:end]
                pos = end+1
                if name.startswith(_EXPORT):
                    action = _BUTTON_ACTION.get(int(name[len(_EXPORT):]))
                    cap = bindings.get(("INPUT_" + action).encode()) if action else None
                    if cap:
                        symbols[child] = cap[1:-1].decode("ascii")
    if not symbols:
        return None
    shapes, sprites, shared = {}, {}, {}
    for kind, data, _ in tags:
        if kind != 39:
            continue
        sprite, = struct.unpack_from("<H", data)
        if sprite not in symbols:
            continue
        placed = list(_tags(data, 4))
        if any(k != 26 or p[:1] != b"\x06" for k,p,_ in placed[:-1]) or placed[-1][0] != 0:
            raise ValueError("Unsupported input icon sprite layout.")
        ids = [struct.unpack_from("<H", p, 3)[0] for _,p,_ in placed[:-1]]
        if len(ids) < 3 or any(i in shapes for i in ids):
            raise ValueError("Unsupported input icon shape dictionary.")
        shapes.update(dict.fromkeys(ids, b""))
        layers = []
        for i, key in enumerate(("outer", "inner", "label:" + symbols[sprite])):
            if key not in shared:
                paths = _cap_outline(618 if i==0 else 578) if i<2 else _keycap_paths(symbols[sprite])
                color = (250,250,250) if i==1 else (20,20,24)
                shapes[ids[i]] = _vector_shape(ids[i], color, paths)
                shared[key] = ids[i]
            layers.append(_tag(26, struct.pack("<BHHB", 6, i+1, shared[key], 0)))
        sprites[sprite] = _tag(39, data[:4] + b"".join(layers) + _tag(0,b""))
    if sprites.keys() != symbols.keys():
        raise ValueError("Incomplete input icon library.")
    body = bytearray(movie[:start])
    for kind, data, raw in tags:
        lookup = shapes if kind==32 else sprites if kind==39 else {}
        body.extend(lookup.get(int.from_bytes(data[:2],"little"), raw))
    if len(body) > len(movie):
        raise ValueError("Keyboard glyphs exceed the input icon movie capacity.")
    # Keep the original inflated stream size. Scaleform stops at the movie's End tag.
    return bytes(body).ljust(len(movie), b"\0"), len(sprites)


def replace_labels(resource, bindings):
    if len(resource) < 0x60 or resource[16:21] != b"0DRPz":
        return None
    table = struct.unpack_from("<Q", resource, 0x38)[0] - _BASE
    count = struct.unpack_from("<I", resource, 0x48)[0]
    if count != 4 or not 0x50 <= table <= len(resource) - 16 * count:
        return None
    fields = list(struct.iter_unpack("<IIQ", resource[table:table + 16 * count]))
    if [f[0] for f in fields] != [0xa8478889, 0x99331089, 0x101, 0x10]:
        return None
    _, size, pointer = fields[-1]
    start = pointer - _BASE
    if size == 0 or start < 0x50 or start + size > len(resource):
        return None
    pool = bytearray(resource[start:start + size])
    offset = changes = 0
    def key_legend(match):
        cap = bindings.get(match[1], match[0])
        return _TEXT_LEGENDS.get(cap, cap)
    for entry in bytes(pool).split(b"\0")[:-1]:
        if b"~INPUT_" in entry:
            updated = _TOKEN.sub(key_legend, entry)
            if updated != entry and len(updated) <= len(entry):
                # Preserve the address of every other string and all indexed offsets.
                pool[offset:offset + len(entry)] = updated.ljust(len(entry), b"\0")
                changes += 1
        offset += len(entry) + 1
    if changes:
        return resource[:start] + pool + resource[start + size:], changes


def replacements(archive, bindings):
    if archive[:4] != b"9FPR":
        return
    start = int.from_bytes(archive[4:8], "little")
    if not 8 <= start < len(archive):
        return
    occupied = start
    for header in _ZLIB.finditer(archive, start):
        pos = header.start()
        if pos % 16 or pos < occupied:
            continue
        try:
            prefix = zlib.decompressobj().decompress(archive[pos:pos + 256], 112)
            glyph_movie = prefix.startswith(b"GFX") and b"input_icons" in prefix
            if not glyph_movie and prefix[16:21] != b"0DRPz":
                continue
            decoder = zlib.decompressobj()
            data = archive[pos:pos + 512 * 1024]
            resource = decoder.decompress(data, 2 * 1024 * 1024)
            if not decoder.eof:
                continue
        except zlib.error:
            continue
        length = len(data) - len(decoder.unused_data)
        occupied = pos + length
        result = replace_button_glyphs(resource, bindings) if glyph_movie else replace_labels(resource, bindings)
        if result:
            updated, changes = result
            # Different deflate block sizes fit different language pools. Never
            # extend into the next record or change the archive's directory.
            for memory in (8, 4, 3, 5, 6, 2):
                encoder = zlib.compressobj(9, zlib.DEFLATED, 15, memory)
                compressed = encoder.compress(updated) + encoder.flush()
                if len(compressed) <= length:
                    yield pos, compressed.ljust(length, b"\0"), changes, glyph_movie
                    break
            else:
                if glyph_movie:
                    raise ValueError("The mapped HUD icon library does not fit this title archive.")


def create_copy(source, destination, bindings):
    """Only expose a prepared copy when the game's shared button library was replaced."""
    with source.open("rb") as stream:
        if source.stat().st_size < 8:
            return 0, 0
        with mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as archive:
            patches = list(replacements(archive, bindings))
        glyphs = sum(n for _, _, n, is_movie in patches if is_movie)
        if not glyphs:
            return 0, 0
        with destination.open("xb") as output:
            shutil.copyfileobj(stream, output, 1024 * 1024)
            for pos, data, _, _ in patches:
                output.seek(pos)
                output.write(data)
    return sum(n for _, _, n, is_movie in patches if not is_movie), glyphs

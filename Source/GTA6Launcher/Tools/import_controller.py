"""Pack the Microsoft GDK Gamepad sample for the WPF viewer.

Pass the sample's media/Model directory, the repository LICENSE and output ZIP.
Requires NumPy and Pillow. The SDKMESH reader is specific to the pinned asset.
"""
import argparse
import hashlib
import io
import struct
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

import numpy as np
from PIL import Image, ImageDraw


REVISION = "b1e77b6ac10f95b07296025fda171ca2ddc42dbe"
DIGEST = "6873114837fcc842ed6885e433aeecf1f87e5e97524729933a786bc8e667bae5"

def control_groups(part, faces, xyz):
    """Assign whole components of the pinned SDKMESH to their mechanical controls."""
    center = xyz[faces].mean(axis=1)
    x = center[:, 0]
    if part in (0, 1):
        anchors = np.array([[3.9, 1.7], [5.0, 2.6], [2.9, 2.7], [3.9, 3.6]])
        distance = ((center[:, None, :2] - anchors) ** 2).sum(axis=2)
        names = np.array(["A", "B", "X", "Y"])[distance.argmin(axis=1)]
    elif part == 4:
        # Bumpers overlap the centre bridge in silhouette. Weld seam duplicates
        # to classify entire shells without cutting triangles off the bridge.
        points, ids = np.unique(xyz[faces.ravel()].round(4), axis=0, return_inverse=True)
        parents = list(range(len(points)))
        def root(i):
            while parents[i] != i:
                parents[i] = parents[parents[i]]
                i = parents[i]
            return i
        for a, b, c in ids.reshape(-1, 3):
            group = root(a)
            parents[root(b)] = group
            parents[root(c)] = group
        roots = np.array([root(i) for i in range(len(points))])
        names = np.full(len(faces), "", dtype="U4")
        groups = roots[ids.reshape(-1, 3)[:, 0]]
        for group in np.unique(groups):
            cx = points[roots == group, 0].mean()
            names[groups == group] = "LB" if cx < -2.2 else "RB" if cx > 2.2 else ""
    elif part in (10, 12):
        names = np.where(x < 0, "LS", "RS")
    elif part == 11:
        names = np.where(x < 0, "LT", "RT")
    elif part == 13:
        names = np.where(x < -.55, "View", np.where(x > .55, "Menu", "Share"))
    else:
        yield {7: "Dpad", 8: "Guide"}.get(part, ""), faces
        return
    for name in np.unique(names):
        yield name, faces[names == name]


def face_cap_art(vertices, cap_vertices, artwork, atlas):
    """Bake the original letter face onto the cap so it stays seated at oblique angles."""
    lo = cap_vertices[:, :2].min(axis=0)
    span = np.ptp(cap_vertices[:, :2], axis=0)

    def project(xy):
        uv = .02 + .96 * (xy - lo) / span
        uv[..., 1] = 1 - uv[..., 1]
        return uv

    triangles = vertices[artwork]
    corners = triangles[:, :, :3]
    normal = np.cross(corners[:, 1] - corners[:, 0], corners[:, 2] - corners[:, 0])
    top = normal[:, 2] > .55 * np.linalg.norm(normal, axis=1)
    coords = triangles[:, :, 6:].mean(axis=1) % 1
    colors = atlas[(coords[:, 1] * atlas.shape[0]).astype(int), (coords[:, 0] * atlas.shape[1]).astype(int)]
    visible = top & (colors.max(axis=1) > 100)
    ink = tuple(np.median(colors[visible], axis=0).astype(int))
    size = 1024
    surface = Image.new("RGB", (size, size), (24, 26, 28))
    draw = ImageDraw.Draw(surface)
    for tri in (project(corners[visible, :, :2]) * (size - 1)).round().astype(int):
        draw.polygon([tuple(point) for point in tri], fill=ink)
    output = io.BytesIO()
    surface.resize((512, 512), Image.Resampling.LANCZOS).save(output, format="PNG")
    return project(cap_vertices[:, :2]), output.getvalue()


def trigger_seat(block, faces):
    """Close the opening below the stock trigger with a stationary recessed lining."""
    points, weld = np.unique(block[:, :3].round(5), axis=0, return_inverse=True)
    triangles = weld[faces.reshape(-1, 3)]
    edges = np.concatenate([triangles[:, [0, 1]], triangles[:, [1, 2]], triangles[:, [2, 0]]])
    unique, counts = np.unique(np.sort(edges, axis=1), axis=0, return_counts=True)
    boundary = unique[counts == 1]
    # The lowest open loop is the trigger's mounting rim. The other open loops
    # belong to the embossed letters and the small molded grip dots.
    neighbors = {}
    for a, b in boundary:
        neighbors.setdefault(a, []).append(b)
        neighbors.setdefault(b, []).append(a)
    start = min(neighbors, key=lambda i: points[i, 1])
    loop = [start]
    previous, current = -1, start
    while True:
        a, b = neighbors[current]
        previous, current = current, b if a == previous else a
        if current == start:
            break
        loop.append(current)
    rim = points[loop].copy()
    # The SDK mesh stops at the moving cap. Bridge its inboard edge under the
    # bumper so a grazing view cannot see through the split when the cap retracts.
    depth = np.ptp(rim[:, 2])
    front = (rim[:, 2] - rim[:, 2].min()) / depth
    overlap = np.clip((front - .48) / .26, 0, 1)
    rim[:, 2] += .34 * depth * overlap
    rim[:, 1] += .12 * depth * overlap
    center = rim.mean(axis=0)
    floor = center + (rim - center) * .85
    floor[:, 1] -= .035
    center[1] -= .045
    n = len(rim)
    pos = np.vstack([rim, floor, center])
    idx = np.arange(n)
    nxt = (idx + 1) % n
    faces = np.vstack([np.column_stack([idx, nxt, idx + n]),
                       np.column_stack([nxt, nxt + n, idx + n]),
                       np.column_stack([idx + n, nxt + n, np.full(n, 2*n)])])
    normals = np.zeros_like(pos)
    a, b, c = pos[faces].transpose(1, 0, 2)
    cross = np.cross(b - a, c - a)
    if cross[:, 1].sum() < 0:
        faces = faces[:, ::-1]
        cross = -cross
    for corner in faces.T:
        np.add.at(normals, corner, cross)
    normals /= np.maximum(np.linalg.norm(normals, axis=1, keepdims=True), 1e-10)
    front = np.column_stack([pos, normals, np.zeros((len(pos), 2))])
    back = front.copy()
    back[:, 3:6] *= -1
    return np.vstack([front, back]), np.vstack([faces, faces[:, ::-1] + len(pos)])


def pack(source, license_file, output):
    data = (source / "XboxControllerGen3.sdkmesh").read_bytes()
    if hashlib.sha256(data).hexdigest() != DIGEST:
        raise ValueError("The importer expects XboxControllerGen3.sdkmesh from " + REVISION)
    _, _, _, _, _, _, _, _, subsets, _, _, vb, ib, _, subset_table, _, materials = struct.unpack_from(
        "<IB3x3Q6I6Q", data)
    count, _, stride = struct.unpack_from("<3Q", data, vb)
    indices, _, _, index_offset = struct.unpack_from("<QQI4xQ", data, ib)
    vertex_offset, = struct.unpack_from("<Q", data, vb + 280)
    vertices = np.frombuffer(data, "<f4", count * stride // 4, vertex_offset).reshape(count, 8).copy()
    triangles = np.frombuffer(data, "<u4", indices, index_offset)
    # Face buttons point towards -Z in the source. WPF's camera faces -Z.
    xyz = vertices[:, :3].copy()
    vertices[:, [2, 5]] *= -1
    positions = vertices[:, :3]
    positions -= (positions.min(axis=0) + positions.max(axis=0)) / 2
    positions /= np.ptp(positions[:, 0]) / 2
    mesh = io.BytesIO()
    parts = 0
    textures = {}
    def write_part(block, indices, tint, alpha, power, texture, control):
        mesh.write(struct.pack("<II4Bf", len(block), indices.size, *tint, round(alpha * 255), power))
        mesh.write(texture.encode("ascii") + b"\0" + control.encode("ascii") + b"\0")
        mesh.write(block.astype("<f4").tobytes())
        mesh.write(indices.astype("<u4").tobytes())

    lettering = {}
    atlas = np.asarray(Image.open(source / "colors-on-black.dds").convert("RGB"))
    for i in range(subsets):
        mat, _, start, size, base, _ = struct.unpack_from("<II4x4Q", data, subset_table + 144*i + 100)
        faces = triangles[start:start + size].reshape(-1, 3)[:, [0, 2, 1]] + base
        material = materials + mat * 1256
        tex = data[material+360:material+620].split(b"\0")[0].decode("ascii")
        r, g, b, alpha = struct.unpack_from("<4f", data, material + 1140)
        power, = struct.unpack_from("<f", data, material + 1204)
        # A small graphite floor keeps the sample's near-black resins readable
        # under WPF's fixed lighting. Leave button artwork and emissive mark intact.
        rgb = np.array([r, g, b])
        rgb = np.clip(1.055 * rgb ** (1 / 2.4) - .055, 0, 1)
        if max(rgb) < .15 and alpha == 1:
            rgb = .13 + .52 * rgb
        tint = np.rint(255 * rgb).astype("uint8")
        name = ("m%d.png" % mat) if tex else ""
        for control, faceset in control_groups(i, faces, xyz):
            if i == 0:
                lettering[control] = faceset
                continue
            used, remap = np.unique(faceset.ravel(), return_inverse=True)
            block = vertices[used].copy()
            if i == 1:
                name = "cap-" + control + ".png"
                block[:, 6:], textures[name] = face_cap_art(vertices, block, lettering[control], atlas)
                alpha, power = 1, 72
            write_part(block, remap, tint, alpha, power, name, control)
            parts += 1
            if i == 11:
                lining, lining_faces = trigger_seat(block, remap)
                write_part(lining, lining_faces, (18, 20, 21), 1, 56, "", "")
                parts += 1
        if i != 0 and tex and name not in textures:
            pixels = np.asarray(Image.open(source / tex).convert("RGB"))
            image = Image.fromarray(np.rint(pixels * rgb).astype("uint8"))
            buf = io.BytesIO()
            image.save(buf, format="PNG")
            textures[name] = buf.getvalue()
    with ZipFile(output, "w", compression=ZIP_DEFLATED) as archive:
        archive.writestr("mesh.bin", struct.pack("<I", parts) + mesh.getvalue())
        for name, contents in textures.items():
            archive.writestr(name, contents)
        archive.write(license_file, "LICENSE.txt")
        archive.writestr("SOURCE.txt", "Microsoft Xbox GDK Samples / Gamepad / XboxControllerGen3\n"
                         f"https://github.com/microsoft/Xbox-GDK-Samples/tree/{REVISION}/Samples/System/Gamepad/media/Model\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ("source", "license_file", "output"):
        parser.add_argument(argument, type=Path)
    args = parser.parse_args()
    pack(args.source, args.license_file, args.output)

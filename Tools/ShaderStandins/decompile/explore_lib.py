import pickle, struct


def load(pkl):
    data = pickle.load(open(pkl, "rb"))
    tree, (platform, segs) = data["tree"], data["chunks"][0]
    seg0 = segs[0]
    count = struct.unpack_from("<i", seg0, 0)[0]
    entries = [struct.unpack_from("<iii", seg0, 4 + 12 * i) for i in range(count)]
    return data["name"], tree, segs, entries


def entry_bytes(segs, entries, i):
    off, ln, sg = entries[i]
    return segs[sg][off:off + ln]


def read_subprogram(b):
    pos = 0
    version, ptype = struct.unpack_from("<ii", b, pos); pos += 8
    pos += 12
    if version >= 201608170:
        pos += 4
    n = struct.unpack_from("<i", b, pos)[0]; pos += 4
    kws = []
    for _ in range(n):
        ln = struct.unpack_from("<i", b, pos)[0]; pos += 4
        kws.append(b[pos:pos + ln].decode()); pos += ln
        pos = (pos + 3) & ~3
    if 201806140 <= version < 202012090:
        n = struct.unpack_from("<i", b, pos)[0]; pos += 4
        for _ in range(n):
            ln = struct.unpack_from("<i", b, pos)[0]; pos += 4
            pos += ln; pos = (pos + 3) & ~3
    ln = struct.unpack_from("<i", b, pos)[0]; pos += 4
    code = b[pos:pos + ln]; pos += ln
    pos = (pos + 3) & ~3
    return version, ptype, kws, code, b[pos:]

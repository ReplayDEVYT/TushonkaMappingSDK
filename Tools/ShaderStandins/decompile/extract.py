# Stage 1: pull named shaders out of the game's shader bundle into pickles (typetree + decompressed
# program segments), and list every shader name in the bundle. Slow part: reading the bundle.
#   extract.py <bundle> <out dir> <names.txt> [more names.txt...]
import sys, os, re, pickle
import UnityPy
from UnityPy.helpers import CompressionHelper

bundle, out = sys.argv[1], sys.argv[2]
wanted = set()
for path in sys.argv[3:]:
    for line in open(path, encoding="utf-8"):
        line = line.strip()
        if line and not line.startswith("#"):
            wanted.add(line)
os.makedirs(out, exist_ok=True)

def sanitize(name):
    return re.sub(r"[^A-Za-z0-9]+", "_", name).strip("_")

env = UnityPy.load(bundle)
names = []
for o in env.objects:
    if o.type.name != "Shader":
        continue
    try:
        tree = o.read_typetree()
    except Exception as e:
        print("typetree failed", o.path_id, e)
        continue
    name = tree.get("m_ParsedForm", {}).get("m_Name")
    names.append(name)
    if name not in wanted:
        continue
    blob = bytes(tree["compressedBlob"])
    chunks = []
    for p, platform in enumerate(tree["platforms"]):
        offs, comp, decomp = tree["offsets"][p], tree["compressedLengths"][p], tree["decompressedLengths"][p]
        if not isinstance(offs, list):
            offs, comp, decomp = [offs], [comp], [decomp]
        segs = []
        for off, c, d in zip(offs, comp, decomp):
            segs.append(CompressionHelper.decompress_lz4(blob[off:off + c], d))
        chunks.append((platform, segs))
    tree = dict(tree)
    del tree["compressedBlob"]
    with open(os.path.join(out, sanitize(name) + ".pkl"), "wb") as f:
        pickle.dump({"name": name, "tree": tree, "chunks": chunks}, f)
    print("saved", name, "segments", [len(s) for s in chunks[0][1]])
    wanted.discard(name)

with open(os.path.join(out, "allnames.txt"), "w", encoding="utf-8") as f:
    f.write("\n".join(sorted(n for n in names if n)))
print(len(names), "shaders in bundle;", "missing:", sorted(wanted))

# Stage 2: decode a cached shader pickle into readable text.
#   dump.py <pkl> <out dir> [--all]
# Writes summary.txt (properties, passes, states, every variant's keywords and bindings) and one annotated
# DXBC listing per variant whose keywords carry no "noise" keyword (stereo, instancing, fog, lightmaps,
# TAA/DLSS...), or every variant with --all. Each cbN[k] / tN / sN in a listing is annotated with the
# parameter name it holds, from the pass's common parameters and the variant's parameter blob.
import sys, os, re, struct, pickle
import dxbc
from explore_lib import load, entry_bytes, read_subprogram

NOISE = {"STEREO_INSTANCING_ON", "UNITY_SINGLE_PASS_STEREO", "STEREO_MULTIVIEW_ON", "STEREO_CUBEMAP_RENDER_ON", "INSTANCING_ON",
         "FOG_LINEAR", "FOG_EXP", "FOG_EXP2", "LIGHTMAP_ON", "DYNAMICLIGHTMAP_ON", "DIRLIGHTMAP_COMBINED", "SHADOWS_SHADOWMASK",
         "LIGHTMAP_SHADOW_MIXING", "EDITOR_VISUALIZATION", "TAA", "DLSS", "VERTEXLIGHT_ON", "POINT_COOKIE", "DIRECTIONAL_COOKIE",
         "SHADOWS_DEPTH", "SHADOWS_CUBE", "SHADOWS_SOFT", "LIGHTPROBE_SH", "PROCEDURAL_INSTANCING_ON", "DOTS_INSTANCING_ON",
         "LOD_FADE_CROSSFADE", "SKINNEDMESHMATERIAL", "WINTER_SNOW", "Rain"}
STAGES = [("progVertex", "vs"), ("progFragment", "ps"), ("progGeometry", "gs"), ("progHull", "hs"), ("progDomain", "ds")]
BLEND = {0: "Zero", 1: "One", 2: "DstColor", 3: "SrcColor", 4: "OneMinusDstColor", 5: "SrcAlpha", 6: "OneMinusSrcColor", 7: "DstAlpha",
         8: "OneMinusDstAlpha", 9: "SrcAlphaSaturate", 10: "OneMinusSrcAlpha"}
CMP = {0: "Disabled", 1: "Never", 2: "Less", 3: "Equal", 4: "LEqual", 5: "Greater", 6: "NotEqual", 7: "GEqual", 8: "Always"}
CULL = {0: "Off", 1: "Front", 2: "Back"}
STENCILOP = {0: "Keep", 1: "Zero", 2: "Replace", 3: "IncrSat", 4: "DecrSat", 5: "Invert", 6: "IncrWrap", 7: "DecrWrap"}
PROPTYPE = {0: "Color", 1: "Vector", 2: "Float", 3: "Range", 4: "Texture", 5: "Int"}
PASSTYPE = {0: "Normal", 1: "UsePass", 2: "GrabPass"}


class Reader:
    def __init__(self, b):
        self.b, self.pos = b, 0

    def i32(self):
        v = struct.unpack_from("<i", self.b, self.pos)[0]
        self.pos += 4
        return v

    def string(self):
        n = self.i32()
        s = self.b[self.pos:self.pos + n].decode("utf-8", "replace")
        self.pos += n
        self.pos = (self.pos + 3) & ~3
        return s

    def done(self):
        return self.pos >= len(self.b)


def parse_param_blob(b):
    """Per-variant parameters: {'cbs': {name: {'size': n, 'params': [(offset, name, type, dim, arraySize)]}}, 'bindings': [...]}"""
    r = Reader(b)
    version = r.i32()
    groups = r.i32()
    cbs = {}
    for g in range(groups):
        name = r.string()
        size = r.i32()
        nparams = r.i32()
        params = []
        for _ in range(nparams):
            pname = r.string()
            a, ptype, dim, arr, c, offset = r.i32(), r.i32(), r.i32(), r.i32(), r.i32(), r.i32()
            params.append((offset, pname, ptype, dim, arr, a, c))
        nstructs = r.i32()
        for _ in range(nstructs):
            # unseen so far; stop decoding rather than guess
            return {"cbs": cbs, "bindings": [], "note": "struct params not decoded"}
        if name:
            cbs[name] = {"size": size, "params": params}
    bindings = []
    nbind = r.i32()
    for _ in range(nbind):
        bname = r.string()
        btype = r.i32()
        if btype == 0:    # texture: index, sampler index, dim<<1|multisampled
            idx, smp, dim = r.i32(), r.i32(), r.i32()
            bindings.append(("tex", bname, idx, smp, dim >> 1, dim & 1))
        elif btype == 1:  # constant buffer binding: index, extra
            idx, extra = r.i32(), r.i32()
            bindings.append(("cb", bname, idx, extra))
        else:             # buffer / uav / sampler: assume index + extra
            idx, extra = r.i32(), r.i32()
            bindings.append((f"type{btype}", bname, idx, extra))
    leftover = len(b) - r.pos
    return {"cbs": cbs, "bindings": bindings, "leftover": leftover}


def state_text(st):
    b = st["rtBlend0"]
    blend = f"Blend {BLEND.get(int(b['srcBlend']['val']))} {BLEND.get(int(b['destBlend']['val']))}, {BLEND.get(int(b['srcBlendAlpha']['val']))} {BLEND.get(int(b['destBlendAlpha']['val']))}"
    parts = [f"ZWrite {'On' if st['zWrite']['val'] else 'Off'}", f"ZTest {CMP.get(int(st['zTest']['val']))}", f"Cull {CULL.get(int(st['culling']['val']))}", blend,
             f"ColorMask {int(b['colMask']['val'])}", f"Offset {st['offsetFactor']['val']}, {st['offsetUnits']['val']}"]
    so = st["stencilOp"]
    if int(so["comp"]["val"]) not in (0, 8) or st["stencilWriteMask"]["val"] != 255 or int(so["pass"]["val"]) != 0:
        parts.append(f"Stencil Ref {int(st['stencilRef']['val'])} Read {int(st['stencilReadMask']['val'])} Write {int(st['stencilWriteMask']['val'])} "
                     f"Comp {CMP.get(int(so['comp']['val']))} Pass {STENCILOP.get(int(so['pass']['val']))} Fail {STENCILOP.get(int(so['fail']['val']))} ZFail {STENCILOP.get(int(so['zFail']['val']))}")
    if st["alphaToMask"]["val"]:
        parts.append("AlphaToMask On")
    return "; ".join(parts)


def sanitize(s):
    return re.sub(r"[^A-Za-z0-9]+", "_", s).strip("_") or "none"


def main():
    pkl, out = sys.argv[1], sys.argv[2]
    everything = "--all" in sys.argv
    os.makedirs(out, exist_ok=True)
    name, tree, segs, entries = load(pkl)
    pf = tree["m_ParsedForm"]
    kwn = pf["m_KeywordNames"]
    lines = [f"Shader \"{name}\"", f"fallback: {pf['m_FallbackName']}  customEditor: {pf['m_CustomEditorName']}", "", "Properties:"]
    for p in pf["m_PropInfo"]["m_Props"]:
        vals = [p[f"m_DefValue[{i}]"] for i in range(4)]
        extra = p["m_DefTexture"]["m_DefaultName"] if p["m_Type"] == 4 else vals
        lines.append(f"  {p['m_Name']} ({p['m_Description']}) {PROPTYPE.get(p['m_Type'])} attrs={p['m_Attributes']} flags={p['m_Flags']} default={extra}")
    lines += ["", f"Keywords: {kwn}", ""]
    written = 0
    for si, sub in enumerate(pf["m_SubShaders"]):
        lines.append(f"SubShader {si} LOD {sub['m_LOD']} tags {sub['m_Tags']['tags']}")
        for pi, p in enumerate(sub["m_Passes"]):
            names = dict((idx, nm) for nm, idx in p["m_NameIndices"])
            lines.append(f" Pass {pi} {PASSTYPE.get(p['m_Type'])} name='{p['m_Name']}' use='{p['m_UseName']}' tags {p['m_Tags']['tags']}")
            if p["m_Type"] != 0:
                continue
            lines.append(f"   {state_text(p['m_State'])}")
            for stage, short in STAGES:
                prog = p.get(stage)
                if not prog:
                    continue
                tiers = prog["m_PlayerSubPrograms"]
                if not any(tiers):
                    continue
                cp = prog["m_CommonParameters"]
                common_cbs = {}
                for cb in cp["m_ConstantBuffers"]:
                    params = [(v["m_Index"], names.get(v["m_NameIndex"], "?"), v["m_Type"], v["m_Dim"], v["m_ArraySize"]) for v in cb["m_VectorParams"]]
                    params += [(m["m_Index"], names.get(m["m_NameIndex"], "?") + f"[{m['m_RowCount']}x4]", m["m_Type"], 4 * m["m_RowCount"], m["m_ArraySize"]) for m in cb["m_MatrixParams"]]
                    common_cbs[names.get(cb["m_NameIndex"], "?")] = {"size": cb["m_Size"], "params": params}
                common_bind = {names.get(b["m_NameIndex"], "?"): b["m_Index"] for b in cp["m_ConstantBufferBindings"]}
                common_tex = [(names.get(t["m_NameIndex"], "?"), t["m_Index"], t["m_SamplerIndex"], t["m_Dim"]) for t in cp["m_TextureParams"]]
                lines.append(f"   {short}: common cbuffers {{{', '.join(f'{k}:{v['size']}' for k, v in common_cbs.items())}}} bound {common_bind} textures {common_tex}")
                layouts = {cbn: {(q[0], q[1], q[3]) for q in info["params"]} for cbn, info in common_cbs.items()}
                for ti, tier in enumerate(tiers):
                    for vi, e in enumerate(tier):
                        kws = [kwn[k] for k in e["m_KeywordIndices"]]
                        pbi = prog["m_ParameterBlobIndices"][ti][vi]
                        params = parse_param_blob(entry_bytes(segs, entries, pbi))
                        for cbn, info in params["cbs"].items():
                            layouts.setdefault(cbn, set()).update((q[0], q[1], q[3]) for q in info["params"])
                        bind_text = ", ".join(f"{b[1]}=t{b[2]}/s{b[3]}" if b[0] == "tex" else f"{b[1]}=b{b[2]}" for b in params["bindings"])
                        lines.append(f"     [{vi}] blob {e['m_BlobIndex']} tier {ti} kws {kws} :: {bind_text}" + (f" (leftover {params['leftover']})" if params.get("leftover") else ""))
                        if not everything and any(k in NOISE for k in kws):
                            continue
                        version, ptype, _, code, rest = read_subprogram(entry_bytes(segs, entries, e["m_BlobIndex"]))
                        text = annotate(dxbc.disassemble(code), common_cbs, common_bind, common_tex, params)
                        # later subshaders' passes share the first's numbers; keep them apart
                        fname = (f"s{si}_" if si else "") + f"p{pi}_{short}_{sanitize('_'.join(kws))}.txt"
                        with open(os.path.join(out, fname), "w", encoding="utf-8") as f:
                            f.write(f"// {name} pass {pi} {stage} keywords {kws}\n// {state_text(p['m_State'])}\n")
                            f.write("// cbuffers: " + "; ".join(f"b{slot}={cbn}" for cbn, slot in cb_slots(common_bind, params).items()) + "\n")
                            f.write(text)
                        written += 1
                for cbn, entries_ in layouts.items():
                    if cbn.startswith("Unity") and cbn != "UnityLighting":
                        continue
                    lines.append(f"     layout {cbn}: " + ", ".join(f"{off}:{nm}({dim})" for off, nm, dim in sorted(entries_)))
    with open(os.path.join(out, "summary.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    print(f"{name}: summary + {written} listings in {out}")


def cb_slots(common_bind, params):
    slots = dict(common_bind)
    for b in params["bindings"]:
        if b[0] == "cb":
            slots[b[1]] = b[2]
    return slots


def annotate(text, common_cbs, common_bind, common_tex, params):
    # slot -> (cb name, [(offset, name, dim)])
    slots = {}
    cbs = dict(common_cbs)
    for cbn, info in params["cbs"].items():
        merged = dict(cbs.get(cbn, {"size": info["size"], "params": []}))
        merged = {"size": merged["size"], "params": list(merged["params"]) + [(q[0], q[1], q[2], q[3], q[4]) for q in info["params"]]}
        cbs[cbn] = merged
    for cbn, slot in cb_slots(common_bind, params).items():
        slots[slot] = (cbn, cbs.get(cbn, {"params": []})["params"])
    tex = {}
    for t in common_tex:
        tex[f"t{t[1]}"] = t[0]
        tex[f"s{t[2]}"] = t[0]
    for b in params["bindings"]:
        if b[0] == "tex":
            tex[f"t{b[2]}"] = b[1]
            tex[f"s{b[3]}"] = b[1]

    def cbname(m):
        slot, reg = int(m.group(1)), int(m.group(2))
        if slot not in slots:
            return None
        cbn, plist = slots[slot]
        off = reg * 16
        found = []
        for (poff, pname, ptype, dim, arr) in plist:
            size = max(dim, 1) * 4 * max(arr, 1)
            if poff < off + 16 and poff + size > off:
                if size > 16 and poff <= off:
                    found.append(f"{pname}[{(off - poff) // 16}]")
                elif poff <= off and poff + size >= off + 16:
                    found.append(pname)
                else:
                    # a scalar/vector inside this register: name its components
                    first = max(poff, off) - off
                    comps = "xyzw"[first // 4:(min(poff + size, off + 16) - off) // 4]
                    found.append(f"{comps}:{pname}")
        return " ".join(found) if found else f"{cbn}+{off}"

    out = []
    for line in text.splitlines():
        if line.startswith("//") or line.startswith("dcl_") or not line.strip():
            out.append(line)
            continue
        notes = []
        for m in re.finditer(r"cb(\d+)\[(\d+)\]", line):
            n = cbname(m)
            if n and f"cb{m.group(1)}[{m.group(2)}]={n}" not in notes:
                notes.append(f"cb{m.group(1)}[{m.group(2)}]={n}")
        for m in re.finditer(r"\b([ts]\d+)\b", line):
            if m.group(1) in tex and f"{m.group(1)}={tex[m.group(1)]}" not in notes:
                notes.append(f"{m.group(1)}={tex[m.group(1)]}")
        out.append(line + (("   // " + ", ".join(notes)) if notes else ""))
    return "\n".join(out)


main()

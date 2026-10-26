# Compile-checks the CGPROGRAM blocks of a Unity .shader with Windows' d3dcompiler, against Unity's own
# CGIncludes, the way Unity would for D3D11 (gamma colour space). Reports errors with original file/line.
#   hlslcheck.py <file.shader> [--kw A,B] [--kw ...] [--all-combos]
import sys, os, re, ctypes, itertools
from ctypes import wintypes

UNITY = os.environ.get("UNITY_CGINCLUDES", r"C:\Program Files\Unity 2022.3.43f1\Editor\Data\CGIncludes")
# the SDK's shader folder, from this script's place in the repo (Tools/ShaderStandins/decompile) or the environment
SDK_SHADERS = os.environ.get("SDK_SHADERS") or os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                                            "..", "..", "..", "Unity", "TushonkaMappingSDK", "Assets", "TarkovSDK", "Shaders"))

_d3d = ctypes.WinDLL("d3dcompiler_47.dll")
_d3d.D3DCompile.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_char_p,
                            ctypes.c_char_p, wintypes.UINT, wintypes.UINT, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p)]
_d3d.D3DCompile.restype = ctypes.c_long
_GetPtr = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)
_GetSize = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)
_Release = ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)


class Macro(ctypes.Structure):
    _fields_ = [("Name", ctypes.c_char_p), ("Definition", ctypes.c_char_p)]


def blob_text(blob):
    vtable = ctypes.cast(ctypes.c_void_p(blob), ctypes.POINTER(ctypes.c_void_p))[0]
    entries = ctypes.cast(ctypes.c_void_p(vtable), ctypes.POINTER(ctypes.c_void_p))
    ptr = _GetPtr(entries[3])(blob)
    size = _GetSize(entries[4])(blob)
    text = ctypes.string_at(ptr, size).decode("utf-8", "replace").rstrip("\x00")
    _Release(entries[2])(blob)
    return text


def compile_hlsl(source, entry, profile, defines):
    macros = (Macro * (len(defines) + 1))()
    for i, (k, v) in enumerate(defines):
        macros[i] = Macro(k.encode(), str(v).encode())
    src = source.encode("utf-8")
    code, errors = ctypes.c_void_p(), ctypes.c_void_p()
    # D3DCOMPILE_SKIP_OPTIMIZATION keeps it fast; 0x800 = D3DCOMPILE_ENABLE_STRICTNESS is off like Unity
    hr = _d3d.D3DCompile(src, len(src), b"shader", macros, None, entry.encode(), profile.encode(), 0x4 | 0x1000, 0, ctypes.byref(code), ctypes.byref(errors))
    text = blob_text(errors.value) if errors.value else ""
    if code.value:
        _Release(ctypes.cast(ctypes.cast(ctypes.c_void_p(code.value), ctypes.POINTER(ctypes.c_void_p))[0], ctypes.POINTER(ctypes.c_void_p))[2])(code.value)
    return hr == 0, text


def flatten(path, search, seen_stack=()):
    """Inline #includes recursively with #line directives so errors point at the real files."""
    out = [f'#line 1 "{path}"']
    with open(path, encoding="utf-8", errors="replace") as f:
        lines = f.read().split("\n")
    for i, line in enumerate(lines, 1):
        m = re.match(r'\s*#\s*include\s+"([^"]+)"', line)
        if m:
            name = m.group(1)
            found = None
            for d in [os.path.dirname(path)] + search:
                cand = os.path.normpath(os.path.join(d, name))
                if os.path.exists(cand):
                    found = cand
                    break
            if found is None:
                out.append(f'#error include not found: {name}')
            elif found in seen_stack:
                out.append("// recursive include skipped")
            else:
                out.extend(flatten(found, search, seen_stack + (found,)))
                out.append(f'#line {i + 1} "{path}"')
            continue
        if re.match(r"\s*#\s*pragma\b", line):
            out.append("")  # Unity pragmas mean nothing to fxc
            continue
        out.append(line)
    return out


def blocks(text):
    """(pass tags, program text, start line) for each CGPROGRAM/HLSLPROGRAM block."""
    result = []
    # Unity prepends every CGINCLUDE/HLSLINCLUDE block of the file to each program
    shared = "\n".join(m.group(2) for m in re.finditer(r"\b(CGINCLUDE|HLSLINCLUDE)\b(.*?)\b(ENDCG|ENDHLSL)\b", text, re.S))
    text = re.sub(r"\b(CGINCLUDE|HLSLINCLUDE)\b.*?\b(ENDCG|ENDHLSL)\b", lambda m: "\n" * m.group(0).count("\n"), text, flags=re.S)
    pos = 0
    while True:
        m = re.search(r"\b(CGPROGRAM|HLSLPROGRAM)\b", text[pos:])
        if not m:
            break
        start = pos + m.end()
        end_kw = "ENDCG" if m.group(1) == "CGPROGRAM" else "ENDHLSL"
        e = text.find(end_kw, start)
        if e < 0:
            break
        head = text[:pos + m.start()]
        # LightMode of the innermost Pass before this block
        last_pass = head.rfind("Pass")
        tags = re.findall(r'"(\w+)"\s*=\s*"([^"]+)"', head[last_pass:]) if last_pass >= 0 else []
        result.append((dict(tags), shared + "\n" + text[start:e], text[:start].count("\n") + 1 - shared.count("\n") - 1))
        pos = e + len(end_kw)
    return result


def pass_keyword_sets(tags):
    """the engine keyword sets Unity's multi_compile_fwdbase/fwdadd shortcuts would add"""
    mode = tags.get("LightMode", "")
    if mode == "ForwardBase": return [["DIRECTIONAL"], ["DIRECTIONAL", "SHADOWS_SCREEN", "LIGHTPROBE_SH", "VERTEXLIGHT_ON"]]
    if mode == "ForwardAdd": return [["POINT"], ["SPOT", "SHADOWS_DEPTH"], ["DIRECTIONAL", "SHADOWS_SCREEN"]]
    if mode == "ShadowCaster": return [["SHADOWS_DEPTH"], ["SHADOWS_CUBE"]]
    return [[]]


def pass_defines(tags):
    mode = tags.get("LightMode", "")
    d = []
    if mode == "ForwardBase": d.append(("UNITY_PASS_FORWARDBASE", 1))
    elif mode == "ForwardAdd": d.append(("UNITY_PASS_FORWARDADD", 1))
    elif mode == "ShadowCaster": d.append(("UNITY_PASS_SHADOWCASTER", 1))
    elif mode == "Deferred": d.append(("UNITY_PASS_DEFERRED", 1))
    elif mode == "Meta": d.append(("UNITY_PASS_META", 1))
    return d


BASE_DEFINES = [("SHADER_API_D3D11", 1), ("SHADER_API_DESKTOP", 1), ("UNITY_COMPILER_HLSL", 1), ("UNITY_VERSION", 202234),
                ("UNITY_COLORSPACE_GAMMA", 1), ("UNITY_UV_STARTS_AT_TOP", 1), ("UNITY_REVERSED_Z", 1), ("UNITY_PBS_USE_BRDF1", 1),
                ("UNITY_SPECCUBE_BOX_PROJECTION", 1), ("UNITY_SPECCUBE_BLENDING", 1), ("UNITY_ENABLE_REFLECTION_BUFFERS", 1),
                ("UNITY_LIGHT_PROBE_PROXY_VOLUME", 1), ("UNITY_ENABLE_DETAIL_NORMALMAP", 1), ("UNITY_HARDWARE_TIER3", 1),
                ("UNITY_USE_DITHER_MASK_FOR_ALPHABLENDED_SHADOWS", 1), ("UNITY_NO_SCREENSPACE_SHADOWS", 0)]


def main():
    args = sys.argv[1:]
    path = args[0]
    extra = []
    all_combos = "--all-combos" in args
    for i, a in enumerate(args):
        if a == "--kw":
            extra.append([k for k in args[i + 1].split(",") if k])
    text = open(path, encoding="utf-8").read()
    search = [SDK_SHADERS, os.path.join(SDK_SHADERS, "Game"), UNITY]
    failures = 0
    for tags, body, line0 in blocks(text):
        vert = re.search(r"#pragma\s+vertex\s+(\w+)", body)
        frag = re.search(r"#pragma\s+fragment\s+(\w+)", body)
        target = re.search(r"#pragma\s+target\s+([\d.]+)", body)
        if not vert or not frag:
            print(f"{path}:{line0}: skipped (no vertex/fragment pragma; surface shader?)")
            continue
        sm = "5_0" if target and float(target.group(1)) >= 5 else "4_0"
        groups = []
        for m in re.finditer(r"#pragma\s+(?:multi_compile|shader_feature)(?:_local)?(?:_fragment|_vertex)?[ \t]+([^\n]*)", body):
            words = [w for w in m.group(1).split() if not w.startswith("//")]
            groups.append([w if w not in ("__", "_") else "" for w in words])
        combos = [[]]
        if groups:
            if all_combos:
                combos = [[k for k in c if k] for c in itertools.product(*groups)]
            else:
                combos = [[]]
                for g in groups:
                    for alt in g:
                        if alt and [alt] not in combos:
                            combos.append([alt])
                combos.append([g[0] for g in groups if g[0]])
        combos += extra
        # the block itself, with includes inlined; write it to a temp file so #line names it
        tmp = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_block.hlsl")
        with open(tmp, "w", encoding="utf-8") as f:
            f.write(body)
        flat = "\n".join(flatten(tmp, search))
        flat = flat.replace(f'"{tmp}"', f'"{os.path.basename(path)}+{line0 - 1}"')
        combos = [c + e for c in combos for e in pass_keyword_sets(tags)]
        for combo in combos:
            defines = BASE_DEFINES + pass_defines(tags) + [(k, 1) for k in combo]
            for entry, stage in ((vert.group(1), "vs"), (frag.group(1), "ps")):
                ok, log = compile_hlsl(flat, entry, f"{stage}_{sm}", defines)
                errors = [l for l in log.splitlines() if "error" in l.lower()]
                if not ok or errors:
                    failures += 1
                    print(f"FAIL {os.path.basename(path)} pass {tags.get('LightMode', '?')} {stage} {entry} kws={combo}")
                    for l in (errors or log.splitlines())[:12]:
                        print("   ", l.strip())
        print(f"{os.path.basename(path)}: pass {tags.get('LightMode', '?')}: {len(combos)} keyword set(s) checked")
    print("FAILURES", failures)
    sys.exit(1 if failures else 0)


main()

"""Extracts a retail map's weather and sky setup for the Unity SDK's "Full Weather System".

Reads the "Weather" hierarchy (TOD_Sky, WeatherController, clouds, wind, rain, snow, ambient)
from a built-in scene of the user's own game install and writes, into the SDK project:

    weather.json            objects, transforms, every component's serialized values, materials
    Textures/<name>.png     textures the setup's materials and components use
    Meshes/<name>.json      sky dome / sun / moon meshes as vertex data

The SDK's builder (Tarkov SDK > Map > Time of Day / Weather...) recreates the hierarchy from these
files. Nothing here is shipped with the SDK: each author extracts from their own game.

Usage (needs UnityPy and TypeTreeGeneratorAPI: pip install UnityPy TypeTreeGeneratorAPI):
    python extract_weather.py <EscapeFromTarkov_Data> <output dir> [scene level, default 15 = Customs] [root, default Weather]
    python extract_weather.py <EscapeFromTarkov_Data> <output dir> --mixer-only

Both also write mixer_effects.json: the per-room ambient reverb/delay/send settings of Customs' sound
scene, used by the SDK's Ambient Audio System.
"""
import json
import os
import struct
import sys

import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

UNITY_VERSION = "2022.3.43f1"


class Extractor:
    def __init__(self, data_dir, level, out_dir):
        self.data_dir = data_dir
        self.out_dir = out_dir
        gen = TypeTreeGenerator(UNITY_VERSION)
        gen.load_local_dll_folder(os.path.join(data_dir, "Managed"))
        self.generator = gen
        env = UnityPy.load(os.path.join(data_dir, f"level{level}"))
        env.typetree_generator = gen
        self.sf = list(env.files.values())[0]
        self.externals = [e.path for e in self.sf.externals]
        self._ext_files = {}
        self.materials, self.textures, self.meshes, self.assets = {}, {}, {}, {}
        os.makedirs(os.path.join(out_dir, "Textures"), exist_ok=True)
        os.makedirs(os.path.join(out_dir, "Meshes"), exist_ok=True)

    # ------------------------------------------------------------ object access

    def file(self, file_id):
        if file_id == 0:
            return self.sf
        path = self.externals[file_id - 1]
        if path not in self._ext_files:
            name = os.path.basename(path)
            if name == "unity default resources":
                full = os.path.join(self.data_dir, "Resources", name)
            else:
                full = os.path.join(self.data_dir, name)
            env = UnityPy.load(full)
            env.typetree_generator = self.generator
            self._ext_files[path] = list(env.files.values())[0]
        return self._ext_files[path]

    def obj(self, file, path_id):
        return file.objects[path_id]

    @staticmethod
    def mb_header(obj):
        raw = obj.get_raw_data()
        go = struct.unpack_from("<q", raw, 4)[0]
        sfid, spid = struct.unpack_from("<iq", raw, 16)
        return go, sfid, spid

    def script_name(self, file, sfid, spid):
        # a script reference inside an external file is relative to that file's own externals
        owner = file
        target_file = owner if sfid == 0 else self._relative(owner, sfid)
        script = target_file.objects[spid].read()
        return (script.m_Namespace + "." if script.m_Namespace else "") + script.m_ClassName

    def _relative(self, owner, file_id):
        if owner is self.sf:
            return self.file(file_id)
        path = owner.externals[file_id - 1].path
        name = os.path.basename(path)
        full = os.path.join(self.data_dir, "Resources", name) if name == "unity default resources" else os.path.join(self.data_dir, name)
        if path not in self._ext_files:
            env = UnityPy.load(full)
            env.typetree_generator = self.generator
            self._ext_files[path] = list(env.files.values())[0]
        return self._ext_files[path]

    # ------------------------------------------------------------ references

    def ref(self, pptr, owner):
        fid, pid = pptr.get("m_FileID", 0), pptr.get("m_PathID", 0)
        if pid == 0:
            return None
        if owner is self.sf and fid == 0:
            return {"ref": "self", "pid": pid}
        try:
            file = owner if fid == 0 else self._relative(owner, fid)
            obj = file.objects[pid]
        except Exception:
            return {"ref": "missing"}
        kind = obj.type.name
        try:
            if kind == "Material":
                return {"ref": "material", "key": self.material(file, obj)}
            if kind in ("Texture2D", "Cubemap"):
                return {"ref": "texture", "key": self.texture(obj)}
            if kind == "Mesh":
                return {"ref": "mesh", "key": self.mesh(obj)}
            if kind == "Shader":
                return {"ref": "shader", "name": obj.read().m_ParsedForm.m_Name}
            if kind == "ComputeShader":
                return {"ref": "computeshader", "name": obj.read().m_Name}
            if kind == "MonoBehaviour":
                return {"ref": "asset", "key": self.scriptable(file, obj)}
            if kind == "RenderTexture":
                rt = obj.read_typetree()
                return {"ref": "rendertexture", "name": rt.get("m_Name"), "width": rt.get("m_Width", 256),
                        "height": rt.get("m_Height", 256), "format": rt.get("m_ColorFormat", 0), "depth": rt.get("m_DepthStencilFormat", 0)}
            return {"ref": "unsupported", "type": kind, "name": getattr(obj.read(), "m_Name", None)}
        except Exception as e:
            return {"ref": "error", "type": kind, "error": str(e)}

    def convert(self, value, owner):
        if isinstance(value, dict):
            if set(value.keys()) == {"m_FileID", "m_PathID"}:
                return self.ref(value, owner)
            return {k: self.convert(v, owner) for k, v in value.items()}
        if isinstance(value, list):
            return [self.convert(v, owner) for v in value]
        if isinstance(value, (bytes, bytearray)):
            return None
        return value

    # ------------------------------------------------------------ assets

    def material(self, file, obj):
        key = f"{obj.read().m_Name}_{obj.path_id & 0xffffffff:08x}"
        if key in self.materials:
            return key
        self.materials[key] = None  # cycle guard
        t = obj.read_typetree()
        shader = self.ref(t["m_Shader"], file)
        props = t.get("m_SavedProperties", {})
        textures = {}
        for name, env in props.get("m_TexEnvs", []):
            tex = self.ref(env["m_Texture"], file)
            textures[name] = {
                "texture": tex["key"] if tex and tex.get("ref") == "texture" else None,
                "scale": [env["m_Scale"]["x"], env["m_Scale"]["y"]],
                "offset": [env["m_Offset"]["x"], env["m_Offset"]["y"]],
            }
        self.materials[key] = {
            "name": t["m_Name"],
            "shader": shader.get("name") if shader else None,
            "renderQueue": t.get("m_CustomRenderQueue", -1),
            "keywords": t.get("m_ValidKeywords") or (t.get("m_ShaderKeywords") or "").split(),
            "textures": textures,
            "floats": {n: v for n, v in props.get("m_Floats", [])},
            "ints": {n: v for n, v in props.get("m_Ints", [])},
            "colors": {n: [v["r"], v["g"], v["b"], v["a"]] for n, v in props.get("m_Colors", [])},
        }
        return key

    def texture(self, obj):
        data = obj.read()
        key = f"{data.m_Name}_{obj.path_id & 0xffffffff:08x}"
        if key in self.textures:
            return key
        info = {"name": data.m_Name, "type": obj.type.name, "file": None, "normal": False}
        try:
            if obj.type.name == "Texture2D":
                image = data.image
                fmt = str(data.m_TextureFormat)
                lower = data.m_Name.lower()
                if lower.endswith("_n") or "normal" in lower:
                    info["normal"] = True
                    image = self.unpack_normal(image, fmt)
                path = os.path.join("Textures", key + ".png")
                image.save(os.path.join(self.out_dir, path))
                info["file"] = path.replace("\\", "/")
                info["linear"] = not getattr(data, "m_ColorSpace", 1)
                info["wrap"] = data.m_TextureSettings.m_WrapU if hasattr(data, "m_TextureSettings") else 0
            else:
                info["skipped"] = "cubemaps are not extracted"
        except Exception as e:
            info["skipped"] = str(e)
        self.textures[key] = info
        return key

    @staticmethod
    def unpack_normal(image, fmt):
        # game normal maps are stored with X in alpha (DXT5nm) or as RG (BC5); Unity's normal map
        # importer wants an ordinary RGB normal map
        image = image.convert("RGBA")
        r, g, b, a = image.split()
        if "BC5" in fmt:
            x, y = r, g
        elif "DXT5" in fmt or "BC3" in fmt:
            x, y = a, g
        else:
            return image.convert("RGB")
        from PIL import Image, ImageChops
        import math
        px, py = x.load(), y.load()
        z = Image.new("L", image.size)
        pz = z.load()
        for j in range(image.size[1]):
            for i in range(image.size[0]):
                nx, ny = px[i, j] / 127.5 - 1, py[i, j] / 127.5 - 1
                pz[i, j] = int((math.sqrt(max(0.0, 1 - nx * nx - ny * ny)) * 0.5 + 0.5) * 255)
        return Image.merge("RGB", (x, y, z))

    def mesh(self, obj):
        data = obj.read()
        key = f"{data.m_Name}_{obj.path_id & 0xffffffff:08x}"
        if key in self.meshes:
            return key
        handler = MeshHandler(data)
        handler.process()
        submeshes = []
        for sub in handler.get_triangles():
            submeshes.append([i for tri in sub for i in tri])
        out = {
            "name": data.m_Name,
            "vertices": [list(v) for v in handler.m_Vertices or []],
            "normals": [list(v) for v in handler.m_Normals or []],
            "uv": [list(v) for v in handler.m_UV0 or []],
            "submeshes": submeshes,
        }
        path = os.path.join("Meshes", key + ".json")
        with open(os.path.join(self.out_dir, path), "w") as f:
            json.dump(out, f)
        self.meshes[key] = {"name": data.m_Name, "file": path.replace("\\", "/")}
        return key

    def scriptable(self, file, obj):
        key = f"asset_{obj.path_id & 0xffffffff:08x}"
        if key in self.assets:
            return key
        self.assets[key] = None
        _, sfid, spid = self.mb_header(obj)
        tree = obj.read_typetree()
        self.assets[key] = {
            "name": tree.get("m_Name"),
            "type": self.script_name(file, sfid, spid),
            "fields": self.convert({k: v for k, v in tree.items() if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")}, file),
        }
        return key

    # ------------------------------------------------------------ camera

    # The weather system also needs components on the player camera: fog (TOD_Scattering) and
    # rain on the screen. Retail maps get them from LevelSettings.CameraPrefab; SDK maps use the
    # game's fallback camera, so the client copies these onto it.
    CAMERA_TYPES = ("TOD_Scattering", "ScreenWater", "RainScreenDrops", "PrismEffects")

    def camera_effects(self):
        prefab = None
        for o in self.sf.objects.values():
            if o.type.name != "MonoBehaviour":
                continue
            try:
                _, sfid, spid = self.mb_header(o)
                if self.script_name(self.sf, sfid, spid) != "LevelSettings":
                    continue
                prefab = o.read_typetree().get("CameraPrefab")
                break
            except Exception:
                continue
        if not prefab or not prefab.get("m_PathID"):
            print("no LevelSettings.CameraPrefab; camera effects not extracted")
            return []
        file = self.file(prefab["m_FileID"])
        camera = file.objects[prefab["m_PathID"]].read()
        go = file.objects[camera.m_GameObject.path_id].read()
        out = []
        for entry in go.m_Component:
            pptr = entry.component if hasattr(entry, "component") else entry
            obj = file.objects[pptr.path_id]
            if obj.type.name != "MonoBehaviour":
                continue
            _, sfid, spid = self.mb_header(obj)
            kind = self.script_name(file, sfid, spid)
            if kind not in self.CAMERA_TYPES:
                continue
            tree = obj.read_typetree()
            fields = {k: v for k, v in tree.items() if k not in ("m_GameObject", "m_Script", "m_Name", "m_EditorClassIdentifier", "m_EditorHideFlags")}
            out.append({"type": kind, "fields": self.convert(fields, file)})
        print(f"camera effects from {go.m_Name}: {[c['type'] for c in out]}")
        return out

    # ------------------------------------------------------------ hierarchy

    def extract(self, root_name):
        sf = self.sf
        names, transforms, go_of_transform = {}, {}, {}
        for o in sf.objects.values():
            if o.type.name == "GameObject":
                names[o.path_id] = o.read_typetree()
            elif o.type.name in ("Transform", "RectTransform"):
                t = o.read_typetree()
                transforms[o.path_id] = t
                go_of_transform[o.path_id] = t["m_GameObject"]["m_PathID"]
        transform_of_go = {go: tid for tid, go in go_of_transform.items()}
        root = next(pid for pid, go in names.items() if go["m_Name"] == root_name
                    and transforms[transform_of_go[pid]]["m_Father"]["m_PathID"] == 0)

        order, stack = [], [root]
        while stack:
            go = stack.pop(0)
            order.append(go)
            t = transforms[transform_of_go[go]]
            stack[0:0] = [go_of_transform[c["m_PathID"]] for c in t["m_Children"]]
        wanted = set(order)

        objects = []
        for go in order:
            g, t = names[go], transforms[transform_of_go[go]]
            father = t["m_Father"]["m_PathID"]
            objects.append({
                "pid": go,
                "transform": transform_of_go[go],
                "name": g["m_Name"],
                "parent": go_of_transform.get(father) if go != root else None,
                "active": bool(g["m_IsActive"]),
                "layer": g["m_Layer"],
                "tag": g.get("m_Tag", 0),
                "position": [t["m_LocalPosition"][c] for c in "xyz"],
                "rotation": [t["m_LocalRotation"][c] for c in "xyzw"],
                "scale": [t["m_LocalScale"][c] for c in "xyz"],
            })

        components = []
        for o in sf.objects.values():
            kind = o.type.name
            if kind in ("GameObject", "Transform", "RectTransform"):
                continue
            try:
                if kind == "MonoBehaviour":
                    go, sfid, spid = self.mb_header(o)
                    if go not in wanted:
                        continue
                    kind = self.script_name(sf, sfid, spid)
                    tree = o.read_typetree()
                else:
                    tree = o.read_typetree()
                    go = tree.get("m_GameObject", {}).get("m_PathID")
                    if go not in wanted:
                        continue
            except Exception as e:
                print(f"skipped {kind} {o.path_id}: {e}")
                continue
            fields = {k: v for k, v in tree.items() if k not in ("m_GameObject", "m_Script", "m_Name", "m_EditorClassIdentifier", "m_EditorHideFlags")}
            components.append({"pid": o.path_id, "go": go, "type": kind, "fields": self.convert(fields, sf)})

        out = {
            "source": f"level{sys.argv[3] if len(sys.argv) > 3 else 15}",
            "root": root,
            "objects": objects,
            "components": components,
            "camera": self.camera_effects(),
            "materials": self.materials,
            "textures": self.textures,
            "meshes": self.meshes,
            "assets": self.assets,
        }
        with open(os.path.join(self.out_dir, "weather.json"), "w") as f:
            json.dump(out, f, indent=1)
        print(f"{len(objects)} objects, {len(components)} components, {len(self.materials)} materials, "
              f"{len(self.textures)} textures, {len(self.meshes)} meshes, {len(self.assets)} assets -> {self.out_dir}")
        shaders = sorted({m["shader"] for m in self.materials.values() if m and m.get("shader")})
        print("material shaders:", shaders)


# ---------------------------------------------------------------- ambient audio mixer data
#
# The SDK's Ambient Audio System needs the game's per-room mixer settings (ambient reverb, delay
# and effect sends for outdoor / indoor / isolated rooms): AmbientAudioSystem.EffectsData on Customs'
# sound scene. Those ScriptableObjects hold enum-keyed dictionaries that the type tree generator
# reads wrongly, so they're parsed from their raw bytes; the layout is simple and fixed.

class _Raw:
    def __init__(self, data):
        self.b, self.o = data, 0

    def i32(self):
        v = struct.unpack_from("<i", self.b, self.o)[0]
        self.o += 4
        return v

    def f32(self):
        v = struct.unpack_from("<f", self.b, self.o)[0]
        self.o += 4
        return v

    def pptr(self):
        fid, pid = struct.unpack_from("<iq", self.b, self.o)
        self.o += 12
        return fid, pid

    def string(self):
        n = self.i32()
        v = self.b[self.o:self.o + n].decode("utf8")
        self.o = (self.o + n + 3) & ~3
        return v

    def mono_header(self):
        self.pptr()                       # m_GameObject
        self.o = (self.o + 1 + 3) & ~3     # m_Enabled
        self.pptr()                       # m_Script
        return self.string()              # m_Name

    def mixer_parameter(self):
        name = self.string()
        keys = [self.string() for _ in range(self.i32())]
        values = [self.f32() for _ in range(self.i32())]
        return {"paramName": name, "keys": keys, "values": values}


def _scene_level(data_dir, scene_name):
    env = UnityPy.load(os.path.join(data_dir, "globalgamemanagers"))
    for o in env.objects:
        if o.type.name == "BuildSettings":
            for i, path in enumerate(o.read_typetree()["scenes"]):
                if os.path.splitext(os.path.basename(path))[0].lower() == scene_name.lower():
                    return i
    return None


def extract_mixer_effects(data_dir, out_dir, scene_name="Custom_Sound"):
    level = _scene_level(data_dir, scene_name)
    if level is None:
        print(f"mixer effects: scene {scene_name} not found; skipped")
        return
    env = UnityPy.load(os.path.join(data_dir, f"level{level}"))
    sf = list(env.files.values())[0]
    files = {0: sf}

    def file_of(owner, fid):
        if fid == 0:
            return owner
        name = os.path.basename(owner.externals[fid - 1].path)
        full = os.path.join(data_dir, "Resources", name) if name == "unity default resources" else os.path.join(data_dir, name)
        if full not in files:
            files[full] = list(UnityPy.load(full).files.values())[0]
        return files[full]

    for o in sf.objects.values():
        if o.type.name != "MonoBehaviour":
            continue
        raw = o.get_raw_data()
        sfid, spid = struct.unpack_from("<iq", raw, 16)
        try:
            script = file_of(sf, sfid).objects[spid].read()
        except Exception:
            continue
        if script.m_ClassName != "AmbientAudioSystem":
            continue
        r = _Raw(raw)
        r.mono_header()
        r.pptr()                             # _ambientData
        fid, pid = r.pptr()                  # EffectsData
        owner = file_of(sf, fid)
        data = _Raw(owner.objects[pid].get_raw_data())
        out = {"name": data.mono_header(), "source": f"level{level}"}
        for label in ("reverbMixerData", "delayMixerData", "effectsSendsMixerData", "effectsVolumesMixerData"):
            sfid2, spid2 = data.pptr()
            so = _Raw(file_of(owner, sfid2).objects[spid2].get_raw_data())
            out[label] = {"name": so.mono_header()}
            if label == "reverbMixerData":
                out[label]["params"] = [so.mixer_parameter() for _ in range(13)]
            elif label == "delayMixerData":
                out[label]["params"] = [so.mixer_parameter() for _ in range(5)]
            else:
                out[label]["params"] = [so.mixer_parameter() for _ in range(so.i32())]
        with open(os.path.join(out_dir, "mixer_effects.json"), "w") as f:
            json.dump(out, f, indent=1)
        print(f"mixer effects from {scene_name} (level{level}) -> {out_dir}")
        return
    print(f"mixer effects: no AmbientAudioSystem in {scene_name}; skipped")


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    if len(sys.argv) > 3 and sys.argv[3] == "--mixer-only":
        extract_mixer_effects(sys.argv[1], sys.argv[2])
        sys.exit(0)
    level = int(sys.argv[3]) if len(sys.argv) > 3 else 15
    root = sys.argv[4] if len(sys.argv) > 4 else "Weather"
    Extractor(sys.argv[1], level, sys.argv[2]).extract(root)
    extract_mixer_effects(sys.argv[1], sys.argv[2])

"""Writes editor stand-ins for the game's shaders into the Unity SDK.

Each stand-in is named "Tarkov/<game shader name>" and declares the game shader's exact properties
(names, types, defaults, keyword toggles), so materials authored with it carry the data the real
shader reads. Its own shading is a simple lit preview. At raid start the client plugin swaps every
"Tarkov/..." shader for the game's compiled shader of the same name (Client/Loading/ShaderSwap.cs).
The prefix keeps the two apart: a stand-in can never be mistaken for the real shader.

Usage (needs UnityPy: pip install UnityPy):
    python generate_standins.py <EscapeFromTarkov_Data/StreamingAssets/Windows/shaders> shaders.txt <output dir>
"""
import os
import re
import sys

import UnityPy

PREFIX = "Tarkov/"

# ShaderLab drawers and decorators the editor knows; game-only drawers (e.g. [Queue]) are dropped
KNOWN_ATTRIBUTES = {
    "Toggle", "ToggleOff", "MaterialToggle", "MaterialEnum", "KeywordEnum", "Enum", "Space", "Header",
    "IntRange", "PowerSlider", "NoScaleOffset", "Normal", "HDR", "Gamma", "HideInInspector", "MainTexture",
    "MainColor", "PerRendererData",
}
# SerializedProperty.m_Flags bits, as ShaderLab attributes
FLAG_ATTRIBUTES = [(1, "HideInInspector"), (2, "PerRendererData"), (4, "NoScaleOffset"), (8, "Normal"), (16, "HDR"), (32, "Gamma")]
TEXTURE_DIMENSIONS = {2: "2D", 3: "3D", 4: "Cube", 5: "2DArray"}


def number(value):
    text = f"{value:.6g}"
    return text if text not in ("-0",) else "0"


def property_line(p):
    name, desc, kind = p["m_Name"], p["m_Description"].replace('"', "'"), p["m_Type"]
    values = [p[f"m_DefValue[{i}]"] for i in range(4)]
    attributes = [a for a in p.get("m_Attributes", []) if re.match(r"[A-Za-z]+", a).group(0) in KNOWN_ATTRIBUTES]
    attributes += [a for bit, a in FLAG_ATTRIBUTES if p.get("m_Flags", 0) & bit and a not in attributes]
    prefix = "".join(f"[{a}] " for a in attributes)
    if kind == 0:
        body = f'Color) = ({", ".join(number(v) for v in values)})'
    elif kind == 1:
        body = f'Vector) = ({", ".join(number(v) for v in values)})'
    elif kind == 2:
        body = f"Float) = {number(values[0])}"
    elif kind == 3:
        body = f"Range({number(values[1])}, {number(values[2])})) = {number(values[0])}"
    elif kind == 4:
        texture = p.get("m_DefTexture", {})
        dimension = TEXTURE_DIMENSIONS.get(texture.get("m_TexDim"), "2D")
        default = texture.get("m_DefaultName") or ("" if dimension != "2D" else "white")
        body = f'{dimension}) = "{default}" {{}}'
    elif kind == 5:
        body = f"Int) = {int(values[0])}"
    else:
        return None
    return f'        {prefix}{name} ("{desc}", {body}'


def standin(shader_name, parsed):
    props = parsed["m_PropInfo"]["m_Props"]
    names = {p["m_Name"]: p for p in props}
    tags = dict(parsed["m_SubShaders"][0]["m_Tags"]["tags"]) if parsed["m_SubShaders"] else {}
    tags = {k.lower(): v for k, v in tags.items()}
    render_type = tags.get("rendertype", "Opaque")
    queue = tags.get("queue")

    textures_2d = [p["m_Name"] for p in props if p["m_Type"] == 4 and p.get("m_DefTexture", {}).get("m_TexDim") == 2]
    albedo = "_MainTex" if "_MainTex" in names else next((t for t in textures_2d if t != "_BumpMap"), None)
    normal = "_BumpMap" if "_BumpMap" in names else None
    color = "_Color" if "_Color" in names and names["_Color"]["m_Type"] in (0, 1) else None

    options = "Standard fullforwardshadows"
    clip = ""
    if render_type == "TransparentCutout":
        clip = "clip(c.a - (_Cutoff_Standin));"
    elif render_type == "Transparent":
        options += " alpha:fade"

    lines = [f'Shader "{PREFIX}{shader_name}"', "{", "    Properties", "    {"]
    lines += [line for line in (property_line(p) for p in props) if line]
    lines += ["    }", "    SubShader", "    {"]
    tag_text = f'"RenderType" = "{render_type}"' + (f' "Queue" = "{queue}"' if queue else "")
    lines += [f"        Tags {{ {tag_text} }}", "        CGPROGRAM", f"        #pragma surface surf {options}", "        #pragma target 3.0"]
    if albedo:
        lines.append(f"        sampler2D {albedo};")
    if normal and normal != albedo:
        lines.append(f"        sampler2D {normal};")
    if color:
        lines.append(f"        fixed4 {color};")
    if clip:
        cutoff = "_Cutoff" if "_Cutoff" in names and names["_Cutoff"]["m_Type"] in (2, 3) else None
        if cutoff:
            lines.append(f"        float {cutoff};")
        clip = clip.replace("_Cutoff_Standin", cutoff or "0.5")
    uv = f"uv{albedo}" if albedo else None
    lines += ["        struct Input", "        {", f"            float2 {uv or 'dummy'};", "        };"]
    lines += ["        void surf(Input IN, inout SurfaceOutputStandard o)", "        {"]
    sample = f"tex2D({albedo}, IN.{uv})" if albedo else "fixed4(0.5, 0.5, 0.5, 1)"
    lines.append(f"            fixed4 c = {sample}" + (f" * {color};" if color else ";"))
    if clip:
        lines.append(f"            {clip}")
    lines += ["            o.Albedo = c.rgb;", "            o.Alpha = c.a;", "            o.Smoothness = 0.25;"]
    if normal and albedo:
        lines.append(f"            o.Normal = UnpackNormal(tex2D({normal}, IN.{uv}));")
    lines += ["        }", "        ENDCG", "    }", '    FallBack "Diffuse"', "}", ""]
    header = [
        "// <auto-generated>",
        f"// Editor stand-in for the game shader \"{shader_name}\", generated by Tools/ShaderStandins.",
        "// Properties match the game's; the shading is only a preview. The client plugin swaps this",
        "// shader for the game's compiled one at raid start. Do not edit by hand.",
        "// </auto-generated>",
    ]
    return "\n".join(header + lines)


def main():
    bundle, list_file, output = sys.argv[1:4]
    wanted = [line.strip() for line in open(list_file, encoding="utf-8") if line.strip() and not line.startswith("#")]
    found = {}
    env = UnityPy.load(bundle)
    for obj in env.objects:
        if obj.type.name != "Shader":
            continue
        tree = obj.read_typetree()
        parsed = tree.get("m_ParsedForm") or {}
        if parsed.get("m_Name") in wanted:
            found[parsed["m_Name"]] = parsed
    os.makedirs(output, exist_ok=True)
    # shaders decompiled by hand live in Game/ next to the output folder and are never overwritten
    game_dir = os.path.join(os.path.dirname(os.path.normpath(output)), "Game")
    for name in wanted:
        if name not in found:
            print(f"not in the bundle: {name}")
            continue
        file_name = re.sub(r"[^A-Za-z0-9_.-]+", "_", name) + ".shader"
        if os.path.exists(os.path.join(game_dir, file_name)):
            print(f"decompiled by hand in Game/, skipped: {name}")
            continue
        with open(os.path.join(output, file_name), "w", encoding="utf-8", newline="\n") as f:
            f.write(standin(name, found[name]))
        print(f"wrote {file_name}")


if __name__ == "__main__":
    main()

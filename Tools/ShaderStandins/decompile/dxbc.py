# DXBC disassembly through Windows' d3dcompiler_47 (ctypes). Returns the assembly listing as text.
import ctypes
from ctypes import wintypes

_d3d = ctypes.WinDLL("d3dcompiler_47.dll")
_d3d.D3DDisassemble.argtypes = [ctypes.c_void_p, ctypes.c_size_t, wintypes.UINT, ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p)]
_d3d.D3DDisassemble.restype = ctypes.c_long

# ID3DBlob vtable: QueryInterface, AddRef, Release, GetBufferPointer, GetBufferSize
_GetPtr = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)
_GetSize = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)
_Release = ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)

D3D_DISASM_ENABLE_INSTRUCTION_NUMBERING = 0x40
D3D_DISASM_DISABLE_DEBUG_INFO = 0x10


def _blob_text(blob):
    vtable = ctypes.cast(ctypes.c_void_p(blob), ctypes.POINTER(ctypes.c_void_p))[0]
    entries = ctypes.cast(ctypes.c_void_p(vtable), ctypes.POINTER(ctypes.c_void_p))
    ptr = _GetPtr(entries[3])(blob)
    size = _GetSize(entries[4])(blob)
    text = ctypes.string_at(ptr, size).decode("utf-8", "replace").rstrip("\x00").replace("\r\n", "\n")
    _Release(entries[2])(blob)
    return text


def disassemble(code: bytes, numbering=False) -> str:
    start = code.find(b"DXBC")
    if start < 0:
        return "// no DXBC container found"
    data = code[start:]
    buf = ctypes.create_string_buffer(data, len(data))
    out = ctypes.c_void_p()
    flags = D3D_DISASM_ENABLE_INSTRUCTION_NUMBERING if numbering else 0
    hr = _d3d.D3DDisassemble(buf, len(data), flags, None, ctypes.byref(out))
    if hr != 0 or not out.value:
        return f"// D3DDisassemble failed hr=0x{hr & 0xffffffff:08x}"
    return _blob_text(out.value)

"""Replace Windows executable icons and verify their original ICO payloads."""
from __future__ import annotations
import ctypes
import os
from pathlib import Path
import struct

try:
    from .pe_manifest import read_embedded_manifest
except ImportError:
    from pe_manifest import read_embedded_manifest


def icon_resources(data: bytes) -> dict[tuple[int, int, int], bytes]:
    if not 6 <= len(data) <= 16 * 1024 * 1024:
        raise ValueError('ICO size is outside the supported bounds')
    reserved, kind, count = struct.unpack_from('<HHH', data)
    end = 6 + 16 * count
    if reserved or kind != 1 or not 1 <= count <= 256 or end > len(data):
        raise ValueError('Invalid ICO directory')
    resources = {}
    group = bytearray(data[:6])
    for index in range(count):
        entry = data[6 + 16 * index:22 + 16 * index]
        size, offset = struct.unpack_from('<II', entry, 8)
        if not size or offset < end or offset + size > len(data):
            raise ValueError('ICO image is outside the file')
        identifier = index + 1
        resources[(3, identifier, 0)] = data[offset:offset + size]
        group.extend(entry[:12] + struct.pack('<H', identifier))
    resources[(14, 1, 0)] = bytes(group)
    return resources


def _kernel():
    if os.name != 'nt':
        raise ValueError('PE icon resources require Windows')
    dll = ctypes.WinDLL('kernel32', use_last_error=True)
    signatures = {
        'LoadLibraryExW': ([ctypes.c_wchar_p, ctypes.c_void_p, ctypes.c_uint32], ctypes.c_void_p),
        'FreeLibrary': ([ctypes.c_void_p], ctypes.c_int),
        'EnumResourceNamesW': ([ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ssize_t], ctypes.c_int),
        'EnumResourceLanguagesW': ([ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ssize_t], ctypes.c_int),
        'FindResourceExW': ([ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint16], ctypes.c_void_p),
        'LoadResource': ([ctypes.c_void_p, ctypes.c_void_p], ctypes.c_void_p),
        'LockResource': ([ctypes.c_void_p], ctypes.c_void_p),
        'SizeofResource': ([ctypes.c_void_p, ctypes.c_void_p], ctypes.c_uint32),
        'BeginUpdateResourceW': ([ctypes.c_wchar_p, ctypes.c_int], ctypes.c_void_p),
        'UpdateResourceW': ([ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint16, ctypes.c_void_p, ctypes.c_uint32], ctypes.c_int),
        'EndUpdateResourceW': ([ctypes.c_void_p, ctypes.c_int], ctypes.c_int),
    }
    for name, (args, result) in signatures.items():
        function = getattr(dll, name)
        function.argtypes, function.restype = args, result
    return dll


def _name(value):
    return ctypes.c_void_p(value) if isinstance(value, int) else ctypes.cast(ctypes.c_wchar_p(value), ctypes.c_void_p)


def read_icon_resources(executable: Path) -> dict:
    dll = _kernel()
    module = dll.LoadLibraryExW(str(executable.resolve()), None, 0x22)
    if not module:
        raise ctypes.WinError(ctypes.get_last_error())
    resources, failures = {}, []
    name_callback = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ssize_t)
    language_callback = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint16, ctypes.c_ssize_t)
    try:
        for kind in (3, 14):
            def visit_name(_module, _kind, raw_name, _context):
                name = raw_name if raw_name <= 65535 else ctypes.wstring_at(raw_name)
                def visit_language(_module, _kind, _name, language, _context):
                    resource = dll.FindResourceExW(module, _kind, _name, language)
                    loaded = dll.LoadResource(module, resource) if resource else None
                    pointer = dll.LockResource(loaded) if loaded else None
                    size = dll.SizeofResource(module, resource) if resource else 0
                    if not pointer or not size:
                        failures.append(ctypes.get_last_error())
                        return 0
                    resources[(kind, name, language)] = ctypes.string_at(pointer, size)
                    return 1
                callback = language_callback(visit_language)
                if not dll.EnumResourceLanguagesW(module, _kind, raw_name, callback, 0):
                    failures.append(ctypes.get_last_error())
                    return 0
                return 1
            callback = name_callback(visit_name)
            if not dll.EnumResourceNamesW(module, _name(kind), callback, 0):
                error = ctypes.get_last_error()
                if error != 1813:
                    failures.append(error)
        if failures:
            raise ctypes.WinError(failures[0])
        return resources
    finally:
        dll.FreeLibrary(module)


def replace_icon(executable: Path, icon: Path) -> None:
    desired = icon_resources(icon.read_bytes())
    previous = read_icon_resources(executable)
    manifest = read_embedded_manifest(executable)
    dll = _kernel()
    handle = dll.BeginUpdateResourceW(str(executable.resolve()), False)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        for kind, name, language in previous:
            if not dll.UpdateResourceW(handle, _name(kind), _name(name), language, None, 0):
                raise ctypes.WinError(ctypes.get_last_error())
        for (kind, name, language), data in desired.items():
            buffer = ctypes.create_string_buffer(data)
            if not dll.UpdateResourceW(handle, _name(kind), _name(name), language, buffer, len(data)):
                raise ctypes.WinError(ctypes.get_last_error())
    except BaseException:
        dll.EndUpdateResourceW(handle, True)
        raise
    if not dll.EndUpdateResourceW(handle, False):
        raise ctypes.WinError(ctypes.get_last_error())
    if read_icon_resources(executable) != desired or read_embedded_manifest(executable) != manifest:
        raise ValueError('PE icon bytes or execution manifest do not match')

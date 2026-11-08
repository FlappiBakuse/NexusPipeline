"""Embed the product icon into the pinned desktop executable before inventorying it."""
import ctypes
from ctypes import wintypes
import hashlib
from pathlib import Path
import struct
import tempfile


def icon_images(data):
    if len(data) < 6 or struct.unpack_from('<HH', data) != (0, 1):
        raise ValueError('Invalid product icon')
    count = struct.unpack_from('<H', data, 4)[0]
    if not 1 <= count <= 64 or len(data) > 4 * 1024 * 1024 or len(data) < 6 + count * 16:
        raise ValueError('Product icon exceeds its bounds')
    images = []
    for index in range(count):
        entry = data[6 + index * 16:22 + index * 16]
        size, offset = struct.unpack_from('<II', entry, 8)
        if not size or offset < 6 + count * 16 or offset + size > len(data):
            raise ValueError('Invalid product icon image')
        images.append((entry[:12], data[offset:offset + size]))
    return images


def brand_executable(executable, icon, work):
    images = icon_images(icon)
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.LoadLibraryExW.argtypes = [wintypes.LPCWSTR, wintypes.HANDLE, wintypes.DWORD]
    kernel.LoadLibraryExW.restype = wintypes.HMODULE
    kernel.FreeLibrary.argtypes = [wintypes.HMODULE]
    kernel.BeginUpdateResourceW.argtypes = [wintypes.LPCWSTR, wintypes.BOOL]
    kernel.BeginUpdateResourceW.restype = wintypes.HANDLE
    kernel.UpdateResourceW.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p,
                                       wintypes.WORD, ctypes.c_void_p, wintypes.DWORD]
    kernel.EndUpdateResourceW.argtypes = [wintypes.HANDLE, wintypes.BOOL]
    names_callback = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HMODULE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ssize_t)
    language_callback = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HMODULE, ctypes.c_void_p, ctypes.c_void_p, wintypes.WORD, ctypes.c_ssize_t)
    kernel.EnumResourceNamesW.argtypes = [wintypes.HMODULE, ctypes.c_void_p, names_callback, ctypes.c_ssize_t]
    kernel.EnumResourceLanguagesW.argtypes = [wintypes.HMODULE, ctypes.c_void_p, ctypes.c_void_p, language_callback, ctypes.c_ssize_t]
    work.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='icon-', dir=work) as directory:
        target = Path(directory) / 'desktop.exe'
        target.write_bytes(executable)
        module = kernel.LoadLibraryExW(str(target), None, 0x22)
        if not module:
            raise ctypes.WinError(ctypes.get_last_error())
        resources = []

        @language_callback
        def language(_module, kind, name, lang, _parameter):
            resources.append((kind, name if name <= 65535 else ctypes.wstring_at(name), lang))
            return True

        @names_callback
        def named(module, kind, name, _parameter):
            return kernel.EnumResourceLanguagesW(module, kind, name, language, 0)

        try:
            for kind in (3, 14):
                kernel.EnumResourceNamesW(module, kind, named, 0)
        finally:
            kernel.FreeLibrary(module)
        update = kernel.BeginUpdateResourceW(str(target), False)
        if not update:
            raise ctypes.WinError(ctypes.get_last_error())
        committed = False
        try:
            for kind, name, lang in resources:
                resource = ctypes.cast(ctypes.c_wchar_p(name), ctypes.c_void_p) if isinstance(name, str) else name
                if not kernel.UpdateResourceW(update, kind, resource, lang, None, 0):
                    raise ctypes.WinError(ctypes.get_last_error())
            group = struct.pack('<HHH', 0, 1, len(images))
            for index, (entry, image) in enumerate(images, 1):
                data = ctypes.create_string_buffer(image)
                if not kernel.UpdateResourceW(update, 3, index, 0, data, len(image)):
                    raise ctypes.WinError(ctypes.get_last_error())
                group += entry + struct.pack('<H', index)
            data = ctypes.create_string_buffer(group)
            if not kernel.UpdateResourceW(update, 14, 1, 0, data, len(group)):
                raise ctypes.WinError(ctypes.get_last_error())
            if not kernel.EndUpdateResourceW(update, False):
                raise ctypes.WinError(ctypes.get_last_error())
            committed = True
        finally:
            if not committed:
                kernel.EndUpdateResourceW(update, True)
        result = target.read_bytes()
    return result, dict(sourceExecutableSha256=hashlib.sha256(executable).hexdigest(),
                        iconSha256=hashlib.sha256(icon).hexdigest(), executableSha256=hashlib.sha256(result).hexdigest(),
                        producerSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest())

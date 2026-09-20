"""Read and verify the execution-level manifest embedded in a Windows PE."""

from __future__ import annotations

import argparse
import ctypes
import os
from pathlib import Path
from xml.etree import ElementTree


RT_MANIFEST = 24
_MANIFEST_RESOURCE_IDS = (1, 2, 3)
_LOAD_LIBRARY_AS_DATAFILE = 0x00000002
_LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020


class PeManifestError(ValueError):
    """The target is not a readable PE manifest or has the wrong contract."""


def _resource_pointer(value: int) -> ctypes.c_wchar_p:
    return ctypes.cast(ctypes.c_void_p(value), ctypes.c_wchar_p)


def read_embedded_manifest(executable: Path) -> bytes:
    """Read RT_MANIFEST from a PE without executing the image."""

    if os.name != "nt":
        raise PeManifestError("PE manifest 读取只支持 Windows")
    executable = executable.resolve()
    if not executable.is_file():
        raise PeManifestError(f"PE 文件不存在：{executable}")

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    load_library = kernel32.LoadLibraryExW
    load_library.argtypes = [ctypes.c_wchar_p, ctypes.c_void_p, ctypes.c_uint32]
    load_library.restype = ctypes.c_void_p
    find_resource = kernel32.FindResourceW
    find_resource.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_wchar_p]
    find_resource.restype = ctypes.c_void_p
    load_resource = kernel32.LoadResource
    load_resource.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    load_resource.restype = ctypes.c_void_p
    lock_resource = kernel32.LockResource
    lock_resource.argtypes = [ctypes.c_void_p]
    lock_resource.restype = ctypes.c_void_p
    sizeof_resource = kernel32.SizeofResource
    sizeof_resource.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    sizeof_resource.restype = ctypes.c_uint32
    free_library = kernel32.FreeLibrary
    free_library.argtypes = [ctypes.c_void_p]
    free_library.restype = ctypes.c_int

    module = load_library(
        str(executable),
        None,
        _LOAD_LIBRARY_AS_DATAFILE | _LOAD_LIBRARY_AS_IMAGE_RESOURCE,
    )
    if not module:
        error = ctypes.get_last_error()
        raise PeManifestError(f"无法加载 PE 资源：{executable}（Win32={error}）")
    try:
        resource_type = _resource_pointer(RT_MANIFEST)
        resource = None
        for resource_id in _MANIFEST_RESOURCE_IDS:
            resource = find_resource(module, _resource_pointer(resource_id), resource_type)
            if resource:
                break
        if not resource:
            raise PeManifestError(f"PE 中缺少 RT_MANIFEST：{executable}")
        loaded = load_resource(module, resource)
        size = int(sizeof_resource(module, resource))
        pointer = lock_resource(loaded)
        if not loaded or not pointer or size <= 0:
            raise PeManifestError(f"PE manifest 资源为空：{executable}")
        return ctypes.string_at(pointer, size)
    finally:
        free_library(module)


def requested_execution_level(manifest: bytes) -> str:
    try:
        root = ElementTree.fromstring(manifest)
    except ElementTree.ParseError as exc:
        raise PeManifestError(f"PE manifest XML 无效：{exc}") from exc
    levels = [
        element.attrib.get("level", "")
        for element in root.iter()
        if element.tag.rsplit("}", 1)[-1] == "requestedExecutionLevel"
    ]
    if len(levels) != 1 or not levels[0]:
        raise PeManifestError("PE manifest 必须包含唯一 requestedExecutionLevel")
    return levels[0]


def verify_embedded_manifest(executable: Path, expected_level: str) -> str:
    level = requested_execution_level(read_embedded_manifest(executable))
    if level != expected_level:
        raise PeManifestError(f"PE manifest 权限不匹配：{executable}；期望 {expected_level}，实际 {level}")
    return level


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="验证 Windows PE 内嵌执行权限清单")
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--expected-level", required=True, choices=("requireAdministrator", "asInvoker"))
    args = parser.parse_args(argv)
    level = verify_embedded_manifest(args.exe, args.expected_level)
    print(f"[pe-manifest] {args.exe}: {level}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

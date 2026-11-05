"""Read execution levels from compiled PE manifest resources."""
from pathlib import Path
import struct
import xml.etree.ElementTree as ET


def embedded_manifest(path):
    data = Path(path).read_bytes()
    if data[:2] != b"MZ":
        raise ValueError("Not a Windows PE executable")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Missing PE signature")
    sections, optional_size = struct.unpack_from("<H12xH", data, pe + 6)
    optional = pe + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    directories = optional + (112 if magic == 0x20B else 96)
    resource_rva = struct.unpack_from("<I", data, directories + 16)[0]
    section_table = optional + optional_size

    def offset(rva):
        for number in range(sections):
            entry = section_table + 40 * number
            virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from("<IIII", data, entry + 8)
            if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
                return raw_pointer + rva - virtual_address
        raise ValueError("Resource address does not belong to a PE section")

    base = offset(resource_rva)

    def entries(relative):
        position = base + relative
        named, indexed = struct.unpack_from("<HH", data, position + 12)
        return [struct.unpack_from("<II", data, position + 16 + 8 * index)
                for index in range(named + indexed)]

    candidates = [target for identifier, target in entries(0) if identifier == 24]
    if len(candidates) != 1:
        raise ValueError("Expected exactly one manifest resource type")
    target = candidates[0]
    while target & 0x80000000:
        children = entries(target & 0x7FFFFFFF)
        if len(children) != 1:
            raise ValueError("Expected one manifest identity/language")
        _, target = children[0]
    rva, size = struct.unpack_from("<II", data, base + target)
    return data[offset(rva):offset(rva) + size]


def verify_manifests(build_bin, expected):
    results = []
    for assembly in ("NexusPipeline.Host", "NexusPipeline.Maintenance"):
        path = Path(build_bin) / assembly / "release" / (assembly + ".exe")
        root = ET.fromstring(embedded_manifest(path))
        levels = [element.attrib["level"] for element in root.iter()
                  if element.tag.endswith("requestedExecutionLevel")]
        if levels != [expected]:
            raise ValueError(f"{assembly}: expected {expected}, found {levels}")
        results.append({"assembly": assembly, "level": levels[0]})
    return results


if __name__ == "__main__":
    import argparse
    import json
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("build_bin", type=Path)
    parser.add_argument("--expected", choices=["asInvoker", "requireAdministrator"], required=True)
    args = parser.parse_args()
    print(json.dumps(verify_manifests(args.build_bin, args.expected), indent=2))

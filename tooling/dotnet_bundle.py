"""Read the uncompressed .NET 6 bundle format produced by the pinned Windows SDK."""
import json
import re
import struct


MARKER = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')
MAX_IMAGE = 2 * 1024 * 1024
MAX_FILE = 256 * 1024 * 1024


def require(condition, reason):
    if not condition:
        raise ValueError('Single-file bundle: ' + reason)


def take(stream, size):
    data = stream.read(size)
    require(len(data) == size, 'truncated input')
    return data


def read_string(stream, maximum):
    length = 0
    for shift in range(0, 35, 7):
        value = take(stream, 1)[0]
        length |= (value & 127) << shift
        if not value & 128:
            require(0 < length <= maximum, 'string bound')
            return take(stream, length).decode('utf-8', errors='strict')
    raise ValueError('Single-file bundle: invalid string length')


def image_extent(stream):
    stream.seek(0)
    header = take(stream, 64)
    require(header[:2] == b'MZ', 'DOS header')
    offset = struct.unpack_from('<I', header, 60)[0]
    require(64 <= offset <= MAX_IMAGE - 4096, 'PE header offset')
    stream.seek(offset)
    pe = take(stream, 24)
    machine, count, optional = struct.unpack_from('<HH12xH', pe, 4)
    require(pe[:4] == b'PE\0\0' and machine == 0x8664 and 0 < count <= 96 and 112 <= optional <= 4096, 'PE header')
    require(struct.unpack('<H', take(stream, 2))[0] == 0x20b, 'PE32+ required')
    stream.seek(offset + 24 + optional)
    end = offset + 24 + optional + count * 40
    for _ in range(count):
        section = take(stream, 40)
        size, position = struct.unpack_from('<II', section, 16)
        require(position <= MAX_IMAGE and size <= MAX_IMAGE - position, 'native section bound')
        end = max(end, position + size)
    require(end <= MAX_IMAGE, 'native image bound')
    return end


def read_bundle(stream):
    stream.seek(0, 2)
    length = stream.tell()
    require(64 < length <= MAX_FILE, 'executable bound')
    extent = image_extent(stream)
    stream.seek(0)
    image = take(stream, extent)
    marker = image.find(MARKER)
    require(marker >= 8 and image.find(MARKER, marker + 1) == -1, 'bundle marker')
    header = struct.unpack_from('<q', image, marker - 8)[0]
    require(extent <= header < length - 52, 'bundle header offset')
    stream.seek(header)
    major, minor, count = struct.unpack('<IIi', take(stream, 12))
    require((major, minor) == (6, 0) and 0 < count <= 512, 'bundle version/count')
    require(re.fullmatch(r'[A-Za-z0-9_-]{1,128}', read_string(stream, 128)), 'bundle ID')
    deps, deps_size, config, config_size, flags = struct.unpack('<qqqqQ', take(stream, 40))
    require(flags == 0, 'managed extraction mode')
    files, folded, ranges = {}, set(), []
    for _ in range(count):
        offset, size, compressed, kind = struct.unpack('<qqqB', take(stream, 25))
        name = read_string(stream, 240)
        require(not name.startswith('/') and not re.search(r'[\\:%?*"<>|\x00-\x1f]', name) and
                all(part not in ('', '.', '..') and not part.endswith(('.', ' ')) for part in name.split('/')) and
                all(not re.fullmatch(r'(?i)(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\..*)?', part)
                    for part in name.split('/')), 'member path')
        require(name.casefold() not in folded and 0 <= kind <= 5 and compressed == 0 and
                extent <= offset <= header and 0 < size <= header - offset, 'member identity/range')
        folded.add(name.casefold())
        files[name] = {'offset': offset, 'sizeBytes': size, 'kind': kind}
        ranges.append((offset, offset + size))
    ranges.sort()
    require(all(left[1] <= right[0] for left, right in zip(ranges, ranges[1:])), 'overlapping members')
    for kind, location, size in ((3, deps, deps_size), (4, config, config_size)):
        values = [item for item in files.values() if item['kind'] == kind]
        require(len(values) == 1 and (values[0]['offset'], values[0]['sizeBytes']) == (location, size), 'configuration binding')
    return files


def read_member(stream, files, name, kind):
    value = files.get(name)
    require(value is not None and value['kind'] == kind, 'required member: ' + name)
    stream.seek(value['offset'])
    return take(stream, value['sizeBytes'])


def verify_publish(path, project, sdk):
    with path.open('rb') as stream:
        files = read_bundle(stream)
        main = read_member(stream, files, 'NexusPipeline.' + project + '.dll', 1)
        require(main[:2] == b'MZ', 'managed entry')
        if project in ('Maintenance', 'Uninstaller'):
            prefix = 'NexusPipeline.' + project
            require(set(files) == {prefix + suffix for suffix in ('.dll', '.deps.json', '.runtimeconfig.json')},
                    'standalone entry closure')
            if project == 'Maintenance':
                require(path.stat().st_size <= 512 * 1024, 'update window size limit')
        else:
            require(read_member(stream, files, 'NexusPipeline.Extensibility.dll', 1) == sdk, 'shared SDK producer bytes')
        config = json.loads(read_member(stream, files, 'NexusPipeline.' + project + '.runtimeconfig.json', 4))['runtimeOptions']
        frameworks = config.get('frameworks', [config['framework']] if 'framework' in config else [])
        expected = {'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'} if project == 'Host' else {'Microsoft.NETCore.App'}
        require('includedFrameworks' not in config and {item['name'] for item in frameworks} == expected and
                all(re.fullmatch(r'10\.\d+\.\d+', item['version']) for item in frameworks), 'framework-dependent runtime')
        require('System.Private.CoreLib.dll' not in files and 'coreclr.dll' not in files, 'embedded shared runtime')
        return {'bundleVersion': '6.0', 'memberCount': len(files), 'frameworks': frameworks}

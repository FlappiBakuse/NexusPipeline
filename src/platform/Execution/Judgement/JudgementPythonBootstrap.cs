namespace NexusPipeline.Platform.Execution.Judgement;

internal static class JudgementPythonBootstrap
{
    internal const string Source = """
import contextlib
import io
import json
import struct
import sys

def exact(stream, count):
    result = bytearray()
    while len(result) < count:
        part = stream.read(count - len(result))
        if not part:
            raise ValueError('judgement_python_input_incomplete')
        result.extend(part)
    return bytes(result)

class ProbeApi:
    def __init__(self, context, source, target):
        object.__setattr__(self, 'context', context)
        object.__setattr__(self, 'source', source)
        object.__setattr__(self, 'target', target)
        object.__setattr__(self, 'ordinal', 0)

    def __setattr__(self, name, value):
        raise AttributeError('judgement_probe_api_frozen')

    def processes(self, options=None):
        return self.call('processes', {} if options is None else options)

    def windows(self, options=None):
        return self.call('windows', {} if options is None else options)

    def httpGet(self, options=None):
        return self.call('httpGet', {} if options is None else options)

    def call(self, method, options):
        object.__setattr__(self, 'ordinal', self.ordinal + 1)
        if self.ordinal > 16 or not isinstance(options, dict):
            raise ValueError('judgement_probe_options_invalid')
        request = {'format': 'nexus.execution.judgement-probe-request', 'version': 1,
            'identity': self.context['identity'], 'generation': self.context['generation'],
            'inputSha256': self.context['inputSha256'], 'ordinal': self.ordinal,
            'method': method, 'options': options}
        payload = json.dumps(request, ensure_ascii=False, allow_nan=False, separators=(',', ':')).encode('utf-8', errors='strict')
        maximum = 16384 if method == 'httpGet' else 8192
        if not 1 <= len(payload) <= maximum:
            raise ValueError('judgement_probe_options_invalid')
        self.target.write(struct.pack('<i', len(payload)))
        self.target.write(payload)
        self.target.flush()
        size = struct.unpack('<i', exact(self.source, 4))[0]
        if not 1 <= size <= 16777216:
            raise ValueError('judgement_probe_reply_invalid')
        reply = json.loads(exact(self.source, size).decode('utf-8', errors='strict'))
        if set(reply) != {'format', 'version', 'identity', 'generation', 'inputSha256', 'ordinal', 'result', 'faultCode'}:
            raise ValueError('judgement_probe_reply_invalid')
        if reply['format'] != 'nexus.execution.judgement-probe-reply' or reply['version'] != 1 or any(
            reply[key] != request[key] for key in ('identity', 'generation', 'inputSha256', 'ordinal')):
            raise ValueError('judgement_probe_reply_invalid')
        if reply['faultCode'] is not None:
            raise ValueError('judgement_probe_failed')
        if not isinstance(reply['result'], dict):
            raise ValueError('judgement_probe_reply_invalid')
        return reply['result']

class NexusApi:
    __slots__ = ('probes',)
    def __init__(self, probes):
        object.__setattr__(self, 'probes', probes)
    def __setattr__(self, name, value):
        raise AttributeError('judgement_api_frozen')

class ConsoleState:
    def __init__(self):
        self.count = 0
        self.entries = []

class LimitedText(io.TextIOBase):
    def __init__(self, state, level):
        self.state = state
        self.level = level
    def writable(self):
        return True
    def write(self, value):
        size = len(value.encode('utf-8', errors='strict'))
        if self.state.count + size > 16384 or len(self.state.entries) >= 128:
            raise ValueError('judgement_python_text_output_exceeded')
        if value:
            self.state.entries.append({'level': self.level, 'text': value})
            self.state.count += size
        return len(value)

wire_input = sys.stdin.buffer
wire_output = sys.stdout.buffer
size = struct.unpack('<i', exact(wire_input, 4))[0]
if not 1 <= size <= 33554432:
    raise ValueError('judgement_python_input_size_invalid')
request = json.loads(exact(wire_input, size).decode('utf-8', errors='strict'))
if set(request) != {'script', 'input', 'identity', 'generation', 'inputSha256', 'probeEvidenceAllowance'} or not isinstance(request['script'], str) or not isinstance(request['input'], dict):
    raise ValueError('judgement_python_input_unrecognized')
namespace = {'__name__': '__nexus_judgement__', 'nexus': NexusApi(ProbeApi(request, wire_input, wire_output))}
console = ConsoleState()
result = None
fault = None
try:
    with contextlib.redirect_stdout(LimitedText(console, 'log')), contextlib.redirect_stderr(LimitedText(console, 'error')):
        exec(compile(request['script'], '<frozen-judgement>', 'exec'), namespace)
        result = namespace['nexus_judge'](request['input'])
except Exception:
    fault = 'judgement_worker_execution_failed'
payload = json.dumps({'result': result, 'faultCode': fault, 'console': console.entries},
    ensure_ascii=False, allow_nan=False, separators=(',', ':')).encode('utf-8', errors='strict')
if not 1 <= len(payload) <= 131072:
    raise ValueError('judgement_python_output_size_invalid')
wire_output.write(struct.pack('<i', len(payload)))
wire_output.write(payload)
wire_output.flush()
""";
}

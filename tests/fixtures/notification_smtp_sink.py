"""Controlled SMTP sockets with TLS keys confined to their test runtime directory."""
import base64
import hashlib
import json
from pathlib import Path
import socket
import ssl
import sys
import threading

root = Path(sys.argv[1]).resolve(strict=True)
configuration = json.loads((root / "input.json").read_bytes())
state = {"messages": 0, "authenticated": 0, "tls": False, "tlsAttempts": 0, "received": [], "failures": []}
gate = threading.Lock()
threads = []


def publish():
    temporary = root / "state.pending"
    temporary.write_text(json.dumps(state, ensure_ascii=False), encoding="utf-8", newline="\n")
    temporary.replace(root / "state.json")


def update(name, value=None):
    with gate:
        state[name] = state[name] + 1 if value is None else value
        publish()


context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.minimum_version = ssl.TLSVersion.TLSv1_2
context.load_cert_chain(str(root / "certificate.pem"), str(root / "private-key.pem"))


def upgrade(stream):
    update("tlsAttempts")
    secured = context.wrap_socket(stream, server_side=True)
    update("tls", True)
    return secured


class Input:
    def __init__(self, stream):
        self.stream = stream
        self.buffer = bytearray()

    def line(self, maximum=16384):
        while b"\n" not in self.buffer:
            if len(self.buffer) > maximum:
                raise ValueError("line_limit")
            chunk = self.stream.recv(4096)
            if not chunk:
                raise EOFError()
            self.buffer.extend(chunk)
        at = self.buffer.index(10)
        if at == 0 or self.buffer[at - 1] != 13 or at > maximum:
            raise ValueError("line_invalid")
        result = bytes(self.buffer[:at - 1])
        del self.buffer[:at + 1]
        return result


def serve(connection):
    stream = connection
    try:
        stream.settimeout(5)
        if configuration["implicitTls"]:
            stream = upgrade(stream)
        reader = Input(stream)

        def reply(line):
            stream.sendall(line.encode("ascii") + b"\r\n")

        def authenticate(user, password):
            if user == b"sender@example.invalid" and password == b"fixture-password":
                update("authenticated")
                reply("235 authenticated")
            else:
                reply("535 rejected")

        reply("220 localhost controlled notification sink")
        for _ in range(100):
            command = reader.line()
            upper = command.upper()
            if upper.startswith((b"EHLO ", b"HELO ")):
                reply("250-localhost")
                if configuration["startTls"] and not isinstance(stream, ssl.SSLSocket):
                    reply("250-STARTTLS")
                reply("250-AUTH PLAIN LOGIN")
                reply("250 SIZE 16777216")
            elif upper == b"STARTTLS" and configuration["startTls"] and not isinstance(stream, ssl.SSLSocket):
                if reader.buffer:
                    raise ValueError("starttls_buffer_not_empty")
                reply("220 Ready for TLS")
                stream = upgrade(stream)
                reader = Input(stream)
            elif upper.startswith(b"AUTH PLAIN"):
                encoded = command[10:].strip()
                if not encoded:
                    reply("334 ")
                    encoded = reader.line()
                actual = base64.b64decode(encoded, validate=True).split(b"\0")
                if len(actual) < 3:
                    reply("535 rejected")
                else:
                    authenticate(actual[-2], actual[-1])
            elif upper == b"AUTH LOGIN":
                reply("334 VXNlcm5hbWU6")
                user = base64.b64decode(reader.line(), validate=True)
                reply("334 UGFzc3dvcmQ6")
                password = base64.b64decode(reader.line(), validate=True)
                authenticate(user, password)
            elif upper.startswith((b"MAIL FROM:", b"RCPT TO:")) or upper == b"RSET":
                reply("250 accepted")
            elif upper == b"DATA":
                reply("354 End with dot")
                digest = hashlib.sha256()
                prefix = bytearray()
                size = 0
                while True:
                    line = reader.line()
                    if line == b".":
                        break
                    if line.startswith(b".."):
                        line = line[1:]
                    line += b"\r\n"
                    size += len(line)
                    if size > 16777216:
                        raise ValueError("message_limit")
                    digest.update(line)
                    prefix.extend(line[:max(0, 65536 - len(prefix))])
                with gate:
                    if len(state["received"]) >= 8:
                        raise ValueError("message_count_limit")
                    state["received"].append({"size": size, "hash": digest.hexdigest(), "prefix": prefix.decode("utf-8", errors="replace")})
                    state["messages"] += 1
                    publish()
                if configuration["dropDataReply"]:
                    return
                reply("250 message accepted")
            elif upper == b"QUIT":
                reply("221 closing")
                return
            else:
                reply("500 unsupported")
    except (OSError, ValueError, EOFError) as error:
        with gate:
            if len(state["failures"]) < 16:
                state["failures"].append(type(error).__name__)
                publish()
    finally:
        stream.close()


with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
    listener.bind(("127.0.0.1", 0))
    listener.listen(4)
    listener.settimeout(0.2)
    with gate:
        publish()
    (root / "ready.json").write_text(json.dumps({"port": listener.getsockname()[1], "status": "ready"}), encoding="utf-8")
    while not (root / "stop").exists():
        try:
            connection, _ = listener.accept()
        except TimeoutError:
            continue
        if len(threads) >= 8:
            connection.close()
            continue
        worker = threading.Thread(target=serve, args=(connection,), daemon=True)
        threads.append(worker)
        worker.start()
for worker in threads:
    worker.join(timeout=6)

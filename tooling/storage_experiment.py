"""Execute and independently verify the actual bounded history/image storage fixture."""
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import time
import uuid
import zlib
from build_product import build_product
from binary_inputs import capture_binary_inputs, verify_binary_inputs
from source_snapshot import external_directory, write_json


def verify_png(raw):
    if len(raw) != 262144 or raw[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("Fixture PNG signature or size differs")
    position, names, compressed = 8, [], bytearray()
    while position < len(raw):
        length = struct.unpack_from(">I", raw, position)[0]
        kind = raw[position + 4:position + 8]
        data = raw[position + 8:position + 8 + length]
        expected = struct.unpack_from(">I", raw, position + 8 + length)[0]
        if zlib.crc32(kind + data) != expected: raise ValueError("Fixture PNG CRC differs")
        if kind == b"IHDR" and (struct.unpack_from(">II", data) != (64, 64) or data[8:] != bytes([8, 6, 0, 0, 0])):
            raise ValueError("Fixture PNG dimensions or color format differs")
        if kind == b"IDAT": compressed.extend(data)
        names.append(kind)
        position += length + 12
    if names != [b"IHDR", b"IDAT", b"npAD", b"IEND"] or position != len(raw): raise ValueError("Fixture PNG chunk structure differs")
    pixels = zlib.decompress(compressed)
    if len(pixels) != 64 * 257 or any(pixels[row * 257] != 0 for row in range(64)):
        raise ValueError("Fixture PNG pixel stream differs")


def verify_images(runtime, report):
    records = []
    for asset in report["assets"]:
        path = runtime / "blobs/content" / (asset["sha256"] + ".bin")
        raw = path.read_bytes()
        if hashlib.sha256(raw).hexdigest() != asset["sha256"] or len(raw) != asset["sizeBytes"]:
            raise ValueError("Immutable dataset image identity differs")
        verify_png(raw)
        records.append({"path": str(path), "sha256": asset["sha256"], "sizeBytes": len(raw)})
    if len(records) != 1000 or len({item["sha256"] for item in records}) != 1000:
        raise ValueError("Dataset must contain one thousand distinct images")
    return records


def run_experiment(root, build_root, output):
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex[:8]
    run = external_directory(root, output) / run_id
    run.mkdir(parents=True)
    projects = ["OwnedWriter"]
    responsibilities = dict(lint="NOT_RUN", typecheck="NOT_RUN", test="NOT_RUN", build="NOT_RUN")
    receipt = dict(status="FAIL", exitCode=1, preparation="FAIL", responsibilities=responsibilities)
    started = time.monotonic()
    try:
        code = build_product(root, build_root, "test", projects)
        if code:
            receipt.update(exitCode=code, reason="Storage experiment fixture build failed")
            return code
        build_receipt = json.loads((build_root / "build-receipt.json").read_bytes())
        receipt.update(source=build_receipt["source"],
                       buildReceipt=str(build_root / "runs" / build_receipt["runId"] / "build-receipt.json"))
        binary_identity, binaries = capture_binary_inputs(build_root, run, "storage-experiment", projects)
        runtime = run / "runtime"
        runtime.mkdir()
        fixture = build_root / "build/bin/OwnedWriter/release/OwnedWriter.exe"
        dataset = runtime / "dataset"
        report_path = dataset / "dataset-report.json"
        process_receipt = runtime / "owned-process.json"
        command = [str(fixture), "history-dataset-harness", str(dataset), str(report_path), str(process_receipt)]
        env = {**os.environ, "TEMP": str(runtime), "TMP": str(runtime), "NEXUS_TEST_ROOT": str(runtime), "PYTHONDONTWRITEBYTECODE": "1"}
        receipt.update(preparation="PASS", command=command)
        responsibilities["test"] = "FAIL"
        started = time.monotonic()
        process = subprocess.run(command, env=env, capture_output=True, encoding="utf-8", errors="replace", timeout=720)
        (run / "native.log").write_bytes((process.stdout + process.stderr).encode())
        print(process.stdout + process.stderr, flush=True)
        write_json(run / "command.json", {"command": command, "exitCode": process.returncode})
        if process.returncode: raise ValueError("Actual storage fixture exited unsuccessfully")
        report = json.loads(report_path.read_bytes())
        owned = json.loads(process_receipt.read_bytes())
        if report["status"] != "PASS" or [item["rows"] for item in report["stages"]] != [10000, 100000] or owned["status"] != "PASS":
            raise ValueError("Storage fixture did not complete all declared native stages")
        images = verify_images(dataset / "runtime", report)
        write_json(run / "image-byte-verification.json", {"status": "PASS", "decoder": "Independent Python PNG CRC/chunk/zlib verification", "images": images})
        verify_binary_inputs(build_root, binaries)
        responsibilities["test"] = "PASS"
        receipt.update(status="PASS", exitCode=0, datasetReport=str(report_path), ownedProcessReceipt=str(process_receipt),
            applicationBinaryInputs={**binary_identity, "verifiedUnchangedAfterRun": True},
            scope="SQLite keyset history, synthetic PNG and consistent database/content snapshot experiment")
        return 0
    except (OSError, ValueError, KeyError, subprocess.TimeoutExpired) as error:
        receipt["reason"] = str(error)
        print(str(error), file=sys.stderr)
        return 1
    finally:
        receipt["elapsedSeconds"] = time.monotonic() - started
        write_json(run / "test-receipt.json", receipt)
        print(str(run / "test-receipt.json"), flush=True)

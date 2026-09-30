from pathlib import Path
import importlib.util
import sys
spec=importlib.util.spec_from_file_location("batch_required",Path(__file__).with_name("batch-required.py"))
implementation=importlib.util.module_from_spec(spec)
spec.loader.exec_module(implementation)
require=implementation.require
canonical_digest=lambda path: implementation.digest(path.read_bytes().replace(b"\r\n",b"\n"))
main=implementation.main
if __name__ == "__main__":
    main()

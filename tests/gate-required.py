from pathlib import Path
import importlib.util
spec=importlib.util.spec_from_file_location("batch_required",Path(__file__).with_name("batch-required.py"))
implementation=importlib.util.module_from_spec(spec)
spec.loader.exec_module(implementation)
main=implementation.main
if __name__ == "__main__":
    main()

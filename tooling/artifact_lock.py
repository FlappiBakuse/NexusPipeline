"""Prevent simultaneous producers from publishing into one external workspace."""
from contextlib import contextmanager
import os

from desktop_bundle import require_no_links


@contextmanager
def artifact_lock(output):
    require_no_links(output)
    path = output.parent / (output.name + '.producer.lock')
    require_no_links(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('a+b') as stream:
        if stream.tell() == 0:
            stream.write(b'0')
            stream.flush()
        stream.seek(0)
        if os.name == 'nt':
            import msvcrt
            acquire = lambda: msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            release = lambda: msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
        else:
            import fcntl
            acquire = lambda: fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            release = lambda: fcntl.flock(stream.fileno(), fcntl.LOCK_UN)
        try:
            acquire()
        except OSError as error:
            raise ValueError('Another producer owns this candidate workspace') from error
        try:
            yield
        finally:
            stream.seek(0)
            release()

"""Readiness marker file. No /ready endpoint (decided in M3-3): the Docker HEALTHCHECK needs this file AND /health."""
import os
import tempfile
from collections.abc import Awaitable, Callable


class Marker:
    def __init__(self, path: str) -> None:
        self._path = path

    def set(self) -> None:
        # Write a temp file next to the marker, then rename: readers never see a partial file.
        directory = os.path.dirname(self._path) or "."
        fd, tmp = tempfile.mkstemp(dir=directory, prefix=".ready-")
        try:
            os.write(fd, b"ready\n")
        finally:
            os.close(fd)
        os.replace(tmp, self._path)

    def clear(self) -> None:
        try:
            os.unlink(self._path)
        except FileNotFoundError:
            pass


class Readiness:
    """Ready means: the queue is bound (the consumer calls this after bind) and Redis answers a ping."""

    def __init__(self, marker: Marker, ping: Callable[[], Awaitable[object]]) -> None:
        self._marker, self._ping = marker, ping

    async def on_ready(self) -> None:
        await self._ping()  # raises if Redis is down: no marker
        self._marker.set()

    async def on_lost(self) -> None:
        self._marker.clear()

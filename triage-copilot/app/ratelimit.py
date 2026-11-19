"""Per-process limit on triage runs, so a burst cannot drain the LLM quota (a demo guard, not a shared quota).

Sliding window: at most `per_minute` acquisitions in any 60 seconds. A refused call changes nothing.
Not shared across instances; a Redis counter would be the follow-up if the copilot were ever scaled out.
"""
import time
from collections import deque
from collections.abc import Callable

WINDOW_SECONDS = 60.0


class RateLimiter:
    def __init__(self, per_minute: int, clock: Callable[[], float] = time.monotonic) -> None:
        if isinstance(per_minute, bool) or not isinstance(per_minute, int) or not 1 <= per_minute <= 1000:
            raise ValueError("per_minute must be an integer from 1 to 1000")
        self._limit, self._clock = per_minute, clock
        self._taken: deque[float] = deque()

    def try_acquire(self) -> bool:
        now = self._clock()
        while self._taken and now - self._taken[0] >= WINDOW_SECONDS:
            self._taken.popleft()
        if len(self._taken) >= self._limit:
            return False
        self._taken.append(now)
        return True

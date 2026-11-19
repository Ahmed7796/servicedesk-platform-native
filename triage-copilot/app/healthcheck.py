"""Docker HEALTHCHECK: healthy only if the readiness marker exists AND /health returns 200. Standard library only."""
import os
import sys
import urllib.request


def check(marker: str, url: str, timeout: float = 2.0) -> bool:
    if not os.path.exists(marker):
        return False
    try:
        with urllib.request.urlopen(url, timeout=timeout) as resp:
            return resp.status == 200
    except Exception:
        return False


if __name__ == "__main__":
    ok = check(os.environ.get("COPILOT_READY_FILE", "/tmp/copilot-ready"), "http://localhost:8082/health")
    sys.exit(0 if ok else 1)

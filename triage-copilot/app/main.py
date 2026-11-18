from fastapi import FastAPI

app = FastAPI(title="ServiceDesk Triage Copilot")


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}

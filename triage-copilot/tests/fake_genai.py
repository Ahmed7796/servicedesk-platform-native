from types import SimpleNamespace


class FakeGenAI:
    """Stands in for google.genai.Client: only aio.models.generate_content. Never touches the network."""

    def __init__(self, text=None, calls=(), error=None):
        self.text, self.calls_to_return, self.error = text, list(calls), error
        self.requests: list[dict] = []
        self.aio = SimpleNamespace(models=SimpleNamespace(generate_content=self._generate))

    async def _generate(self, *, model, contents, config):
        self.requests.append({"model": model, "contents": contents, "config": config})
        if self.error:
            raise self.error
        fcs = [SimpleNamespace(name=n, args=a) for n, a in self.calls_to_return]
        return SimpleNamespace(text=self.text, function_calls=fcs or None)

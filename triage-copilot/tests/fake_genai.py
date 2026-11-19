from types import SimpleNamespace


class FakeGenAI:
    """Stands in for google.genai.Client: only aio.models.generate_content. Never touches the network."""

    def __init__(self, text=None, calls=(), error=None, lead_text=None):
        """calls: (name, args) or (name, args, thought_signature bytes). lead_text adds a text part before them."""
        self.text, self.calls_to_return, self.error, self.lead_text = text, list(calls), error, lead_text
        self.requests: list[dict] = []
        self.aio = SimpleNamespace(models=SimpleNamespace(generate_content=self._generate))

    async def _generate(self, *, model, contents, config):
        self.requests.append({"model": model, "contents": contents, "config": config})
        if self.error:
            raise self.error
        fcs = [SimpleNamespace(name=c[0], args=c[1]) for c in self.calls_to_return]
        parts = [SimpleNamespace(text=self.lead_text, function_call=None, thought_signature=None)] if self.lead_text else []
        parts += [SimpleNamespace(text=None, function_call=fc, thought_signature=c[2] if len(c) > 2 else None)
                  for fc, c in zip(fcs, self.calls_to_return)]
        candidates = [SimpleNamespace(content=SimpleNamespace(parts=parts))] if parts else None
        return SimpleNamespace(text=self.text, function_calls=fcs or None, candidates=candidates)

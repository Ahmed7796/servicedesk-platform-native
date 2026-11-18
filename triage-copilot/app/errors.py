class ServiceAuthError(Exception):
    """The ServiceDesk API rejected our service credentials (401). Not retried further."""


class ApiUnavailableError(Exception):
    """The ServiceDesk API could not be reached or answered with a server error."""


class ApiForbiddenError(Exception):
    """The ServiceDesk API answered 403: the service role may not do this."""


class ApiResponseError(Exception):
    """The ServiceDesk API answered, but not with the shape the contract promises."""


class LlmRetryableError(Exception):
    """The model provider failed in a way worth retrying later: 429, 5xx, network or timeout."""

class ServiceAuthError(Exception):
    """The ServiceDesk API rejected our service credentials (401). Not retried further."""


class ApiUnavailableError(Exception):
    """The ServiceDesk API could not be reached or answered with a server error."""

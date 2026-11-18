package com.servicedesk.notify.push;

import org.springframework.web.bind.annotation.RestControllerAdvice;
import org.springframework.web.servlet.mvc.method.annotation.ResponseEntityExceptionHandler;

/** Turns unreadable bodies and ResponseStatusException into RFC 9457 application/problem+json. */
@RestControllerAdvice
public class ProblemAdvice extends ResponseEntityExceptionHandler {}

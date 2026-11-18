package com.servicedesk.notify.push;

/** Sends one push. Implementations must not throw; tests use a fake. */
public interface PushSender {
    PushResult send(PushMessage message);
}

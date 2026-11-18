package com.servicedesk.notify.push;

public enum PushResult {
    OK,
    /** Expo said the token is no longer valid (DeviceNotRegistered): delete it. */
    INVALID_TOKEN,
    /** Anything else. Not retried. */
    FAILED
}

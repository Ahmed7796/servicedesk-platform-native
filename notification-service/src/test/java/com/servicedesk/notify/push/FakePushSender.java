package com.servicedesk.notify.push;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.function.Function;

/** Records every message. Behaviour can be changed per test. */
public class FakePushSender implements PushSender {

    public final List<PushMessage> sent = Collections.synchronizedList(new ArrayList<>());
    public volatile Function<PushMessage, PushResult> behaviour = m -> PushResult.OK;

    @Override
    public PushResult send(PushMessage message) {
        sent.add(message);
        return behaviour.apply(message);
    }

    public List<String> recipients() {
        return sent.stream().map(PushMessage::to).sorted().toList();
    }

    public void reset() {
        sent.clear();
        behaviour = m -> PushResult.OK;
    }
}

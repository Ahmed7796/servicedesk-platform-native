package com.servicedesk.notify.events;

import com.servicedesk.notify.push.PushNotifier;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.amqp.AmqpRejectAndDontRequeueException;
import org.springframework.amqp.core.Message;
import org.springframework.amqp.rabbit.annotation.RabbitListener;
import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.stereotype.Component;
import tools.jackson.databind.json.JsonMapper;

/**
 * Parse, de-duplicate, apply. Order matters: check Redis, commit the projection (inside
 * {@link ProjectionHandler#handle}), and only then mark Redis. A crash between commit and
 * mark redelivers the event, and the projection is idempotent on its own.
 *
 * <p>Malformed bodies are rejected without requeue (dead-lettered). Any other exception
 * propagates to the listener's bounded retry (3 attempts), then the dead-letter queue.
 */
@Component
@ConditionalOnProperty(name = "app.messaging.enabled", havingValue = "true", matchIfMissing = true)
public class EventListener {

    private static final Logger log = LoggerFactory.getLogger(EventListener.class);

    private final JsonMapper mapper;
    private final ProcessedEventStore store;
    private final ProjectionHandler handler;
    private final PushNotifier notifier;

    public EventListener(JsonMapper mapper, ProcessedEventStore store, ProjectionHandler handler,
                         PushNotifier notifier) {
        this.mapper = mapper;
        this.store = store;
        this.handler = handler;
        this.notifier = notifier;
    }

    @RabbitListener(queues = RabbitTopology.QUEUE)
    public void onMessage(Message message) {
        EventEnvelope event = parse(message);
        if (store.isProcessed(event.eventId())) {
            log.debug("Event {} already processed; skipped", event.eventId());
            return;
        }
        // The projection has committed when handle returns. Push comes after that, outside the
        // transaction, and only when this call changed a row (a replay returns empty). A push
        // failure is logged and never fails the event. A crash before the push loses it (at most once).
        handler.handle(event).ifPresent(notice -> {
            try {
                notifier.notifyPending(notice);
            } catch (RuntimeException e) {
                log.warn("Push for request {} failed: {}", notice.requestId(), e.getClass().getSimpleName());
            }
        });
        store.markProcessed(event.eventId());
    }

    private EventEnvelope parse(Message message) {
        EventEnvelope event;
        try {
            event = mapper.readValue(message.getBody(), EventEnvelope.class);
        } catch (RuntimeException e) {
            throw new AmqpRejectAndDontRequeueException("Malformed event body", e);
        }
        if (event == null || event.eventId() == null || event.type() == null
                || event.requestId() == null || event.occurredAt() == null) {
            throw new AmqpRejectAndDontRequeueException(
                    "Event is missing eventId, type, requestId or occurredAt");
        }
        return event;
    }
}

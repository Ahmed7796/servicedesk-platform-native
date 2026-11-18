package com.servicedesk.notify.events;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.Mockito.doThrow;
import static org.mockito.Mockito.inOrder;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.verifyNoInteractions;
import static org.mockito.Mockito.when;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.UUID;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.mockito.InOrder;
import org.springframework.amqp.AmqpRejectAndDontRequeueException;
import org.springframework.amqp.core.Message;
import org.springframework.amqp.core.MessageBuilder;
import tools.jackson.databind.json.JsonMapper;

/** Listener logic with fakes: no broker, no Redis, no database. */
class EventListenerTest {

    static final UUID EVENT_ID = UUID.fromString("0b9f3d1e-6a2c-4e8b-9f1d-3c5a7e9b1d20");

    ProjectionHandler handler = mock(ProjectionHandler.class);
    ProcessedEventStore store = mock(ProcessedEventStore.class);
    EventListener listener = new EventListener(JsonMapper.builder().build(), store, handler);

    static String example() throws Exception {
        return Files.readString(Path.of("../contracts/events/examples/request.submitted.json"));
    }

    static Message message(String body) {
        return MessageBuilder.withBody(body.getBytes(StandardCharsets.UTF_8)).build();
    }

    @BeforeEach
    void notProcessedYet() {
        when(store.isProcessed(any())).thenReturn(false);
    }

    @Test
    void newEventIsHandledThenMarkedInThatOrder() throws Exception {
        listener.onMessage(message(example()));

        InOrder order = inOrder(store, handler);
        order.verify(store).isProcessed(EVENT_ID);
        order.verify(handler).handle(any());
        order.verify(store).markProcessed(EVENT_ID);
    }

    @Test
    void alreadyProcessedEventIsSkipped() throws Exception {
        when(store.isProcessed(EVENT_ID)).thenReturn(true);

        listener.onMessage(message(example()));

        verifyNoInteractions(handler);
        verify(store, never()).markProcessed(any());
    }

    @Test
    void handlerFailureIsRethrownAndNotMarked() throws Exception {
        doThrow(new IllegalStateException("db down")).when(handler).handle(any());

        assertThatThrownBy(() -> listener.onMessage(message(example())))
                .isInstanceOf(IllegalStateException.class);

        verify(store, never()).markProcessed(any());
    }

    @Test
    void redisFailureOnCheckIsRethrownAndNothingIsApplied() throws Exception {
        when(store.isProcessed(any())).thenThrow(new IllegalStateException("redis down"));

        assertThatThrownBy(() -> listener.onMessage(message(example())))
                .isInstanceOf(IllegalStateException.class);

        verifyNoInteractions(handler);
    }

    @Test
    void malformedBodiesAreRejectedWithoutRequeue() throws Exception {
        String[] bad = {
            "not json at all",
            "",
            "{\"type\": \"request.submitted\"}",
            example().replace("\"eventId\"", "\"eventIdX\""),
            example().replace("\"type\"", "\"typeX\""),
            example().replace("\"requestId\"", "\"requestIdX\""),
            example().replace("\"occurredAt\"", "\"occurredAtX\""),
        };
        for (String body : bad) {
            assertThatThrownBy(() -> listener.onMessage(message(body)))
                    .as(body)
                    .isInstanceOf(AmqpRejectAndDontRequeueException.class);
        }
        verifyNoInteractions(handler);
        assertThat(bad).hasSize(7);
    }
}

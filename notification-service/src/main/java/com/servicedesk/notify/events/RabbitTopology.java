package com.servicedesk.notify.events;

import org.springframework.amqp.core.Binding;
import org.springframework.amqp.core.BindingBuilder;
import org.springframework.amqp.core.FanoutExchange;
import org.springframework.amqp.core.Queue;
import org.springframework.amqp.core.QueueBuilder;
import org.springframework.amqp.core.TopicExchange;
import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

/**
 * Declared by RabbitAdmin when the first connection opens. The exchange has the same
 * arguments as servicedesk-api (topic, durable, not auto-delete), so redeclaring is idempotent.
 */
@Configuration
@ConditionalOnProperty(name = "app.messaging.enabled", havingValue = "true", matchIfMissing = true)
public class RabbitTopology {

    public static final String EXCHANGE = "servicedesk.events";
    public static final String QUEUE = "notify.events";
    public static final String DEAD_LETTER_EXCHANGE = "notify.dlx";
    public static final String DLQ = "notify.events.dlq";

    @Bean
    TopicExchange eventsExchange() {
        return new TopicExchange(EXCHANGE, true, false);
    }

    @Bean
    FanoutExchange deadLetterExchange() {
        return new FanoutExchange(DEAD_LETTER_EXCHANGE, true, false);
    }

    @Bean
    Queue notifyEventsQueue() {
        return QueueBuilder.durable(QUEUE).deadLetterExchange(DEAD_LETTER_EXCHANGE).build();
    }

    @Bean
    Queue deadLetterQueue() {
        return QueueBuilder.durable(DLQ).build();
    }

    @Bean
    Binding notifyEventsBinding(Queue notifyEventsQueue, TopicExchange eventsExchange) {
        return BindingBuilder.bind(notifyEventsQueue).to(eventsExchange).with("request.#");
    }

    @Bean
    Binding deadLetterBinding(Queue deadLetterQueue, FanoutExchange deadLetterExchange) {
        return BindingBuilder.bind(deadLetterQueue).to(deadLetterExchange);
    }
}

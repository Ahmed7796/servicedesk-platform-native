package com.servicedesk.notify.overdue;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.time.Duration;
import java.util.Map;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;
import org.springframework.boot.context.properties.bind.Binder;
import org.springframework.boot.context.properties.source.MapConfigurationPropertySource;

class StepSlaPropertiesTest {

    static StepSlaProperties bind(String value) {
        return new Binder(new MapConfigurationPropertySource(Map.of("app.notify.step-sla", value)))
                .bind("app.notify", StepSlaProperties.class).get();
    }

    @ParameterizedTest
    @ValueSource(strings = {"PT24H", "PT1M"})
    void positiveIsoDurationsBind(String value) {
        assertThat(bind(value).duration()).isEqualTo(Duration.parse(value));
    }

    @ParameterizedTest
    @ValueSource(strings = {"PT0S", "-PT1H", "24", "24h", "abc", "PT24", ""})
    void notAStrictPositiveIsoDurationFailsStartupNamingTheEnvVarAndAnExample(String value) {
        assertThatThrownBy(() -> bind(value)).rootCause().hasMessageContaining("NOTIFY_STEP_SLA").hasMessageContaining("PT24H");
    }
}

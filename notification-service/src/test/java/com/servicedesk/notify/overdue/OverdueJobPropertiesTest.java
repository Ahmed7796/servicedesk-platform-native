package com.servicedesk.notify.overdue;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.time.Duration;
import java.util.Map;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;
import org.springframework.boot.context.properties.EnableConfigurationProperties;
import org.springframework.boot.context.properties.bind.Binder;
import org.springframework.boot.context.properties.source.MapConfigurationPropertySource;
import org.springframework.boot.test.context.runner.ApplicationContextRunner;

class OverdueJobPropertiesTest {

    static OverdueJobProperties bind(Map<String, String> source) {
        return new Binder(new MapConfigurationPropertySource(source))
                .bind("app.notify.overdue-job", OverdueJobProperties.class).get();
    }

    static OverdueJobProperties bind(String value) {
        return bind(Map.of("app.notify.overdue-job.interval", value));
    }

    @ParameterizedTest
    @ValueSource(strings = {"PT1M", "PT30S", "PT0.5S"})
    void positiveIsoDurationsBind(String value) {
        assertThat(bind(value).duration()).isEqualTo(Duration.parse(value));
    }

    @Test
    void unsetDefaultsToOneMinute() {
        // as Spring creates the bean when nothing is configured (no yml involved)
        new ApplicationContextRunner().withUserConfiguration(Enable.class).run(ctx ->
                assertThat(ctx.getBean(OverdueJobProperties.class).duration()).isEqualTo(Duration.ofMinutes(1)));
    }

    @EnableConfigurationProperties(OverdueJobProperties.class)
    static class Enable {
    }

    @ParameterizedTest
    @ValueSource(strings = {"PT0S", "-PT1M", "60", "1m", "abc", "PT1", ""})
    void notAStrictPositiveIsoDurationFailsStartupNamingTheEnvVarAndAnExample(String value) {
        assertThatThrownBy(() -> bind(value))
                .rootCause().hasMessageContaining("NOTIFY_OVERDUE_JOB_INTERVAL")
                .hasMessageContaining("PT1M");
    }
}

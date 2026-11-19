package com.servicedesk.notify.overdue;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.reset;
import static org.mockito.Mockito.timeout;
import static org.mockito.Mockito.verify;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;
import org.springframework.boot.context.properties.EnableConfigurationProperties;
import org.springframework.boot.test.context.ConfigDataApplicationContextInitializer;
import org.springframework.boot.test.context.runner.ApplicationContextRunner;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.scheduling.annotation.ScheduledAnnotationBeanPostProcessor;

/** No Docker and no full app: a context runner with a mocked OverdueJob. */
class OverdueSchedulingConfigTest {

    @Configuration
    @EnableConfigurationProperties(OverdueJobProperties.class)
    static class Deps {
        static final OverdueJob JOB = mock(OverdueJob.class);

        @Bean
        OverdueJob overdueJob() {
            return JOB;
        }
    }

    ApplicationContextRunner runner() {
        return new ApplicationContextRunner().withUserConfiguration(Deps.class, OverdueSchedulingConfig.class);
    }

    @Test
    void presentWhenTheFlagIsUnset() {
        runner().run(ctx -> {
            assertThat(ctx).hasSingleBean(OverdueSchedulingConfig.class);
            assertThat(ctx).hasSingleBean(ScheduledAnnotationBeanPostProcessor.class);
        });
    }

    @Test
    void absentWhenDisabled() {
        runner().withPropertyValues("app.notify.overdue-job.enabled=false").run(ctx -> {
            assertThat(ctx).doesNotHaveBean(OverdueSchedulingConfig.class);
            assertThat(ctx).doesNotHaveBean(ScheduledAnnotationBeanPostProcessor.class);
        });
    }

    @ParameterizedTest
    @ValueSource(strings = {"test", "dbtest", "pipeline"})
    void absentInTheTestProfilesBecauseTheirYmlFilesDisableIt(String profile) {
        runner().withInitializer(new ConfigDataApplicationContextInitializer())
                .withPropertyValues("spring.profiles.active=" + profile).run(ctx -> {
                    assertThat(ctx).doesNotHaveBean(OverdueSchedulingConfig.class);
                    assertThat(ctx).doesNotHaveBean(ScheduledAnnotationBeanPostProcessor.class);
                });
    }

    @Test
    void enabledWithAShortIntervalCallsRunOnceRepeatedly() {
        reset(Deps.JOB); // earlier tests' startup ticks must not count
        runner().withPropertyValues("app.notify.overdue-job.interval=PT0.1S").run(ctx ->
                verify(Deps.JOB, timeout(3000).atLeast(2)).runOnce());
    }

    @Test
    void aBadIntervalStopsStartup() {
        runner().withPropertyValues("app.notify.overdue-job.interval=60").run(ctx -> {
            assertThat(ctx).hasFailed();
            assertThat(ctx.getStartupFailure()).rootCause().hasMessageContaining("NOTIFY_OVERDUE_JOB_INTERVAL");
        });
    }
}
